using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voltage.Editor.ProjectFile;
using Voltage.Project;

namespace Voltage.Editor.Builders;

/// <summary>Rules editor plugins register in code; project rules from ProjectSettings.json win on the same extension.</summary>
public static class AssetBuildRules
{
	private static readonly List<ProjectSettings.AssetBuildRule> Registered = new();

	public static IReadOnlyList<ProjectSettings.AssetBuildRule> Plugins
	{
		get
		{
			lock (Registered)
				return Registered.ToList();
		}
	}

	public static void Register(ProjectSettings.AssetBuildRule rule)
	{
		if (rule == null || string.IsNullOrWhiteSpace(rule.Name))
			throw new ArgumentException("a rule needs a name");
		lock (Registered)
		{
			Registered.RemoveAll(r => r.Name == rule.Name);
			Registered.Add(rule);
		}
	}

	public static void Unregister(string name)
	{
		lock (Registered)
			Registered.RemoveAll(r => r.Name == name);
	}

	/// <summary>Extension to rule, plugin rules first so project rules overwrite them.</summary>
	internal static Dictionary<string, ProjectSettings.AssetBuildRule> Resolve(ProjectSettings.AssetBuildSettings settings)
	{
		var map = new Dictionary<string, ProjectSettings.AssetBuildRule>(StringComparer.OrdinalIgnoreCase);
		List<ProjectSettings.AssetBuildRule> plugins;
		lock (Registered)
			plugins = Registered.ToList();
		foreach (var rule in plugins.Concat(settings.Rules ?? new List<ProjectSettings.AssetBuildRule>()))
			foreach (var ext in AssetBuildPipeline.NormalisedExtensions(rule))
				map[ext] = rule;
		return map;
	}

	/// <summary>The single .csproj in the assembly's folder or one of its parents inside the project, else null.</summary>
	public static string FindExtensionProject(IGameProject project, string assemblyPath)
	{
		var root = Path.GetFullPath(project.ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
		while (!string.IsNullOrEmpty(dir) && dir.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
		{
			if (Directory.Exists(dir) && !string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
			{
				var projects = Directory.GetFiles(dir, "*.csproj");
				if (projects.Length == 1)
					return projects[0];
			}
			dir = Path.GetDirectoryName(dir);
		}
		return null;
	}

	/// <summary>True when the DLL is missing or any source next to the csproj is newer than it.</summary>
	public static bool IsStale(string assemblyPath, string csproj)
	{
		if (!File.Exists(assemblyPath))
			return true;
		var built = File.GetLastWriteTimeUtc(assemblyPath);
		var folder = Path.GetDirectoryName(csproj) ?? ".";
		return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
			.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
			.Any(f => File.GetLastWriteTimeUtc(f) > built);
	}
}
