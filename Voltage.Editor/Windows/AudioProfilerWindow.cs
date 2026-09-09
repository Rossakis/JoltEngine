using ImGuiNET;
using Voltage.Audio;
using Num = System.Numerics;

namespace Voltage.Editor.Windows
{
	/// <summary>
	/// Live read-only view of the software audio mixer's profiling counters (mix time vs. budget, active voices,
	/// underruns). Only meaningful while the software mixing backend is active; otherwise shows a hint.
	/// </summary>
	public class AudioProfilerWindow
	{
		public bool IsOpen;

		private static readonly Num.Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);
		private static readonly Num.Vector4 MutedGreen = new(0.4f, 0.7f, 0.45f, 1f);
		private static readonly Num.Vector4 Green = new(0.3f, 1f, 0.4f, 1f);
		private static readonly Num.Vector4 Amber = new(1f, 0.8f, 0.2f, 1f);
		private static readonly Num.Vector4 Red = new(1f, 0.3f, 0.3f, 1f);

		public void Draw()
		{
			if (!IsOpen)
				return;

			ImGui.SetNextWindowSize(new Num.Vector2(360, 220), ImGuiCond.FirstUseEver);
			if (!Gui.Begin("Audio Profiler ###AudioProfiler", ref IsOpen))
			{
				Gui.End();
				return;
			}

			if (Core.Audio == null)
			{
				ImGui.TextColored(Red, "Audio manager not initialized.");
				Gui.End();
				return;
			}

			DrawBackendStatus();
			ImGui.Separator();

			if (!Core.Audio.TryGetSoftwareAudioStats(out var stats))
			{
				ImGui.TextColored(Muted, "DSP profiling (mix load, voices, underruns) is only available on the");
				ImGui.TextColored(Muted, "software mixing backend. Set AudioManager.PreferSoftwareBackend");
				ImGui.TextColored(Muted, "(see Voltage.Editor/Program.cs) to enable it.");
				Gui.End();
				return;
			}

			var latencyMs = stats.SampleRate > 0 ? stats.BufferFrames * 1000.0 / stats.SampleRate : 0.0;
			ImGui.TextColored(Muted,
				$"SDL audio: {stats.SampleRate}Hz {stats.Channels}ch, {stats.BufferFrames}-frame buffer (~{latencyMs:0.0} ms)");
			Tip("The output device format negotiated with SDL: sample rate, channel count and the size of one\n" +
				"mix callback buffer. The buffer size sets the output latency (buffer / sample rate) - smaller is\n" +
				"snappier but leaves less time to mix, so it underruns more easily.");
			ImGui.Spacing();

			var load01 = stats.LoadPercent / 100f;
			var barColor = stats.LoadPercent < 50f ? Green : stats.LoadPercent <= 80f ? Amber : Red;
			ImGui.PushStyleColor(ImGuiCol.PlotHistogram, barColor);
			ImGui.ProgressBar(load01, new Num.Vector2(-1f, 0f), $"{stats.LoadPercent:0}% of budget");
			ImGui.PopStyleColor();
			Tip("How much of each buffer's real-time budget the mixer uses. The mix must finish before the buffer\n" +
				"is due; sustained load above ~50% risks underruns (glitches).");

			ImGui.Spacing();

			ImGui.Text($"mix {stats.MixAvgMs:0.00} ms / {stats.BudgetMs:0.0} ms  (peak {stats.MixPeakMs:0.00} ms)");
			Tip("Average time spent mixing one buffer, against the time available for it (the budget), plus the\n" +
				"recent peak. Budget = buffer frames / sample rate.");
			ImGui.Text($"Active voices: {stats.ActiveVoices}");
			Tip("Sounds currently being mixed (playing, non-disposed). Each adds to the per-buffer mix cost.");
			ImGui.TextColored(stats.Underruns > 0 ? Red : MutedGreen, $"Underruns: {stats.Underruns}");
			Tip("Cumulative times the mix didn't finish before its buffer was due, each an audible glitch. It only\n" +
				"ever counts up; a steady 0 is healthy.");

			ImGui.Spacing();
			ImGui.PushStyleColor(ImGuiCol.Text, Muted);
			ImGui.TextWrapped("Underruns > 0 = audible glitches. Sustained load > ~50% risks them.");
			ImGui.PopStyleColor();

			Gui.End();
		}

		/// <summary>Shows which audio backend is active and whether it's actually producing sound.</summary>
		private void DrawBackendStatus()
		{
			var backend = Core.Audio.Backend;

			if (backend is SoftwareMixingAudioBackend sw)
			{
				ImGui.TextUnformatted("Backend: Software mixing (SDL + DSP)");
				Tip("Custom CPU mixer with DSP (reverb). Selected via AudioManager.PreferSoftwareBackend.");
				ImGui.SameLine();

				if (sw.IsDeviceOpen)
					ImGui.TextColored(Green, "working");
				else if (!sw.DeviceOpenAttempted)
				{
					ImGui.TextColored(Muted, "opens on first sound");
					Tip("The SDL output device opens lazily the first time a sound plays; this is normal before then.");
				}
				else
				{
					ImGui.TextColored(Red, "device failed (silent)");
					Tip("SDL could not open an output device, so no audio is produced. Check the OS default audio device.");
				}

				return;
			}

			if (backend != null)
			{
				ImGui.TextUnformatted($"Backend: {FriendlyName(backend)}");
				ImGui.SameLine();
				ImGui.TextColored(Green, "working");
				Tip("MonoGame's built-in audio (OpenAL on desktop). No DSP or mix profiling - switch to the software\n" +
					"backend for that.");
				return;
			}

			ImGui.TextColored(Red, "Backend: none (no audio).");
		}

		private static string FriendlyName(IAudioBackend backend) => backend switch
		{
			MonoGameAudioBackend => "MonoGame (OpenAL)",
			_ => backend.GetType().Name,
		};

		private static void Tip(string text)
		{
			if (ImGui.IsItemHovered())
				ImGui.SetTooltip(text);
		}
	}
}
