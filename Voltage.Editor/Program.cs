using System;
using System.IO;
using Voltage.Editor.Diagnostics;
using Voltage.Utils;

namespace Voltage.Editor;

public class Program
{
	public static string[] CommandLineArgs { get; private set; }

	public static void Main(string[] args)
	{
		CommandLineArgs = args;
		EditorRunMode.Apply(Voltage.Gateway.GatewayOptions.FromArgs(args, true));

		// First, so nothing writes to the pre-migration locations on the way up.
		Persistence.EditorStorage.Initialize();

		// Catches unhandled exceptions and logs them to a file & Editor Debug Window
		AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
		System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

		// Preflight critical native runtime libraries (libGL, SDL2) BEFORE any graphics init.
		// If they're missing, SDL/MonoGame would crash cryptically below; instead we print clear,
		// distro-aware install instructions and exit cleanly. No-op / trivially passes off Linux.
		if (!RuntimeDependencyPreflight.CheckCriticalOrExit())
		{
			Environment.Exit(1);
			return;
		}

		// Audio backend: uncomment to route ALL audio through the software mixing backend — real DSP
		// (per-voice occlusion low-pass + global reverb). It probes the platform and falls back to the
		// MonoGame backend automatically if unsupported, so this is safe to leave on for testing. Must be set
		// before the Editor (and its AudioManager) is constructed. Watch the console for a "[Audio] Using…" line.
		Audio.AudioManager.PreferSoftwareBackend = true;

		using var game = new Editor();
		game.Run();
	}

	private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		var ex = e.ExceptionObject as Exception;
		var message = ex != null
			? $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}"
			: e.ExceptionObject?.ToString() ?? "Unknown error";

		var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [FATAL] Unhandled exception " +
		                 $"(IsTerminating={e.IsTerminating}):\n{message}\n" + RecentLogLines();

		// Crash Log File
		try
		{
			// A crash log beats a tidy location if storage resolution is itself broken.
			string logDir;
			try { logDir = Persistence.EditorStorage.LogsDirectory; }
			catch { logDir = AppContext.BaseDirectory; }

			var logPath = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
			File.WriteAllText(logPath, logMessage);
		}
		catch { }

		// Editor
		try
		{
			Debug.Error(logMessage);
		}
		catch { }

		// Leaving the runtime to finish the crash raises the OS error dialog on Windows, which blocks until a
		// person dismisses it. The log is written, so exit now; a debugger still gets its break first.
		if (e.IsTerminating && !System.Diagnostics.Debugger.IsAttached)
			Environment.Exit(70);
	}

	/// <summary>The tail of the editor log, so a crash file shows what led up to it.</summary>
	private static string RecentLogLines(int count = 40)
	{
		try
		{
			var entries = Debug.GetLogEntries();
			var start = Math.Max(0, entries.Count - count);
			var lines = new System.Text.StringBuilder("\nRecent log:\n");
			for (var i = start; i < entries.Count; i++)
			{
				var entry = entries[i];
				lines.Append($"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Type}] {entry.Message} ({entry.CallerClass}:{entry.CallerLine})\n");
			}
			return lines.ToString();
		}
		catch
		{
			return string.Empty;
		}
	}

	private static void OnUnobservedTaskException(
		object sender,
		System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
	{
		var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Unobserved task exception:\n" +
		              $"{e.Exception}\n";

		try { Debug.Error(message); }
		catch { }

		// Mark as observed so the process doesn't terminate.
		e.SetObserved();
	}
}