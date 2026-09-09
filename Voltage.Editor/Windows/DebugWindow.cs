using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Utils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Persistence;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Windows
{
	public class DebugWindow
	{
		private readonly
			List<(Debug.LogType Type, string Message, string CallerClass, int CallerLine, int Count, DateTime
				LatestTimestamp)> _groupedBuffer = new();

		private PersistentInt _maxMessages = new("DebugWindow_MaxMessages", 350);
		private PersistentBool _isGroupLogsOn = new("DebugWindow_GroupLogs", false);
		private PersistentBool _isCollapseTextOn = new("DebugWindow_CollapseText", false);
		private ImGuiManager _imguiManager;
		private string _copiedText = string.Empty;

		private static readonly Dictionary<Debug.LogType, Num.Vector4> LogTypeColors = new()
		{
			{ Debug.LogType.Error, new Num.Vector4(1f, 0.2f, 0.2f, 1f) }, // Red
			{ Debug.LogType.Warn, new Num.Vector4(1f, 0.8f, 0.2f, 1f) }, // Orange
			{ Debug.LogType.Info, new Num.Vector4(0.5f, 0.9f, 1f, 1f) }, // Cyan
			{ Debug.LogType.Trace, new Num.Vector4(0.7f, 0.7f, 0.7f, 1f) }, // Gray
			{ Debug.LogType.Log, new Num.Vector4(0.8f, 0.9f, 1f, 1f) }, // Default (light blue)
			{ Debug.LogType.Success, new Num.Vector4(0.2f, 1f, 0.3f, 1f) } // Bright Green
		};

		// Helper to get font scale by log type
		private float GetFontScale(Debug.LogType type)
		{
			return type switch
			{
				Debug.LogType.Error => 1.3f,
				Debug.LogType.Warn => 1.2f,
				Debug.LogType.Info => 1.1f,
				Debug.LogType.Success => 1.1f,
				Debug.LogType.Log => 1.0f,
				Debug.LogType.Trace => 1.0f,
				_ => 1.0f
			};
		}

		public void Draw()
		{
			if (_imguiManager == null)
				_imguiManager = Core.GetGlobalManager<ImGuiManager>();

			var viewport = ImGui.GetMainViewport();
			var workPos = viewport.WorkPos;
			var workSize = viewport.WorkSize;
			ImGui.SetNextWindowPos(
				new Num.Vector2(workPos.X, workPos.Y + workSize.Y * 0.7f), ImGuiCond.FirstUseEver);
			ImGui.SetNextWindowSize(
				new Num.Vector2(workSize.X, workSize.Y * 0.3f), ImGuiCond.FirstUseEver);

			Gui.Begin("Debug Log ###DebugWindow", ImGuiWindowFlags.HorizontalScrollbar);

			// Controls row
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Num.Vector2(4, 4));

			bool collapseText = _isCollapseTextOn.Value;
			if (Gui.Checkbox("Collapse Text", ref collapseText))
			{
				_isCollapseTextOn.Value = collapseText;
			}

			ImGui.SameLine();

			bool groupLogsValue = _isGroupLogsOn.Value;
			if (Gui.Checkbox("Group Logs", ref groupLogsValue))
			{
				_isGroupLogsOn.Value = groupLogsValue;
			}

			ImGui.SameLine();
			if (Gui.Button("Clear"))
			{
				Debug.ClearLogEntries();
			}

			ImGui.PushItemWidth(200);
			var maxMessagesInput = _maxMessages.Value;
			if (Gui.InputInt("Max Messages", ref maxMessagesInput))
			{
				_maxMessages.Value = Math.Clamp(maxMessagesInput, 1, 100000);
			}

			ImGui.PopItemWidth();
			ImGui.PopStyleVar();

			ImGui.Separator();

			var logEntries = Debug.GetLogEntries();

			ImGui.BeginChild("DebugLogScroll", new Num.Vector2(0, -ImGui.GetFrameHeightWithSpacing()), true,
				ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

			var wheel = ImGui.GetIO().MouseWheel;
			if (wheel != 0)
			{
				// Immediately switch focus to DebugWindow if we want to scroll with mouse
				if (ImGui.IsWindowHovered() || ImGui.IsWindowFocused())
				{
					if (!ImGui.IsWindowFocused())
						ImGui.SetWindowFocus();

					ImGui.SetScrollY(ImGui.GetScrollY() - wheel * ImGui.GetTextLineHeightWithSpacing() * 3f);
				}
			}

			if (groupLogsValue)
			{
				_groupedBuffer.Clear();

				// Group by Type, Message, CallerClass, CallerLine
				var groupDict =
					new Dictionary<(Debug.LogType, string, string, int), (int Count, DateTime LatestTimestamp)>();
				for (int i = 0; i < logEntries.Count; i++)
				{
					var entry = logEntries[i];
					var key = (entry.Type, entry.Message, entry.CallerClass, entry.CallerLine);
					if (groupDict.TryGetValue(key, out var val))
					{
						groupDict[key] = (val.Count + 1,
							entry.Timestamp > val.LatestTimestamp ? entry.Timestamp : val.LatestTimestamp);
					}
					else
					{
						groupDict[key] = (1, entry.Timestamp);
					}
				}

				foreach (var timeStamp in groupDict)
				{
					_groupedBuffer.Add((timeStamp.Key.Item1, timeStamp.Key.Item2, timeStamp.Key.Item3, timeStamp.Key.Item4, timeStamp.Value.Count,
						timeStamp.Value.LatestTimestamp));
				}

				_groupedBuffer.Sort((a, b) => b.LatestTimestamp.CompareTo(a.LatestTimestamp));

				foreach (var group in _groupedBuffer)
				{
					DrawLogEntry(group.Type, group.Message, group.CallerClass, group.CallerLine, group.LatestTimestamp,
						group.Count, collapseText);
				}
			}
			else
			{
				// For non-grouped, still show all, but limited by MaxMessages
				int startIdx = Math.Max(0, logEntries.Count - _maxMessages.Value);
				for (int i = logEntries.Count - 1; i >= startIdx; i--)
				{
					var entry = logEntries[i];
					DrawLogEntry(entry.Type, entry.Message, entry.CallerClass, entry.CallerLine, entry.Timestamp, 1,
						collapseText);
				}
			}

			ImGui.EndChild();
			Gui.End();
		}

		private void DrawLogEntry(Debug.LogType type, string message, string callerClass, int callerLine,
			DateTime timestamp, int count, bool collapseText)
		{
			var color = LogTypeColors.TryGetValue(type, out var c) ? c : LogTypeColors[Debug.LogType.Log];
			string text = $"[{timestamp:HH:mm:ss}] {message} ({callerClass}:{callerLine})";
			if (count > 1)
			{
				text += count > 99 ? "  (x100+)" : $"  (x{count})";
			}

			// Get appropriate font index based on log type
			int fontIndex = type switch
			{
				Debug.LogType.Error => 3,
				Debug.LogType.Warn => 2,
				Debug.LogType.Info => 1,
				Debug.LogType.Success => 1,
				_ => 0
			};

			// Push the appropriate font (if available)
			var io = ImGui.GetIO();
			if (io.Fonts.Fonts.Size > fontIndex)
			{
				ImGui.PushFont(io.Fonts.Fonts[fontIndex]);
			}

			var cursorScreenPos = ImGui.GetCursorScreenPos();

			if (collapseText)
			{
				ImGui.PushTextWrapPos(0.0f);
				DrawMessageIcon(type, color);
				ImGui.SameLine();
				ImGuiSafe.TextColoredSafe(color, text);
				ImGui.PopTextWrapPos();
			}
			else
			{
				DrawMessageIcon(type, color);
				ImGui.SameLine();
				ImGuiSafe.TextColoredSafe(color, text);
			}

			// Pop font if we pushed one
			if (io.Fonts.Fonts.Size > fontIndex)
			{
				ImGui.PopFont();
			}

			var itemRectMin = cursorScreenPos;
			var itemRectMax = ImGui.GetItemRectMax();

			itemRectMin.X -= 2;
			itemRectMin.Y -= 2;
			itemRectMax.X += 2;
			itemRectMax.Y += 2;

			// Create an invisible button overlay for reliable click detection
			ImGui.SetCursorScreenPos(itemRectMin);
			var buttonSize = new Num.Vector2(itemRectMax.X - itemRectMin.X, itemRectMax.Y - itemRectMin.Y);
			Gui.InvisibleButton($"##logentry_{text.GetHashCode()}_{timestamp.Ticks}", buttonSize);

			if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				_copiedText = text;
				ImGui.OpenPopup($"LogContextMenu##{text.GetHashCode()}_{timestamp.Ticks}");
			}

			if (Gui.BeginPopup($"LogContextMenu##{text.GetHashCode()}_{timestamp.Ticks}"))
			{
				if (Gui.MenuItem("Copy text"))
				{
					Clipboard.SetContents(_copiedText);
				}

				Gui.EndPopup();
			}

			ImGui.SetWindowFontScale(1.0f);
			ImGui.Spacing();
		}

		#region Message Icon Methods

		public static nint WarningIconId => ImguiImageLoader.WarningIconId;
		public static nint ErrorIconId => ImguiImageLoader.ErrorIconId;
		public static nint InfoIconId => ImguiImageLoader.InfoIconId;
		public static nint SuccessIconId => ImguiImageLoader.SuccessIconId;

		/// <summary>
		/// Draws the appropriate icon for the given log type, using the provided color.
		/// </summary>
		private void DrawMessageIcon(Debug.LogType type, Num.Vector4 color)
		{
			float iconSize = ImGui.GetFont().FontSize + 4;
			nint iconId = type switch
			{
				Debug.LogType.Error => ErrorIconId,
				Debug.LogType.Warn => WarningIconId,
				Debug.LogType.Info => InfoIconId,
				Debug.LogType.Success => SuccessIconId,
				_ => InfoIconId
			};
			ImGui.Image(iconId, new Num.Vector2(iconSize, iconSize), new Num.Vector2(0, 0), new Num.Vector2(1, 1),
				color);
		}

		#endregion
	}
}
