using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Editor.Persistence;
using Voltage.Editor.Scripting;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Play mode, script compilation and game-view capture.</summary>
internal static class PlayScriptCommands
{
	private static readonly TimeSpan CompileTimeout = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(10);

	public static void Register(GatewayCommandTable table)
	{
		table.Add("play.state", "Edit, play or paused.", (_, _) => PlayState());

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

		table.Add("play.pause", "Pause or resume play mode. params: paused=true", (args, _) =>
		{
			if (Core.IsEditMode)
				throw new GatewayException("not in play mode");
			Core.InvokeSwitchPauseMode(args.Bool("paused", true));
			return PlayState();
		});

		table.Add("play.reset", "Reset the running scene to its saved state.", (_, _) =>
		{
			Core.InvokeResetScene();
			return PlayState();
		});

		table.Add("scripts.compile", "Compile the project's scripts; answers with diagnostics. params: reloadScene=false", (args, ctx) =>
		{
			var manager = ctx.ImGui.ScriptManager ?? throw new GatewayException("no project with a scripts folder is loaded");

			// The watcher does nothing at all for an empty scripts folder, so the completion event would never come.
			var scriptsFolder = ProjectFile.ProjectManager.Instance.GetScriptsFolder();
			if (string.IsNullOrEmpty(scriptsFolder) || !Directory.Exists(scriptsFolder) || !Directory.EnumerateFiles(scriptsFolder, "*.cs", SearchOption.AllDirectories).Any())
				return new { success = true, errors = new System.Collections.Generic.List<string>(), assembly = (string)null, componentTypes = new System.Collections.Generic.List<string>(), note = "no script files to compile" };

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
				manager.CompileScripts(args.Bool("reloadScene"));
			}
			catch (Exception ex)
			{
				manager.OnCompilationComplete -= onDone;
				throw new GatewayException($"compile failed to start: {ex.Message}");
			}

			return GatewayTasks.WithTimeout(tcs, CompileTimeout, () => manager.OnCompilationComplete -= onDone, "compilation timed out");
		});

		table.Add("scripts.types", "Component types defined by the compiled scripts.", (_, ctx) =>
		{
			var manager = ctx.ImGui.ScriptManager ?? throw new GatewayException("no project with a scripts folder is loaded");
			return manager.GetScriptComponentTypes().Select(t => t.FullName).OrderBy(n => n).ToList();
		});

		table.Add("screenshot", "Save a PNG after the next frame: the whole editor window by default, or only the game view. params: path (default: editor cache Screenshots folder), window=true, scale=1 (window only, 0.05-1), x, y, width, height (window-pixel crop, applied before scale)", (args, ctx) =>
		{
			var path = args.String("path");
			if (string.IsNullOrWhiteSpace(path))
			{
				var dir = Path.Combine(EditorStorage.CacheRoot, "Screenshots");
				Directory.CreateDirectory(dir);
				path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
			}
			path = Path.GetFullPath(path);

			if (args.Bool("window", true))
			{
				Microsoft.Xna.Framework.Rectangle? crop = null;
				if (args.Has("width") && args.Has("height"))
					crop = new Microsoft.Xna.Framework.Rectangle(args.Int("x"), args.Int("y"), args.Int("width"), args.Int("height"));
				return GatewayTasks.WithTimeout(ctx.Dispatcher.CaptureWindow(path, args.Float("scale", 1f), crop), ScreenshotTimeout, "no frame was drawn in time");
			}

			var scene = Core.Scene ?? throw new GatewayException("no scene loaded");
			var tcs = new TaskCompletionSource<object>();
			scene.RequestScreenshot(texture =>
			{
				try
				{
					using (texture)
					using (var stream = File.Create(path))
						texture.SaveAsPng(stream, texture.Width, texture.Height);
					tcs.TrySetResult(new { path, width = texture.Width, height = texture.Height });
				}
				catch (Exception ex)
				{
					tcs.TrySetException(new GatewayException($"screenshot failed: {ex.Message}"));
				}
			});

			return GatewayTasks.WithTimeout(tcs, ScreenshotTimeout, null, "no frame was drawn in time");
		});
	}

	private static object PlayState() => new
	{
		editMode = Core.IsEditMode,
		playing = !Core.IsEditMode && !Core.IsPauseMode,
		paused = !Core.IsEditMode && Core.IsPauseMode
	};
}
