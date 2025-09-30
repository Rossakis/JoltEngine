using System;
using System.Collections.Generic;
using ImGuiNET;
using Nez;
using Nez.Utils;
using Num = System.Numerics;

namespace Nez.ImGuiTools.Inspectors
{
    public class DebugWindow
    {
        private const int MaxMessages = 200;
        private ImGuiManager _imguiManager;
        private bool _collapseText = true; 
        private bool _groupLogs = false;   
        private readonly List<(Debug.LogType Type, string Message, int Count, DateTime LatestTimestamp)> _groupedBuffer = new();

        private static readonly Dictionary<Debug.LogType, Num.Vector4> LogTypeColors = new()
        {
            { Debug.LogType.Error, new Num.Vector4(1f, 0.2f, 0.2f, 1f) },   // Red
            { Debug.LogType.Warn,  new Num.Vector4(1f, 0.8f, 0.2f, 1f) },   // Orange
            { Debug.LogType.Info,  new Num.Vector4(0.5f, 0.9f, 1f, 1f) },   // Cyan
            { Debug.LogType.Trace, new Num.Vector4(0.7f, 0.7f, 0.7f, 1f) }, // Gray
            { Debug.LogType.Log,   new Num.Vector4(0.8f, 0.9f, 1f, 1f) }    // Default (light blue)
        };

        public void Draw()
        {
            if (_imguiManager == null)
                _imguiManager = Core.GetGlobalManager<ImGuiManager>();

            var windowPosX = Screen.Width - _imguiManager.InspectorTabWidth + _imguiManager.InspectorWidthOffset;
            var windowPosY = _imguiManager.MainWindowPositionY + 20f * _imguiManager.FontSizeMultiplier;
            var windowWidth = _imguiManager.InspectorTabWidth - _imguiManager.InspectorWidthOffset;
            var windowHeight = Screen.Height - windowPosY;

            ImGui.SetNextWindowPos(new Num.Vector2(windowPosX, windowPosY), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Num.Vector2(windowWidth, windowHeight), ImGuiCond.Always);

            ImGui.Begin("##DebugLog",
                ImGuiWindowFlags.NoDocking |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.HorizontalScrollbar);

            // Controls row
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Num.Vector2(4, 4));
            ImGui.Checkbox("Collapse Text", ref _collapseText);
            ImGui.SameLine();
            ImGui.Checkbox("Group Logs", ref _groupLogs);
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                Debug.ClearLogEntries();
            }
            ImGui.PopStyleVar();

            ImGui.Separator();

            var logEntries = Debug.GetLogEntries();
            int startIdx = Math.Max(0, logEntries.Count - MaxMessages);

            ImGui.BeginChild("DebugLogScroll", new Num.Vector2(0, -ImGui.GetFrameHeightWithSpacing()), true, ImGuiWindowFlags.HorizontalScrollbar);

            if (_groupLogs)
            {
                // Group by message and type, ignoring timestamp
                _groupedBuffer.Clear();
                // Use a dictionary to group by (Type, Message)
                var groupDict = new Dictionary<(Debug.LogType, string), (int Count, DateTime LatestTimestamp)>();
                for (int i = startIdx; i < logEntries.Count; i++)
                {
                    var entry = logEntries[i];
                    var key = (entry.Type, entry.Message);
                    if (groupDict.TryGetValue(key, out var val))
                    {
                        // Update count and latest timestamp
                        groupDict[key] = (val.Count + 1, entry.Timestamp > val.LatestTimestamp ? entry.Timestamp : val.LatestTimestamp);
                    }
                    else
                    {
                        groupDict[key] = (1, entry.Timestamp);
                    }
                }
                // Add to buffer in descending order of latest timestamp
                foreach (var kvp in groupDict)
                {
                    _groupedBuffer.Add((kvp.Key.Item1, kvp.Key.Item2, kvp.Value.Count, kvp.Value.LatestTimestamp));
                }
                _groupedBuffer.Sort((a, b) => b.LatestTimestamp.CompareTo(a.LatestTimestamp));

                foreach (var group in _groupedBuffer)
                {
                    var color = LogTypeColors.TryGetValue(group.Type, out var c) ? c : LogTypeColors[Debug.LogType.Log];
                    string text = $"[{group.LatestTimestamp:HH:mm:ss}] {group.Message}";
                    if (group.Count > 1)
                        text += $"  (x{group.Count})";

                    if (_collapseText)
                    {
                        ImGui.PushTextWrapPos(0.0f);
                        ImGui.TextColored(color, text);
                        ImGui.PopTextWrapPos();
                    }
                    else
                    {
                        ImGui.TextColored(color, text);
                    }
                }
            }
            else
            {
                for (int i = logEntries.Count - 1; i >= startIdx; i--)
                {
                    var entry = logEntries[i];
                    var color = LogTypeColors.TryGetValue(entry.Type, out var c) ? c : LogTypeColors[Debug.LogType.Log];
                    string text = $"[{entry.Timestamp:HH:mm:ss}] {entry.Message}";

                    if (_collapseText)
                    {
                        ImGui.PushTextWrapPos(0.0f);
                        ImGui.TextColored(color, text);
                        ImGui.PopTextWrapPos();
                    }
                    else
                    {
                        ImGui.TextColored(color, text);
                    }
                }
            }

            ImGui.EndChild();
            ImGui.End();
        }
    }
}
