using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Scripting;
using Voltage.Editor.Utils;

namespace Voltage.Editor.Builders;

/// <summary>
/// Handles building/exporting a game project into a standalone executable.
/// Produces <c>bin/&lt;Configuration&gt;/&lt;rid&gt;</c> inside the game project folder containing:
///   - The published executable (AOT + trimmed)
///   - Voltage engine content files (compiled effects, etc.)
///   - Project content/assets (copied raw or compiled via MGCB)
///   - Project Data folder (scenes, prefabs, serialized component data)
///   - Project settings
/// </summary>
public static class GameBuilder
{
	/// <summary>
	/// Where a published game lands: <c>&lt;project&gt;/bin/&lt;Configuration&gt;/&lt;rid&gt;</c>. bin\ is already
	/// excluded from the SDK's item globs and from the generated .gitignore.
	/// </summary>
	public static string GetBuildOutputDirectory(IGameProject project, BuildPlatform platform, string configuration) =>
		Path.Combine(project.ProjectPath, "bin", configuration, platform.FolderSuffix);

	/// <inheritdoc cref="GetBuildOutputDirectory(IGameProject,BuildPlatform,string)"/>
	public static string GetBuildOutputDirectory(IGameProject project, BuildPlatform platform, bool debugBuild) =>
		GetBuildOutputDirectory(project, platform, debugBuild ? "Debug" : "Release");

	// Events for progress tracking
	public static event Action<string> OnBuildStepStarted;
	public static event Action<string, bool> OnBuildStepCompleted;
	public static event Action<int> OnBuildStarted;
	public static event Action<bool, string> OnBuildFinished;

	/// <summary>
	/// Builds the game project into a standalone executable.
	/// </summary>
	/// <param name="project">The game project to build</param>
	/// <param name="platform">Target platform to publish for</param>
	/// <param name="compileAssets">Compile the Content folder with MGCB before copying; see AssetBuildService.</param>
	/// <param name="debugBuild">If true, publishes in Debug configuration; otherwise Release.</param>
	/// <param name="useLinuxCompatContainer">If true, Linux AOT publishes are run inside an old-glibc
	/// container so the binary runs on stock SteamOS / older distros. Ignored for non-Linux targets.</param>
	/// <param name="cancellationToken">Token to cancel the build</param>
	/// <returns>True if build succeeded</returns>
	public static Task<bool> BuildGameAsync(IGameProject project, BuildPlatform platform, bool compileAssets, bool debugBuild, bool useLinuxCompatContainer, CancellationToken cancellationToken) =>
		BuildGameAsync(project, platform, compileAssets, debugBuild, useLinuxCompatContainer, true, cancellationToken);

