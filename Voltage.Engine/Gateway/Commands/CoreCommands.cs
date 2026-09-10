using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Graphics;
using Voltage.Console;
using Voltage.Utils;
using static Voltage.Gateway.InputSimulator;

namespace Voltage.Gateway.Commands;

/// <summary>Commands every host answers: liveness, logs, console, screenshots, synthetic input, events and batches.</summary>
public static class CoreCommands
{
	private static readonly TimeSpan ScreenshotTimeout = TimeSpan.FromSeconds(10);

	public static void Register(GatewayCommandTable table)
	{
		table.Add("ping", "Round-trip check.", (_, _) => new { pong = true, time = DateTime.UtcNow }).ReadOnly();

		table.Add("commands", "List every gateway command with its help text, declared parameters and flags.", (_, ctx) =>
			ctx.Commands.All.Select(c => c.Describe()).ToList()).ReadOnly();

		table.Add("status", "Host, scene, play state and frame timing.", (_, ctx) => Status(ctx)).ReadOnly();

		table.Add("app.exit", "Quit the host process; in the editor this is the same as editor.exit without force.", (_, _) =>
		{
			Core.Exit();
			return new { exiting = true };
		}).Destructive().Unsafe();

		table.Add("console.exec", "Run a debug-console line.", (args, _) =>
		{
			var console = DebugConsole.Instance ?? throw new GatewayException("debug console is not initialized");
			return new { lines = console.Execute(args.Require("line")) };
		}, P.Str("line", "Console command line", required: true)).Unsafe();

		table.Add("console.commands", "Names of the debug-console commands.", (_, _) =>
			(DebugConsole.Instance?.CommandNames ?? Enumerable.Empty<string>()).OrderBy(n => n).ToList()).ReadOnly();

		table.Add("log.tail", "Most recent log entries.", (args, _) =>
		{
			var count = Math.Clamp(args.Int("count", 50), 1, 500);
			var level = args.String("level");
			IEnumerable<Debug.LogEntry> entries = Debug.GetLogEntries();
			if (!string.IsNullOrEmpty(level) && Enum.TryParse<Debug.LogType>(level, true, out var type))
				entries = entries.Where(e => e.Type == type);
			return entries.TakeLast(count).Select(LogEntry).ToList();
		}, P.Int("count", "Entries to return, 1-500", 50), P.Enum("level", "Only entries of this type", new[] { "Error", "Warn", "Log", "Info", "Trace", "Success" })).ReadOnly();

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

		table.Add("log.clear", "Clear the log buffer.", (_, _) =>
		{
			Debug.ClearLogEntries();
			return new { cleared = true };
		}).Destructive();

		table.Add("events.subscribe", "Stream lifecycle events (scene, project, play mode, compile, selection) to this connection as 'editor' events.", (_, ctx) =>
		{
			ctx.Client.EventsSubscribed = true;
			return new { subscribed = true };
		});

		table.Add("events.unsubscribe", "Stop streaming lifecycle events.", (_, ctx) =>
		{
			ctx.Client.EventsSubscribed = false;
			return new { subscribed = false };
		});

		table.Add("wait.event", "Answer with the next matching event, or fail after the timeout.", (args, ctx) =>
			ctx.Dispatcher.WaitForEvent(args.Require("name"), TimeSpan.FromSeconds(Math.Clamp(args.Float("timeout", 30f), 0.01f, 3600f))),
			P.Str("name", "Event name, 'group.*' prefix or '*'", required: true),
			P.Float("timeout", "Seconds to wait", 30f)).ReadOnly();

		table.Add("batch", "Run several requests in order and answer once with every result.", (args, ctx) =>
		{
			if (!args.TryGet("requests", out var element) || element.ValueKind != JsonValueKind.Array)
				throw new GatewayException("missing parameter 'requests' (array of {method, params})");

			var requests = new List<(string, JsonElement)>();
			foreach (var item in element.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
					throw new GatewayException("each request needs a 'method' string");
				requests.Add((method.GetString(), item.TryGetProperty("params", out var p) ? p.Clone() : default));
			}

			return ctx.Dispatcher.RunBatch(ctx, requests, args.Bool("stopOnError", true));
		}, P.List("requests", "Requests as {method, params} objects", "object", true), P.Bool("stopOnError", "Stop at the first failure", true));

		table.Add("screenshot", "Save a PNG after the next frame: the whole window by default, or only the game render.", (args, ctx) =>
		{
			var path = args.String("path");
			var dir = ctx.Dispatcher.ScreenshotDirectory;
			if (string.IsNullOrWhiteSpace(path))
			{
				Directory.CreateDirectory(dir);
				path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
			}
			path = ctx.Dispatcher.Options.Safe ? GatewayPaths.RequireInside(path, dir, "screenshot path") : Path.GetFullPath(path);

			if (args.Bool("window", true))
			{
				Microsoft.Xna.Framework.Rectangle? crop = null;
				if (args.Has("width") && args.Has("height"))
					crop = new Microsoft.Xna.Framework.Rectangle(args.Int("x"), args.Int("y"), args.Int("width"), args.Int("height"));
				return GatewayTasks.WithTimeout(ctx.Dispatcher.CaptureWindow(path, args.Float("scale", 1f), crop), ScreenshotTimeout, "no frame was drawn in time");
			}

			var scene = Core.Scene ?? throw new GatewayException("no scene loaded");
			var scale = Math.Clamp(args.Float("scale", 1f), 0.05f, 1f);
			var tcs = new TaskCompletionSource<object>();
			scene.RequestScreenshot(texture =>
			{
				try
				{
					using (texture)
					{
						var pixels = new Microsoft.Xna.Framework.Color[texture.Width * texture.Height];
						texture.GetData(pixels);
						var (data, w, h) = GatewayDispatcher.Downscale(pixels, texture.Width, texture.Height, scale);
						using var scaled = new Texture2D(texture.GraphicsDevice, w, h);
						scaled.SetData(data);
						using (var stream = File.Create(path))
							scaled.SaveAsPng(stream, w, h);
						tcs.TrySetResult(new { path, width = w, height = h, scale, renderWidth = texture.Width, renderHeight = texture.Height });
					}
				}
				catch (Exception ex)
				{
					tcs.TrySetException(new GatewayException($"screenshot failed: {ex.Message}"));
				}
			});

			return GatewayTasks.WithTimeout(tcs, ScreenshotTimeout, null, "no frame was drawn in time");
		},
			P.Str("path", "Output file; default is a timestamped file in the host's Screenshots folder"),
			P.Bool("window", "Whole window with UI (true) or the game render only", true),
			P.Float("scale", "Capture scale, 0.05-1", 1f),
			P.Int("x", "Crop left edge in window pixels, applied before scale"),
			P.Int("y", "Crop top edge in window pixels"),
			P.Int("width", "Crop width; needs height"),
			P.Int("height", "Crop height; needs width"));

		table.Add("ui.info", "Window size, cursor position, focus and whether synthetic input owns the devices.", (_, ctx) =>
		{
			var pp = Core.GraphicsDevice.PresentationParameters;
			var mouse = Voltage.Input.CurrentMouseState;
			return new
			{
				width = pp.BackBufferWidth,
				height = pp.BackBufferHeight,
				mouse = new { x = mouse.X, y = mouse.Y },
				scaledMouse = new { x = Voltage.Input.ScaledMousePosition.X, y = Voltage.Input.ScaledMousePosition.Y },
				mouseDelta = new { x = Voltage.Input.MousePositionDelta.X, y = Voltage.Input.MousePositionDelta.Y },
				focused = Core.Instance.IsActive,
				fps = Time.DeltaTime > 0 ? 1f / Time.DeltaTime : 0f,
				input = ctx.Dispatcher.Input.State()
			};
		}).ReadOnly();

		RegisterInput(table);
	}

