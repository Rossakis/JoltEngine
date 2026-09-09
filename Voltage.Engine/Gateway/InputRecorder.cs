using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Voltage.Gateway;

/// <summary>Turns the frame-by-frame device state into input.script steps, so a session can be replayed with voltage run.</summary>
public sealed class InputRecorder
{
	public const int MaxSteps = 100_000;

	private readonly List<object> _steps = new();
	private readonly StringBuilder _text = new();
	private MouseState _mouse;
	private KeyboardState _keyboard;
	private int _pendingFrames;
	private float _started, _seconds;
	private bool _mouseOn, _keyboardOn, _textOn;

	public bool Recording { get; private set; }

	public int Frames { get; private set; }

	public bool Truncated { get; private set; }

	public float Seconds => Recording ? Voltage.Utils.Time.TotalTime - _started : _seconds;

	public IReadOnlyList<object> Steps => _steps;

	public void Start(bool mouse, bool keyboard, bool text)
	{
		if (Recording)
			throw new GatewayException("already recording");

		_steps.Clear();
		_text.Clear();
		_pendingFrames = 0;
		Frames = 0;
		Truncated = false;
		_mouseOn = mouse;
		_keyboardOn = keyboard;
		_textOn = text;
		_mouse = Voltage.Input.CurrentMouseState;
		_keyboard = Voltage.Input.CurrentKeyboardState;
		_started = Voltage.Utils.Time.TotalTime;
		Recording = true;
		if (_textOn && Core.Instance?.Window != null)
			Core.Instance.Window.TextInput += OnTextInput;
	}

	/// <summary>Stops and returns the steps recorded so far; a stopped recorder keeps them until the next start.</summary>
	public IReadOnlyList<object> Stop()
	{
		if (Recording)
		{
			Recording = false;
			_seconds = Voltage.Utils.Time.TotalTime - _started;
			if (_textOn && Core.Instance?.Window != null)
				Core.Instance.Window.TextInput -= OnTextInput;
			FlushText();
		}
		return _steps;
	}

	/// <summary>Main thread, once per frame after the simulator applied its state, so agent-driven sessions record too.</summary>
	public void Sample()
	{
		if (!Recording)
			return;

		Frames++;
		var mouse = Voltage.Input.CurrentMouseState;
		var keyboard = Voltage.Input.CurrentKeyboardState;

		if (_mouseOn)
		{
			if (mouse.X != _mouse.X || mouse.Y != _mouse.Y)
				Add(new { action = "move", x = mouse.X, y = mouse.Y });
			Button("left", _mouse.LeftButton, mouse.LeftButton, mouse);
			Button("right", _mouse.RightButton, mouse.RightButton, mouse);
			Button("middle", _mouse.MiddleButton, mouse.MiddleButton, mouse);
			var notches = (mouse.ScrollWheelValue - _mouse.ScrollWheelValue) / 120;
			if (notches != 0)
				Add(new { action = "scroll", notches, x = mouse.X, y = mouse.Y });
		}

		if (_keyboardOn)
		{
			foreach (var key in keyboard.GetPressedKeys())
				if (!_keyboard.IsKeyDown(key))
					Add(new { action = "keyDown", key = key.ToString() });
			foreach (var key in _keyboard.GetPressedKeys())
				if (!keyboard.IsKeyDown(key))
					Add(new { action = "keyUp", key = key.ToString() });
		}

		_mouse = mouse;
		_keyboard = keyboard;
		_pendingFrames++;
	}

	private void Button(string name, ButtonState before, ButtonState now, MouseState mouse)
	{
		if (before == now)
			return;
		Add(new { action = now == ButtonState.Pressed ? "down" : "up", button = name, x = mouse.X, y = mouse.Y });
	}

	private void OnTextInput(object sender, TextInputEventArgs e)
	{
		if (!Recording || char.IsControl(e.Character))
			return;
		_text.Append(e.Character);
	}

	/// <summary>Typed characters travel with the frame they arrived in, so text lands between the keys around it.</summary>
	private void FlushText()
	{
		if (_text.Length == 0)
			return;
		Add(new { action = "type", text = _text.ToString() });
		_text.Clear();
	}

	private void Add(object step)
	{
		if (_steps.Count >= MaxSteps)
		{
			Truncated = true;
			return;
		}

		FlushText();
		if (_pendingFrames > 0)
		{
			_steps.Add(new { action = "wait", frames = _pendingFrames });
			_pendingFrames = 0;
		}
		_steps.Add(step);
	}

	/// <summary>Status keeps the reply small; the steps travel only when asked for, as on stop.</summary>
	public object State(bool includeSteps) => new
	{
		recording = Recording,
		stepCount = _steps.Count,
		steps = includeSteps ? _steps : null,
		frames = Frames,
		seconds = Seconds,
		truncated = Truncated
	};
}
