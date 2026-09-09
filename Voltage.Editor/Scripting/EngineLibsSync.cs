using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Utils;

namespace Voltage.Editor.Scripting
{
	/// <summary>
	/// Responsible for syncing engine DLLs into a game project's EngineLibs folder.
	///
	/// Two modes:
	/// <list type="bullet">
	///   <item><see cref="SyncToProject"/>  fast file-copy of the running editor's assemblies
	///     (with EDITOR defined). Used by the Roslyn script compiler and IDE references.</item>
	///   <item><see cref="BuildRuntimeLibs"/>  invokes <c>dotnet build -c Release</c> on the engine
	///     projects to produce DLLs <b>without</b> EDITOR. Used before <c>dotnet publish</c> in the
	///     game build pipeline so the shipped executable has clean runtime behaviour.</item>
	/// </list>
	/// </summary>
	public static class EngineLibsSync
	{
		public const string EngineLibsFolderName = "EngineLibs";
		public const string SourceGeneratorDllName = "Voltage.SourceGenerators.dll";

		/// <summary>
		/// The managed DLL file names that belong in EngineLibs and are safe to load as
		/// Roslyn metadata references. This is the single source of truth used by:
		/// <list type="bullet">
		///   <item><see cref="BuildRuntimeLibs"/>  to cherry-pick from build staging</item>
		///   <item><see cref="ScriptCompiler"/>  to reference only managed DLLs, not native ones</item>
		///   <item>The game project .csproj template  via the Reference glob</item>
		/// </list>
		/// Native DLLs (SDL2, OpenAL, clretwrc, etc.) must NEVER appear in this list.
		/// </summary>
		public static readonly string[] ManagedReferenceDlls =
		{
			"Voltage.dll",
			"Voltage.Persistence.dll",
			// Voltage.FarseerPhysics is intentionally absent: it is an installed PLUGIN, referenced by
			// game projects via the generated PluginLibs/Plugins.g.props instead of EngineLibs.
			"MonoGame.Framework.dll",
			// Managed audio decoders pulled in transitively by Voltage.dll (see AudioDecoders). They are
			// referenced by the game .csproj so `dotnet publish` copies them into the shipped output, and
			// synced into EngineLibs below. Pure-managed, no native binaries.
			"NVorbis.dll",
			"NLayer.dll",
		};

		/// <summary>
		/// Managed dependencies of the engine that come from NuGet (not built from source and not named
		/// "Voltage*"), so the assembly-scan sync would otherwise miss them. Copied into EngineLibs by
		/// both sync paths so the game's HintPath references resolve and publish includes them.
		/// </summary>
		private static readonly string[] AdditionalManagedDependencyNames =
		{
			"NVorbis",
			"NLayer",
		};

		/// <summary>
		/// Subset of <see cref="ManagedReferenceDlls"/> that are built from source by
		/// <see cref="BuildRuntimeLibs"/>. MonoGame is excluded because it comes from NuGet.
		/// </summary>
		private static readonly string[] BuiltEngineDlls =
		{
			"Voltage.dll",
			"Voltage.Persistence.dll",
		};

		/// <summary>
		/// Whether the EngineLibs folder exists and contains at least the core Voltage DLLs.
		/// </summary>
		public static bool IsReady(string projectPath)
		{
			if (string.IsNullOrEmpty(projectPath))
				return false;

			var engineLibsPath = GetEngineLibsPath(projectPath);
			if (!Directory.Exists(engineLibsPath))
				return false;

			var voltageDlls = Directory.GetFiles(engineLibsPath, "Voltage.*.dll");
			return voltageDlls.Length > 0;
		}

		/// <summary>
		/// Returns the EngineLibs directory path for a given project.
		/// </summary>
		public static string GetEngineLibsPath(string projectPath)
		{
			return Path.Combine(projectPath, EngineLibsFolderName);
		}

		#region Editor Sync (fast file copy, EDITOR-flavored DLLs)

