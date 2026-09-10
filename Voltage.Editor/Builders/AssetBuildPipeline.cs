using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Voltage.Editor.ProjectFile;
using Voltage.Project;

namespace Voltage.Editor.Builders;

internal enum AssetBuildAction
{
	Compile,
	Copy,
	Skip
}

/// <summary>One Content file and what the build does with it.</summary>
internal sealed class AssetBuildItem
{
	public string SourcePath;
	public string RelativePath;
	public string AssetName;
	public AssetBuildAction Action;
	public string Importer;
	public string Processor;
	public List<KeyValuePair<string, string>> Parameters = new();
	public string Reason;
	public string Outcome;
	public string Error;

	public string OutputXnb(string outputDir) => Path.Combine(outputDir, AssetName.Replace('/', Path.DirectorySeparatorChar) + ".xnb");
}

/// <summary>Everything one MGCB run needs: the classified files, the response file and the output layout.</summary>
internal sealed class AssetBuildPlan
{
	public List<AssetBuildItem> Items = new();
	public string Platform;
	public string OutputDir;
	public string IntermediateDir;
	public string MgcbPath;
	public string PipelineDll;
	public bool PipelineAvailable;
	public bool Compress;

	public IEnumerable<AssetBuildItem> Compiled => Items.Where(i => i.Action == AssetBuildAction.Compile);
}

/// <summary>Turns the Content folder into an MGCB response file and a content.index per the Asset Build contract.</summary>
internal static class AssetBuildPipeline
{
	public const string IndexFileName = "content.index";

	private static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tga" };
	private static readonly string[] AsepriteExtensions = { ".aseprite", ".ase" };
	private static readonly string[] NeverCompiled = { ".fx", ".mgfxo", ".vtileset", ".vasset", ".vtimeline", ".vprefab", ".vscene", ".json", ".tmx", ".tsx", ".meta" };

	public static string PipelineDllPath => Path.Combine(AppContext.BaseDirectory, "Pipeline", "Voltage.Pipeline.dll");

