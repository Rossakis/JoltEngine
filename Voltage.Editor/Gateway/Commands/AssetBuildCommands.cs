using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Voltage.Editor.Builders;
using Voltage.Editor.ProjectFile;
using Voltage.Gateway;
using static Voltage.Editor.Gateway.GatewayValues;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>The asset build tool: settings, a run into the project, its status and cleanup.</summary>
internal static class AssetBuildCommands
{
	private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);

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
			if (args.Has("stripSources")) { s.StripSources = args.Bool("stripSources"); changed = true; }
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
			P.Bool("stripSources", "Leave compiled sources out of the build output"),
			P.List("include", "Include globs relative to Content"),
			P.List("exclude", "Exclude globs relative to Content"));

		table.Add("assetbuild.run", "Compile the Content folder with MGCB into the project's Build/Assets folder (or output) and answer with the report.", (args, _) =>
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
				if (!Inside(output, Path.Combine(project.ProjectPath, "Build")) && !Inside(output, Path.Combine(project.ProjectPath, "obj")))
					throw new GatewayException("output must be inside the project's Build or obj folder");
			}
			var task = AssetBuildService.RunAsync(project, settings, output, platform, args.Bool("clean"), args.Bool("copyRaw", true), CancellationToken.None);
			return GatewayTasks.FromTask(task, r => r.Describe(), (float)RunTimeout.TotalSeconds);
		},
			P.Enum("platform", "MGCB platform override", AssetBuildSettingsStore.Platforms),
			P.Bool("clean", "Delete the output and intermediate folders first", false),
			P.Bool("copyRaw", "Also copy the files the contract keeps raw", true),
			P.Str("output", "Output folder inside the project; default Build/Assets/<platform>")).Unsafe();

		table.Add("assetbuild.status", "Tool version, manifest state, last output and index size.", (_, _) =>
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
				lastOutput,
				indexPath = File.Exists(index) ? index : null,
				indexEntries = File.Exists(index) ? File.ReadAllLines(index).Count(l => l.Length > 0) : 0,
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

	private static bool Inside(string path, string root)
	{
		var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		return full.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	private static object Describe(Voltage.Project.ProjectSettings.AssetBuildSettings s, bool saved) => new
	{
		enabled = s.Enabled,
		platform = s.Platform,
		compress = s.Compress,
		premultiplyAlpha = s.PremultiplyAlpha,
		textureFormat = s.TextureFormat,
		compileAudio = s.CompileAudio,
		stripSources = s.StripSources,
		include = s.Include ?? new List<string>(),
		exclude = s.Exclude ?? new List<string>(),
		saved
	};
}