		/// <summary>
		/// Copies all currently loaded Voltage and MonoGame/FNA assemblies (and their PDBs)
		/// into the project's EngineLibs folder. These are the editor-flavored DLLs (compiled
		/// with EDITOR defined) and are used for the Roslyn script compiler and IDE references.
		/// Returns the number of files copied.
		/// </summary>
		public static int SyncToProject(string projectPath)
		{
			if (string.IsNullOrEmpty(projectPath))
			{
				EditorDebug.Error("Cannot sync EngineLibs: project path is null or empty.", "EngineLibsSync");
				return 0;
			}

			var engineLibsPath = GetEngineLibsPath(projectPath);
			Directory.CreateDirectory(engineLibsPath);

			var assemblies = GetAssembliesToSync();
			int synced = 0;

			foreach (var assembly in assemblies)
			{
				if (string.IsNullOrEmpty(assembly.Location) || !File.Exists(assembly.Location))
					continue;

				try
				{
					var dllName = Path.GetFileName(assembly.Location);
					var destDll = Path.Combine(engineLibsPath, dllName);

					if (ShouldCopyFile(assembly.Location, destDll))
					{
						File.Copy(assembly.Location, destDll, overwrite: true);
						synced++;

						// Also copy the PDB if it exists (needed for script debugging)
						var sourcePdb = Path.ChangeExtension(assembly.Location, ".pdb");
						if (File.Exists(sourcePdb))
						{
							var destPdb = Path.Combine(engineLibsPath, Path.GetFileName(sourcePdb));
							File.Copy(sourcePdb, destPdb, overwrite: true);
						}
					}
				}
				catch (Exception ex)
				{
					EditorDebug.Error($"Failed to copy {assembly.GetName().Name}: {ex.Message}", "EngineLibsSync");
				}
			}

			SyncSourceGeneratorDll(engineLibsPath);
			CopyAdditionalManagedDeps(engineLibsPath);
			SyncTrimmerRoots(projectPath);
			SyncMonoGamePackageVersion(projectPath);

			if (synced > 0)
				EditorDebug.Log($"Synced {synced} engine DLL(s) to: {engineLibsPath}", "EngineLibsSync");
			else
				EditorDebug.Log("EngineLibs already up to date.", "EngineLibsSync");

			return synced;
		}

		private static readonly Regex MonoGamePackageVersion =
			new(@"(<PackageReference\s+Include=""MonoGame\.[^""]+""\s+Version="")([^""]+)("")", RegexOptions.Compiled);

		/// <summary>Points the game project's MonoGame packages at the version the editor runs, so a synced engine DLL and the game's own reference never disagree.</summary>
		public static void SyncMonoGamePackageVersion(string projectPath)
		{
			var version = MonoGameVersionResolver.GetVersion();
			foreach (var csproj in Directory.GetFiles(projectPath, "*.csproj"))
			{
				try
				{
					var text = File.ReadAllText(csproj);
					var updated = MonoGamePackageVersion.Replace(text, m => m.Groups[2].Value == version ? m.Value : m.Groups[1].Value + version + m.Groups[3].Value);
					if (updated == text)
						continue;

					File.WriteAllText(csproj, updated);
					EditorDebug.Log($"Updated MonoGame package references in {Path.GetFileName(csproj)} to {version}.", "EngineLibsSync");
				}
				catch (Exception ex)
				{
					EditorDebug.Warn($"Could not update MonoGame version in {Path.GetFileName(csproj)}: {ex.Message}", "EngineLibsSync");
				}
			}
		}

		#endregion

		#region Runtime Build (dotnet build, NO EDITOR)

