using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Microsoft.Xna.Framework.Input;
using Voltage.Editor.Hotkeys;
using Voltage.Gateway;
using Voltage.Utils;
using static Voltage.Gateway.InputSimulator;

namespace Voltage.Editor.Gateway.Commands;

/// <summary>Editor hotkeys, window sizing and the game-view facts an agent needs to aim; the input.* commands themselves live in the engine.</summary>
internal static class InputCommands
{
	public static void Register(GatewayCommandTable table)
	{
		table.Add("ui.info", "Window size, cursor position, game-view placement and scale, focus, and whether synthetic input owns the devices.", (_, ctx) =>
		{
			var pp = Core.GraphicsDevice.PresentationParameters;
			var mouse = Input.CurrentMouseState;
			var scene = Core.Scene;
			var imGui = ctx.ImGui();
			return new
			{
				width = pp.BackBufferWidth,
				height = pp.BackBufferHeight,
				mouse = new { x = mouse.X, y = mouse.Y },
				scaledMouse = new { x = Input.ScaledMousePosition.X, y = Input.ScaledMousePosition.Y },
				mouseDelta = new { x = Input.MousePositionDelta.X, y = Input.MousePositionDelta.Y },
				gameView = new
				{
					x = imGui.GameWindowPosition.X,
					y = imGui.GameWindowPosition.Y,
					width = imGui.GameWindowSize.X,
					height = imGui.GameWindowSize.Y,
					renderTargetWidth = scene?.SceneRenderTargetSize.X,
					renderTargetHeight = scene?.SceneRenderTargetSize.Y,
					resolutionScale = new { x = Input.ResolutionScale.X, y = Input.ResolutionScale.Y },
					resolutionOffset = new { x = Input.ResolutionOffset.X, y = Input.ResolutionOffset.Y },
					prefabEditScene = imGui.IsInPrefabEditScene
				},
				focused = Core.Instance.IsActive,
				fps = Time.DeltaTime > 0 ? 1f / Time.DeltaTime : 0f,
				itemCount = UiRegistry.Items.Count,
				windowCount = UiRegistry.Windows.Count,
				input = ctx.Dispatcher.Input.State()
			};
		}).ReadOnly();

		table.Add("ui.resize", "Resize the editor window, for repeatable screenshot coordinates.", (args, _) =>
		{
			var width = args.Int("width");
			var height = args.Int("height");
			if (width < 320 || height < 240)
				throw new GatewayException("width and height must be at least 320x240");
			ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.Windowed);
			Screen.SetSize(width, height);
			return new { width, height };
		}, P.Int("width", "Window width, at least 320", required: true), P.Int("height", "Window height, at least 240", required: true));

		table.Add("ui.maximize", "Size the editor window to the usable screen area.", (_, _) =>
		{
			ScreenUtils.ApplyScreenChange(ScreenUtils.ScreenMode.WindowedMax);
			Utils.EditorWindowLayout.FitToUsableBounds();
			var pp = Core.GraphicsDevice.PresentationParameters;
			return new { width = pp.BackBufferWidth, height = pp.BackBufferHeight };
		});

		table.Add("hotkey.list", "Editor hotkeys with their current bindings.", (_, _) =>
			EditorHotkeys.Actions.Select(a => new
			{
				id = a.Id,
				category = a.Category,
				label = a.Label,
				primary = a.Primary.IsBound ? a.Primary.ToString() : null,
				alternate = a.Alternate.IsBound ? a.Alternate.ToString() : null
			}).ToList()).ReadOnly();

		table.Add("hotkey.press", "Press an editor hotkey by id, e.g. Global.SaveScene.", (args, ctx) =>
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
			return ctx.Dispatcher.Input.Enqueue(KeyPress(FromImGuiKey(binding.Key), modifiers));
		}, P.Str("id", "Hotkey id from hotkey.list", required: true)).Unsafe();
	}

	/// <summary>Maps an ImGui key from a hotkey binding to the XNA key the editor's key map feeds it from.</summary>
	public static Keys FromImGuiKey(ImGuiKey key)
	{
		switch (key)
		{
			case ImGuiKey.Tab: return Keys.Tab;
			case ImGuiKey.LeftArrow: return Keys.Left;
			case ImGuiKey.RightArrow: return Keys.Right;
			case ImGuiKey.UpArrow: return Keys.Up;
			case ImGuiKey.DownArrow: return Keys.Down;
			case ImGuiKey.PageUp: return Keys.PageUp;
			case ImGuiKey.PageDown: return Keys.PageDown;
			case ImGuiKey.Home: return Keys.Home;
			case ImGuiKey.End: return Keys.End;
			case ImGuiKey.Delete: return Keys.Delete;
			case ImGuiKey.Backspace: return Keys.Back;
			case ImGuiKey.Enter: return Keys.Enter;
			case ImGuiKey.Escape: return Keys.Escape;
			case ImGuiKey.LeftCtrl: return Keys.LeftControl;
			case ImGuiKey.RightCtrl: return Keys.RightControl;
			case ImGuiKey.LeftShift: return Keys.LeftShift;
			case ImGuiKey.RightShift: return Keys.RightShift;
			case ImGuiKey.LeftAlt: return Keys.LeftAlt;
			case ImGuiKey.RightAlt: return Keys.RightAlt;
		}

		var name = key.ToString();
		if (name.Length == 2 && name[0] == '_' && char.IsDigit(name[1]))
			return Keys.D0 + (name[1] - '0');
		if (System.Enum.TryParse<Keys>(name, out var mapped))
			return mapped;
		throw new GatewayException($"hotkey key {key} has no keyboard mapping");
	}
}
