using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Voltage.Cli;

/// <summary>What a running (or last-run) editor or game wrote to gateway.json.</summary>
public sealed record GatewayInfo(int Port, string Token, int Pid, DateTime Started, string Exe, string[] Args, string Logs, string Host, string Game)
{
	/// <summary>Mirrors the engine's GatewayStorage layout so the CLI needs no engine reference. host is "editor" or "game".</summary>
	public static string DefaultInfoPath(string host = "editor")
	{
		var game = host == "game";
		var overridden = Environment.GetEnvironmentVariable("VOLTAGE_EDITOR_DATA");
		string root;
		if (!string.IsNullOrWhiteSpace(overridden))
			root = game ? Path.Combine(overridden, "Data", "Runtime") : Path.Combine(overridden, "Data");
		else
		{
			var folder = game ? "Runtime" : "Editor";
			string baseDir;
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support");
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
				baseDir = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg;
			}
			else
				baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			root = Path.Combine(baseDir, "VoltageEngine", folder);
		}

		return Path.Combine(root, "gateway.json");
	}

	public string HostLabel => Host == "game" ? (string.IsNullOrEmpty(Game) ? "game" : $"game '{Game}'") : "editor";

	/// <summary>Reads the file without checking whether the process that wrote it is still alive.</summary>
	public static GatewayInfo Read(string path)
	{
		if (!File.Exists(path))
			throw new CliException($"no gateway has been recorded yet ({path} is missing). Start the Voltage Editor, or a game with --gateway, first.");

		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var root = doc.RootElement;
		return new GatewayInfo(
			root.GetProperty("port").GetInt32(),
			root.GetProperty("token").GetString(),
			root.TryGetProperty("pid", out var pid) ? pid.GetInt32() : 0,
			root.TryGetProperty("started", out var started) && started.TryGetDateTime(out var when) ? when : DateTime.MinValue,
			root.TryGetProperty("exe", out var exe) ? exe.GetString() : null,
			root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array ? args.EnumerateArray().Select(a => a.GetString()).ToArray() : Array.Empty<string>(),
			root.TryGetProperty("logs", out var logs) ? logs.GetString() : null,
			root.TryGetProperty("host", out var host) && host.ValueKind == JsonValueKind.String ? host.GetString() : "editor",
			root.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.String ? game.GetString() : null);
	}

	/// <summary>Reads the file and insists the process that wrote it is still running; a crash log, when one exists, is named in the error.</summary>
	public static GatewayInfo Load(string path)
	{
		var info = Read(path);
		if (info.Pid == 0 || IsRunning(info.Pid))
			return info;

		var crash = info.NewestCrashLog();
		var relaunch = info.Host == "game" ? "Run 'voltage start --game <exe>' to relaunch it." : "Run 'voltage start' to relaunch it.";
		var hint = crash != null ? $"It crashed; see {crash}. {relaunch}" : relaunch;
		throw new CliException($"the {info.HostLabel} that wrote {path} (pid {info.Pid}) is no longer running. {hint}");
	}

	/// <summary>Newest crash log written after this process started, or null.</summary>
	public string NewestCrashLog()
	{
		if (string.IsNullOrEmpty(Logs) || !Directory.Exists(Logs))
			return null;

		return Directory.GetFiles(Logs, "crash_*.log")
			.Select(f => new FileInfo(f))
			.Where(f => f.LastWriteTimeUtc >= Started.AddSeconds(-5))
			.OrderByDescending(f => f.LastWriteTimeUtc)
			.Select(f => f.FullName)
			.FirstOrDefault();
	}

	public static bool IsRunning(int pid)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	/// <summary>Launches the editor recorded in gateway.json (or the given exe) and waits until its gateway answers.</summary>
	public static GatewayInfo Start(string infoPath, string exeOverride, string projectPath, TimeSpan wait)
	{
		GatewayInfo previous = null;
		try { previous = Read(infoPath); } catch (CliException) { }

		if (previous != null && previous.Pid != 0 && IsRunning(previous.Pid))
			throw new CliException($"an editor is already running (pid {previous.Pid})");

		var exe = exeOverride ?? previous?.Exe;
		if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
			throw new CliException("no editor executable is known yet; pass --exe <path to Voltage.Editor> the first time");

		var arguments = !string.IsNullOrEmpty(projectPath)
			? new[] { Path.GetFullPath(projectPath) }
			: previous?.Args ?? Array.Empty<string>();
		return Launch(exe, arguments, infoPath, wait, "editor");
	}

	/// <summary>Launches a built game with its gateway on and waits until it answers.</summary>
	public static GatewayInfo StartGame(string infoPath, string exe, TimeSpan wait)
	{
		if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
			throw new CliException("pass the path of a built game executable: voltage start --game <exe>");

		GatewayInfo previous = null;
		try { previous = Read(infoPath); } catch (CliException) { }
		if (previous != null && previous.Pid != 0 && IsRunning(previous.Pid))
			throw new CliException($"a game with a gateway is already running (pid {previous.Pid}); quit it first (voltage --game app.exit)");

		return Launch(Path.GetFullPath(exe), new[] { "--gateway", "--gateway-port", "0", "--gateway-info", infoPath }, infoPath, wait, "game");
	}

	private static GatewayInfo Launch(string exe, string[] arguments, string infoPath, TimeSpan wait, string what)
	{
		// The child must not inherit this process's stdout: a caller capturing our output would otherwise
		// block until the child exits. Windows can detach through the shell; elsewhere the pipes are drained.
		var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
		var startInfo = new ProcessStartInfo(exe)
		{
			UseShellExecute = windows,
			RedirectStandardOutput = !windows,
			RedirectStandardError = !windows,
			WorkingDirectory = Path.GetDirectoryName(exe) ?? "."
		};
		foreach (var arg in arguments)
			startInfo.ArgumentList.Add(arg);

		var process = Process.Start(startInfo) ?? throw new CliException($"could not start {exe}");
		if (!windows)
		{
			process.OutputDataReceived += (_, _) => { };
			process.ErrorDataReceived += (_, _) => { };
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
		}
		var deadline = DateTime.UtcNow + wait;

		while (DateTime.UtcNow < deadline)
		{
			Thread.Sleep(500);
			if (process.HasExited)
				throw new CliException($"the {what} exited during startup with code {process.ExitCode}");

			GatewayInfo info;
			try { info = Read(infoPath); } catch (Exception) { continue; }
			if (info.Pid != process.Id)
				continue;

			try
			{
				using var connection = new GatewayConnection(info.Port, info.Token, TimeSpan.FromSeconds(5));
				connection.Call("ping", null);
				return info;
			}
			catch (CliException)
			{
			}
		}

		throw new CliException($"the {what} (pid {process.Id}) did not answer within {wait.TotalSeconds:0}s");
	}
}