	public static AssetBuildPlan CreatePlan(IGameProject project, ProjectSettings.AssetBuildSettings settings, string outputDir, string platformOverride = null)
	{
		var platform = AssetBuildSettingsStore.MgcbPlatform(string.IsNullOrWhiteSpace(platformOverride) ? settings.Platform : platformOverride);
		var plan = new AssetBuildPlan
		{
			Platform = platform,
			OutputDir = Path.GetFullPath(outputDir),
			IntermediateDir = Path.Combine(AssetBuildSettingsStore.IntermediateDirectory(project, platform), "obj"),
			MgcbPath = Path.Combine(project.ProjectPath, "obj", "AssetBuild", platform + ".mgcb"),
			PipelineDll = PipelineDllPath,
			PipelineAvailable = File.Exists(PipelineDllPath),
			Compress = settings.Compress
		};

		var contentRoot = project.ContentsFolder;
		if (!Directory.Exists(contentRoot))
			return plan;

		var include = (settings.Include ?? new List<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(GlobToRegex).ToList();
		var exclude = (settings.Exclude ?? new List<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(GlobToRegex).ToList();
		var projectRoot = Path.GetFullPath(project.ProjectPath);
		var usedNames = new Dictionary<string, AssetBuildItem>(StringComparer.OrdinalIgnoreCase);

		foreach (var file in Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
		{
			var relativeToContent = Path.GetRelativePath(contentRoot, file).Replace('\\', '/');
			var item = new AssetBuildItem
			{
				SourcePath = file,
				RelativePath = Path.GetRelativePath(projectRoot, file).Replace('\\', '/')
			};
			plan.Items.Add(item);

			if (include.Count > 0 && !include.Any(r => r.IsMatch(relativeToContent)))
			{
				Skip(item, "not matched by an include glob");
				continue;
			}
			if (exclude.Any(r => r.IsMatch(relativeToContent)))
			{
				Copy(item, "excluded by a glob");
				continue;
			}
			if (relativeToContent.StartsWith("Voltage/", StringComparison.OrdinalIgnoreCase))
			{
				Copy(item, "engine content is never compiled");
				continue;
			}
			if (!IsResponseSafe(file))
			{
				// One option per line in the response file: these characters would split or forge lines.
				Skip(item, "file name contains ';', a line break, or starts with '#', '$' or '/'");
				continue;
			}

			Classify(item, relativeToContent, settings, plan);
			if (item.Action != AssetBuildAction.Compile)
				continue;

			var baseName = StripExtension(relativeToContent);
			var name = baseName;
			if (usedNames.ContainsKey(name))
				name = baseName + "_" + Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
			for (var n = 2; usedNames.ContainsKey(name); n++)
				name = baseName + "_" + n;
			usedNames[name] = item;
			item.AssetName = name;
		}

		return plan;
	}

	private static void Classify(AssetBuildItem item, string relativeToContent, ProjectSettings.AssetBuildSettings settings, AssetBuildPlan plan)
	{
		var ext = Path.GetExtension(relativeToContent).ToLowerInvariant();
		if (NeverCompiled.Contains(ext))
		{
			Copy(item, ext == ".fx" || ext == ".mgfxo" ? "effects use the Effects menu" : "kept as a file; its reader needs no compile");
			return;
		}

		if (TextureExtensions.Contains(ext))
		{
			// The pipeline's processor blacks out alpha-zero texels like Texture2D.FromStream, which the premultiplied blend state relies on.
			Compile(item, "TextureImporter", plan.PipelineAvailable ? "VoltageTextureProcessor" : "TextureProcessor");
			item.Parameters.Add(new("ColorKeyEnabled", "False"));
			item.Parameters.Add(new("GenerateMipmaps", "False"));
			item.Parameters.Add(new("PremultiplyAlpha", settings.PremultiplyAlpha ? "True" : "False"));
			item.Parameters.Add(new("ResizeToPowerOfTwo", "False"));
			item.Parameters.Add(new("MakeSquare", "False"));
			item.Parameters.Add(new("TextureFormat", string.Equals(settings.TextureFormat, "Compressed", StringComparison.OrdinalIgnoreCase) ? "Compressed" : "Color"));
			return;
		}

		if (AsepriteExtensions.Contains(ext))
		{
			if (plan.PipelineAvailable)
			{
				Compile(item, "AsepriteImporter", "AsepriteProcessor");
				item.Parameters.Add(new("PremultiplyAlpha", settings.PremultiplyAlpha ? "True" : "False"));
			}
			else
				Copy(item, "Voltage.Pipeline.dll is missing beside the editor");
			return;
		}

		if (ext == ".fnt")
		{
			if (plan.PipelineAvailable)
			{
				Compile(item, "BitmapFontImporter", "BitmapFontProcessor");
				item.Parameters.Add(new("PremultiplyAlpha", settings.PremultiplyAlpha ? "True" : "False"));
			}
			else
				Copy(item, "Voltage.Pipeline.dll is missing beside the editor");
			return;
		}

		if (ext == ".wav" || ext == ".ogg" || ext == ".mp3")
		{
			if (!settings.CompileAudio)
			{
				Copy(item, "CompileAudio is off");
				return;
			}
			Compile(item, ext == ".wav" ? "WavImporter" : ext == ".ogg" ? "OggImporter" : "Mp3Importer", "SoundEffectProcessor");
			item.Parameters.Add(new("Quality", "Best"));
			return;
		}

		Copy(item, "no compiler for this type");
	}

	private static void Compile(AssetBuildItem item, string importer, string processor)
	{
		item.Action = AssetBuildAction.Compile;
		item.Importer = importer;
		item.Processor = processor;
	}

	private static void Copy(AssetBuildItem item, string reason)
	{
		item.Action = AssetBuildAction.Copy;
		item.Reason = reason;
	}

	private static void Skip(AssetBuildItem item, string reason)
	{
		item.Action = AssetBuildAction.Skip;
		item.Reason = reason;
	}

	private static bool IsResponseSafe(string path)
	{
		if (path.IndexOfAny(new[] { ';', '\r', '\n' }) >= 0)
			return false;
		var name = Path.GetFileName(path);
		return name.Length > 0 && name[0] != '#' && name[0] != '$' && name[0] != '/';
	}

	private static string StripExtension(string relative)
	{
		var slash = relative.LastIndexOf('/');
		var dot = relative.LastIndexOf('.');
		return dot > slash ? relative.Substring(0, dot) : relative;
	}

	/// <summary>Writes the response file; paths are absolute so the file runs from any working directory.</summary>
	public static void WriteMgcb(AssetBuildPlan plan)
	{
		var sb = new StringBuilder();
		sb.AppendLine("# Generated by the Voltage asset build; edit ProjectSettings.json instead.");
		sb.AppendLine($"/outputDir:{plan.OutputDir}");
		sb.AppendLine($"/intermediateDir:{plan.IntermediateDir}");
		sb.AppendLine($"/platform:{plan.Platform}");
		sb.AppendLine("/profile:HiDef");
		sb.AppendLine("/incremental");
		if (plan.Compress)
			sb.AppendLine("/compress");
		if (plan.PipelineAvailable)
			sb.AppendLine($"/reference:{plan.PipelineDll}");
		sb.AppendLine();

		foreach (var item in plan.Compiled)
		{
			sb.AppendLine($"/importer:{item.Importer}");
			sb.AppendLine($"/processor:{item.Processor}");
			foreach (var p in item.Parameters)
				sb.AppendLine($"/processorParam:{p.Key}={p.Value}");
			sb.AppendLine($"/build:{item.SourcePath};{item.AssetName}");
			sb.AppendLine();
		}

		WriteAtomically(plan.MgcbPath, sb.ToString());
	}

	/// <summary>content.index: one "source path TAB asset name" line per compiled asset, in plan order.</summary>
	public static string WriteIndex(AssetBuildPlan plan)
	{
		var path = Path.Combine(plan.OutputDir, IndexFileName);
		var lines = plan.Compiled.Where(i => i.Outcome == "compiled").Select(i => i.RelativePath + "\t" + i.AssetName);
		WriteAtomically(path, string.Join("\n", lines) + "\n");
		return path;
	}

	/// <summary>A crash mid-write must not leave a truncated file the game would trust.</summary>
	private static void WriteAtomically(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
		var temp = path + ".tmp";
		File.WriteAllText(temp, text, new UTF8Encoding(false));
		File.Move(temp, path, true);
	}

	/// <summary>Glob to regex: ** spans folders, * and ? stay inside one segment; case-insensitive everywhere so Windows and Linux agree.</summary>
	public static Regex GlobToRegex(string glob)
	{
		var pattern = new StringBuilder("^");
		var g = glob.Replace('\\', '/').Trim();
		if (g.StartsWith("./", StringComparison.Ordinal))
			g = g.Substring(2);
		for (var i = 0; i < g.Length; i++)
		{
			var c = g[i];
			if (c == '*')
			{
				if (i + 1 < g.Length && g[i + 1] == '*')
				{
					i++;
					if (i + 1 < g.Length && g[i + 1] == '/')
					{
						i++;
						pattern.Append("(?:.*/)?");
					}
					else
						pattern.Append(".*");
				}
				else
					pattern.Append("[^/]*");
			}
			else if (c == '?')
				pattern.Append("[^/]");
			else
				pattern.Append(Regex.Escape(c.ToString()));
		}
		pattern.Append('$');
		return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}
}