		/// <summary>
		/// Builds the engine projects in Release configuration (without EDITOR defined) and
		/// copies only the managed Voltage DLLs into the project's EngineLibs folder.
		/// A temporary staging directory is used for the build output so that native libraries,
		/// .deps.json files, and other build artefacts do not pollute EngineLibs.
		///
		/// Call this once before <c>dotnet publish</c> in the game build pipeline.
		/// </summary>
		/// <param name="projectPath">Root path of the game project</param>
		/// <param name="debugBuild">If true, builds in Debug; otherwise Release</param>
		/// <returns>True if the build succeeded</returns>
		public static bool BuildRuntimeLibs(string projectPath, bool debugBuild = false)
		{
			if (string.IsNullOrEmpty(projectPath))
			{
				EditorDebug.Error("Cannot build runtime libs: project path is null or empty.", "EngineLibsSync");
				return false;
			}

			var solutionDir = FindSolutionDir();
			if (solutionDir == null)
			{
				EditorDebug.Error("Cannot build runtime libs: solution directory not found.", "EngineLibsSync");
				return false;
			}

			var engineLibsPath = GetEngineLibsPath(projectPath);
			Directory.CreateDirectory(engineLibsPath);

			var configuration = debugBuild ? "Debug" : "Release";

			// Use a temporary staging directory so dotnet build's full output (native libs,
			// .deps.json, etc.) doesn't pollute the EngineLibs folder.
			var stagingDir = Path.Combine(Path.GetTempPath(), "VoltageEngineLibsBuild", Guid.NewGuid().ToString("N"));

			try
			{
				Directory.CreateDirectory(stagingDir);

				// Build each engine project in the non-EDITOR configuration.
				// Order matters: dependencies first.
				var projectsToBuild = new[]
				{
					Path.Combine(solutionDir, "Voltage.Persistence", "Voltage.Persistence.csproj"),
					Path.Combine(solutionDir, "Voltage.Engine", "Voltage.Engine.csproj"),
				};

				foreach (var csproj in projectsToBuild)
				{
					if (!File.Exists(csproj))
					{
						EditorDebug.Warn($"Engine project not found, skipping: {csproj}", "EngineLibsSync");
						continue;
					}

					EditorDebug.Log($"Building {Path.GetFileName(csproj)} ({configuration})...", "EngineLibsSync");

					if (!RunDotnetBuild(csproj, configuration, stagingDir))
					{
						EditorDebug.Error($"Failed to build {Path.GetFileName(csproj)} in {configuration}.", "EngineLibsSync");
						return false;
					}
				}

				// Cherry-pick only the managed Voltage DLLs (and their PDBs) from the staging dir
				int promoted = 0;
				foreach (var dllName in BuiltEngineDlls)
				{
					var srcDll = Path.Combine(stagingDir, dllName);
					if (!File.Exists(srcDll))
					{
						EditorDebug.Warn($"Expected managed DLL not found in staging: {dllName}", "EngineLibsSync");
						continue;
					}

					var destDll = Path.Combine(engineLibsPath, dllName);
					File.Copy(srcDll, destDll, overwrite: true);
					promoted++;

					// Copy PDB if available (useful for debugging game builds)
					var srcPdb = Path.ChangeExtension(srcDll, ".pdb");
					if (File.Exists(srcPdb))
					{
						var destPdb = Path.Combine(engineLibsPath, Path.GetFileName(srcPdb));
						File.Copy(srcPdb, destPdb, overwrite: true);
					}
				}

				EditorDebug.Log($"Promoted {promoted} managed engine DLL(s) to EngineLibs.", "EngineLibsSync");

				// Copy MonoGame/FNA framework DLLs from the running editor (these are config-agnostic)
				CopyFrameworkAssemblies(engineLibsPath);

				// Copy third-party managed deps (NVorbis/NLayer) — config-agnostic, from the editor's output
				CopyAdditionalManagedDeps(engineLibsPath);

				// Source generator is config-agnostic (netstandard2.0, no EDITOR references)
				SyncSourceGeneratorDll(engineLibsPath);

				// Ensure TrimmerRoots.xml is present in the game project for NativeAOT builds
				SyncTrimmerRoots(projectPath);

				EditorDebug.Log($"Runtime engine libs built and synced to: {engineLibsPath}", "EngineLibsSync");
				return true;
			}
			finally
			{
				// Clean up the temporary staging directory
				try
				{
					if (Directory.Exists(stagingDir))
						Directory.Delete(stagingDir, recursive: true);
				}
				catch (Exception ex)
				{
					EditorDebug.Warn($"Could not clean up staging directory: {ex.Message}", "EngineLibsSync");
				}
			}
		}

