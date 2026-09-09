using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.Scripting;

namespace Voltage.Editor.ProjectFile
{
	/// <summary>
	/// Generates Visual Studio solution and project files for game projects
	/// </summary>
	public static class ProjectStructureGenerator
	{
		/// <summary>
		/// Returns the MonoGame.Framework.DesktopGL package version currently used by the editor.
		/// Resolved at runtime from the loaded MonoGame assembly so game projects always match.
		/// Falls back to a sensible default if detection fails.
		/// </summary>
		private static string GetMonoGameVersion()
		{
			try
			{
				// The MonoGame assembly is already loaded  grab its version from metadata.
				var monoGameAssembly = AppDomain.CurrentDomain.GetAssemblies()
					.FirstOrDefault(a => a.GetName().Name == "MonoGame.Framework");

				if (monoGameAssembly != null)
				{
					// Try InformationalVersion first  NuGet packages set this to the full semver string
					var infoAttr = monoGameAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
					if (infoAttr != null && !string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
					{
						// Strip any metadata suffix (e.g. "+abc123") that NuGet won't accept
						var version = infoAttr.InformationalVersion;
						var plusIndex = version.IndexOf('+');
						if (plusIndex >= 0)
							version = version[..plusIndex];

						EditorDebug.Log($"Detected MonoGame version from InformationalVersion: {version}", "ProjectStructure");
						return version;
					}

					var assemblyVersion = monoGameAssembly.GetName().Version;
					if (assemblyVersion != null)
					{
						var version = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
						EditorDebug.Log($"Detected MonoGame version from AssemblyVersion: {version}", "ProjectStructure");
						return version;
					}
				}
			}
			catch (Exception ex)
			{
				EditorDebug.Warn($"Could not detect MonoGame version: {ex.Message}", "ProjectStructure");
			}

			// Fallback  keep in sync with Voltage.Editor.csproj manually if this is ever reached
			const string fallback = "3.8.5.1";
			EditorDebug.Warn($"Using fallback MonoGame version: {fallback}", "ProjectStructure");
			return fallback;
		}

		/// <summary>
		/// Creates a complete Visual Studio solution structure for a game project
		/// </summary>
		public static bool CreateProjectStructure(
			string projectName,
			string projectPath,
			Version version)
		{
			try
			{
				EditorDebug.Log("Creating directory structure...", "ProjectStructure");
				Directory.CreateDirectory(projectPath);
				
				var scriptsFolder = Path.Combine(projectPath, "Scripts");
				var effectsFolder = Path.Combine(projectPath, "Effects");
				var contentFolder = Path.Combine(projectPath, "Content");
				var propertiesFolder = Path.Combine(projectPath, "Properties");
				
				Directory.CreateDirectory(scriptsFolder);
				Directory.CreateDirectory(effectsFolder);
				Directory.CreateDirectory(contentFolder);
				Directory.CreateDirectory(propertiesFolder);

				var settingsFolder = Path.Combine(projectPath, "Settings");
				Directory.CreateDirectory(settingsFolder);
				var settingsPath = Path.Combine(settingsFolder, "ProjectSettings.json");
				if (!File.Exists(settingsPath))
				{
					File.WriteAllText(settingsPath, "{\n\t\"WindowWidth\": 1280,\n\t\"WindowHeight\": 720\n}");
					EditorDebug.Log("Created default ProjectSettings.json", "ProjectStructure");
				}

				// Sync engine DLLs into the new project before generating files that reference them
				EngineLibsSync.SyncToProject(projectPath);

				CreateProjectFile(projectName, projectPath, version);
				CreateSolutionFile(projectName, projectPath);
				CreateProgramFile(projectName, projectPath);
				CreateAssemblyInfoFile(projectName, propertiesFolder, version);
				CreateExampleScript(scriptsFolder, projectName);
				CreateGitIgnoreFile(projectPath);
				CopyEditorIcon(projectPath);
				CopyDefaultFont(projectPath);

				EditorDebug.Log($"Successfully created project structure at: {projectPath}", "ProjectStructure");
				return true;
			}
			catch (Exception ex)
			{
				EditorDebug.Error($"Failed to create project structure: {ex.Message}", "ProjectStructure");
				EditorDebug.Error($"Stack trace: {ex.StackTrace}", "ProjectStructure");
				return false;
			}
		}

		private static void CreateProjectFile(string projectName, string projectPath, Version version)
		{
			var projectFilePath = Path.Combine(projectPath, $"{projectName}.csproj");
			EditorDebug.Log($"Creating project file: {projectFilePath}", "ProjectStructure");

			var monoGameVersion = GetMonoGameVersion();

			var referenceItems = new System.Text.StringBuilder();
			foreach (var dllName in EngineLibsSync.ManagedReferenceDlls)
			{
				var assemblyName = Path.GetFileNameWithoutExtension(dllName);
				referenceItems.AppendLine($"    <Reference Include=\"{assemblyName}\">");
				referenceItems.AppendLine($"      <HintPath>$(MSBuildThisFileDirectory)EngineLibs\\{dllName}</HintPath>");
				referenceItems.AppendLine($"    </Reference>");
			}

			var csprojContent = $@"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
  <!-- Change this to 'WinExe' if you don't want a console/terminal window to appear alongside the game window-->
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>{projectName}</AssemblyName>
    <RootNamespace>{projectName}</RootNamespace>
    <Version>{version}</Version>
    <StartupObject>{projectName}.Program</StartupObject>
    <PublishAot>true</PublishAot>

    <!-- Disable auto-generated assembly info to avoid conflicts with Properties/AssemblyInfo.cs -->
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>

    <!-- Build\ holds published copies of the engine DLLs; globbing them in lets a publish resolve against an old build instead of EngineLibs\.  -->
    <DefaultItemExcludes>$(DefaultItemExcludes);Build\**;PluginLibs\**</DefaultItemExcludes>
  </PropertyGroup>

  <!-- Only set the application icon if Icon.ico exists in the project root -->
  <PropertyGroup Condition=""Exists('Icon.ico')"">
    <ApplicationIcon>Icon.ico</ApplicationIcon>
  </PropertyGroup>

  <PropertyGroup Condition=""'$(Configuration)|$(Platform)'=='Debug|AnyCPU'"">
    <DefineConstants>TRACE;DEBUG;OS_WINDOWS</DefineConstants>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <PropertyGroup Condition=""'$(Configuration)|$(Platform)'=='Release|AnyCPU'"">
    <DefineConstants>TRACE;OS_WINDOWS</DefineConstants>
    <OutputType>WinExe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""MonoGame.Framework.DesktopGL"" Version=""{monoGameVersion}"" />
    <PackageReference Include=""MonoGame.Content.Builder.Task"" Version=""{monoGameVersion}"" />
  </ItemGroup>

  <!--
    EngineLibs: local copies of the Voltage engine DLLs, auto-synced by the Voltage Editor
    on every project load. Do NOT commit this folder (it is .gitignored).
    If these DLLs are missing, open the project in the Voltage Editor first to regenerate them.

    IMPORTANT: Only managed DLLs are listed here. Never add native binaries (SDL2, OpenAL,
    clretwrc, etc.)  they are resolved automatically by the MonoGame NuGet package.
  -->
  <ItemGroup>
{referenceItems}  </ItemGroup>

  <!--
    Voltage.SourceGenerators: Roslyn incremental source generator that auto-generates a
    ComponentData subclass and Data property override for every partial Component subclass.
    Produces AOT-safe, zero-reflection serialization code at compile time.
    Must be declared as an Analyzer item, not a Reference.
  -->
  <ItemGroup>
    <Analyzer Include=""$(MSBuildThisFileDirectory)EngineLibs\Voltage.SourceGenerators.dll"" />
  </ItemGroup>

  <!--
    Trimmer roots: preserve types needed by the Voltage JSON serializer's reflection-based
    deserialization. Without this, NativeAOT strips field metadata from MonoGame structs
    (Vector2, Color, etc...) and collection constructors, causing runtime crashes.
  -->
  <ItemGroup>
    <TrimmerRootDescriptor Include=""TrimmerRoots.xml"" Condition=""Exists('TrimmerRoots.xml')"" />
  </ItemGroup>

  <ItemGroup>
    <Content Include=""Content\**\*.*"">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include=""Data\**\*.*"">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include=""ProjectSettings.json"">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

  <!-- Icon files embedded into the assembly so the executable carries its own icon -->
  <ItemGroup>
    <EmbeddedResource Include=""Icon.ico"" Condition=""Exists('Icon.ico')"" />
    <EmbeddedResource Include=""Icon.bmp"" Condition=""Exists('Icon.bmp')"" />
  </ItemGroup>

  <!--
    Project plugins: generated by the Voltage Editor from plugins.json / plugins.lock.json.
    Supplies plugin assembly references, plugin source files, trimmer roots, and native-library
    copies. Regenerated on project open and before every build — do not edit Plugins.g.props.
  -->
  <Import Project=""$(MSBuildThisFileDirectory)PluginLibs/Plugins.g.props"" Condition=""Exists('$(MSBuildThisFileDirectory)PluginLibs/Plugins.g.props')"" />
</Project>";

			File.WriteAllText(projectFilePath, csprojContent);
			Debug.Info($"Project file created with MonoGame version: {monoGameVersion}", "ProjectStructure");
		}

		private static void CreateSolutionFile(string projectName, string projectPath)
		{
			var solutionFilePath = Path.Combine(projectPath, $"{projectName}.sln");
			var projectGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();

			var slnContent = $@"Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project(""{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}"") = ""{projectName}"", ""{projectName}.csproj"", ""{projectGuid}""
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{projectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{projectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{projectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{projectGuid}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal";

			// Write solution file with UTF-8 encoding and no BOM
			File.WriteAllText(solutionFilePath, slnContent, new System.Text.UTF8Encoding(false));
		}

		private static void CreateProgramFile(string projectName, string projectPath)
		{
			var programFilePath = Path.Combine(projectPath, "Program.cs");

			var programContent = $@"using System;
using Voltage;
using Voltage.Project;
using Voltage.Utils;

namespace {projectName}
{{
	/// <summary>
	/// Entry point for {projectName}
	/// </summary>
	public static class Program
	{{
		[STAThread]
		static void Main()
		{{
			using (var game = new {projectName}Game())
			{{
				game.Run();
			}}
		}}
	}}

	/// <summary>
	/// Main game class for {projectName}
	/// </summary>
	public class {projectName}Game : Core
	{{
		public {projectName}Game() : base()
		{{
#if OS_MAC
		// Mac directory needs to be set manually, or it won't find the Content folder
        Directory.SetCurrentDirectory(AppContext
            .BaseDirectory);
#endif

            // Load game settings
            var settingsPath = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                ""ProjectSettings.json""
            );

            if (System.IO.File.Exists(settingsPath))
            {{
                var json = System.IO.File.ReadAllText(settingsPath);
                ProjectSettings.Instance = Voltage.Serialization.AotDeserializers.DeserializeProjectSettings(json);
            }}
        }}

        protected override void Initialize()
		{{
			base.Initialize();

			var font = Content.LoadBitmapFont(""Content/Voltage/Fonts/VoltageDefaultBMFont.fnt"");
			Graphics.Instance = new Graphics(font);
			Window.Title = ""{projectName}"";

            // Apply project settings to the runtime
            var settings = ProjectSettings.Instance;

            // Load the initial scene configured in ProjectSettings.json
            if (settings == null || string.IsNullOrEmpty(settings.InitialScene))
                throw new Exception(""Initial scene is not specified in ProjectSettings.json!"");

            // Loads the level, sets it as the active Scene, and applies the display + design
            // resolution settings from ProjectSettings. Use Scene.LoadLevel(name) to switch levels at runtime.
            Scene.LoadLevel(settings.InitialScene, applyDisplaySettings: true);
        }}
    }}
}}
";

			File.WriteAllText(programFilePath, programContent);
		}

		private static void CreateAssemblyInfoFile(string projectName, string propertiesFolder, Version version)
		{
			var assemblyInfoPath = Path.Combine(propertiesFolder, "AssemblyInfo.cs");

			var assemblyInfoContent = $@"using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle(""{projectName}"")]
[assembly: AssemblyDescription(""Game project created with Voltage Engine"")]
[assembly: AssemblyConfiguration("""")]
[assembly: AssemblyCompany("""")]
[assembly: AssemblyProduct(""{projectName}"")]
[assembly: AssemblyCopyright(""Copyright  {DateTime.Now.Year}"")]
[assembly: AssemblyTrademark("""")]
[assembly: AssemblyCulture("""")]

[assembly: ComVisible(false)]

[assembly: Guid(""{Guid.NewGuid()}"")]

[assembly: AssemblyVersion(""{version}"")]
[assembly: AssemblyFileVersion(""{version}"")]
";

			File.WriteAllText(assemblyInfoPath, assemblyInfoContent);
		}

		private static void CreateExampleScript(string scriptsFolder, string projectName)
		{
			var exampleScriptPath = Path.Combine(scriptsFolder, "ExampleComponent.cs");

			var exampleScriptContent = $@"using Microsoft.Xna.Framework;
using Voltage;
using Voltage.Utils;

namespace {projectName}.Scripts
{{
	/// <summary>
	/// Example component that can be attached to entities
	/// </summary>
	public partial class ExampleComponent : Component, IUpdatable
	{{
		public float Speed = 100f;

		public override void OnStart()
		{{
			base.OnStart();
			Debug.Log($""ExampleComponent added to {{Entity.Name}}"");
		}}

		public void Update()
		{{
			// Example: Simple movement
			var velocity = Vector2.Zero;
			
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.W))
				velocity.Y -= 1 * Speed * Time.DeltaTime;
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.S))
				velocity.Y += 1 * Speed * Time.DeltaTime;
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.A))
				velocity.X -= 1 * Speed * Time.DeltaTime;
			if (Input.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.D))
				velocity.X += 1 * Speed * Time.DeltaTime;

			if (velocity != Vector2.Zero)
			{{
				Entity.Transform.Position += velocity;
			}}
		}}
	}}
}}
";

			File.WriteAllText(exampleScriptPath, exampleScriptContent);
		}

