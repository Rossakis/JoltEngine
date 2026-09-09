using System;
using System.IO;

namespace Voltage.Gateway;

/// <summary>Gateway settings read from the host's command line.</summary>
public sealed class GatewayOptions
{
	public const int DefaultPort = 47800;

	public bool Enabled { get; init; } = true;

	public int Port { get; init; } = DefaultPort;

	/// <summary>Refuses commands marked unsafe: process launches, crashes, exit, writes outside the project.</summary>
	public bool Safe { get; init; }

	/// <summary>Where the discovery file goes; null means the host's default location.</summary>
	public string InfoFilePath { get; init; }

	/// <summary>Recorded in the info file so 'voltage start' can relaunch with the same arguments.</summary>
	public string[] Args { get; init; } = Array.Empty<string>();

	/// <summary>Crash logs live here; recorded in the info file for the CLI.</summary>
	public string LogsDirectory { get; init; }

	/// <summary>"editor" or "game".</summary>
	public string Host { get; init; } = "game";

	/// <summary>Window title or assembly name, so a CLI can tell which game answered.</summary>
	public string Name { get; init; }

	/// <summary>Startup prompts are logged and reported as events instead of opening modals.</summary>
	public bool NoPrompts { get; init; }

	/// <summary>Hidden window, loop never pauses, prompts suppressed: for CI and unattended agents.</summary>
	public bool Headless { get; init; }

	/// <summary>Understands --gateway, --no-gateway, --gateway-port N (or =N, 0 = any free port), --gateway-safe, --gateway-info PATH, --no-prompts and --headless.</summary>
	public static GatewayOptions FromArgs(string[] args, bool defaultEnabled)
	{
		var enabled = defaultEnabled;
		var port = DefaultPort;
		var safe = false;
		var noPrompts = false;
		var headless = false;
		string info = null;

		for (var i = 0; args != null && i < args.Length; i++)
		{
			var arg = args[i];
			if (Is(arg, "--no-gateway"))
				enabled = false;
			else if (Is(arg, "--gateway"))
				enabled = true;
			else if (Is(arg, "--gateway-safe"))
				safe = true;
			else if (Is(arg, "--no-prompts"))
				noPrompts = true;
			else if (Is(arg, "--headless"))
				headless = noPrompts = true;
			else if (TryValue(args, ref i, "--gateway-port", out var portText))
				port = int.TryParse(portText, out var parsed) && parsed >= 0 && parsed <= 65535 ? parsed : DefaultPort;
			else if (TryValue(args, ref i, "--gateway-info", out var infoText))
				info = Path.GetFullPath(infoText);
		}

		return new GatewayOptions { Enabled = enabled, Port = port, Safe = safe, InfoFilePath = info, Args = args ?? Array.Empty<string>(), NoPrompts = noPrompts, Headless = headless };
	}

	/// <summary>Options for a built game: off unless --gateway or VOLTAGE_GATEWAY=1 asks for it.</summary>
	public static GatewayOptions ForRuntime(string[] args, string gameName)
	{
		var env = Environment.GetEnvironmentVariable("VOLTAGE_GATEWAY");
		var fromEnv = env is "1" or "true" or "True";
		var options = FromArgs(args, fromEnv);
		return new GatewayOptions
		{
			Enabled = options.Enabled,
			Port = options.Port,
			Safe = options.Safe,
			InfoFilePath = options.InfoFilePath ?? GatewayStorage.RuntimeInfoPath,
			Args = options.Args,
			LogsDirectory = GatewayStorage.RuntimeLogsDirectory,
			Host = "game",
			Name = gameName,
			NoPrompts = options.NoPrompts,
			Headless = options.Headless
		};
	}

	/// <summary>True for a gateway flag whose value is the next argument, so hosts can skip it when looking for positional arguments.</summary>
	public static bool TakesValue(string arg) => Is(arg, "--gateway-port") || Is(arg, "--gateway-info");

	private static bool Is(string arg, string name) => arg.Equals(name, StringComparison.OrdinalIgnoreCase);

	private static bool TryValue(string[] args, ref int i, string name, out string value)
	{
		value = null;
		var arg = args[i];
		if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
		{
			value = arg.Substring(name.Length + 1);
			return true;
		}

		if (Is(arg, name) && i + 1 < args.Length)
		{
			value = args[++i];
			return true;
		}

		return false;
	}
}
