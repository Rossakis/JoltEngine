using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Scripting;
using Voltage.Editor.Utils;
using Voltage.Project;

namespace Voltage.Editor.Builders;

/// <summary>Writes a pipeline extension project, its runtime asset and reader, and the rule that routes an extension through them.</summary>
public static class AssetBuildScaffold
{
	public sealed class Result
	{
		public string Folder;
		public string Project;
		public List<string> Files = new();
		public string RuntimeFile;
		public ProjectSettings.AssetBuildRule Rule;
	}

	public static Result Create(IGameProject project, string name, string extension)
	{
		if (project == null)
			throw new InvalidOperationException("no project loaded");
		var typeName = ScriptTemplates.Identifier(name);
		if (!ScriptTemplates.IsIdentifier(name) || typeName.Length < 2)
			throw new ArgumentException($"'{name}' is not a valid extension name; use letters and digits, e.g. Level");
		var ext = NormaliseExtension(extension);

		var folder = Path.Combine(project.ProjectPath, "Pipeline", typeName);
		if (Directory.Exists(folder))
			throw new InvalidOperationException($"{folder} already exists");
		var runtimeFile = Path.Combine(project.ScriptsFolder, "Content", typeName + "Asset.cs");
		if (File.Exists(runtimeFile))
			throw new InvalidOperationException($"{runtimeFile} already exists");

		var settings = AssetBuildSettingsStore.Get();
		settings.Rules ??= new List<ProjectSettings.AssetBuildRule>();
		if (settings.Rules.Any(r => r.Name == typeName))
			throw new InvalidOperationException($"a rule named {typeName} already exists; remove it first");

		var ns = ScriptTemplates.Namespace(project.ProjectName, new[] { "Content" });
		var assembly = GameAssemblyName(project);
		var pipelineNs = typeName + "Pipeline";
		var result = new Result { Folder = folder, RuntimeFile = runtimeFile };

		Directory.CreateDirectory(folder);
		Write(result, Path.Combine(folder, typeName + ".csproj"), Csproj());
		result.Project = result.Files[0];
		Write(result, Path.Combine(folder, typeName + "Importer.cs"), Importer(pipelineNs, typeName, ext));
		Write(result, Path.Combine(folder, typeName + "Processor.cs"), Processor(pipelineNs, typeName));
		Write(result, Path.Combine(folder, typeName + "Writer.cs"), Writer(pipelineNs, typeName, ns, assembly));
		Directory.CreateDirectory(Path.GetDirectoryName(runtimeFile) ?? ".");
		File.WriteAllText(runtimeFile, Runtime(ns, typeName), new UTF8Encoding(false));
		ExcludePipelineFromGame(project);

		result.Rule = new ProjectSettings.AssetBuildRule
		{
			Name = typeName,
			Extensions = new List<string> { ext },
			Action = "compile",
			Importer = typeName + "Importer",
			Processor = typeName + "Processor",
			Assembly = $"Pipeline/{typeName}/bin/Release/net8.0/{typeName}.dll"
		};
		settings.Rules.Add(result.Rule);
		AssetBuildSettingsStore.Save(project);
		return result;
	}

	/// <summary>The game csproj globs every .cs under the project, so the pipeline sources must be kept out of the game build.</summary>
	public const string PipelineExcludeItems = "  <ItemGroup>\n    <Compile Remove=\"Pipeline\\**\" />\n    <None Remove=\"Pipeline\\**\" />\n  </ItemGroup>\n";

	private static void ExcludePipelineFromGame(IGameProject project)
	{
		var csproj = Directory.GetFiles(project.ProjectPath, "*.csproj").FirstOrDefault();
		if (csproj == null)
			return;
		var text = File.ReadAllText(csproj);
		if (text.Contains("Remove=\"Pipeline\\**\"", StringComparison.Ordinal))
			return;
		var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
		if (end < 0)
			return;
		var newline = text.Contains("\r\n") ? "\r\n" : "\n";
		File.WriteAllText(csproj, text.Substring(0, end) + PipelineExcludeItems.Replace("\n", newline) + newline + text.Substring(end), new UTF8Encoding(false));
	}