	private static void RegisterInput(GatewayCommandTable table)
	{
		table.Add("input.state", "Synthetic input state: captured flag, virtual cursor and its last delta, held buttons and keys, whether the last script double-clicked.", (_, ctx) => ctx.Dispatcher.Input.State()).ReadOnly();

		table.Add("input.release", "Hand the mouse and keyboard back to the user.", (_, ctx) =>
		{
			ctx.Dispatcher.Input.Release();
			return ctx.Dispatcher.Input.State();
		});

		table.Add("input.move", "Move the cursor.", (args, ctx) =>
			Run(ctx, new MoveStep(args.Int("x"), args.Int("y")), new WaitStep(1)),
			P.Int("x", "Window pixels", 0), P.Int("y", "Window pixels", 0));

		table.Add("input.click", "Click at a point, or at the current cursor.", (args, ctx) =>
		{
			var (x, y) = Point(args);
			return Run(ctx, Click(x, y, ParseButton(args.String("button")), args.Int("count", 1)).ToArray());
		}, X, Y, ButtonParam, P.Int("count", "Clicks; 2 double-clicks", 1));

		table.Add("input.down", "Press and hold a mouse button.", (args, ctx) =>
		{
			var (x, y) = Point(args);
			return Run(ctx, new MoveStep(x, y), new WaitStep(1), new ButtonStep(ParseButton(args.String("button")), true), new WaitStep(1));
		}, ButtonParam, X, Y);

		table.Add("input.up", "Release a mouse button.", (args, ctx) =>
		{
			var (x, y) = Point(args);
			return Run(ctx, new MoveStep(x, y), new WaitStep(1), new ButtonStep(ParseButton(args.String("button")), false), new WaitStep(1));
		}, ButtonParam, X, Y);

		table.Add("input.hover", "Park the cursor over a point long enough for a tooltip to open.", (args, ctx) =>
		{
			var (x, y) = Point(args);
			return Run(ctx, Hover(x, y, args.Int("frames", 10)).ToArray());
		}, X, Y, P.Int("frames", "Frames to hold", 10));

		table.Add("input.drag", "Press at one point and release at another.", (args, ctx) =>
			Run(ctx, Drag(args.Int("x1"), args.Int("y1"), args.Int("x2"), args.Int("y2"), ParseButton(args.String("button")), args.Int("frames", 12)).ToArray()),
			P.Int("x1", "Start, window pixels", 0), P.Int("y1", "Start", 0), P.Int("x2", "End", 0), P.Int("y2", "End", 0), ButtonParam, P.Int("frames", "Frames the move is spread over", 12));

		table.Add("input.scroll", "Scroll the wheel; positive is up.", (args, ctx) =>
		{
			var (x, y) = Point(args);
			return Run(ctx, new MoveStep(x, y), new WaitStep(1), new ScrollStep(args.Int("notches", 1)), new WaitStep(2));
		}, P.Int("notches", "Wheel notches; negative scrolls down", 1), X, Y);

		table.Add("input.key", "Press a key with optional modifiers.", (args, ctx) =>
		{
			var key = ParseKey(args.Require("key"));
			var modifiers = args.Strings("modifiers").Select(ParseKey).ToList();
			return args.String("action", "press") switch
			{
				"down" => Run(ctx, modifiers.Select(m => (Step)new KeyStep(m, true)).Append(new KeyStep(key, true)).Append(new WaitStep(1)).ToArray()),
				"up" => Run(ctx, new Step[] { new KeyStep(key, false) }.Concat(modifiers.Select(m => (Step)new KeyStep(m, false))).Append(new WaitStep(1)).ToArray()),
				_ => Run(ctx, KeyPress(key, modifiers).ToArray())
			};
		}, P.Str("key", "Key name: a, F5, enter, esc, ctrl or any Keys enum name", required: true),
			P.List("modifiers", "Held modifiers such as [\"ctrl\",\"shift\"]"),
			P.Enum("action", "Tap, or hold and release separately", new[] { "press", "down", "up" }, "press"));

		table.Add("input.type", "Type text into the focused widget; newlines press Enter.", (args, ctx) =>
			Run(ctx, Type(args.Require("text")).ToArray()), P.Str("text", "Text to type", required: true));

		table.Add("input.wait", "Let the host run for some frames.", (args, ctx) =>
			Run(ctx, new WaitStep(Math.Max(1, args.Int("frames", 1)))), P.Int("frames", "Frames to wait", 1));

		table.Add("input.script", "Run a list of input steps in order; a failed step drops the rest of the script.", (args, ctx) =>
		{
			if (!args.TryGet("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
				throw new GatewayException("missing parameter 'steps' (array)");

			var steps = new List<Step>();
			foreach (var element in stepsElement.EnumerateArray())
				steps.AddRange(ParseStep(new GatewayArgs(element)));
			return ctx.Dispatcher.Input.Enqueue(steps);
		}, P.List("steps", "Objects with action = move|click|hover|drag|down|up|scroll|key|keyDown|keyUp|type|wait|waitEvent|release and the same fields as the matching input.* command; waitEvent takes name and timeout (seconds, default 30)", "object", true));

		table.Add("input.record", "Record the mouse, keyboard and typed text as input.script steps for voltage run; synthetic input is recorded too.", (args, ctx) =>
		{
			var recorder = ctx.Dispatcher.Recorder;
			switch (args.String("action", "status").ToLowerInvariant())
			{
				case "start":
					recorder.Start(args.Bool("mouse", true), args.Bool("keyboard", true), args.Bool("text", true));
					return new { recording = true };
				case "stop":
					recorder.Stop();
					return recorder.State(true);
				case "status":
					return recorder.State(args.Bool("steps"));
				default:
					throw new GatewayException("action must be start, stop or status");
			}
		}, P.Enum("action", "Start, stop, or report the recording so far", new[] { "start", "stop", "status" }, "status"),
			P.Bool("mouse", "Record cursor moves, buttons and wheel", true),
			P.Bool("keyboard", "Record key presses and releases", true),
			P.Bool("text", "Record typed characters", true),
			P.Bool("steps", "Include the steps so far in a status reply", false));

		// Synthetic input reaches every menu, so the safe profile withholds it along with the actions it could trigger.
		foreach (var name in new[] { "input.move", "input.click", "input.down", "input.up", "input.hover", "input.drag", "input.scroll", "input.key", "input.type", "input.script" })
			if (table.TryGet(name, out var command))
				command.Unsafe();
	}

	private static readonly GatewayParam X = P.Int("x", "Window pixels; the current cursor when omitted");
	private static readonly GatewayParam Y = P.Int("y", "Window pixels; the current cursor when omitted");
	private static readonly GatewayParam ButtonParam = P.Enum("button", "Mouse button", new[] { "left", "right", "middle" }, "left");

	private static object Status(GatewayContext ctx)
	{
		var dt = Time.DeltaTime;
		return new
		{
			host = ctx.Dispatcher.Options.Host,
			game = ctx.Dispatcher.Options.Name,
			version = VoltageVersion.Engine,
			pid = Environment.ProcessId,
			scene = Core.Scene == null ? null : new { name = Core.Scene.Name, type = Core.Scene.GetType().Name },
			editMode = Core.IsEditMode,
			pauseMode = Core.IsPauseMode,
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

	private static object Run(GatewayContext ctx, params Step[] steps) => ctx.Dispatcher.Input.Enqueue(steps);

	/// <summary>Explicit coordinates, else the physical cursor.</summary>
	public static (int x, int y) Point(GatewayArgs args)
	{
		if (args.Has("x") && args.Has("y"))
			return (args.Int("x"), args.Int("y"));

		var mouse = Voltage.Input.CurrentMouseState;
		return (args.Int("x", mouse.X), args.Int("y", mouse.Y));
	}

	public static Button ParseButton(string name) => (name ?? "left").ToLowerInvariant() switch
	{
		"left" or "" => Button.Left,
		"right" => Button.Right,
		"middle" => Button.Middle,
		_ => throw new GatewayException($"unknown button '{name}'")
	};

	/// <summary>One input.script step to simulator steps.</summary>
	public static IEnumerable<Step> ParseStep(GatewayArgs step)
	{
		var action = step.String("action", "").ToLowerInvariant();
		switch (action)
		{
			case "move":
				return new Step[] { new MoveStep(step.Int("x"), step.Int("y")), new WaitStep(1) };
			case "click":
			{
				var (x, y) = Point(step);
				return Click(x, y, ParseButton(step.String("button")), step.Int("count", 1));
			}
			case "hover":
			{
				var (x, y) = Point(step);
				return Hover(x, y, step.Int("frames", 10));
			}
			case "waitevent":
				return new Step[] { new WaitEventStep(step.Require("name"), TimeSpan.FromSeconds(step.Double("timeout") ?? 30)) };
			case "drag":
				return Drag(step.Int("x1"), step.Int("y1"), step.Int("x2"), step.Int("y2"), ParseButton(step.String("button")), step.Int("frames", 12));
			case "down":
			case "up":
			{
				var (x, y) = Point(step);
				return new Step[] { new MoveStep(x, y), new WaitStep(1), new ButtonStep(ParseButton(step.String("button")), action == "down"), new WaitStep(1) };
			}
			case "scroll":
			{
				var (x, y) = Point(step);
				return new Step[] { new MoveStep(x, y), new WaitStep(1), new ScrollStep(step.Int("notches", 1)), new WaitStep(2) };
			}
			case "key":
				return KeyPress(ParseKey(step.Require("key")), step.Strings("modifiers").Select(ParseKey).ToList());
			case "keydown":
				return new Step[] { new KeyStep(ParseKey(step.Require("key")), true), new WaitStep(1) };
			case "keyup":
				return new Step[] { new KeyStep(ParseKey(step.Require("key")), false), new WaitStep(1) };
			case "type":
				return Type(step.Require("text"));
			case "wait":
				return new Step[] { new WaitStep(Math.Max(1, step.Int("frames", 1))) };
			case "release":
				return new Step[] { new ReleaseStep() };
			default:
				throw new GatewayException($"unknown step action '{action}'");
		}
	}
}
