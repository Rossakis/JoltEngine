using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework.Input;
using Voltage.Editor.Hotkeys;
using Voltage.Utils;
using static Voltage.Editor.Gateway.InputSimulator;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Synthetic mouse and keyboard, editor hotkeys, and the window facts an agent needs to aim.</summary>
internal static class InputCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("ui.info", "Window size, cursor position, game-view placement and scale, focus, and whether synthetic input owns the devices.", (_, ctx) =>
		{
			var pp = Core.GraphicsDevice.PresentationParameters;
			var mouse = Input.CurrentMouseState;
			var scene = Core.Scene;
			return new
			{
				width = pp.BackBufferWidth,
				height = pp.BackBufferHeight,
				mouse = new { x = mouse.X, y = mouse.Y },
				scaledMouse = new { x = Input.ScaledMousePosition.X, y = Input.ScaledMousePosition.Y },
				gameView = new
				{
					x = ctx.ImGui.GameWindowPosition.X,
					y = ctx.ImGui.GameWindowPosition.Y,
					width = ctx.ImGui.GameWindowSize.X,
					height = ctx.ImGui.GameWindowSize.Y,
					renderTargetWidth = scene?.SceneRenderTargetSize.X,
					renderTargetHeight = scene?.SceneRenderTargetSize.Y,
					resolutionScale = new { x = Input.ResolutionScale.X, y = Input.ResolutionScale.Y },
					resolutionOffset = new { x = Input.ResolutionOffset.X, y = Input.ResolutionOffset.Y },
					prefabEditScene = ctx.ImGui.IsInPrefabEditScene
				},
				focused = Core.Instance.IsActive,
				fps = Time.DeltaTime > 0 ? 1f / Time.DeltaTime : 0f,
				input = ctx.Dispatcher.Input.State()
			};
		});

		table.Add("ui.resize", "Resize the editor window, for repeatable screenshot coordinates. params: width, height", (args, _) =>
		{
			var width = args.Int("width");
			var height = args.Int("height");
			if (width < 320 || height < 240)
				throw new GatewayException("width and height must be at least 320x240");
			ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.Windowed);
			Screen.SetSize(width, height);
			return new { width, height };
		});

		table.Add("ui.maximize", "Size the editor window to the usable screen area.", (_, _) =>
		{
			ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.WindowedMax);
			Utils.EditorWindowLayout.FitToUsableBounds();
			var pp = Core.GraphicsDevice.PresentationParameters;
			return new { width = pp.BackBufferWidth, height = pp.BackBufferHeight };
		});

		table.Add("input.state", "Synthetic input state: captured flag, virtual cursor, held buttons and keys.", (_, ctx) => ctx.Dispatcher.Input.State());

		table.Add("input.release", "Hand the mouse and keyboard back to the user.", (_, ctx) =>
		{
			ctx.Dispatcher.Input.Release();
			return ctx.Dispatcher.Input.State();
		});

		table.Add("input.move", "Move the cursor. params: x, y", (args, ctx) =>
			Run(ctx, new MoveStep(args.Int("x"), args.Int("y")), new WaitStep(1)));

		table.Add("input.click", "Click at a point, or at the current cursor. ImGui widgets see every click; the engine's own double-click and mouse-delta helpers only track the physical mouse. params: x, y, button=left|right|middle, count=1", (args, ctx) =>
		{
			var (x, y) = Point(args, ctx);
			return Run(ctx, Click(x, y, ParseButton(args.String("button")), args.Int("count", 1)).ToArray());
		});

		table.Add("input.down", "Press and hold a mouse button. params: button=left, x, y", (args, ctx) =>
		{
			var (x, y) = Point(args, ctx);
			return Run(ctx, new MoveStep(x, y), new WaitStep(1), new ButtonStep(ParseButton(args.String("button")), true), new WaitStep(1));
		});

		table.Add("input.up", "Release a mouse button. params: button=left, x, y", (args, ctx) =>
		{
			var (x, y) = Point(args, ctx);
			return Run(ctx, new MoveStep(x, y), new WaitStep(1), new ButtonStep(ParseButton(args.String("button")), false), new WaitStep(1));
		});

		table.Add("input.drag", "Press at one point and release at another. params: x1, y1, x2, y2, button=left, frames=12", (args, ctx) =>
			Run(ctx, Drag(args.Int("x1"), args.Int("y1"), args.Int("x2"), args.Int("y2"), ParseButton(args.String("button")), args.Int("frames", 12)).ToArray()));

		table.Add("input.scroll", "Scroll the wheel; positive is up. params: notches, x, y", (args, ctx) =>
		{
			var (x, y) = Point(args, ctx);
			return Run(ctx, new MoveStep(x, y), new WaitStep(1), new ScrollStep(args.Int("notches", 1)), new WaitStep(2));
		});

		table.Add("input.key", "Press a key with optional modifiers. params: key, modifiers (list such as [\"ctrl\",\"shift\"]), action=press|down|up", (args, ctx) =>
		{
			var key = ParseKey(args.Require("key"));
			var modifiers = args.Strings("modifiers").Select(ParseKey).ToList();
			return args.String("action", "press") switch
			{
				"down" => Run(ctx, modifiers.Select(m => (Step)new KeyStep(m, true)).Append(new KeyStep(key, true)).Append(new WaitStep(1)).ToArray()),
				"up" => Run(ctx, new Step[] { new KeyStep(key, false) }.Concat(modifiers.Select(m => (Step)new KeyStep(m, false))).Append(new WaitStep(1)).ToArray()),
				_ => Run(ctx, KeyPress(key, modifiers).ToArray())
			};
		});

		table.Add("input.type", "Type text into the focused widget; newlines press Enter. params: text", (args, ctx) =>
			Run(ctx, Type(args.Require("text")).ToArray()));

		table.Add("input.wait", "Let the editor run for some frames. params: frames=1", (args, ctx) =>
			Run(ctx, new WaitStep(Math.Max(1, args.Int("frames", 1)))));

		table.Add("input.script", "Run a list of steps in order. params: steps = [{action: move|click|drag|down|up|scroll|key|type|wait|release, ...same fields as the input.* commands}]", (args, ctx) =>
		{
			if (!args.TryGet("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
				throw new GatewayException("missing parameter 'steps' (array)");

			var steps = new List<Step>();
			foreach (var element in stepsElement.EnumerateArray())
				steps.AddRange(Parse(new GatewayArgs(element), ctx));
			return ctx.Dispatcher.Input.Enqueue(steps);
		});

		table.Add("hotkey.list", "Editor hotkeys with their current bindings.", (_, _) =>
			EditorHotkeys.Actions.Select(a => new
			{
				id = a.Id,
				category = a.Category,
				label = a.Label,
				primary = a.Primary.IsBound ? a.Primary.ToString() : null,
				alternate = a.Alternate.IsBound ? a.Alternate.ToString() : null
			}).ToList());

		table.Add("hotkey.press", "Press an editor hotkey by id, e.g. Global.SaveScene. params: id", (args, ctx) =>
		{
			var id = args.Require("id");
			var action = EditorHotkeys.Find(id) ?? throw new GatewayException($"unknown hotkey '{id}'; see hotkey.list");
			var binding = action.Primary.IsBound ? action.Primary : action.Alternate;
			if (!binding.IsBound)
				throw new GatewayException($"hotkey '{id}' is unbound");

			var modifiers = new List<Keys>();
			if (binding.Ctrl) modifiers.Add(Keys.LeftControl);
			if (binding.Shift) modifiers.Add(Keys.LeftShift);
			if (binding.Alt) modifiers.Add(Keys.LeftAlt);
			return Run(ctx, KeyPress(FromImGuiKey(binding.Key), modifiers).ToArray());
		});
	}

	private static object Run(GatewayContext ctx, params Step[] steps) => ctx.Dispatcher.Input.Enqueue(steps);

	private static (int x, int y) Point(GatewayArgs args, GatewayContext ctx)
	{
		if (args.Has("x") && args.Has("y"))
			return (args.Int("x"), args.Int("y"));

		var mouse = Input.CurrentMouseState;
		return (args.Int("x", mouse.X), args.Int("y", mouse.Y));
	}

	private static Button ParseButton(string name) => (name ?? "left").ToLowerInvariant() switch
	{
		"left" or "" => Button.Left,
		"right" => Button.Right,
		"middle" => Button.Middle,
		_ => throw new GatewayException($"unknown button '{name}'")
	};

	private static IEnumerable<Step> Parse(GatewayArgs step, GatewayContext ctx)
	{
		var action = step.String("action", "").ToLowerInvariant();
		switch (action)
		{
			case "move":
				return new Step[] { new MoveStep(step.Int("x"), step.Int("y")), new WaitStep(1) };
			case "click":
			{
				var (x, y) = Point(step, ctx);
				return Click(x, y, ParseButton(step.String("button")), step.Int("count", 1));
			}
			case "drag":
				return Drag(step.Int("x1"), step.Int("y1"), step.Int("x2"), step.Int("y2"), ParseButton(step.String("button")), step.Int("frames", 12));
			case "down":
			case "up":
			{
				var (x, y) = Point(step, ctx);
				return new Step[] { new MoveStep(x, y), new WaitStep(1), new ButtonStep(ParseButton(step.String("button")), action == "down"), new WaitStep(1) };
			}
			case "scroll":
			{
				var (x, y) = Point(step, ctx);
				return new Step[] { new MoveStep(x, y), new WaitStep(1), new ScrollStep(step.Int("notches", 1)), new WaitStep(2) };
			}
			case "key":
				return KeyPress(ParseKey(step.Require("key")), step.Strings("modifiers").Select(ParseKey).ToList());
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
