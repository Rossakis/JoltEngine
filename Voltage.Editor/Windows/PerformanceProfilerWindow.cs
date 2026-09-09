using System;
using ImGuiNET;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Windows
{
	/// <summary>
	/// Live read-only view of runtime performance: frame rate / frame time (with a rolling graph), managed-heap
	/// size and GC counts, and renderer/scene stats (draw calls, entities, renderers, renderables).
	/// </summary>
	public class PerformanceProfilerWindow
	{
		public bool IsOpen;

		private static readonly Num.Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);
		private static readonly Num.Vector4 Green = new(0.3f, 1f, 0.4f, 1f);
		private static readonly Num.Vector4 Amber = new(1f, 0.8f, 0.2f, 1f);
		private static readonly Num.Vector4 Red = new(1f, 0.3f, 0.3f, 1f);

		// Ring buffer of recent frame times (ms) for the PlotLines graph.
		private const int HistorySize = 120;
		private readonly float[] _frameTimes = new float[HistorySize];
		private int _historyHead;

		public void Draw()
		{
			if (!IsOpen)
				return;

			ImGui.SetNextWindowSize(new Num.Vector2(380, 320), ImGuiCond.FirstUseEver);
			if (!Gui.Begin("Performance Profiler ###PerformanceProfiler", ref IsOpen))
			{
				Gui.End();
				return;
			}

			// Unscaled delta so pausing / time-scaling the game doesn't skew the reading.
			var dtMs = Time.UnscaledDeltaTime * 1000f;
			_frameTimes[_historyHead] = dtMs;
			_historyHead = (_historyHead + 1) % HistorySize;

			var fps = dtMs > 0.0001f ? 1000f / dtMs : 0f;
			var color = fps < 30f ? Red : fps < 55f ? Amber : Green;

			ImGui.TextColored(color, $"{fps:0} FPS");
			ImGui.SameLine();
			ImGui.TextColored(color, $"({dtMs:0.00} ms)");
			Tip("Frames per second and the time for the last frame, from the engine's UNSCALED delta - pausing or\n" +
				"time-scaling the game doesn't skew it. Green >= 55 FPS, amber >= 30, red below 30.");

			// Reorder the ring buffer oldest -> newest so the graph scrolls left.
			var ordered = new float[HistorySize];
			for (var i = 0; i < HistorySize; i++)
				ordered[i] = _frameTimes[(_historyHead + i) % HistorySize];

			ImGui.PlotLines("##frametime", ref ordered[0], HistorySize, 0,
				"frame time (ms)", 0f, 50f, new Num.Vector2(-1f, 70f));

			ImGui.Spacing();
			ImGui.Separator();

			var managedMb = GC.GetTotalMemory(false) / (1024f * 1024f);
			ImGui.Text($"Managed heap: {managedMb:0.0} MB");
			Tip("Managed (GC) heap size only - native buffers (audio, textures, MonoGame) are NOT included, so this\n" +
				"reads lower than the OS process memory. Watch for steady growth, which signals a leak.");
			ImGui.Text($"GC collections  gen0: {GC.CollectionCount(0)}  gen1: {GC.CollectionCount(1)}  gen2: {GC.CollectionCount(2)}");
			Tip("Cumulative GC count per generation since launch - these only ever go UP; that is expected, not a\n" +
				"leak. Gen0 (short-lived garbage) climbs fastest, gen1 slower. A leak shows as rising heap, not as\n" +
				"rising counts. The one to watch is gen2 (full, expensive collections) climbing frequently.");

			ImGui.Spacing();
			ImGui.Separator();

			// DrawCount comes from MonoGame's per-frame graphics metrics.
			var gd = Core.GraphicsDevice;
			if (gd != null)
			{
				ImGui.Text($"Draw calls: {gd.Metrics.DrawCount}");
				Tip("GPU draw calls last frame (MonoGame's metric). Fewer is better; batching sprites into one\n" +
					"material/texture keeps this down. A sudden spike usually means a batch broke.");
			}

			var scene = Core.Scene;
			if (scene != null)
			{
				ImGui.Text($"Entities: {scene.Entities.Count}");
				Tip("Entities in the active scene.");
				ImGui.Text($"Renderers: {scene._renderers.Length}");
				Tip("Renderer passes on the scene (e.g. the default renderer, a deferred-lighting renderer). Each is\n" +
					"a separate pass over the renderables it owns.");
				ImGui.Text($"Renderables: {scene.RenderableComponents.Count}");
				Tip("Renderable components submitted for drawing (sprites, tilemaps, particles, etc.).");
			}
			else
			{
				ImGui.TextColored(Muted, "No active scene.");
			}

			ImGui.Spacing();
			ImGui.PushStyleColor(ImGuiCol.Text, Muted);
			ImGui.TextWrapped("Frame time uses the engine's unscaled delta. Green >= 55 FPS, amber >= 30, red below 30.");
			ImGui.PopStyleColor();

			Gui.End();
		}

		private static void Tip(string text)
		{
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(text);
		}
	}
}
