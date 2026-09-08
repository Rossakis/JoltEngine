using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Voltage.Console;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Undo.Core;
using Voltage.Utils;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Editor-wide commands: status, console, logs, undo and window visibility.</summary>
internal static class EditorCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("ping", "Round-trip check.", (_, _) => new { pong = true, time = DateTime.UtcNow });

		table.Add("commands", "List every gateway command with its help text.", (_, ctx) =>
			ctx.Commands.All.Select(c => new { name = c.Name, help = c.Help }).ToList());

		table.Add("status", "Project, scene, play state, dirty flag and frame timing.", (_, ctx) => Status(ctx));

		table.Add("editor.exit", "Quit the editor. Without force the usual unsaved-changes prompt appears. params: force=false", (args, _) =>
		{
			if (args.Bool("force"))
				Core.ConfirmAndExit();
			else
				Core.Exit();
			return new { exiting = true, forced = args.Bool("force") };
		});

		table.Add("console.exec", "Run a debug-console line. params: line", (args, _) =>
		{
			var console = DebugConsole.Instance ?? throw new GatewayException("debug console is not initialized");
			return new { lines = console.Execute(args.Require("line")) };
		});

		table.Add("console.commands", "Names of the debug-console commands.", (_, _) =>
			(DebugConsole.Instance?.CommandNames ?? Enumerable.Empty<string>()).OrderBy(n => n).ToList());

		table.Add("log.tail", "Most recent log entries. params: count=50, level (Error|Warn|Log|Info|Trace|Success)", (args, _) =>
		{
			var count = Math.Clamp(args.Int("count", 50), 1, 500);
			var level = args.String("level");
			IEnumerable<Debug.LogEntry> entries = Debug.GetLogEntries();
			if (!string.IsNullOrEmpty(level) && Enum.TryParse<Debug.LogType>(level, true, out var type))
				entries = entries.Where(e => e.Type == type);
			return entries.TakeLast(count).Select(LogEntry).ToList();
		});

		table.Add("log.subscribe", "Stream new log entries to this connection as 'log' events.", (_, ctx) =>
		{
			ctx.Client.LogSubscribed = true;
			return new { subscribed = true };
		});

		table.Add("log.unsubscribe", "Stop streaming log entries to this connection.", (_, ctx) =>
		{
			ctx.Client.LogSubscribed = false;
			return new { subscribed = false };
		});

		table.Add("log.clear", "Clear the editor log buffer.", (_, _) =>
		{
			Debug.ClearLogEntries();
			return new { cleared = true };
		});

		table.Add("undo", "Undo the last editor action.", (_, _) =>
		{
			var action = EditorChangeTracker.Undo();
			return new { done = action != null, description = action?.Description };
		});

		table.Add("redo", "Redo the last undone editor action.", (_, _) =>
		{
			var action = EditorChangeTracker.Redo();
			return new { done = action != null, description = action?.Description };
		});

		table.Add("undo.history", "Undo and redo stacks, most recent first.", (_, _) => new
		{
			undo = EditorChangeTracker.UndoActions.Select(a => a.Description).ToList(),
			redo = EditorChangeTracker.RedoActions.Select(a => a.Description).ToList()
		});

		table.Add("window.list", "Editor windows and whether each is visible.", (_, ctx) =>
			WindowToggles(ctx.ImGui).Select(p => new { name = WindowName(p), visible = (bool)p.GetValue(ctx.ImGui) }).ToList());

		table.Add("window.show", "Show or hide an editor window. params: name, visible=true", (args, ctx) =>
		{
			var name = args.Require("name");
			var prop = WindowToggles(ctx.ImGui).FirstOrDefault(p => WindowName(p).Equals(name, StringComparison.OrdinalIgnoreCase))
				?? throw new GatewayException($"unknown window '{name}'; see window.list");
			var visible = args.Bool("visible", true);
			prop.SetValue(ctx.ImGui, visible);
			return new { name = WindowName(prop), visible };
		});
	}

	private static object Status(GatewayContext ctx)
	{
		var project = ProjectManager.Instance?.CurrentProject;
		var sceneManager = SceneManager.Instance;
		var dt = Time.DeltaTime;

		return new
		{
			project = project == null ? null : new { name = project.ProjectName, path = project.ProjectPath },
			scene = sceneManager != null && sceneManager.HasLoadedScene
				? new { name = sceneManager.CurrentSceneName, path = sceneManager.CurrentScenePath }
				: null,
			editMode = Core.IsEditMode,
			pauseMode = Core.IsPauseMode,
			dirty = EditorChangeTracker.IsDirty,
			canUndo = EditorChangeTracker.CanUndo,
			canRedo = EditorChangeTracker.CanRedo,
			frame = Time.FrameCount,
			deltaTime = dt,
			fps = dt > 0 ? 1f / dt : 0f,
			entityCount = Core.Scene?.Entities.Count ?? 0,
			clients = ctx.Server?.ClientCount ?? 0
		};
	}

	public static object LogEntry(Debug.LogEntry e) => new
	{
		type = e.Type.ToString(),
		message = e.Message,
		time = e.Timestamp,
		caller = e.CallerClass,
		line = e.CallerLine
	};

	private static IEnumerable<PropertyInfo> WindowToggles(ImGuiManager imGui) =>
		imGui.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite && p.Name.StartsWith("Show", StringComparison.Ordinal))
			.OrderBy(p => p.Name, StringComparer.Ordinal);

	private static string WindowName(PropertyInfo p) => p.Name.Substring("Show".Length);
}