	/// <summary>With <paramref name="aot"/> false the publish is a plain self-contained build: faster, no C++ toolchain, but not what ships.</summary>
	public static async Task<bool> BuildGameAsync(IGameProject project, BuildPlatform platform, bool compileAssets, bool debugBuild, bool useLinuxCompatContainer, bool aot, CancellationToken cancellationToken)
	{
		if (project == null)
		{
			Debug.Error("No project provided for game build!", "GameBuilder");
			OnBuildFinished?.Invoke(false, "No project provided.");
			return false;
		}

		if (platform == null)
		{
			Debug.Error("No target platform specified for game build!", "GameBuilder");
			OnBuildFinished?.Invoke(false, "No target platform specified.");
			return false;
		}

		var configuration = debugBuild ? "Debug" : "Release";

		var buildDir = GetBuildOutputDirectory(project, platform, configuration);
		OnBuildStarted?.Invoke(7);

		try
		{
			// Clean and create build directory
			if (Directory.Exists(buildDir))
			{
				Directory.Delete(buildDir, true);
			}
			Directory.CreateDirectory(buildDir);

			cancellationToken.ThrowIfCancellationRequested();

			// 1) Build engine DLLs in Release (no EDITOR) and sync to EngineLibs
			OnBuildStepStarted?.Invoke("Building runtime engine libraries (without EDITOR)...");
			bool runtimeLibsSuccess = await Task.Run(
				() => EngineLibsSync.BuildRuntimeLibs(project.ProjectPath, debugBuild),
				cancellationToken);
			OnBuildStepCompleted?.Invoke("Build runtime engine libraries", runtimeLibsSuccess);

			if (!runtimeLibsSuccess)
			{
				OnBuildFinished?.Invoke(false, "Failed to build runtime engine libraries. Check console for errors.");

				// Restore editor-flavored DLLs so the Roslyn script compiler keeps working
				EngineLibsSync.SyncToProject(project.ProjectPath);
				return false;
			}

			cancellationToken.ThrowIfCancellationRequested();

			// 1b) Verify plugins and regenerate the plugin build glue (Plugins.g.props, bootstrap,
			// trimmer roots) from the lockfile-pinned state. An unavailable plugin fails the build —
			// shipping a game whose scenes depend on missing plugin components would be silent data loss.
			OnBuildStepStarted?.Invoke("Preparing project plugins...");
			bool pluginsSuccess = Plugins.PluginSync.SyncForBuild(project, out var pluginError);
			OnBuildStepCompleted?.Invoke("Prepare project plugins", pluginsSuccess);

			if (!pluginsSuccess)
			{
				OnBuildFinished?.Invoke(false, pluginError);
				EngineLibsSync.SyncToProject(project.ProjectPath);
				return false;
			}

			cancellationToken.ThrowIfCancellationRequested();

			// Advisory toolchain check. Detection is best-effort, so a miss must NOT abort a build that would
			// otherwise succeed (the user may have installed the tools in a way our probes don't recognize).
			// We log a heads-up and let the publish below be the authority: if a dependency is genuinely
			// missing, dotnet/the AOT linker fails with a specific, surfaced error.
			var depResult = Voltage.Diagnostics.NativeDependencyChecker.Check(
				Voltage.Diagnostics.NativeDependencyCatalog.BuildDependencies);
			if (!depResult.AllPresent)
			{
				var missing = string.Join(", ", depResult.Missing.Select(s => s.Dependency.FriendlyName));
				var manualCmd = Voltage.Diagnostics.HostPackageManager.BuildManualInstallCommand(
					depResult.PackageManager, depResult.MissingPackages());
				var hint = string.IsNullOrEmpty(manualCmd)
					? "See the build documentation for your platform's toolchain requirements."
					: $"If the publish fails, install them with: {manualCmd}";
				Debug.Warn(
					$"Build toolchain not fully detected ({missing}). Proceeding anyway. {hint}", "GameBuilder");
			}

			// 2)  Publish the game project (self-contained + trimmed)
			OnBuildStepStarted?.Invoke($"Publishing game executable ({platform.DisplayName}, {configuration}, AOT + Trimmed)...");
			bool publishSuccess = await Task.Run(() => PublishProject(project, platform, configuration, buildDir, useLinuxCompatContainer && aot, aot, cancellationToken), cancellationToken);
			OnBuildStepCompleted?.Invoke("Publish game executable", publishSuccess);

			// Restore editor-flavored DLLs immediately after publish so the Roslyn script
			// compiler and IDE references continue to work correctly.
			EngineLibsSync.SyncToProject(project.ProjectPath);

			if (!publishSuccess)
			{
				OnBuildFinished?.Invoke(false, "Failed to publish game executable. Check console for errors.");
				return false;
			}

			cancellationToken.ThrowIfCancellationRequested();

			// 3) Copy Voltage engine content files (compiled effects, fonts, etc.)
			OnBuildStepStarted?.Invoke("Copying Voltage engine content...");
			bool voltageContentSuccess = CopyVoltageContent(buildDir);
			OnBuildStepCompleted?.Invoke("Copy Voltage engine content", voltageContentSuccess);

			cancellationToken.ThrowIfCancellationRequested();

			// 4) Compile the Content folder with MGCB when asked, then copy the raw files the contract keeps.
			AssetBuildReport assetReport = null;
			if (compileAssets)
			{
				OnBuildStepStarted?.Invoke("Compiling assets with MGCB...");
				assetReport = await AssetBuildService.RunAsync(project, AssetBuildSettingsStore.Get(), Path.Combine(buildDir, "Content"), platform.RuntimeIdentifier, false, false, cancellationToken);
				OnBuildStepCompleted?.Invoke("Compile assets", assetReport.Success);
				if (!assetReport.Success)
				{
					OnBuildFinished?.Invoke(false, "Asset build failed. Check the Asset Build window or console for errors.");
					return false;
				}
			}

			cancellationToken.ThrowIfCancellationRequested();

			OnBuildStepStarted?.Invoke("Copying project assets...");
			bool assetsSuccess = CopyProjectAssets(project, buildDir, assetReport);
			assetsSuccess &= Plugins.PluginSync.CopyPluginContentToBuild(buildDir);
			OnBuildStepCompleted?.Invoke("Copy project assets", assetsSuccess);

			cancellationToken.ThrowIfCancellationRequested();

			// 5) Copy project Data folder (scenes, prefabs, serialized data)
			OnBuildStepStarted?.Invoke("Copying project data...");
			bool dataSuccess = CopyProjectData(project, buildDir);
			OnBuildStepCompleted?.Invoke("Copy project data", dataSuccess);

			cancellationToken.ThrowIfCancellationRequested();

			// 6) Copy project settings
			OnBuildStepStarted?.Invoke("Copying project settings...");
			bool settingsSuccess = CopyProjectSettings(project, buildDir);
			OnBuildStepCompleted?.Invoke("Copy project settings", settingsSuccess);

			bool allSuccess = runtimeLibsSuccess && publishSuccess && voltageContentSuccess && assetsSuccess && dataSuccess && settingsSuccess;

			if (allSuccess)
			{
				OnBuildFinished?.Invoke(true, $"Build succeeded! Output: {buildDir}");
			}
			else
			{
				OnBuildFinished?.Invoke(true, "Build completed with warnings. Check console for details.");
			}

			return allSuccess;
		}
		catch (OperationCanceledException)
		{
			OnBuildFinished?.Invoke(false, "Build cancelled.");

			// Restore editor DLLs on cancellation too
			EngineLibsSync.SyncToProject(project.ProjectPath);
			return false;
		}
		catch (Exception ex)
		{
			OnBuildFinished?.Invoke(false, $"Build failed: {ex.Message}");

			// Restore editor DLLs on failure
			EngineLibsSync.SyncToProject(project.ProjectPath);
			return false;
		}
	}

