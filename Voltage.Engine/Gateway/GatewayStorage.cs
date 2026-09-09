using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Voltage.Gateway;

/// <summary>Per-user locations a built game's gateway writes to; mirrors the editor's storage layout under "Runtime".</summary>
public static class GatewayStorage
{
	public const string OverrideEnvVar = "VOLTAGE_EDITOR_DATA";

	public static string RuntimeRoot => Resolve("Runtime");

	public static string RuntimeInfoPath => Path.Combine(RuntimeRoot, "gateway.json");

	public static string RuntimeLogsDirectory => Path.Combine(RuntimeRoot, "Logs");

	/// <summary>The editor's data root, resolved the same way the editor does so the CLI needs no editor reference.</summary>
	public static string EditorRoot => Resolve("Editor");

	private static string Resolve(string folder)
	{
		var overridden = Environment.GetEnvironmentVariable(OverrideEnvVar);
		if (!string.IsNullOrWhiteSpace(overridden))
			return folder == "Editor" ? Path.Combine(overridden, "Data") : Path.Combine(overridden, "Data", folder);

		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string baseDir;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			baseDir = Path.Combine(home, "Library/Application Support");
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
			baseDir = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".config") : xdg;
		}
		else
			baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

		return Path.Combine(baseDir, "VoltageEngine", folder);
	}
}
