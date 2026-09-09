using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Voltage.Gateway;

/// <summary>Replays scripted mouse and keyboard steps through the engine's Input overrides, one step per frame, so ImGui and the game see them as real input. Once captured it owns both devices until <see cref="Release"/>.</summary>
public sealed class InputSimulator
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

	/// <summary>Blocks the queue until the host emits a matching event; a timeout fails the rest of that script.</summary>
	public sealed record WaitEventStep(string Pattern, TimeSpan Timeout) : Step;

	private readonly Queue<(Step step, int script, TaskCompletionSource<object> done)> _queue = new();
	private readonly HashSet<Keys> _keys = new();
	private readonly List<uint> _chars = new();
	private TaskCompletionSource<object> _waitDone;
	private int _waitFrames;
	private Task<object> _eventWait;
	private string _eventPattern;
	private int _eventScript;
	private int _nextScript, _currentScript;
	private Point _pos, _appliedPos, _delta;
	private bool _left, _right, _middle, _appliedLeft, _doubleClick, _scriptPressed;
	private int _scroll;

	public bool Captured { get; private set; }

	/// <summary>Receives typed characters; the editor points it at ImGui's input queue. Text steps are dropped without one.</summary>
	public Action<uint> TextSink { get; set; }

	/// <summary>Resolves a waitEvent step; the dispatcher points it at its event waiter.</summary>
	public Func<string, TimeSpan, Task<object>> EventWaiter { get; set; }

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

		var script = ++_nextScript;
		for (var i = 0; i < steps.Count; i++)
			_queue.Enqueue((steps[i], script, i == steps.Count - 1 ? done : null));
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

		if (_eventWait != null && _eventWait.IsCompleted)
		{
			var (task, pattern, script) = (_eventWait, _eventPattern, _eventScript);
			_eventWait = null;
			if (task.IsCompletedSuccessfully)
			{
				_waitDone?.TrySetResult(State());
				_waitDone = null;
			}
			else
			{
				_ = task.Exception;
				Fail(script, new GatewayException($"timed out waiting for event '{pattern}'"));
			}
		}

		while (_waitFrames == 0 && _eventWait == null && _queue.Count > 0)
		{
			var (step, script, done) = _queue.Dequeue();
			if (script != _currentScript)
			{
				_currentScript = script;
				_doubleClick = false;
				_scriptPressed = false;
			}
			try
			{
				Execute(step, script);
			}
			catch (GatewayException ex)
			{
				Fail(script, ex, done);
				continue;
			}

			if ((step is WaitStep && _waitFrames > 0) || (step is WaitEventStep && _eventWait != null))
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

		_delta = _pos - _appliedPos;
		_appliedPos = _pos;
		Input.SetMousePositionDelta(_delta);
		if (_left && !_appliedLeft)
		{
			Input.RegisterLeftPress();
			_scriptPressed = true;
		}
		_appliedLeft = _left;
		if (_scriptPressed && Input.DoubleLeftMouseButtonPressed)
			_doubleClick = true;

		if (_chars.Count > 0)
		{
			if (TextSink != null)
				foreach (var c in _chars)
					TextSink(c);
			_chars.Clear();
		}
	}

	/// <summary>Drops the rest of a failed script and reports the error on its task; a trailing release step still takes effect.</summary>
	private void Fail(int script, GatewayException error, TaskCompletionSource<object> done = null)
	{
		var release = false;
		while (_queue.Count > 0 && _queue.Peek().script == script)
		{
			var (step, _, stepDone) = _queue.Dequeue();
			release |= step is ReleaseStep;
			done ??= stepDone;
		}
		if (release)
			Release();
		(done ?? _waitDone)?.TrySetException(error);
		_waitDone = null;
	}

	public void Release()
	{
		if (Captured)
			Input.ResyncMousePosition();
		Captured = false;
		_keys.Clear();
		_chars.Clear();
		_left = _right = _middle = _appliedLeft = false;
		_delta = Point.Zero;
	}

	public object State() => new
	{
		captured = Captured,
		x = _pos.X,
		y = _pos.Y,
		delta = new { x = _delta.X, y = _delta.Y },
		left = _left,
		right = _right,
		middle = _middle,
		doubleClick = _doubleClick,
		keysDown = _keys.Select(k => k.ToString()).ToList(),
		pendingSteps = _queue.Count,
		waitingFor = _eventWait != null ? _eventPattern : null
	};

	private void Execute(Step step, int script)
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
			case WaitEventStep e:
				if (EventWaiter == null)
					throw new GatewayException("this host cannot wait on events");
				_eventWait = EventWaiter(e.Pattern, e.Timeout);
				_eventPattern = e.Pattern;
				_eventScript = script;
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
		_pos = _appliedPos = new Point(mouse.X, mouse.Y);
		_delta = Point.Zero;
		_scroll = mouse.ScrollWheelValue;
		_left = _right = _middle = _appliedLeft = false;
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

	/// <summary>Parks the cursor somewhere long enough for tooltips to open.</summary>
	public static List<Step> Hover(int x, int y, int frames) =>
		new() { new MoveStep(x, y), new WaitStep(Math.Max(1, frames)) };

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