	/// <summary>
	/// Publishes the game project using dotnet publish as an AOT, trimmed deployment.
	/// Note: EngineLibs should already contain runtime (non-EDITOR) DLLs at this point.
	/// </summary>
	private static bool PublishProject(IGameProject project, BuildPlatform platform, string configuration, string buildDir, bool useLinuxCompatContainer, bool aot, CancellationToken cancellationToken)
	{
		try
		{
			var csprojPath = Path.Combine(project.ProjectPath, $"{project.ProjectName}.csproj");

			if (!File.Exists(csprojPath))
			{
				Debug.Error($"Project file not found: {csprojPath}");
				return false;
			}

			EnsureGenerateAssemblyInfoDisabled(csprojPath);

			// Ensure TrimmerRoots.xml is synced to the game project before publishing.
			// Without it, NativeAOT strips type metadata needed by the JSON serializer.
			EngineLibsSync.SyncTrimmerRoots(project.ProjectPath);
			EnsureTrimmerRootsInCsproj(csprojPath);
			EnsurePluginLibsExcludedInCsproj(csprojPath);
			EnsurePluginsImportInCsproj(csprojPath);

			// Linux NativeAOT binaries are linked against the build machine's glibc. The editor often
			// runs inside a recent distrobox (newer glibc than the SteamOS host / older distros), so a
			// direct publish produces a binary that only starts in that build environment. Routing the
			// "publish" through an old-glibc container fixes the floor.
			if (useLinuxCompatContainer && LinuxContainerBuild.IsLinux(platform))
			{
				var outcome = LinuxContainerBuild.Publish(
					csprojPath, project.ProjectName, project.ProjectPath, platform.RuntimeIdentifier,
					configuration, buildDir, cancellationToken, out var containerMessage);

				switch (outcome)
				{
					case LinuxContainerBuild.Outcome.Succeeded:
						EditorDebug.Log($"Linux publish: {containerMessage}.", "GameBuilder");
						return true;

					case LinuxContainerBuild.Outcome.Failed:
						Debug.Error($"Linux container publish failed: {containerMessage}. Check console for details.");
						return false;

					case LinuxContainerBuild.Outcome.Unavailable:
					default:
						EditorDebug.Warn(
							$"glibc-compat container unavailable ({containerMessage}). Falling back to a direct " +
							"NativeAOT publish — the resulting binary may require this machine's glibc and may not " +
							"run on stock SteamOS or older Linux systems.", "GameBuilder");
						break; // Fall through to the direct publish below.
				}
			}

			var arguments = $"publish \"{csprojPath}\" " +
			                $"-c {configuration} " +
			                $"-r {platform.RuntimeIdentifier} " +
			                $"-o \"{buildDir}\" " +
			                $"--self-contained true " +
			                (aot
				                ? $"-p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=link -p:TrimmerRootAssembly={project.ProjectName} "
				                : "-p:PublishAot=false -p:PublishTrimmed=false ") +
			                $"-p:IncludeNativeLibrariesForSelfExtract=true";

			var processInfo = new ProcessStartInfo
			{
				FileName = "dotnet",
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true,
				WorkingDirectory = project.ProjectPath
			};

			using var process = Process.Start(processInfo);
			if (process == null)
			{
				Debug.Error("Failed to start dotnet publish process");
				return false;
			}

			// Read stdout and stderr asynchronously to prevent deadlocks on large output
			var outputTask = process.StandardOutput.ReadToEndAsync();
			var errorTask = process.StandardError.ReadToEndAsync();
			process.WaitForExit();

			var output = outputTask.Result;
			var error = errorTask.Result;

			if (cancellationToken.IsCancellationRequested)
				return false;

			if (process.ExitCode != 0)
			{
				Debug.Error($"dotnet publish failed (exit code {process.ExitCode})");
				if (!string.IsNullOrWhiteSpace(error))
					Debug.Error($"Errors:\n{error}");
				if (!string.IsNullOrWhiteSpace(output))
					Debug.Error($"Output:\n{output}");
				return false;
			}

			return true;
		}
		catch (Exception ex)
		{
			Debug.Error($"Error during dotnet publish: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Finds the game executable in the build output directory.
	/// </summary>
	public static string FindGameExecutable(IGameProject project, BuildPlatform platform, bool debugBuild = false)
	{
		var buildDir = GetBuildOutputDirectory(project, platform, debugBuild);
		if (!Directory.Exists(buildDir))
			return null;

		// On Windows, look for .exe; on Linux/macOS, look for file without extension
		var exeName = platform.RuntimeIdentifier.StartsWith("win")
			? $"{project.ProjectName}.exe"
			: project.ProjectName;

		var exePath = Path.Combine(buildDir, exeName);
		return File.Exists(exePath) ? exePath : null;
	}

	/// <summary>
	/// Ensures the .csproj contains GenerateAssemblyInfo=false to prevent CS0579
	/// duplicate assembly attribute errors when Properties/AssemblyInfo.cs exists.
	/// This patches older projects created before the fix was added to the template.
	/// </summary>
	private static void EnsureGenerateAssemblyInfoDisabled(string csprojPath)
	{
		try
		{
			var content = File.ReadAllText(csprojPath);

			if (content.Contains("GenerateAssemblyInfo", StringComparison.OrdinalIgnoreCase))
				return; // Already present, nothing to do

			// Check if Properties/AssemblyInfo.cs exists only patch if it does
			var projectDir = Path.GetDirectoryName(csprojPath);
			var assemblyInfoPath = Path.Combine(projectDir!, "Properties", "AssemblyInfo.cs");
			if (!File.Exists(assemblyInfoPath))
				return; // No manual AssemblyInfo, auto-generation is fine

			// Insert <GenerateAssemblyInfo>false</GenerateAssemblyInfo> into the first PropertyGroup
			const string marker = "</PropertyGroup>";
			var insertIndex = content.IndexOf(marker, StringComparison.Ordinal);
			if (insertIndex < 0)
				return;

			var insertion = "\n    <!-- Disable auto-generated assembly info to avoid conflicts with Properties/AssemblyInfo.cs -->" +
			                "\n    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n  ";

			content = content.Insert(insertIndex, insertion);
			File.WriteAllText(csprojPath, content);
		}
		catch (Exception ex)
		{
			EditorDebug.Warn($"Could not patch .csproj for GenerateAssemblyInfo: {ex.Message}", "GameBuilder");
		}
	}

	/// <summary>
	/// Ensures the .csproj contains a TrimmerRootDescriptor item for TrimmerRoots.xml.
	/// Patches older projects created before the trimmer roots fix was added to the template.
	/// </summary>
	private static void EnsureTrimmerRootsInCsproj(string csprojPath)
	{
		try
		{
			var content = File.ReadAllText(csprojPath);

			if (content.Contains("TrimmerRootDescriptor", StringComparison.OrdinalIgnoreCase))
				return; // Already present

			// Insert before the closing </Project> tag
			const string marker = "</Project>";
			var insertIndex = content.LastIndexOf(marker, StringComparison.Ordinal);
			if (insertIndex < 0)
				return;

			var insertion =
				"\n  <!-- Trimmer roots: preserve types needed by Voltage JSON serializer -->\n" +
				"  <ItemGroup>\n" +
				"    <TrimmerRootDescriptor Include=\"TrimmerRoots.xml\" Condition=\"Exists('TrimmerRoots.xml')\" />\n" +
				"  </ItemGroup>\n";

			content = content.Insert(insertIndex, insertion);
			File.WriteAllText(csprojPath, content);

			EditorDebug.Log("Patched .csproj with TrimmerRootDescriptor for NativeAOT.", "GameBuilder");
		}
		catch (Exception ex)
		{
			EditorDebug.Warn($"Could not patch .csproj for TrimmerRoots: {ex.Message}", "GameBuilder");
		}
	}

	/// <summary>
	/// Keeps PluginLibs out of the SDK's default item globs. Plugins.g.props adds back exactly the
	/// files a game build needs, so without this a plugin's sources compile alongside the DLL that
	/// already contains them (CS0436), its editor-only sources come too and fail on ImGuiNET and
	/// Voltage.Editor, and the generated bootstrap is included twice (CS2002).
	/// </summary>
	public static void EnsurePluginLibsExcludedInCsproj(string csprojPath)
	{
		const string exclude = @"PluginLibs\**";

		try
		{
			var content = File.ReadAllText(csprojPath);

			var existing = Regex.Match(content, @"<DefaultItemExcludes>(?<value>.*?)</DefaultItemExcludes>",
				RegexOptions.Singleline);

			if (existing.Success)
			{
				if (existing.Groups["value"].Value.Contains("PluginLibs", StringComparison.OrdinalIgnoreCase))
					return;

				content = content.Remove(existing.Index, existing.Length)
					.Insert(existing.Index, $"<DefaultItemExcludes>{existing.Groups["value"].Value};{exclude}</DefaultItemExcludes>");
			}
			else
			{
				const string marker = "</Project>";
				var insertIndex = content.LastIndexOf(marker, StringComparison.Ordinal);
				if (insertIndex < 0)
					return;

				content = content.Insert(insertIndex,
					"\n  <!-- PluginLibs is populated by Plugins.g.props, never by the SDK's default globs -->\n" +
					"  <PropertyGroup>\n" +
					$"    <DefaultItemExcludes>$(DefaultItemExcludes);{exclude}</DefaultItemExcludes>\n" +
					"  </PropertyGroup>\n");
			}

			File.WriteAllText(csprojPath, content);
			EditorDebug.Log("Patched .csproj to keep PluginLibs out of the default compile glob.", "GameBuilder");
		}
		catch (Exception ex)
		{
			EditorDebug.Warn($"Could not patch .csproj for the PluginLibs exclude: {ex.Message}", "GameBuilder");
		}
	}

	/// <summary>
	/// Ensures the .csproj imports the generated PluginLibs/Plugins.g.props, which wires plugin
	/// references, sources, trimmer roots, and native copies into the game build. The import is
	/// Exists-conditioned, so projects without plugins are unaffected. Patches projects created
	/// before the plugin system existed.
	/// </summary>
	public static void EnsurePluginsImportInCsproj(string csprojPath)
	{
		try
		{
			var content = File.ReadAllText(csprojPath);

			if (content.Contains("Plugins.g.props", StringComparison.OrdinalIgnoreCase))
				return; // Already present

			// Insert before the closing </Project> tag
			const string marker = "</Project>";
			var insertIndex = content.LastIndexOf(marker, StringComparison.Ordinal);
			if (insertIndex < 0)
				return;

			var insertion =
				"\n  <!-- Project plugins: generated by the Voltage Editor from plugins.json (references, sources, natives) -->\n" +
				"  <Import Project=\"$(MSBuildThisFileDirectory)PluginLibs/Plugins.g.props\" Condition=\"Exists('$(MSBuildThisFileDirectory)PluginLibs/Plugins.g.props')\" />\n";

			content = content.Insert(insertIndex, insertion);
			File.WriteAllText(csprojPath, content);

			EditorDebug.Log("Patched .csproj with the PluginLibs/Plugins.g.props import.", "GameBuilder");
		}
		catch (Exception ex)
		{
			EditorDebug.Warn($"Could not patch .csproj for the plugins import: {ex.Message}", "GameBuilder");
		}
	}

	/// <summary>
	/// Copies the Voltage engine content files (compiled effects, fonts, etc.) to the build output.
	/// These are located in the editor's Content/Voltage directory.
	/// </summary>
	private static bool CopyVoltageContent(string buildDir)
	{
		try
		{
			var editorDir = FindEditorProjectDir();
			var voltageContentSrc = Path.Combine(editorDir, "Content", "Voltage");

			if (!Directory.Exists(voltageContentSrc))
			{
				Debug.Warn($"Voltage content directory not found: {voltageContentSrc}");
				EditorDebug.Warn("No Voltage engine content found to copy. Effects may be missing.", "GameBuilder");
				return true;
			}

			// Editor-only directories that should NOT be included in game builds
			var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"Layouts",
				"User"
			};

			var voltageContentDest = Path.Combine(buildDir, "Content", "Voltage");
			CopyDirectoryRecursiveFiltered(voltageContentSrc, voltageContentDest, excludedDirs);

			// Copy the default bitmap font and its texture into Content/Voltage/Fonts.
			// Core.Initialize() loads it from "Content/Voltage/Fonts/VoltageDefaultBMFont.fnt".
			var contentSrcDir = Path.Combine(editorDir, "Content");
			var fontDestDir = Path.Combine(voltageContentDest, "Fonts");
			Directory.CreateDirectory(fontDestDir);

			var defaultFontFiles = new[]
			{
				"VoltageDefaultBMFont.fnt",
				"VoltageDefaultBMFont_0.png"
			};

			foreach (var fontFile in defaultFontFiles)
			{
				var srcPath = Path.Combine(contentSrcDir, fontFile);
				if (File.Exists(srcPath))
				{
					var destPath = Path.Combine(fontDestDir, fontFile);
					File.Copy(srcPath, destPath, true);
					EditorDebug.Log($"Copied default font file: {fontFile}", "GameBuilder");
				}
				else
				{
					EditorDebug.Warn($"Default font file not found: {srcPath}", "GameBuilder");
				}
			}

			var copiedFiles = Directory.Exists(voltageContentDest)
				? Directory.GetFiles(voltageContentDest, "*.*", SearchOption.AllDirectories)
				: Array.Empty<string>();
			EditorDebug.Log($"Copied {copiedFiles.Length} Voltage engine content file(s).", "GameBuilder");
			return true;
		}
		catch (Exception ex)
		{
			Debug.Error($"Error copying Voltage content: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Recursively copies a directory and all its contents, excluding directories whose names
	/// match any entry in the <paramref name="excludedDirNames"/> set.
	/// </summary>
	private static void CopyDirectoryRecursiveFiltered(string sourceDir, string destDir, HashSet<string> excludedDirNames)
	{
		Directory.CreateDirectory(destDir);

		foreach (var file in Directory.GetFiles(sourceDir))
		{
			var destFile = Path.Combine(destDir, Path.GetFileName(file));
			File.Copy(file, destFile, true);
		}

		foreach (var dir in Directory.GetDirectories(sourceDir))
		{
			var dirName = Path.GetFileName(dir);
			if (excludedDirNames.Contains(dirName))
				continue;

			CopyDirectoryRecursiveFiltered(dir, Path.Combine(destDir, dirName), excludedDirNames);
		}
	}

	/// <summary>Copies the Content folder; after an asset build only the raw files the contract keeps, and compiled sources unless stripped.</summary>
	private static bool CopyProjectAssets(IGameProject project, string buildDir, AssetBuildReport assetReport)
	{
		try
		{
			var contentSrc = project.ContentsFolder;

			if (!Directory.Exists(contentSrc))
			{
				EditorDebug.Log("No Content folder found in project, skipping asset copy.", "GameBuilder");
				return true;
			}

			var contentDest = Path.Combine(buildDir, "Content");
			if (assetReport != null)
			{
				var plan = new AssetBuildPlan { OutputDir = contentDest };
				lock (assetReport.Lock)
					plan.Items = assetReport.Items.ToList();
				AssetBuildService.CopyRaw(project, plan, assetReport);
			}
			else
				CopyDirectoryRecursive(contentSrc, contentDest);

			var copiedFiles = Directory.Exists(contentDest)
				? Directory.GetFiles(contentDest, "*.*", SearchOption.AllDirectories)
				: Array.Empty<string>();
			EditorDebug.Log($"Copied {copiedFiles.Length} project content file(s).", "GameBuilder");
			return true;
		}
		catch (Exception ex)
		{
			Debug.Error($"Error copying project assets: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Copies the project's Data folder (scenes, prefabs, serialized component data) to the build output.
	/// Also copies the compiled scripts DLL if available.
	/// </summary>
	private static bool CopyProjectData(IGameProject project, string buildDir)
	{
		try
		{
			// Copy the Data folder (scenes, prefabs, etc.)
			var dataSrc = project.DataFolder;

			if (Directory.Exists(dataSrc))
			{
				var dataDest = Path.Combine(buildDir, "Data");
				CopyDirectoryRecursive(dataSrc, dataDest);

				var copiedFiles = Directory.GetFiles(dataDest, "*.*", SearchOption.AllDirectories);
				EditorDebug.Log($"Copied {copiedFiles.Length} data file(s) (scenes, prefabs, etc.).", "GameBuilder");
			}
			else
			{
				EditorDebug.Log("No Data folder found in project, skipping.", "GameBuilder");
			}

			// Copy the compiled scripts assembly if it exists.
			// The scripts are compiled by Roslyn into an in-memory assembly during editor sessions,
			// but the dotnet publish step compiles the Scripts/*.cs files directly into the game executable
			// via the .csproj. So the scripts are already included in the published output.
			// We just need to ensure the script source files are part of the project.

			return true;
		}
		catch (Exception ex)
		{
			Debug.Error($"Error copying project data: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Copies the ProjectSettings.json to the build output directory.
	/// </summary>
	private static bool CopyProjectSettings(IGameProject project, string buildDir)
	{
		try
		{
			var settingsSrc = Path.Combine(project.ProjectPath, "ProjectSettings.json");

			if (File.Exists(settingsSrc))
			{
				var settingsDest = Path.Combine(buildDir, "ProjectSettings.json");
				File.Copy(settingsSrc, settingsDest, true);
				EditorDebug.Log("Copied ProjectSettings.json to build output.", "GameBuilder");
			}
			else
			{
				EditorDebug.Warn("ProjectSettings.json not found, skipping.", "GameBuilder");
			}

			return true;
		}
		catch (Exception ex)
		{
			Debug.Error($"Error copying project settings: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Recursively copies a directory and all its contents.
	/// </summary>
	private static void CopyDirectoryRecursive(string sourceDir, string destDir)
	{
		Directory.CreateDirectory(destDir);

		foreach (var file in Directory.GetFiles(sourceDir))
		{
			var destFile = Path.Combine(destDir, Path.GetFileName(file));
			File.Copy(file, destFile, true);
		}

		foreach (var dir in Directory.GetDirectories(sourceDir))
		{
			var dirName = Path.GetFileName(dir);
			CopyDirectoryRecursive(dir, Path.Combine(destDir, dirName));
		}
	}

	/// <summary>
	/// Finds the Voltage.Editor project directory by walking up from the app base directory.
	/// </summary>
	private static string FindEditorProjectDir()
	{
		var dir = AppContext.BaseDirectory;
		var di = new DirectoryInfo(dir);
		while (di != null)
		{
			if (File.Exists(Path.Combine(di.FullName, "Voltage.Editor.csproj")))
				return di.FullName;
			di = di.Parent;
		}

		return AppContext.BaseDirectory;
	}
}
