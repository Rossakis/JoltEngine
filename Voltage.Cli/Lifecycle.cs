using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Voltage.Cli;

/// <summary>stop and restart: the edit, rebuild, relaunch loop an agent runs against the editor.</summary>
public static class Lifecycle
{
	/// <summary>Asks the recorded process to quit and waits for its pid to disappear.</summary>
	public static int Stop(string infoPath, bool game, TimeSpan wait)
	{
		GatewayInfo info;
		try
		{
			info = GatewayInfo.Read(infoPath);
		}
		catch (CliException)
		{
			Console.WriteLine($"nothing recorded at {infoPath}; nothing to stop");
			return 0;
		}

		if (info.Pid == 0 || !GatewayInfo.IsRunning(info.Pid))
		{
			Console.WriteLine($"no {info.HostLabel} is running (last pid {info.Pid})");
			return 0;
		}

		try
		{
			using var connection = new GatewayConnection(info.Port, info.Token, TimeSpan.FromSeconds(10));
			if (game)
				connection.Call("app.exit", null);
			else
				connection.Call("editor.exit", JsonSerializer.SerializeToElement(new { force = true }));
		}
		catch (CliException ex) when (ex.Message.Contains("--gateway-safe", StringComparison.Ordinal))
		{
			throw new CliException($"the {info.HostLabel} (pid {info.Pid}) runs under --gateway-safe, which refuses exits; stop it with Stop-Process/taskkill or from its window");
		}
		catch (CliException ex) when (ex.Message.Contains("closed the connection", StringComparison.Ordinal) || ex.Message.Contains("stopped responding", StringComparison.Ordinal))
		{
			// The process can drop the socket before the reply leaves; the pid check below is the answer.
		}

		var deadline = DateTime.UtcNow + wait;
		while (DateTime.UtcNow < deadline)
		{
			if (!GatewayInfo.IsRunning(info.Pid))
			{
				Console.WriteLine($"stopped the {info.HostLabel} (pid {info.Pid})");
				return 0;
			}
			Thread.Sleep(250);
		}

		throw new CliException($"the {info.HostLabel} (pid {info.Pid}) is still running after {wait.TotalSeconds:0}s");
	}

	/// <summary>Stops the recorded process, optionally rebuilds the editor project next to its executable, and starts it again.</summary>
	public static int Restart(string infoPath, bool game, bool rebuild, TimeSpan wait, string projectPath, IReadOnlyList<string> passthrough)
	{
		var info = GatewayInfo.Read(infoPath);
		if (game && rebuild)
			throw new CliException("--rebuild applies to the editor only; rebuild and republish a game from the editor");

		var stop = Stop(infoPath, game, wait);
		if (stop != 0)
			return stop;

		if (rebuild)
			Rebuild(info.Exe);

		var started = game
			? GatewayInfo.StartGame(infoPath, info.Exe, wait)
			: GatewayInfo.Start(infoPath, null, projectPath, wait, passthrough);
		Console.WriteLine(JsonSerializer.Serialize(new { pid = started.Pid, port = started.Port, exe = started.Exe, host = started.Host, game = started.Game, info = infoPath }, new JsonSerializerOptions { WriteIndented = true }));
		return 0;
	}

	/// <summary>Builds Voltage.Editor.csproj found from bin/&lt;Configuration&gt;/&lt;rid&gt;/Voltage.Editor.exe, streaming the compiler output.</summary>
	private static void Rebuild(string exe)
	{
		if (string.IsNullOrWhiteSpace(exe))
			throw new CliException("no editor executable is recorded; start the editor once before --rebuild");

		var ridDir = Path.GetDirectoryName(Path.GetFullPath(exe));
		var configurationDir = ridDir == null ? null : Path.GetDirectoryName(ridDir);
		var binDir = configurationDir == null ? null : Path.GetDirectoryName(configurationDir);
		var projectDir = binDir == null ? null : Path.GetDirectoryName(binDir);
		var csproj = projectDir == null ? null : Path.Combine(projectDir, "Voltage.Editor.csproj");
		if (csproj == null || !File.Exists(csproj) || !string.Equals(Path.GetFileName(binDir), "bin", StringComparison.OrdinalIgnoreCase))
			throw new CliException($"cannot find Voltage.Editor.csproj from {exe}; --rebuild expects bin/<Configuration>/<rid>/Voltage.Editor.exe inside the source tree");

		var configuration = Path.GetFileName(configurationDir);
		Console.Error.WriteLine($"dotnet build {csproj} -c {configuration}");
		var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = projectDir };
		startInfo.ArgumentList.Add("build");
		startInfo.ArgumentList.Add(csproj);
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add(configuration);
		startInfo.ArgumentList.Add("--nologo");

		using var process = Process.Start(startInfo) ?? throw new CliException("could not start dotnet");
		process.WaitForExit();
		if (process.ExitCode != 0)
			throw new CliException($"dotnet build failed with exit code {process.ExitCode}; the editor was not restarted");
	}

	/// <summary>Turns the CLI's launch flags into editor arguments.</summary>
	public static List<string> TakePassthrough(List<string> args, Func<List<string>, string, string> takeOption, Func<List<string>, string, bool> takeFlag)
	{
		var result = new List<string>();
		if (takeFlag(args, "--headless"))
			result.Add("--headless");
		if (takeFlag(args, "--safe"))
			result.Add("--gateway-safe");
		if (takeFlag(args, "--no-prompts"))
			result.Add("--no-prompts");
		var port = takeOption(args, "--gateway-port");
		if (port != null)
		{
			result.Add("--gateway-port");
			result.Add(port);
		}
		return result;
	}

	/// <summary>Prints the CLI version and, when something answers, the host's engine version.</summary>
	public static int Version(string infoPath)
	{
		var version = typeof(Lifecycle).Assembly.GetName().Version;
		Console.WriteLine($"voltage {(version == null || version.Major == 0 && version.Minor == 0 ? "dev" : version.ToString(3))}");
		try
		{
			var info = GatewayInfo.Load(infoPath);
			using var connection = new GatewayConnection(info.Port, info.Token, TimeSpan.FromSeconds(5));
			var status = connection.Call("status", null);
			var engine = status.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "unknown";
			Console.WriteLine($"{info.HostLabel} engine {engine} (pid {info.Pid}, port {info.Port})");
		}
		catch (CliException ex)
		{
			Console.WriteLine($"no editor or game answered: {ex.Message}");
		}
		return 0;
	}
}
