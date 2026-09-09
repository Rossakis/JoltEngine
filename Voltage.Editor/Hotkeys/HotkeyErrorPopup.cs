using ImGuiNET;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Hotkeys
{
	/// <summary>Centred error message that dismisses itself after a few seconds, or on OK.</summary>
	public static class HotkeyErrorPopup
	{
		private const string PopupId = "HotkeyErrorPopup";
		private const float Duration = 5f;

		private static string _message;
		private static float _timer;
		private static bool _openRequested;

		public static void Show(string message)
		{
			_message = message;
			_timer = Duration;
			_openRequested = true;
		}

		public static void Draw()
		{
			if (string.IsNullOrEmpty(_message))
				return;

			if (_openRequested)
			{
				ImGui.OpenPopup(PopupId);
				_openRequested = false;
			}

			var center = ImGui.GetMainViewport().GetCenter();
			ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(new Num.Vector2(420, 0), ImGuiCond.Appearing);

			bool open = true;
			if (!Gui.BeginPopupModal(PopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
			{
				_message = null;
				return;
			}

			_timer -= ImGui.GetIO().DeltaTime;

			ImGuiSafe.TextColoredSafe(new Num.Vector4(1f, 0.45f, 0.4f, 1f), "Cannot set that shortcut");
			ImGui.Separator();

			ImGui.PushTextWrapPos(400f);
			ImGuiSafe.TextWrappedSafe(_message);
			ImGui.PopTextWrapPos();

			VoltageEditorUtils.MediumVerticalSpace();

			var seconds = _timer > 0f ? (int)_timer + 1 : 0;
			var dismissed = Gui.Button($"OK ({seconds})", new Num.Vector2(120, 0)) || _timer <= 0f;

			if (dismissed)
			{
				_message = null;
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}
}
