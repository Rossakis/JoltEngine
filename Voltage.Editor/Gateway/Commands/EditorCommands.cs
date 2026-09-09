using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Undo.Core;
using Voltage.Gateway;
using Voltage.Utils;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Editor-wide commands: status, exit, undo and window visibility.</summary>
internal static class EditorCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("status", "Project, scene, play state, dirty flag and frame timing.", (_, ctx) => Status(ctx)).ReadOnly();

		table.Add("editor.exit", "Quit the editor. Without force the usual unsaved-changes prompt appears.", (args, _) =>
		{
			if (args.Bool("force"))
				Core.ConfirmAndExit();
			else
				Core.Exit();
			return new { exiting = true, forced = args.Bool("force") };
		}, P.Bool("force", "Skip the unsaved-changes prompt", false)).Destructive().Unsafe();

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
		}).ReadOnly();

		table.Add("undo.group", "Fold every undoable edit between begin and end into one undo step.", (args, _) =>
		{
			switch (args.String("action", "begin"))
			{
				case "begin":
					if (EditorChangeTracker.InGroup)
						throw new GatewayException("an undo group is already open; call undo.group action=end first");
					EditorChangeTracker.BeginGroup(args.String("description", "Gateway edits"));
					return new { open = true };
				case "end":
					var count = EditorChangeTracker.EndGroup();
					if (count < 0)
						throw new GatewayException("no undo group is open");
					return new { open = false, actions = count };
				default:
					throw new GatewayException("action must be begin or end");
			}
		}, P.Enum("action", "Open or close the group", new[] { "begin", "end" }, "begin"), P.Str("description", "Label in the undo history", "Gateway edits"));

		// Wraps the engine's batch so its edits can land as one undo step.
		var batch = table.All.First(c => c.Name == "batch");
		table.Add("batch", batch.Help, (args, ctx) =>
		{
			var group = args.String("undoGroup");
			if (string.IsNullOrEmpty(group))
				return batch.Handler(args, ctx);
			if (EditorChangeTracker.InGroup)
				throw new GatewayException("an undo group is already open");

			EditorChangeTracker.BeginGroup(group);
			Task<object> task;
			try
			{
				task = (Task<object>)batch.Handler(args, ctx);
			}
			catch
			{
				EditorChangeTracker.EndGroup();
				throw;
			}

			var done = new TaskCompletionSource<object>();
			task.ContinueWith(t =>
			{
				EditorChangeTracker.EndGroup();
				if (t.IsCompletedSuccessfully)
					done.TrySetResult(t.Result);
				else
					done.TrySetException(t.Exception?.GetBaseException() ?? new GatewayException("cancelled"));
			}, TaskContinuationOptions.ExecuteSynchronously);
			return done.Task;
		}, batch.Params.Append(P.Str("undoGroup", "Fold the batch's edits into one undo step with this label")).ToArray());

		table.Add("window.list", "Editor windows and whether each is visible.", (_, ctx) =>
			WindowToggles(ctx.ImGui()).Select(p => new { name = WindowName(p), visible = (bool)p.GetValue(ctx.ImGui()) }).ToList()).ReadOnly();

		table.Add("window.show", "Show or hide an editor window.", (args, ctx) =>
		{
			var name = args.Require("name");
			var prop = WindowToggles(ctx.ImGui()).FirstOrDefault(p => WindowName(p).Equals(name, StringComparison.OrdinalIgnoreCase))
				?? throw new GatewayException($"unknown window '{name}'; see window.list");
			var visible = args.Bool("visible", true);
			prop.SetValue(ctx.ImGui(), visible);
			return new { name = WindowName(prop), visible };
		}, P.Str("name", "Window name from window.list", required: true), P.Bool("visible", "Show (true) or hide", true));
	}

	private static object Status(GatewayContext ctx)
	{
		var project = ProjectManager.Instance?.CurrentProject;
		var sceneManager = SceneManager.Instance;
		var dt = Time.DeltaTime;

		return new
		{
			host = "editor",
			pid = Environment.ProcessId,
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
			clients = ctx.Server?.ClientCount ?? 0,
			safe = ctx.Dispatcher.Options.Safe,
			headless = EditorRunMode.Headless,
			noPrompts = EditorRunMode.NoPrompts,
			suppressedPrompts = EditorGatewayDispatcher.SuppressedPrompts
		};
	}

	private static IEnumerable<PropertyInfo> WindowToggles(ImGuiManager imGui) =>
		imGui.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite && p.Name.StartsWith("Show", StringComparison.Ordinal))
			.OrderBy(p => p.Name, StringComparer.Ordinal);

	private static string WindowName(PropertyInfo p) => p.Name.Substring("Show".Length);
}
