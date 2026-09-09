using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Utils;

namespace Voltage.Editor.Utils;

public static class NotificationSystem
{
	private static readonly Queue<string> _queue = new();
	private static string _currentText = string.Empty;
	private static float _timer;

	private const float NotificationDuration = 2.5f;

	public static void ShowTimedNotification(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return;

		_queue.Enqueue(text);

		// If nothing is currently playing, start immediately
		if (string.IsNullOrEmpty(_currentText))
		{
			AdvanceQueue();
		}
	}

	private static void AdvanceQueue()
	{
		if (_queue.Count > 0)
		{
			_currentText = _queue.Dequeue();
			_timer = NotificationDuration;
		}
		else
		{
			_currentText = string.Empty;
		}
	}

	public static void Draw()
	{
		if (string.IsNullOrEmpty(_currentText))
			return;

		_timer -= Time.DeltaTime;
		if (_timer <= 0f)
		{
			AdvanceQueue();
			return;
		}

		DrawMainNotification(ImGui.GetMainViewport());
		DrawNextPreview(ImGui.GetMainViewport());
	}

	private static void DrawMainNotification(ImGuiViewportPtr viewport)
	{
		var alpha = Mathf.Clamp01(_timer / NotificationDuration);
		var textSize = ImGui.CalcTextSize(_currentText);

		var pos = new System.Numerics.Vector2(
			(viewport.Size.X - textSize.X) * 0.5f,
			viewport.Size.Y * 0.2f
		);

		ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
		ImGui.SetNextWindowBgAlpha(0.35f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);

		if (Gui.Begin("##NotificationMain",
			ImGuiWindowFlags.NoDecoration |
			ImGuiWindowFlags.NoInputs |
			ImGuiWindowFlags.AlwaysAutoResize |
			ImGuiWindowFlags.NoSavedSettings))
		{
			ImGuiSafe.TextColoredSafe(
				new System.Numerics.Vector4(1, 1, 1, alpha),
				_currentText
			);
		}

		Gui.End();
		ImGui.PopStyleVar();
	}

	private static void DrawNextPreview(ImGuiViewportPtr viewport)
	{
		if (_queue.Count == 0)
			return;

		var nextText = _queue.Peek();
		var textSize = ImGui.CalcTextSize(nextText);

		var pos = new System.Numerics.Vector2(
			(viewport.Size.X - textSize.X) * 0.5f,
			viewport.Size.Y * 0.2f + 40f // offset under main box
		);

		ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
		ImGui.SetNextWindowBgAlpha(0.15f);
		ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 4f);

		if (Gui.Begin("##NotificationPreview",
			ImGuiWindowFlags.NoDecoration |
			ImGuiWindowFlags.NoInputs |
			ImGuiWindowFlags.AlwaysAutoResize |
			ImGuiWindowFlags.NoSavedSettings))
		{
			ImGuiSafe.TextColoredSafe(
				new System.Numerics.Vector4(1, 1, 1, 0.5f),
				nextText
			);
		}

		Gui.End();
		ImGui.PopStyleVar();
	}
}
