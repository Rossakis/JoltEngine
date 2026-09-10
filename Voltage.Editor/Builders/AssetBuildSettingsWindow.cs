using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Builders;

/// <summary>Edits the project's asset-build settings and saves them into ProjectSettings.json.</summary>
public sealed class AssetBuildSettingsWindow
{
	private bool _open;
	private bool _enabled, _compress, _premultiply, _compileAudio, _strip;
	private int _format;
	private string _platform = "";
	private string _include = "";
	private string _exclude = "";
	private string _message;

	private static readonly string[] Formats = { "Color", "Compressed" };

	public void Open()
	{
		var s = AssetBuildSettingsStore.Get();
		_enabled = s.Enabled;
		_compress = s.Compress;
		_premultiply = s.PremultiplyAlpha;
		_compileAudio = s.CompileAudio;
		_strip = s.StripSources;
		_format = string.Equals(s.TextureFormat, "Compressed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
		_platform = s.Platform ?? "DesktopGL";
		_include = string.Join("\n", s.Include ?? new List<string>());
		_exclude = string.Join("\n", s.Exclude ?? new List<string>());
		_message = null;
		_open = true;
	}

	public void Draw()
	{
		if (!_open)
			return;

		ImGui.SetNextWindowSize(new Num.Vector2(520, 0), ImGuiCond.Appearing);
		if (Gui.Begin("Asset Build Settings", ref _open, ImGuiWindowFlags.NoCollapse))
		{
			Gui.Checkbox("Compile assets on build", ref _enabled);
			Gui.InputText("Platform", ref _platform, 64);
			if (ImGui.IsItemHovered())
				ImGuiSafe.SetTooltipSafe("MGCB platform; every desktop target uses DesktopGL. Unknown names save as DesktopGL. Known: " + string.Join(", ", AssetBuildSettingsStore.Platforms));
			Gui.Combo("Texture format", ref _format, Formats, Formats.Length);
			Gui.Checkbox("Premultiply alpha", ref _premultiply);
			Gui.Checkbox("Compress .xnb files", ref _compress);
			Gui.Checkbox("Compile audio", ref _compileAudio);
			if (ImGui.IsItemHovered())
				ImGuiSafe.SetTooltipSafe("Converts wav, ogg and mp3 to PCM .xnb files, which are larger than the sources");
			Gui.Checkbox("Strip sources", ref _strip);
			if (ImGui.IsItemHovered())
				ImGuiSafe.SetTooltipSafe("Leave raw files with a compiled counterpart out of the build output");

			VoltageEditorUtils.SmallVerticalSpace();
			ImGuiSafe.TextSafe("Include globs, one per line");
			Gui.InputTextMultiline("##include", ref _include, 4096, new Num.Vector2(-1, 60));
			ImGuiSafe.TextSafe("Exclude globs, one per line");
			Gui.InputTextMultiline("##exclude", ref _exclude, 4096, new Num.Vector2(-1, 60));

			VoltageEditorUtils.SmallVerticalSpace();
			if (Gui.Button("Save"))
				Save();
			ImGui.SameLine();
			if (Gui.Button("Close"))
				_open = false;
			if (_message != null)
			{
				ImGui.SameLine();
				ImGuiSafe.TextSafe(_message);
			}
		}
		Gui.End();
	}

	private void Save()
	{
		try
		{
			var s = AssetBuildSettingsStore.Get();
			s.Enabled = _enabled;
			s.Compress = _compress;
			s.PremultiplyAlpha = _premultiply;
			s.CompileAudio = _compileAudio;
			s.StripSources = _strip;
			s.TextureFormat = Formats[Math.Clamp(_format, 0, Formats.Length - 1)];
			s.Platform = AssetBuildSettingsStore.IsValidPlatform(_platform.Trim()) ? AssetBuildSettingsStore.MgcbPlatform(_platform) : "DesktopGL";
			_platform = s.Platform;
			s.Include = Lines(_include);
			s.Exclude = Lines(_exclude);
			AssetBuildSettingsStore.Save(ProjectManager.Instance.CurrentProject);
			_message = "Saved";
		}
		catch (Exception ex)
		{
			_message = ex.Message;
		}
	}

	private static List<string> Lines(string text) =>
		text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
