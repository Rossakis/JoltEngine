using System;
using System.IO;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.ImGuiCore;

public partial class ImGuiManager
{
	private static string _pendingProjectLoadPath;

	/// <summary>
	/// Queues a project load for the start of the next frame. Loading takes seconds and drives its
	/// own frames to stay responsive, which it cannot do from inside the frame a menu or dialog is
	/// already drawing.
	/// </summary>
	public static void RequestProjectLoad(string voltageFilePath)
	{
		if (!string.IsNullOrWhiteSpace(voltageFilePath))
			_pendingProjectLoadPath = voltageFilePath;
	}

	/// <summary>Runs a queued load. Called at the top of Update, before the frame is begun.</summary>
	private void ProcessPendingProjectLoad()
	{
		var path = _pendingProjectLoadPath;
		if (path == null)
			return;

		_pendingProjectLoadPath = null;
		ProjectManager.Instance.LoadProject(path);
	}

	/// <summary>
	/// Draws and presents a single overlay frame outside the normal loop, so a synchronous load can
	/// show what it is doing instead of leaving a frozen window behind.
	/// </summary>
	private void RenderProjectLoadFrame()
	{
		if (_renderer == null || Core.GraphicsDevice == null)
			return;

		_renderer.BeforeLayout(1f / 60f);
		DrawProjectLoadOverlay();

		Core.GraphicsDevice.SetRenderTarget(null);
		Core.GraphicsDevice.Clear(new Color(18, 18, 20));
		_renderer.AfterLayout();
		Core.GraphicsDevice.Present();
	}

	private static void DrawProjectLoadOverlay()
	{
		var viewport = ImGui.GetMainViewport();
		var size = new Num.Vector2(560, 0);

		ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(size, ImGuiCond.Always);

		const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove
			| ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize
			| ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking;

		if (Gui.Begin("##ProjectLoadOverlay", flags))
		{
			ImGui.TextUnformatted($"Loading {ProjectLoadProgress.Title}");
			ImGui.Spacing();

			var phase = ProjectLoadProgress.Phase ?? string.Empty;
			ImGui.TextUnformatted($"{phase}  ({ProjectLoadProgress.PhaseIndex}/{ProjectLoadProgress.PhaseCount})");

			var fraction = Math.Clamp(
				ProjectLoadProgress.PhaseIndex / (float)Math.Max(1, ProjectLoadProgress.PhaseCount), 0f, 1f);
			ImGui.ProgressBar(fraction, new Num.Vector2(-1, 6), string.Empty);

			ImGui.Spacing();
			ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(0.65f, 0.65f, 0.68f, 1f));
			ImGui.TextUnformatted(Elide(ProjectLoadProgress.Detail, 78));
			ImGui.PopStyleColor();
		}

		Gui.End();
	}

	/// <summary>Keeps a long path on one line by dropping the middle, which is the least useful part.</summary>
	private static string Elide(string value, int max)
	{
		if (string.IsNullOrEmpty(value))
			return " ";
		if (value.Length <= max)
			return value;

		var keep = (max - 3) / 2;
		return value[..keep] + "..." + value[^keep..];
	}
}
