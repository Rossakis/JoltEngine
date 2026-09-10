using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voltage.Project;

namespace Voltage.Editor.Builders;

/// <summary>One check for every rule source (settings file, window, plugin, gateway): rule values end up on response-file lines and as MGCB references.</summary>
internal static class AssetBuildRuleValidation
{
	public static readonly string[] Actions = { "compile", "copy", "skip" };

	/// <summary>The rule's action, lower-cased, with compile as the default.</summary>
	public static string ActionOf(ProjectSettings.AssetBuildRule rule) => (rule.Action ?? "compile").Trim().ToLowerInvariant();

	/// <summary>Null when the rule is usable, else the problem; the resolved assembly path is set only when it is inside the project.</summary>
	public static string Validate(ProjectSettings.AssetBuildRule rule, string projectRoot, out string assembly)
	{
		assembly = null;
		if (rule == null || string.IsNullOrWhiteSpace(rule.Name) || !IsLineSafe(rule.Name))
			return "a rule needs a name";

		var extensions = AssetBuildPipeline.NormalisedExtensions(rule).ToList();
		if (extensions.Count == 0)
			return "a rule needs at least one extension";
		var badExtension = extensions.FirstOrDefault(e => e.Skip(1).Any(c => !char.IsLetterOrDigit(c)));
		if (badExtension != null)
			return $"extension '{badExtension}' must be letters and digits";

		var action = ActionOf(rule);
		if (!Actions.Contains(action))
			return $"unknown action '{rule.Action}'; use compile, copy or skip";
		if (action != "compile")
			return null;

		if (!IsIdentifier(rule.Importer))
			return $"importer must be a class name, not '{rule.Importer}'";
		if (!IsIdentifier(rule.Processor))
			return $"processor must be a class name, not '{rule.Processor}'";

		foreach (var parameter in rule.Parameters ?? new List<string>())
		{
			var eq = parameter.IndexOf('=');
			if (eq <= 0 || !IsLineSafe(parameter))
				return $"parameter '{parameter}' must look like Key=Value";
		}

		if (string.IsNullOrWhiteSpace(rule.Assembly))
			return null;
		if (!IsLineSafe(rule.Assembly))
			return "assembly path contains characters a response file cannot carry";

		var resolved = Path.GetFullPath(Path.IsPathRooted(rule.Assembly) ? rule.Assembly : Path.Combine(projectRoot, rule.Assembly));
		var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!resolved.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
			return "assembly must be inside the project";
		assembly = resolved;
		return null;
	}

	public static bool IsIdentifier(string value) =>
		!string.IsNullOrWhiteSpace(value) && value.Trim().All(c => char.IsLetterOrDigit(c) || c == '_' || c == '.');

	/// <summary>Response files hold one option per line, and /build splits on ';'.</summary>
	public static bool IsLineSafe(string value) =>
		value != null && value.IndexOfAny(new[] { '\r', '\n', ';' }) < 0;
}
