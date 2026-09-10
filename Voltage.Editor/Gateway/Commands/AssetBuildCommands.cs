using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Voltage.Editor.Builders;
using Voltage.Editor.ProjectFile;
using Voltage.Gateway;
using Voltage.Project;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>The asset build tool: settings, rules, extension scaffolding, a run into the project, its status and cleanup.</summary>
internal static class AssetBuildCommands
{
	private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
	private static readonly string[] Actions = { "compile", "copy", "skip" };

	public static void Register(GatewayCommandTable table)
	{
		table.Add("assetbuild.settings", "Read the asset-build settings, or change the ones passed and save ProjectSettings.json.", (args, _) =>
		{
			var project = RequireProject();
			var s = AssetBuildSettingsStore.Get();
			var changed = false;
			if (args.Has("enabled")) { s.Enabled = args.Bool("enabled"); changed = true; }
			if (args.Has("platform")) { s.Platform = Platform(args.String("platform")); changed = true; }
			if (args.Has("compress")) { s.Compress = args.Bool("compress"); changed = true; }
			if (args.Has("premultiplyAlpha")) { s.PremultiplyAlpha = args.Bool("premultiplyAlpha"); changed = true; }
			if (args.Has("textureFormat")) { s.TextureFormat = TextureFormat(args.String("textureFormat")); changed = true; }
			if (args.Has("compileAudio")) { s.CompileAudio = args.Bool("compileAudio"); changed = true; }
			if (args.Has("include")) { s.Include = args.Strings("include"); changed = true; }
			if (args.Has("exclude")) { s.Exclude = args.Strings("exclude"); changed = true; }
			if (changed)
				AssetBuildSettingsStore.Save(project);
			return Describe(s, changed);
		},
			P.Bool("enabled", "Compile assets on every game build"),
			P.Enum("platform", "MGCB platform; desktop targets use DesktopGL", AssetBuildSettingsStore.Platforms),
			P.Bool("compress", "LZ4-compress .xnb files"),
			P.Bool("premultiplyAlpha", "Premultiply texture alpha at build time"),
			P.Enum("textureFormat", "Texture storage", new[] { "Color", "Compressed" }),
			P.Bool("compileAudio", "Convert wav, ogg and mp3 to SoundEffect .xnb"),
			P.List("include", "Include globs relative to Content"),
			P.List("exclude", "Exclude globs relative to Content"));

		table.Add("assetbuild.rules", "The project's asset-build rules and the ones editor plugins registered.", (_, _) =>
		{
			RequireProject();
			return new
			{
				project = (AssetBuildSettingsStore.Get().Rules ?? new List<ProjectSettings.AssetBuildRule>()).Select(DescribeRule).ToList(),
				plugins = AssetBuildRules.Plugins.Select(DescribeRule).ToList()
			};
		}).ReadOnly();

		table.Add("assetbuild.rule.set", "Create or update a project rule by name; unspecified fields keep their value.", (args, _) =>
		{
			var project = RequireProject();
			var settings = AssetBuildSettingsStore.Get();
			settings.Rules ??= new List<ProjectSettings.AssetBuildRule>();
			var name = args.Require("name").Trim();
			var rule = settings.Rules.FirstOrDefault(r => r.Name == name);
			var created = rule == null;
			rule ??= new ProjectSettings.AssetBuildRule { Name = name };

			if (args.Has("extensions"))
			{
				rule.Extensions = args.Strings("extensions");
				var bad = AssetBuildPipeline.NormalisedExtensions(rule).FirstOrDefault(e => e.Skip(1).Any(c => !char.IsLetterOrDigit(c)));
				if (bad != null)
					throw new GatewayException($"extension '{bad}' must be letters and digits");
			}
			if (args.Has("action"))
			{
				var action = args.String("action").Trim().ToLowerInvariant();
				if (!Actions.Contains(action))
					throw new GatewayException($"action must be one of {string.Join(", ", Actions)}");
				rule.Action = action;
			}
			if (args.Has("importer")) rule.Importer = Identifierish(args.String("importer"), "importer");
			if (args.Has("processor")) rule.Processor = Identifierish(args.String("processor"), "processor");
			if (args.Has("parameters"))
			{
				rule.Parameters = args.Strings("parameters");
				var bad = rule.Parameters.FirstOrDefault(p => p.IndexOf('=') <= 0 || p.IndexOfAny(new[] { '\r', '\n' }) >= 0);
				if (bad != null)
					throw new GatewayException($"parameter '{bad}' must look like Key=Value");
			}
			if (args.Has("assembly"))
				rule.Assembly = args.String("assembly").Trim();
			var problem = AssetBuildRuleValidation.Validate(rule, project.ProjectPath, out string resolvedAssembly);
			if (problem != null)
				throw new GatewayException(problem);

			if (created)
				settings.Rules.Add(rule);
			AssetBuildSettingsStore.Save(project);
			return new { created, rule = DescribeRule(rule) };
		},
			P.Str("name", "Rule name; the key for updates", required: true),
			P.List("extensions", "File extensions the rule applies to, e.g. .lvl"),
			P.Enum("action", "compile through the importer and processor, or copy or skip the file", Actions),
			P.Str("importer", "MGCB importer class name"),
			P.Str("processor", "MGCB processor class name"),
			P.List("parameters", "Processor parameters as Key=Value"),
			P.Str("assembly", "Project-relative path of the DLL that holds the importer and processor; empty for built-in ones")).Destructive();

		table.Add("assetbuild.rule.remove", "Delete a project rule by name.", (args, _) =>
		{
			var project = RequireProject();
			var settings = AssetBuildSettingsStore.Get();
			var name = args.Require("name").Trim();
			var removed = settings.Rules?.RemoveAll(r => r.Name == name) ?? 0;
			if (removed == 0)
				throw new GatewayException($"no rule named '{name}'");
			AssetBuildSettingsStore.Save(project);
			return new { removed = name };
		}, P.Str("name", "Rule name", required: true)).Destructive();

		table.Add("assetbuild.scaffold", "Create a pipeline extension project under Pipeline/<name>, its runtime asset and reader under Scripts/Content, and the rule that uses them.", (args, _) =>
		{
			var project = RequireProject();
			try
			{
				var result = AssetBuildScaffold.Create(project, args.Require("name"), args.Require("extension"));
				return new { folder = result.Folder, project = result.Project, files = result.Files, runtime = result.RuntimeFile, rule = DescribeRule(result.Rule) };
			}
			catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
			{
				throw new GatewayException(ex.Message);
			}
		},
			P.Str("name", "Extension name, also the asset type prefix, e.g. Level", required: true),
			P.Str("extension", "File extension the importer accepts, e.g. .lvl", required: true)).Destructive();

		table.Add("assetbuild.run", "Compile the Content folder with MGCB into the project's bin/AssetBuild folder (or output) and answer with the report.", (args, _) =>
		{
			var project = RequireProject();
			if (AssetBuildService.IsRunning)
				throw new GatewayException("an asset build is already running");
			var settings = AssetBuildSettingsStore.Get();
			var platform = args.Has("platform") ? Platform(args.String("platform")) : null;
			var output = args.String("output");
			if (string.IsNullOrWhiteSpace(output))
				output = AssetBuildSettingsStore.DefaultOutputDirectory(project, platform ?? settings.Platform);
			else
			{
				output = Path.GetFullPath(Path.IsPathRooted(output) ? output : Path.Combine(project.ProjectPath, output));
				if (!Inside(output, Path.Combine(project.ProjectPath, "bin", "AssetBuild")) && !Inside(output, Path.Combine(project.ProjectPath, "obj")))
					throw new GatewayException("output must be inside the project's bin/AssetBuild or obj folder");
				// The output is emptied first, so a game build folder must never be named.
				if (File.Exists(Path.Combine(output, project.ProjectName + ".exe")) || File.Exists(Path.Combine(output, project.ProjectName)) || File.Exists(Path.Combine(output, "ProjectSettings.json")))
					throw new GatewayException("output looks like a game build folder; use bin/AssetBuild/<platform>");
			}
			var task = AssetBuildService.RunAsync(project, settings, output, platform, args.Bool("clean"), args.Bool("copyRaw", true), CancellationToken.None);
			return GatewayTasks.FromTask(task, r => r.Describe(), (float)RunTimeout.TotalSeconds);
		},
			P.Enum("platform", "MGCB platform override", AssetBuildSettingsStore.Platforms),
			P.Bool("clean", "Also delete the intermediate folder first; the output is always rebuilt from empty", false),
			P.Bool("copyRaw", "Also copy the files the contract keeps raw", true),
			P.Str("output", "Output folder inside the project's bin or obj; default bin/AssetBuild/<platform>")).Unsafe();

		table.Add("assetbuild.status", "Tool version, manifest state, the standalone output and whether it is stale, last run and index size.", (_, _) =>
		{
			var project = RequireProject();
			var settings = AssetBuildSettingsStore.Get();
			var output = AssetBuildSettingsStore.DefaultOutputDirectory(project, settings.Platform);
			var last = AssetBuildService.LastReport;
			string lastOutput;
			lock (last?.Lock ?? new object())
				lastOutput = last?.OutputDir;
			var index = Path.Combine(lastOutput ?? output, AssetBuildPipeline.IndexFileName);
			return new
			{
				toolVersion = MgcbRunner.ToolVersion,
				manifest = MgcbRunner.ManifestPath(project),
				restored = MgcbRunner.IsRestored(project),
				pipelineDll = AssetBuildPipeline.PipelineDllPath,
				pipelineAvailable = File.Exists(AssetBuildPipeline.PipelineDllPath),
				running = AssetBuildService.IsRunning,
				output,
				outputExists = Directory.Exists(output),
				outputStale = AssetBuildSettingsStore.IsOutputStale(project, settings.Platform),
				lastOutput,
				indexPath = File.Exists(index) ? index : null,
				indexEntries = File.Exists(index) ? File.ReadAllLines(index).Count(l => l.Length > 0) : 0,
				rules = (settings.Rules ?? new List<ProjectSettings.AssetBuildRule>()).Count + AssetBuildRules.Plugins.Count,
				settings = Describe(settings, false)
			};
		}).ReadOnly();

		table.Add("assetbuild.clean", "Delete the standalone asset-build output and intermediates for a platform.", (args, _) =>
		{
			var project = RequireProject();
			var platform = Platform(args.Has("platform") ? args.String("platform") : AssetBuildSettingsStore.Get().Platform);
			AssetBuildService.Clean(project, platform);
			return new { cleaned = AssetBuildSettingsStore.DefaultOutputDirectory(project, platform) };
		}, P.Enum("platform", "MGCB platform; the setting when omitted", AssetBuildSettingsStore.Platforms)).Destructive();
	}

