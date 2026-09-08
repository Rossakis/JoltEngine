using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Voltage.Editor.Gateway;

/// <summary>Replays scripted mouse and keyboard steps through the engine's Input overrides, one step per frame, so ImGui and the game see them as real input. Once captured it owns both devices until <see cref="Release"/>.</summary>
internal sealed class InputSimulator
{
	public enum Button { Left, Right, Middle }

	public abstract record Step;

	public sealed record MoveStep(int X, int Y) : Step;

	public sealed record ButtonStep(Button Button, bool Down) : Step;

	public sealed record KeyStep(Keys Key, bool Down) : Step;

	public sealed record TextStep(string Text) : Step;

	public sealed record ScrollStep(int Notches) : Step;

	public sealed record WaitStep(int Frames) : Step;

	public sealed record ReleaseStep : Step;

	private readonly Queue<(Step step, TaskCompletionSource<object> done)> _queue = new();
	private readonly HashSet<Keys> _keys = new();
	private readonly List<uint> _chars = new();
	private TaskCompletionSource<object> _waitDone;
	private int _waitFrames;
	private Point _pos;
	private bool _left, _right, _middle;
	private int _scroll;

	public bool Captured { get; private set; }

	public int PendingSteps => _queue.Count;

	/// <summary>Queues steps in order; the task completes with <see cref="State"/> once the last one has been applied.</summary>
	public Task<object> Enqueue(IReadOnlyList<Step> steps)
	{
		var done = new TaskCompletionSource<object>();
		if (steps.Count == 0)
		{
			done.SetResult(State());
			return done.Task;
		}

		for (var i = 0; i < steps.Count; i++)
			_queue.Enqueue((steps[i], i == steps.Count - 1 ? done : null));
		return done.Task;
	}

	/// <summary>Main thread, after Input.Update and before ImGui reads the frame's input.</summary>
	public void Apply()
	{
		if (_waitFrames > 0 && --_waitFrames == 0)
		{
			_waitDone?.TrySetResult(State());
			_waitDone = null;
		}

		while (_waitFrames == 0 && _queue.Count > 0)
		{
			var (step, done) = _queue.Dequeue();
			Execute(step);

			if (step is WaitStep && _waitFrames > 0)
				_waitDone = done;
			else
				done?.TrySetResult(State());
		}

		if (!Captured)
			return;

		Input.SetCurrentMouseState(new MouseState(_pos.X, _pos.Y, _scroll,
			_left ? ButtonState.Pressed : ButtonState.Released,
			_middle ? ButtonState.Pressed : ButtonState.Released,
			_right ? ButtonState.Pressed : ButtonState.Released,
			ButtonState.Released, ButtonState.Released));
		Input.SetCurrentKeyboardState(new KeyboardState(_keys.ToArray()));

		if (_chars.Count > 0)
		{
			var io = ImGui.GetIO();
			foreach (var c in _chars)
				io.AddInputCharacter(c);
			_chars.Clear();
		}
	}

	public void Release()
	{
		Captured = false;
		_keys.Clear();
		_chars.Clear();
		_left = _right = _middle = false;
	}

	public object State() => new
	{
		captured = Captured,
		x = _pos.X,
		y = _pos.Y,
		left = _left,
		right = _right,
		middle = _middle,
		keysDown = _keys.Select(k => k.ToString()).ToList(),
		pendingSteps = _queue.Count
	};

	private void Execute(Step step)
	{
		switch (step)
		{
			case MoveStep m:
				Capture();
				_pos = new Point(m.X, m.Y);
				break;
			case ButtonStep b:
				Capture();
				if (b.Button == Button.Left) _left = b.Down;
				else if (b.Button == Button.Right) _right = b.Down;
				else _middle = b.Down;
				break;
			case KeyStep k:
				Capture();
				if (k.Down) _keys.Add(k.Key);
				else _keys.Remove(k.Key);
				break;
			case TextStep t:
				Capture();
				foreach (var rune in t.Text.EnumerateRunes())
					_chars.Add((uint)rune.Value);
				break;
			case ScrollStep s:
				Capture();
				_scroll += s.Notches * 120;
				break;
			case WaitStep w:
				_waitFrames = Math.Max(0, w.Frames);
				break;
			case ReleaseStep:
				Release();
				break;
		}
	}

	/// <summary>Starts from the hardware cursor so the first synthetic move is relative to what the user sees.</summary>
	private void Capture()
	{
		if (Captured)
			return;

		Captured = true;
		var mouse = Input.CurrentMouseState;
		_pos = new Point(mouse.X, mouse.Y);
		_scroll = mouse.ScrollWheelValue;
		_left = _right = _middle = false;
		_keys.Clear();
	}