		/// <summary>
		/// Runs <c>dotnet build</c> for a single project with the given configuration,
		/// outputting into the <paramref name="outputDir"/>.
		/// </summary>
		private static bool RunDotnetBuild(string csprojPath, string configuration, string outputDir)
		{
			try
			{
				var arguments = $"build \"{csprojPath}\" " +
				                $"-c {configuration} " +
				                $"-o \"{outputDir}\" " +
				                $"-v quiet";

				var processInfo = new ProcessStartInfo
				{
					FileName = "dotnet",
					Arguments = arguments,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetDirectoryName(csprojPath)
				};

				using var process = Process.Start(processInfo);
				if (process == null)
				{
					EditorDebug.Error("Failed to start dotnet build process.", "EngineLibsSync");
					return false;
				}

				var outputTask = process.StandardOutput.ReadToEndAsync();
				var errorTask = process.StandardError.ReadToEndAsync();
				process.WaitForExit();

				var output = outputTask.Result;
				var error = errorTask.Result;

				if (process.ExitCode != 0)
				{
					EditorDebug.Error($"dotnet build failed (exit code {process.ExitCode})", "EngineLibsSync");
					if (!string.IsNullOrWhiteSpace(error))
						EditorDebug.Error($"stderr:\n{error}", "EngineLibsSync");
					if (!string.IsNullOrWhiteSpace(output))
						EditorDebug.Error($"stdout:\n{output}", "EngineLibsSync");
					return false;
				}

				return true;
			}
			catch (Exception ex)
			{
				EditorDebug.Error($"Exception running dotnet build: {ex.Message}", "EngineLibsSync");
				return false;
			}
		}

		/// <summary>
		/// Copies the MonoGame framework assemblies from the running process into the output directory.
		/// These DLLs are configuration-agnostic (no EDITOR branches) so we can copy them directly.
		/// </summary>
		private static void CopyFrameworkAssemblies(string destDir)
		{
			var frameworkAssemblies = new[]
			{
				typeof(Microsoft.Xna.Framework.Vector2).Assembly,
				typeof(Microsoft.Xna.Framework.Graphics.Texture2D).Assembly,
			};

			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var asm in frameworkAssemblies)
			{
				if (string.IsNullOrEmpty(asm.Location) || !File.Exists(asm.Location))
					continue;

				var fileName = Path.GetFileName(asm.Location);
				if (!seen.Add(fileName))
					continue;

				var destPath = Path.Combine(destDir, fileName);
				if (ShouldCopyFile(asm.Location, destPath))
				{
					File.Copy(asm.Location, destPath, overwrite: true);

					var pdb = Path.ChangeExtension(asm.Location, ".pdb");
					if (File.Exists(pdb))
						File.Copy(pdb, Path.Combine(destDir, Path.GetFileName(pdb)), overwrite: true);
				}
			}
		}

		/// <summary>
		/// Copies the third-party managed audio-decoder DLLs (<see cref="AdditionalManagedDependencyNames"/>)
		/// from the running editor's output directory into <paramref name="destDir"/>. These are transitive
		/// runtime dependencies of Voltage.dll (NuGet packages, not named "Voltage*"), so the assembly-scan
		/// sync would otherwise miss them — and the game references them from EngineLibs so publish ships them.
		/// They are configuration-agnostic (no EDITOR branches), so the editor's copy is correct for runtime.
		/// </summary>
		private static void CopyAdditionalManagedDeps(string destDir)
		{
			var baseDir = AppContext.BaseDirectory;
			if (string.IsNullOrEmpty(baseDir))
				return;

			foreach (var name in AdditionalManagedDependencyNames)
			{
				var src = Path.Combine(baseDir, name + ".dll");
				if (!File.Exists(src))
				{
					EditorDebug.Warn($"Managed dependency '{name}.dll' not found next to the editor at: {baseDir}", "EngineLibsSync");
					continue;
				}

				var dest = Path.Combine(destDir, name + ".dll");
				try
				{
					if (ShouldCopyFile(src, dest))
						File.Copy(src, dest, overwrite: true);
				}
				catch (Exception ex)
				{
					EditorDebug.Error($"Failed to copy {name}.dll: {ex.Message}", "EngineLibsSync");
				}
			}
		}

