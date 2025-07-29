using ImGuiNET;
using Nez;
using Nez.Utils;

namespace Nez.Editor;

public class NotificationSystem
{
    private static string _notificationText = "";
    private static float _notificationTimer;
    private const float NotificationDuration = 4.5f; // Seconds

    public static void ShowNotification(string text)
    {
        _notificationText = text;
        _notificationTimer = NotificationDuration;
    }

    public static void Draw()
    {
        if (_notificationTimer > 0)
        {
            _notificationTimer -= Time.DeltaTime;

            // Calculate position
            var viewport = ImGui.GetMainViewport();
            var textSize = ImGui.CalcTextSize(_notificationText);
            var windowPos = new System.Numerics.Vector2(
                (viewport.Size.X - textSize.X) * 0.5f, // Center horizontally
                viewport.Size.Y * 0.2f // 20% from top
            );

            // Set up window
            ImGui.SetNextWindowPos(windowPos, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.35f); // Transparent background
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 5f);

            // Begin notification window
            if (ImGui.Begin("##Notification",
                    ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoInputs |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings))
            {
                // Fade out effect
                var alpha = Mathf.Clamp01(_notificationTimer);
                ImGui.TextColored(new System.Numerics.Vector4(1, 1, 1, alpha), _notificationText);
            }

            ImGui.End();
            ImGui.PopStyleVar();
        }
    }
}