	/// <summary>Parses "ctrl", "enter", "a", "F5", "D3" and every Keys enum name.</summary>
	public static Keys ParseKey(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new GatewayException("missing key name");

		switch (name.Trim().ToLowerInvariant())
		{
			case "ctrl": case "control": return Keys.LeftControl;
			case "shift": return Keys.LeftShift;
			case "alt": return Keys.LeftAlt;
			case "win": case "super": case "cmd": case "meta": return Keys.LeftWindows;
			case "enter": case "return": return Keys.Enter;
			case "esc": case "escape": return Keys.Escape;
			case "backspace": case "back": return Keys.Back;
			case "del": case "delete": return Keys.Delete;
			case "ins": case "insert": return Keys.Insert;
			case "space": return Keys.Space;
			case "tab": return Keys.Tab;
			case "up": return Keys.Up;
			case "down": return Keys.Down;
			case "left": return Keys.Left;
			case "right": return Keys.Right;
			case "pgup": case "pageup": return Keys.PageUp;
			case "pgdn": case "pagedown": return Keys.PageDown;
			case "home": return Keys.Home;
			case "end": return Keys.End;
		}

		var trimmed = name.Trim();
		if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
			return Keys.D0 + (trimmed[0] - '0');
		if (Enum.TryParse<Keys>(trimmed, true, out var key))
			return key;
		throw new GatewayException($"unknown key '{name}'");
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
		if (Enum.TryParse<Keys>(name, out var mapped))
			return mapped;
		throw new GatewayException($"hotkey key {key} has no keyboard mapping");
	}

	/// <summary>Move, settle, press, settle, release, settle: the spacing ImGui needs to register a click.</summary>
	public static List<Step> Click(int x, int y, Button button, int count)
	{
		var steps = new List<Step> { new MoveStep(x, y), new WaitStep(2) };
		for (var i = 0; i < Math.Max(1, count); i++)
		{
			steps.Add(new ButtonStep(button, true));
			steps.Add(new WaitStep(2));
			steps.Add(new ButtonStep(button, false));
			steps.Add(new WaitStep(2));
		}
		return steps;
	}

	public static List<Step> Drag(int x1, int y1, int x2, int y2, Button button, int frames)
	{
		frames = Math.Max(2, frames);
		var steps = new List<Step> { new MoveStep(x1, y1), new WaitStep(2), new ButtonStep(button, true), new WaitStep(2) };
		for (var i = 1; i <= frames; i++)
		{
			var t = i / (float)frames;
			steps.Add(new MoveStep((int)MathF.Round(x1 + (x2 - x1) * t), (int)MathF.Round(y1 + (y2 - y1) * t)));
			steps.Add(new WaitStep(1));
		}
		steps.Add(new WaitStep(2));
		steps.Add(new ButtonStep(button, false));
		steps.Add(new WaitStep(2));
		return steps;
	}

	/// <summary>Holds modifiers, taps the key, releases in reverse.</summary>
	public static List<Step> KeyPress(Keys key, IReadOnlyList<Keys> modifiers)
	{
		var steps = new List<Step>();
		foreach (var m in modifiers)
		{
			steps.Add(new KeyStep(m, true));
			steps.Add(new WaitStep(1));
		}
		steps.Add(new KeyStep(key, true));
		steps.Add(new WaitStep(2));
		steps.Add(new KeyStep(key, false));
		steps.Add(new WaitStep(1));
		foreach (var m in modifiers.Reverse())
		{
			steps.Add(new KeyStep(m, false));
			steps.Add(new WaitStep(1));
		}
		return steps;
	}

	/// <summary>Types printable text; newlines become Enter presses.</summary>
	public static List<Step> Type(string text)
	{
		var steps = new List<Step>();
		var buffer = new StringBuilder();
		foreach (var c in text)
		{
			if (c == '\r')
				continue;
			if (c == '\n')
			{
				Flush();
				steps.AddRange(KeyPress(Keys.Enter, Array.Empty<Keys>()));
				continue;
			}
			buffer.Append(c);
		}
		Flush();
		steps.Add(new WaitStep(2));
		return steps;

		void Flush()
		{
			if (buffer.Length == 0)
				return;
			steps.Add(new TextStep(buffer.ToString()));
			steps.Add(new WaitStep(1));
			buffer.Clear();
		}
	}
}
