using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Voltage.Editor.Scripting;
using Voltage.Gateway;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Play mode, script compilation and game-view capture.</summary>
internal static class PlayScriptCommands
{
	private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(2);

	public static void Register(GatewayCommandTable table)
	{
		table.Add("play.state", "Edit, play or paused.", (_, _) => PlayState()).ReadOnly();

		table.Add("play.start", "Enter play mode.", (_, _) =>
		{
			if (Core.Scene == null)
				throw new GatewayException("no scene loaded");
			if (Core.IsEditMode)
				Core.InvokeSwitchEditMode(false);
			return PlayState();
		});

		table.Add("play.stop", "Return to edit mode.", (_, _) =>
		{
			if (!Core.IsEditMode)
			{
				Core.IsPauseMode = false;
				Core.InvokeSwitchEditMode(true);
			}
			return PlayState();
		});

		table.Add("play.pause", "Pause or resume play mode.", (args, _) =>
		{
			if (Core.IsEditMode)
				throw new GatewayException("not in play mode");
			Core.InvokeSwitchPauseMode(args.Bool("paused", true));
			return PlayState();
		}, P.Bool("paused", "Pause (true) or resume (false)", true));

		table.Add("play.reset", "Reset the running scene to its saved state.", (_, _) =>
		{
			Core.InvokeResetScene();
			return PlayState();
		});

		table.Add("scripts.compile", "Compile the project's scripts; answers with diagnostics.", (args, ctx) => CompileAsync(ctx, args.Bool("reloadScene")),
			P.Bool("reloadScene", "Reload the scene after a successful compile", false));

		table.Add("scripts.types", "Component types defined by the compiled scripts.", (_, ctx) =>
		{
			var manager = ctx.ImGui().ScriptManager ?? throw new GatewayException("no project with a scripts folder is loaded");
			return manager.GetScriptComponentTypes().Select(t => t.FullName).OrderBy(n => n).ToList();
		}).ReadOnly();
	}

	/// <summary>Compiles the project's scripts and completes with the diagnostics; shared by every command that edits scripts.</summary>
	internal static Task<object> CompileAsync(GatewayContext ctx, bool reloadScene)
	{
		var manager = ctx.ImGui().ScriptManager ?? throw new GatewayException("no project with a scripts folder is loaded");

		// The watcher does nothing at all for an empty scripts folder, so the completion event would never come.
		var scriptsFolder = ProjectFile.ProjectManager.Instance.GetScriptsFolder();
		if (string.IsNullOrEmpty(scriptsFolder) || !Directory.Exists(scriptsFolder) || !Directory.EnumerateFiles(scriptsFolder, "*.cs", SearchOption.AllDirectories).Any())
			return Task.FromResult<object>(new { success = true, errors = new System.Collections.Generic.List<string>(), assembly = (string)null, componentTypes = new System.Collections.Generic.List<string>(), note = "no script files to compile" });

		var tcs = new TaskCompletionSource<object>();
		Action<CompilationResult, bool> onDone = null;
		onDone = (result, _) =>
		{
			manager.OnCompilationComplete -= onDone;
			tcs.TrySetResult(new
			{
				success = result.Success,
				errors = result.Errors ?? new System.Collections.Generic.List<string>(),
				assembly = result.Assembly?.GetName().Name,
				componentTypes = result.Success ? manager.GetScriptComponentTypes().Select(t => t.FullName).ToList() : null
			});
		};
		manager.OnCompilationComplete += onDone;

		try
		{
			manager.CompileScripts(reloadScene);
		}
		catch (Exception ex)
		{
			manager.OnCompilationComplete -= onDone;
			throw new GatewayException($"compile failed to start: {ex.Message}");
		}

		return GatewayTasks.WithTimeout(tcs, CompileTimeout, () => manager.OnCompilationComplete -= onDone, "compilation timed out");
	}

	private static object PlayState() => new
	{
		editMode = Core.IsEditMode,
		playing = !Core.IsEditMode && !Core.IsPauseMode,
		paused = !Core.IsEditMode && Core.IsPauseMode
	};
}