	private static string Platform(string value)
	{
		try
		{
			return AssetBuildSettingsStore.MgcbPlatform(value);
		}
		catch (ArgumentException ex)
		{
			throw new GatewayException(ex.Message);
		}
	}

	private static string TextureFormat(string value)
	{
		if (string.Equals(value, "Color", StringComparison.OrdinalIgnoreCase))
			return "Color";
		if (string.Equals(value, "Compressed", StringComparison.OrdinalIgnoreCase))
			return "Compressed";
		throw new GatewayException($"textureFormat must be Color or Compressed, not '{value}'");
	}

	/// <summary>Importer and processor names end up on response-file lines, so only identifier characters pass.</summary>
	private static string Identifierish(string value, string what)
	{
		var v = (value ?? "").Trim();
		if (v.Length == 0 || v.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '.'))
			throw new GatewayException($"{what} must be a class name, not '{value}'");
		return v;
	}

	private static bool Inside(string path, string root)
	{
		var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return full.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	private static object DescribeRule(ProjectSettings.AssetBuildRule r) => new
	{
		name = r.Name,
		extensions = AssetBuildPipeline.NormalisedExtensions(r).ToList(),
		action = r.Action,
		importer = r.Importer,
		processor = r.Processor,
		parameters = r.Parameters ?? new List<string>(),
		assembly = r.Assembly
	};

	private static object Describe(ProjectSettings.AssetBuildSettings s, bool saved) => new
	{
		enabled = s.Enabled,
		platform = s.Platform,
		compress = s.Compress,
		premultiplyAlpha = s.PremultiplyAlpha,
		textureFormat = s.TextureFormat,
		compileAudio = s.CompileAudio,
		include = s.Include ?? new List<string>(),
		exclude = s.Exclude ?? new List<string>(),
		rules = (s.Rules ?? new List<ProjectSettings.AssetBuildRule>()).Select(DescribeRule).ToList(),
		saved
	};
}
