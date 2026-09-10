using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using NUnit.Framework;
using Voltage.Cli;

namespace Voltage.Editor.Tests;

/// <summary>One headless editor on a throwaway project and data folder, shared by every fixture in the run.</summary>
[SetUpFixture]
public sealed class EditorSession
{
	private static Process _editor;
	private static string _root;

	public static GatewayConnection Connection { get; private set; }

	public static string InfoPath { get; private set; }

	public static string ProjectFile { get; private set; }

	public static string DataDirectory { get; private set; }

	public static string EditorExe { get; private set; }

	public static JsonElement Call(string method, object parameters = null)
	{
		var element = parameters == null ? (JsonElement?)null : JsonSerializer.SerializeToElement(parameters);
		return Connection.Call(method, element);
	}

	/// <summary>Polls ui.find until the widget has been drawn; new scene-graph rows appear a frame or two after the entity.</summary>
	public static JsonElement WaitForWidget(string label, string window, int timeoutMs = 5000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (true)
		{
			var matches = Call("ui.find", new { label, window, exact = true });
			if (matches.GetArrayLength() > 0)
				return matches[0];
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException($"'{label}' never appeared in {window}");
			Thread.Sleep(100);
		}
	}

	/// <summary>A second socket, for calls that must wait while the main one keeps working.</summary>
	public static GatewayConnection Connect()
	{
		var info = GatewayInfo.Read(InfoPath);
		return new GatewayConnection(info.Port, info.Token, TimeSpan.FromSeconds(60));
	}

	[OneTimeSetUp]
	public void Start()
	{
		var exe = FindEditor();
		EditorExe = exe;
		_root = Path.Combine(Path.GetTempPath(), "voltage-gateway-tests", Guid.NewGuid().ToString("N"));
		DataDirectory = Path.Combine(_root, "data");
		Directory.CreateDirectory(DataDirectory);
		InfoPath = Path.Combine(_root, "gateway.json");

		var start = new ProcessStartInfo(exe)
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(exe) ?? "."
		};
		start.Environment["VOLTAGE_EDITOR_DATA"] = DataDirectory;
		foreach (var arg in new[] { "--headless", "--gateway-port", "0", "--gateway-info", InfoPath })
			start.ArgumentList.Add(arg);

		_editor = Process.Start(start) ?? throw new InvalidOperationException($"could not start {exe}");
		_editor.OutputDataReceived += (_, _) => { };
		_editor.ErrorDataReceived += (_, _) => { };
		_editor.BeginOutputReadLine();
		_editor.BeginErrorReadLine();

		Connection = WaitForGateway(TimeSpan.FromSeconds(180));
		var created = Call("project.create", new { name = "GatewaySmoke", directory = Path.Combine(_root, "projects") });
		ProjectFile = created.GetProperty("voltageFile").GetString();
		WaitForProject(TimeSpan.FromSeconds(180));
		Call("scene.create", new { name = "TestScene" });
	}

	[OneTimeTearDown]
	public void Stop()
	{
		try
		{
			if (Connection != null && Connection.IsOpen)
			{
				SaveEditorLog();
				Call("editor.exit", new { force = true });
			}
		}
		catch (Exception)
		{
		}

		var exited = _editor == null || _editor.WaitForExit(10_000);
		if (!exited)
		{
			try { _editor.Kill(true); } catch (Exception) { }
		}

		Connection?.Dispose();
		if (Environment.GetEnvironmentVariable("VOLTAGE_TESTS_KEEP") != "1")
			try { Directory.Delete(_root, true); } catch (Exception) { }
		else
			TestContext.Progress.WriteLine($"kept {_root}");
		Assert.That(exited, Is.True, "the editor did not exit within 10 s of editor.exit");
	}

	/// <summary>The editor's in-memory log, written beside the test results so a CI failure can be read.</summary>
	private static void SaveEditorLog()
	{
		var entries = Call("log.tail", new { count = 500 });
		var lines = new System.Text.StringBuilder();
		foreach (var entry in entries.EnumerateArray())
			lines.AppendLine($"{entry.GetProperty("time").GetString()} [{entry.GetProperty("type").GetString()}] {entry.GetProperty("message").GetString()}");
		File.WriteAllText(Path.Combine(TestContext.CurrentContext.WorkDirectory, "editor-log.txt"), lines.ToString());
	}

	private static GatewayConnection WaitForGateway(TimeSpan wait)
	{
		var deadline = DateTime.UtcNow + wait;
		while (DateTime.UtcNow < deadline)
		{
			if (_editor.HasExited)
				throw new InvalidOperationException($"the editor exited with code {_editor.ExitCode} during startup; see {DataDirectory}");

			Thread.Sleep(500);
			GatewayInfo info;
			try { info = GatewayInfo.Read(InfoPath); } catch (Exception) { continue; }
			if (info.Pid != _editor.Id)
				continue;

			try
			{
				var connection = new GatewayConnection(info.Port, info.Token, TimeSpan.FromSeconds(120));
				connection.Call("ping", null);
				return connection;
			}
			catch (CliException)
			{
			}
		}

		throw new TimeoutException($"the editor gateway did not answer within {wait.TotalSeconds:0} s");
	}

	/// <summary>project.create answers once the scene is current; the scripts compile in the background before status lists the project.</summary>
	private static void WaitForProject(TimeSpan wait)
	{
		var deadline = DateTime.UtcNow + wait;
		while (DateTime.UtcNow < deadline)
		{
			var status = Call("status");
			if (status.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object)
				return;
			Thread.Sleep(500);
		}

		throw new TimeoutException("the sample project did not load in time");
	}

	/// <summary>VOLTAGE_EDITOR_EXE, else the editor built for this test assembly's configuration.</summary>
	private static string FindEditor()
	{
		var overridden = Environment.GetEnvironmentVariable("VOLTAGE_EDITOR_EXE");
		if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
			return overridden;

		var testDir = new DirectoryInfo(AppContext.BaseDirectory);
		var configuration = testDir.Parent?.Name ?? "Editor-Debug";
		var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Voltage.Editor.exe" : "Voltage.Editor";
		var rid = RuntimeInformation.RuntimeIdentifier;

		for (var dir = testDir; dir != null; dir = dir.Parent)
		{
			var editorBin = Path.Combine(dir.FullName, "Voltage.Editor", "bin", configuration);
			if (!Directory.Exists(editorBin))
				continue;

			foreach (var candidate in new[] { Path.Combine(editorBin, rid, exeName), Path.Combine(editorBin, exeName) })
				if (File.Exists(candidate))
					return candidate;

			foreach (var ridDir in Directory.GetDirectories(editorBin))
			{
				var candidate = Path.Combine(ridDir, exeName);
				if (File.Exists(candidate))
					return candidate;
			}
		}

		throw new FileNotFoundException("no built editor found; build Voltage.Editor first or set VOLTAGE_EDITOR_EXE");
	}
}
