using System;
using System.IO;
using Voltage.Editor.ProjectFile;
using Voltage.Project;

namespace Voltage.Editor.Builders;

/// <summary>The project's asset-build settings, kept in ProjectSettings.json next to the other project settings.</summary>
public static class AssetBuildSettingsStore
{
	public static ProjectSettings.AssetBuildSettings Get() => ProjectSettings.Instance.AssetBuild ??= new ProjectSettings.AssetBuildSettings();

	public static void Save(IGameProject project)
	{
		if (project == null)
			throw new InvalidOperationException("no project loaded");
		var path = Path.Combine(project.ProjectPath, "ProjectSettings.json");
		var json = Voltage.Persistence.Json.ToJson(ProjectSettings.Instance, new Voltage.Persistence.JsonSettings { PrettyPrint = true });
		File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
	}

	/// <summary>The platforms MGCB accepts; the name also becomes a folder and a response-file line, so nothing else is allowed through.</summary>
	public static readonly string[] Platforms = { "DesktopGL", "Windows", "Android", "iOS", "Switch", "PlayStation4", "PlayStation5", "XboxOne", "WindowsStoreApp" };

	public static bool IsValidPlatform(string platform) => Array.Exists(Platforms, p => p.Equals(platform, StringComparison.OrdinalIgnoreCase));

	/// <summary>Every desktop target uses the DesktopGL content format; any other name must be one MGCB knows.</summary>
	public static string MgcbPlatform(string platformOrRid)
	{
		if (string.IsNullOrWhiteSpace(platformOrRid))
			return "DesktopGL";
		var p = platformOrRid.Trim();
		if (p.StartsWith("win", StringComparison.OrdinalIgnoreCase) || p.StartsWith("linux", StringComparison.OrdinalIgnoreCase) || p.StartsWith("osx", StringComparison.OrdinalIgnoreCase) || p.Equals("desktop", StringComparison.OrdinalIgnoreCase))
			return "DesktopGL";
		var known = Array.Find(Platforms, k => k.Equals(p, StringComparison.OrdinalIgnoreCase));
		return known ?? throw new ArgumentException($"unknown MGCB platform '{p}'; use one of {string.Join(", ", Platforms)}");
	}

	/// <summary>Where "Build assets now" writes: &lt;project&gt;/Build/Assets/&lt;platform&gt;.</summary>
	public static string DefaultOutputDirectory(IGameProject project, string platform) =>
		Path.Combine(project.ProjectPath, "Build", "Assets", MgcbPlatform(platform));

	public static string IntermediateDirectory(IGameProject project, string platform) =>
		Path.Combine(project.ProjectPath, "obj", "AssetBuild", MgcbPlatform(platform));
}