		private static void CreateGitIgnoreFile(string projectPath)
		{
			var gitignorePath = Path.Combine(projectPath, ".gitignore");

			var gitignoreContent = @"## Ignore Visual Studio temporary files, build results, and
## files generated by popular Visual Studio add-ons.

# User-specific files
*.suo
*.user
*.userosscache
*.sln.docstates

# Build results
[Dd]ebug/
[Dd]ebugPublic/
[Rr]elease/
[Rr]eleases/
x64/
x86/
bld/
[Bb]in/
[Oo]bj/

# Visual Studio cache/options directory
.vs/

# MSTest test Results
[Tt]est[Rr]esult*/
[Bb]uild[Ll]og.*

# NuGet Packages
*.nupkg
**/packages/*

# MonoGame
Content/bin/
Content/obj/

# Voltage Engine - auto-synced engine DLLs (regenerated by the Voltage Editor on project load)
EngineLibs/

# Voltage Engine - restored plugin payloads (regenerated from plugins.json + plugins.lock.json,
# which ARE committed). Never commit binaries or NDA SDK files from here.
PluginLibs/

# Voltage Engine - which plugins THIS machine resolves from a folder of its own. Paths here mean
# nothing on anyone else's machine, which is exactly why they are kept out of plugins.json.
plugins.local.json

# Rider
.idea/

# OS generated files
.DS_Store
Thumbs.db
";

			File.WriteAllText(gitignorePath, gitignoreContent);
			Debug.Log($"Created .gitignore: {gitignorePath}");
		}

		/// <summary>
		/// Copies Icon.ico and Icon.bmp from the editor's directory to the game project.
		/// Each file is searched for independently so a missing .bmp never blocks .ico.
		/// </summary>
		private static void CopyEditorIcon(string projectPath)
		{
			try
			{
				var iconSource = FindEditorFile("Icon.ico");
				var bmpIconSource = FindEditorFile("Icon.bmp");

				if (iconSource != null)
				{
					File.Copy(iconSource, Path.Combine(projectPath, "Icon.ico"), overwrite: true);
				}
				else
				{
					EditorDebug.Error("Icon.ico not found. Game project will build without an application icon.", "ProjectStructure");
				}

				if (bmpIconSource != null)
				{
					File.Copy(bmpIconSource, Path.Combine(projectPath, "Icon.bmp"), overwrite: true);
				}
				else
				{
					EditorDebug.Error("Icon.bmp not found. Game project will build without a bitmap icon.", "ProjectStructure");
				}
			}
			catch (Exception ex)
			{
				EditorDebug.Error($"Could not copy icon files: {ex.Message}", "ProjectStructure");
			}
		}

		/// <summary>
		/// Searches for an editor asset file by name.
		/// Checks next to the running assembly first, then walks up the directory tree
		/// looking for the Voltage.Editor project root (works during development).
		/// Returns the full path if found, or null.
		/// </summary>
		private static string FindEditorFile(string fileName)
		{
			// Running assembly (deployed editor)
			var editorDir = Path.GetDirectoryName(typeof(ProjectStructureGenerator).Assembly.Location);
			if (!string.IsNullOrEmpty(editorDir))
			{
				var candidate = Path.Combine(editorDir, fileName);
				if (File.Exists(candidate))
					return candidate;
			}

			// Walk up from BaseDirectory looking for the Voltage.Editor project root
			var di = new DirectoryInfo(AppContext.BaseDirectory);
			while (di != null)
			{
				// Check inside a "Voltage.Editor" subdirectory of the current node
				var inSubDir = Path.Combine(di.FullName, "Voltage.Editor", fileName);
				if (File.Exists(inSubDir))
					return inSubDir;

				// Check if we're already inside the Voltage.Editor directory itself
				var inCurrentDir = Path.Combine(di.FullName, fileName);
				if (File.Exists(inCurrentDir) && File.Exists(Path.Combine(di.FullName, "Voltage.Editor.csproj")))
					return inCurrentDir;

				di = di.Parent;
			}

			return null;
		}

		/// <summary>
		/// Copies the Voltage default bitmap font files (VoltageDefaultBMFont.fnt and
		/// VoltageDefaultBMFont.png) into the game project's Content/Voltage/Fonts directory.
		/// At runtime (non-editor builds), Core.Initialize loads the font from
		/// "Content/Voltage/Fonts/VoltageDefaultBMFont.fnt".
		/// </summary>
		private static void CopyDefaultFont(string projectPath)
		{
			var defaultFontFiles = new[]
			{
				"VoltageDefaultBMFont.fnt",
				"VoltageDefaultBMFont.png"
			};

			var destDir = Path.Combine(projectPath, "Content", "Voltage", "Fonts");
			Directory.CreateDirectory(destDir);

			var fontSourceDir = FindDefaultFontsDir();
			if (fontSourceDir == null)
			{
				EditorDebug.Warn("Could not locate DefaultContent/Fonts directory. " +
				                 "The game project may fail to initialize without the default font.",
					"ProjectStructure");
				return;
			}

			foreach (var fontFile in defaultFontFiles)
			{
				var srcPath = Path.Combine(fontSourceDir, fontFile);
				if (File.Exists(srcPath))
				{
					File.Copy(srcPath, Path.Combine(destDir, fontFile), overwrite: true);
					EditorDebug.Log($"Copied default font: {fontFile}", "ProjectStructure");
				}
				else
				{
					EditorDebug.Warn($"Default font file not found: {srcPath}", "ProjectStructure");
				}
			}
		}

		/// <summary>
		/// Ensures the default bitmap font files exist in the given game project's
		/// Content/Voltage/Fonts directory. Called on every project load so that
		/// missing or deleted font files are automatically restored.
		/// </summary>
		public static void EnsureDefaultFontExists(string projectPath)
		{
			if (string.IsNullOrEmpty(projectPath))
				return;

			var defaultFontFiles = new[]
			{
				"VoltageDefaultBMFont.fnt",
				"VoltageDefaultBMFont.png"
			};

			var destDir = Path.Combine(projectPath, "Content", "Voltage", "Fonts");

			// Quick check: if every file already exists, skip the search entirely
			bool allPresent = Directory.Exists(destDir);
			if (allPresent)
			{
				foreach (var f in defaultFontFiles)
				{
					if (!File.Exists(Path.Combine(destDir, f)))
					{
						allPresent = false;
						break;
					}
				}
			}

			if (allPresent)
				return;

			Directory.CreateDirectory(destDir);

			var fontSourceDir = FindDefaultFontsDir();
			if (fontSourceDir == null)
			{
				EditorDebug.Warn("Could not locate DefaultContent/Fonts directory to restore default font.",
					"ProjectStructure");
				return;
			}

			foreach (var fontFile in defaultFontFiles)
			{
				var destPath = Path.Combine(destDir, fontFile);
				if (File.Exists(destPath))
					continue;

				var srcPath = Path.Combine(fontSourceDir, fontFile);
				if (File.Exists(srcPath))
				{
					File.Copy(srcPath, destPath, overwrite: false);
					EditorDebug.Log($"Restored missing default font: {fontFile}", "ProjectStructure");
				}
				else
				{
					EditorDebug.Warn($"Default font source not found: {srcPath}", "ProjectStructure");
				}
			}
		}

		/// <summary>
		/// Searches for the DefaultContent/Fonts directory that contains the Voltage
		/// bitmap font files. Checks next to the editor assembly, then walks up the
		/// directory tree looking for the Voltage.Editor project folder.
		/// </summary>
		private static string FindDefaultFontsDir()
		{
			// 1. Next to the running editor assembly (deployed editor)
			var editorDir = Path.GetDirectoryName(typeof(ProjectStructureGenerator).Assembly.Location);
			if (!string.IsNullOrEmpty(editorDir))
			{
				var candidate = Path.Combine(editorDir, "DefaultContent", "Fonts");
				if (Directory.Exists(candidate))
					return candidate;
			}

			// 2. Walk up from BaseDirectory to find the Voltage.Editor project root
			var di = new DirectoryInfo(AppContext.BaseDirectory);
			while (di != null)
			{
				if (File.Exists(Path.Combine(di.FullName, "Voltage.Editor.csproj")))
				{
					var candidate = Path.Combine(di.FullName, "DefaultContent", "Fonts");
					if (Directory.Exists(candidate))
						return candidate;
				}

				di = di.Parent;
			}

			return null;
		}
	}
}