	/// <summary>The name MonoGame resolves the reader from: the csproj's AssemblyName, else its file name.</summary>
	private static string GameAssemblyName(IGameProject project)
	{
		var csproj = Directory.GetFiles(project.ProjectPath, "*.csproj").FirstOrDefault();
		if (csproj == null)
			return ScriptTemplates.Identifier(project.ProjectName);
		var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(csproj), @"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>");
		return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(csproj);
	}

	public static string NormaliseExtension(string extension)
	{
		var ext = (extension ?? "").Trim().ToLowerInvariant().TrimStart('.');
		if (ext.Length == 0 || ext.Any(c => !char.IsLetterOrDigit(c)))
			throw new ArgumentException($"'{extension}' is not a usable extension; use letters and digits, e.g. .lvl");
		return "." + ext;
	}

	private static void Write(Result result, string path, string text)
	{
		File.WriteAllText(path, text, new UTF8Encoding(false));
		result.Files.Add(path);
	}

	private static string Csproj() => $@"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""MonoGame.Framework.Content.Pipeline"" Version=""{MonoGameVersionResolver.GetVersion()}"" />
    <PackageReference Include=""MonoGame.Framework.DesktopGL"" Version=""{MonoGameVersionResolver.GetVersion()}"" ExcludeAssets=""runtime;native;contentFiles;build;buildTransitive;analyzers"" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include=""Voltage.Pipeline"" Condition=""'$(VoltagePipelineDir)' != ''"">
      <HintPath>$(VoltagePipelineDir)Voltage.Pipeline.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
";

	private static string Importer(string ns, string type, string ext) => $@"using System.IO;
using Microsoft.Xna.Framework.Content.Pipeline;

namespace {ns};

/// <summary>What the importer read; the processor may change it before the writer stores it.</summary>
public sealed class {type}Content
{{
	public string Name;
	public string Text;
}}

[ContentImporter(""{ext}"", DisplayName = ""{type} - Voltage"", DefaultProcessor = ""{type}Processor"")]
public sealed class {type}Importer : ContentImporter<{type}Content>
{{
	public override {type}Content Import(string filename, ContentImporterContext context) =>
		new() {{ Name = Path.GetFileNameWithoutExtension(filename), Text = File.ReadAllText(filename) }};
}}
";

	private static string Processor(string ns, string type) => $@"using System.ComponentModel;
using Microsoft.Xna.Framework.Content.Pipeline;

namespace {ns};

[ContentProcessor(DisplayName = ""{type} - Voltage"")]
public sealed class {type}Processor : ContentProcessor<{type}Content, {type}Content>
{{
	[DefaultValue(true)]
	public bool TrimWhitespace {{ get; set; }} = true;

	public override {type}Content Process({type}Content input, ContentProcessorContext context)
	{{
		if (TrimWhitespace && input.Text != null)
			input.Text = input.Text.Trim();
		return input;
	}}
}}
";

	private static string Writer(string ns, string type, string runtimeNs, string assembly) => $@"using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Serialization.Compiler;

namespace {ns};

[ContentTypeWriter]
public sealed class {type}Writer : ContentTypeWriter<{type}Content>
{{
	protected override void Write(ContentWriter output, {type}Content value)
	{{
		output.Write(value.Name ?? """");
		output.Write(value.Text ?? """");
	}}

	public override string GetRuntimeReader(TargetPlatform targetPlatform) => ""{runtimeNs}.{type}Reader, {assembly}"";

	public override string GetRuntimeType(TargetPlatform targetPlatform) => ""{runtimeNs}.{type}Asset, {assembly}"";
}}
";

	private static string Runtime(string ns, string type) => $@"using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework.Content;
using Voltage.Systems;

namespace {ns};

/// <summary>The runtime shape of a {type} file; loaded from its .xnb in a build and parsed from the source elsewhere.</summary>
public sealed class {type}Asset
{{
	public string Name;
	public string Text;

	public static void Register() =>
		VoltageContentManager.RegisterLoader<{type}Asset>((content, path) => new {type}Asset {{ Name = Path.GetFileNameWithoutExtension(path), Text = File.ReadAllText(path) }});
}}

public sealed class {type}Reader : ContentTypeReader<{type}Asset>
{{
	protected override {type}Asset Read(ContentReader input, {type}Asset existingInstance) =>
		new() {{ Name = input.ReadString(), Text = input.ReadString() }};
}}

internal static class {type}AssetRegistration
{{
	[ModuleInitializer]
	internal static void Init() => {type}Asset.Register();
}}
";
}