		/// <summary>
		/// Returns the NuGet global packages cache path.
		/// </summary>
		private static string GetNuGetPackageCachePath()
		{
			var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var standardPath = Path.Combine(userProfile, ".nuget", "packages");

			if (Directory.Exists(standardPath))
				return standardPath;

			var envPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
			if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath))
				return envPath;

			return null;
		}

		#endregion

		#region Shared Helpers

		/// <summary>
		/// Determines whether the source file should be copied to the destination.
		/// </summary>
		private static bool ShouldCopyFile(string source, string dest)
		{
			if (!File.Exists(dest))
				return true;

			var sourceInfo = new FileInfo(source);
			var destInfo = new FileInfo(dest);

			if (sourceInfo.Length != destInfo.Length)
				return true;

			if (sourceInfo.LastWriteTimeUtc > destInfo.LastWriteTimeUtc)
				return true;

			return false;
		}

		/// <summary>
		/// Copies Voltage.SourceGenerators.dll into the target folder.
		/// </summary>
		private static bool SyncSourceGeneratorDll(string engineLibsPath)
		{
			var sourcePath = FindSourceGeneratorDll();
			if (sourcePath == null)
			{
				EditorDebug.Warn($"{SourceGeneratorDllName} not found in any search path.", "EngineLibsSync");
				return false;
			}

			var destPath = Path.Combine(engineLibsPath, SourceGeneratorDllName);
			if (!ShouldCopyFile(sourcePath, destPath))
				return true;

			try
			{
				File.Copy(sourcePath, destPath, overwrite: true);
				EditorDebug.Log($"Copied {SourceGeneratorDllName} to EngineLibs from: {sourcePath}", "EngineLibsSync");
				return true;
			}
			catch (Exception ex)
			{
				EditorDebug.Error($"Failed to copy {SourceGeneratorDllName}: {ex.Message}", "EngineLibsSync");
				return false;
			}
		}

		/// <summary>
		/// Searches for Voltage.SourceGenerators.dll in multiple locations and returns the
		/// <b>newest</b> match (by last-write time).
		/// </summary>
		private static string FindSourceGeneratorDll()
		{
			var searchDirs = new List<string>();

			var editorDir = Path.GetDirectoryName(typeof(EngineLibsSync).Assembly.Location);
			if (!string.IsNullOrEmpty(editorDir))
				searchDirs.Add(editorDir);

			var baseDir = AppContext.BaseDirectory;
			if (!string.IsNullOrEmpty(baseDir))
				searchDirs.Add(baseDir);

			var solutionDir = FindSolutionDir();
			if (solutionDir != null)
			{
				var generatorProjectDir = Path.Combine(
					solutionDir, "Voltage.SourceGenerators", "Voltage.SourceGenerators");

				// Enumerate every build-config output folder (bin/<Config>/netstandard2.0/...)
				// If the generator hasn't been built yet these dirs are absent.
				var binDir = Path.Combine(generatorProjectDir, "bin");
				if (Directory.Exists(binDir))
				{
					try
					{
						searchDirs.AddRange(Directory.GetDirectories(binDir, "netstandard2.0",
							SearchOption.AllDirectories));
					}
					catch (Exception ex)
					{
						EditorDebug.Warn($"Could not enumerate generator bin dir '{binDir}': {ex.Message}",
							"EngineLibsSync");
					}
				}
			}

			var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string newestPath = null;
			DateTime newestTime = DateTime.MinValue;

			foreach (var dir in searchDirs)
			{
				if (string.IsNullOrEmpty(dir) || !searched.Add(dir))
					continue;

				var candidate = Path.Combine(dir, SourceGeneratorDllName);
				if (!File.Exists(candidate))
					continue;

				var writeTime = File.GetLastWriteTimeUtc(candidate);
				if (writeTime > newestTime)
				{
					newestTime = writeTime;
					newestPath = candidate;
				}
			}

			if (newestPath != null)
			{
				EditorDebug.Log($"Found {SourceGeneratorDllName} (newest, {newestTime:u}) at: {newestPath}",
					"EngineLibsSync");
				return newestPath;
			}

			EditorDebug.Warn($"Searched {searched.Count} paths for {SourceGeneratorDllName}: " +
			                 string.Join(", ", searched), "EngineLibsSync");
			return null;
		}

		/// <summary>
		/// Collects all assemblies that should be synced for editor use: Voltage.* and MonoGame/FNA.
		/// Only returns managed assemblies that are part of the engine  never native DLLs.
		/// </summary>
		private static List<Assembly> GetAssembliesToSync()
		{
			var result = new List<Assembly>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				var name = asm.GetName().Name;
				if (name == null)
					continue;

				if (name.StartsWith("Voltage", StringComparison.OrdinalIgnoreCase))
				{
					// Plugin-provided assemblies (e.g. a bundled Voltage.* engine-module plugin) live in
					// PluginLibs, not EngineLibs — syncing their editor-flavored DLLs here would leak them
					// into the game's engine references.
					if (Plugins.PluginManager.Instance.IsPluginAssembly(asm.Location))
						continue;

					if (seen.Add(name))
						result.Add(asm);
				}
			}

			var frameworkAssemblies = new[]
			{
				typeof(Microsoft.Xna.Framework.Vector2).Assembly,
				typeof(Microsoft.Xna.Framework.Graphics.Texture2D).Assembly,
			};

			foreach (var asm in frameworkAssemblies)
			{
				var name = asm.GetName().Name;
				if (name != null && seen.Add(name))
					result.Add(asm);
			}

			return result;
		}

		/// <summary>
		/// Finds the solution root directory by walking up from the editor's base directory.
		/// </summary>
		private static string FindSolutionDir()
		{
			var dir = AppContext.BaseDirectory;
			var di = new DirectoryInfo(dir);
			while (di != null)
			{
				if (Directory.Exists(Path.Combine(di.FullName, "Voltage.Engine")))
					return di.FullName;
				di = di.Parent;
			}

			return null;
		}

		#endregion

		/// <summary>
		/// Name of the trimmer roots XML file that must accompany game projects
		/// for NativeAOT to preserve types used by reflection-based JSON serialization.
		/// </summary>
		private const string TrimmerRootsFileName = "TrimmerRoots.xml";

		/// <summary>
		/// Copies TrimmerRoots.xml from the Voltage.Engine project directory into the
		/// game project root so that <c>dotnet publish -p:PublishAot=true</c> can find it.
		/// </summary>
		public static bool SyncTrimmerRoots(string projectPath)
		{
			if (string.IsNullOrEmpty(projectPath))
				return false;

			var solutionDir = FindSolutionDir();
			if (solutionDir == null)
			{
				EditorDebug.Warn("Cannot sync TrimmerRoots.xml: solution directory not found.", "EngineLibsSync");
				return false;
			}

			var srcPath = Path.Combine(solutionDir, "Voltage.Engine", TrimmerRootsFileName);
			if (!File.Exists(srcPath))
			{
				EditorDebug.Warn($"TrimmerRoots.xml not found at: {srcPath}", "EngineLibsSync");
				return false;
			}

			var destPath = Path.Combine(projectPath, TrimmerRootsFileName);
			try
			{
				if (ShouldCopyFile(srcPath, destPath))
				{
					File.Copy(srcPath, destPath, overwrite: true);
					EditorDebug.Log("Synced TrimmerRoots.xml to game project.", "EngineLibsSync");
				}
				return true;
			}
			catch (Exception ex)
			{
				EditorDebug.Error($"Failed to copy TrimmerRoots.xml: {ex.Message}", "EngineLibsSync");
				return false;
			}
		}
	}
}
