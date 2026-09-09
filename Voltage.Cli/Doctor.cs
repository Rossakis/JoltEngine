using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Voltage.Cli;

/// <summary>Checks everything between this CLI and a running editor or game, and says what to fix.</summary>
public static class Doctor
{
	private sealed record Check(string Status, string Name, string Detail);

	public static int Run(string editorInfoPath, string gameInfoPath, bool json)
	{
		var checks = new List<Check>();
		var editor = CheckHost(editorInfoPath, "editor", checks);
		CheckBuild(editor, checks);
		CheckDotnet(checks);
		CheckHost(gameInfoPath, "game", checks);
		CheckMcpConfig(checks);

		if (json)
			Console.WriteLine(JsonSerializer.Serialize(checks.Select(c => new { status = c.Status, check = c.Name, detail = c.Detail }), new JsonSerializerOptions { WriteIndented = true }));
		else
			foreach (var check in checks)
				Console.WriteLine($"{check.Status,-4} {check.Name}: {check.Detail}");

		return checks.Any(c => c.Status == "FAIL") ? 1 : 0;
	}

	private static GatewayInfo CheckHost(string infoPath, string host, List<Check> checks)
	{
		var label = host == "editor" ? "editor" : "game";
		if (!File.Exists(infoPath))
		{
			checks.Add(new Check(host == "editor" ? "FAIL" : "OK", $"{label} gateway file",
				host == "editor" ? $"{infoPath} is missing; start the Voltage Editor once" : "none recorded (run a game with --gateway, or build.run gateway=true)"));
			return null;
		}

		GatewayInfo info;
		try
		{
			info = GatewayInfo.Read(infoPath);
			checks.Add(new Check("OK", $"{label} gateway file", $"{infoPath} (port {info.Port}, pid {info.Pid}, started {info.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss})"));
		}
		catch (Exception ex)
		{
			checks.Add(new Check("FAIL", $"{label} gateway file", $"{infoPath} is not readable: {ex.Message}"));
			return null;
		}

		if (info.Pid != 0 && !GatewayInfo.IsRunning(info.Pid))
		{
			var crash = info.NewestCrashLog();
			checks.Add(new Check(host == "editor" ? "FAIL" : "WARN", $"{label} process", crash != null
				? $"pid {info.Pid} is gone and crashed; see {crash}"
				: $"pid {info.Pid} is not running ({(host == "editor" ? "voltage start" : "voltage start --game <exe>")} relaunches it)"));
			return info;
		}

		checks.Add(new Check("OK", $"{label} process", $"pid {info.Pid} is running{(info.Game != null ? $" ({info.Game})" : "")}"));

		try
		{
			var watch = Stopwatch.StartNew();
			using var connection = new GatewayConnection(info.Port, info.Token, TimeSpan.FromSeconds(5));
			connection.Call("ping", null);
			checks.Add(new Check("OK", $"{label} gateway", $"127.0.0.1:{info.Port} answered ping in {watch.ElapsedMilliseconds} ms"));
		}
		catch (CliException ex)
		{
			checks.Add(new Check("FAIL", $"{label} gateway", ex.Message));
		}

		var crashAfter = info.NewestCrashLog();
		if (crashAfter != null)
			checks.Add(new Check("WARN", $"{label} crash log", $"{crashAfter} was written after this {label} started"));

		return info;
	}

	private static void CheckBuild(GatewayInfo editor, List<Check> checks)
	{
		if (editor == null)
			return;

		if (string.IsNullOrEmpty(editor.Exe) || !File.Exists(editor.Exe))
		{
			checks.Add(new Check("WARN", "editor executable", $"{editor.Exe ?? "(none)"} does not exist; 'voltage start' needs --exe"));
			return;
		}

		var exeTime = File.GetLastWriteTimeUtc(editor.Exe);
		checks.Add(new Check("OK", "editor executable", $"{editor.Exe} (built {exeTime.ToLocalTime():yyyy-MM-dd HH:mm})"));

		var cli = typeof(Doctor).Assembly.Location;
		if (File.Exists(cli))
		{
			var cliTime = File.GetLastWriteTimeUtc(cli);
			if (exeTime < cliTime.AddMinutes(-1))
				checks.Add(new Check("WARN", "editor build age", $"the editor build is {(cliTime - exeTime).TotalHours:0.#} h older than this CLI; rebuild it if commands are missing"));
			else
				checks.Add(new Check("OK", "editor build age", "the editor build is at least as new as this CLI"));
		}

		if (editor.Pid != 0 && GatewayInfo.IsRunning(editor.Pid))
		{
			try
			{
				using var process = Process.GetProcessById(editor.Pid);
				if (process.StartTime.ToUniversalTime() < exeTime)
					checks.Add(new Check("WARN", "running editor", "the editor started before its executable was last built; restart it to pick up the build"));
			}
			catch (Exception)
			{
			}
		}
	}

	private static void CheckDotnet(List<Check> checks)
	{
		try
		{
			var startInfo = new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
			using var process = Process.Start(startInfo);
			var version = process?.StandardOutput.ReadToEnd().Trim();
			if (process != null && process.WaitForExit(10000) && process.ExitCode == 0 && !string.IsNullOrEmpty(version))
				checks.Add(new Check("OK", "dotnet SDK", version));
			else
				checks.Add(new Check("WARN", "dotnet SDK", "dotnet --version failed; script compiles and builds need the .NET 8 SDK"));
		}
		catch (Exception ex)
		{
			checks.Add(new Check("WARN", "dotnet SDK", $"not on PATH ({ex.Message}); script compiles and builds need the .NET 8 SDK"));
		}
	}

	/// <summary>Inside a git checkout, .mcp.json is what lets MCP clients such as Claude Code find 'voltage mcp'.</summary>
	private static void CheckMcpConfig(List<Check> checks)
	{
		var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (dir != null)
		{
			var mcp = Path.Combine(dir.FullName, ".mcp.json");
			if (File.Exists(mcp))
			{
				checks.Add(new Check("OK", "mcp config", mcp));
				return;
			}
			if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
			{
				checks.Add(new Check("WARN", "mcp config", $"no .mcp.json in {dir.FullName}; add one that runs 'voltage mcp' so agents can find the editor"));
				return;
			}
			dir = dir.Parent;
		}

		checks.Add(new Check("OK", "mcp config", "not inside a repository"));
	}
}
