using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Voltage.Project;
using Num = System.Numerics;

namespace Voltage.Editor.Builders;

/// <summary>Edits the project's asset-build settings and rules, scaffolds pipeline extensions, and saves into ProjectSettings.json.</summary>
public sealed class AssetBuildSettingsWindow
{
	private sealed class RuleDraft
	{
		public string Name = "";
		public string Extensions = "";
		public int Action;
		public string Importer = "";
		public string Processor = "";
		public string Parameters = "";
		public string Assembly = "";
	}

	private static readonly string[] Formats = { "Color", "Compressed" };
	private static readonly string[] Actions = { "compile", "copy", "skip" };

	private bool _open;
	private bool _enabled, _compress, _premultiply, _compileAudio;
	private int _format;
	private string _platform = "";
	private string _include = "";
	private string _exclude = "";
	private string _message;
	private readonly List<RuleDraft> _rules = new();
	private string _scaffoldName = "";
	private string _scaffoldExtension = "";
	private bool _showScaffold;

	public void Open()
	{
		var s = AssetBuildSettingsStore.Get();
		_enabled = s.Enabled;
		_compress = s.Compress;
		_premultiply = s.PremultiplyAlpha;
		_compileAudio = s.CompileAudio;
		_format = string.Equals(s.TextureFormat, "Compressed", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
		_platform = s.Platform ?? "DesktopGL";
		_include = string.Join("\n", s.Include ?? new List<string>());
		_exclude = string.Join("\n", s.Exclude ?? new List<string>());
		_rules.Clear();
		foreach (var r in s.Rules ?? new List<ProjectSettings.AssetBuildRule>())
			_rules.Add(new RuleDraft
			{
				Name = r.Name ?? "",
				Extensions = string.Join(", ", AssetBuildPipeline.NormalisedExtensions(r)),
				Action = Math.Max(0, Array.IndexOf(Actions, (r.Action ?? "compile").ToLowerInvariant())),
				Importer = r.Importer ?? "",
				Processor = r.Processor ?? "",
				Parameters = string.Join(", ", r.Parameters ?? new List<string>()),
				Assembly = r.Assembly ?? ""
			});
		_message = null;
		_open = true;
	}

	/// <summary>Opens the window with the extension scaffold section expanded.</summary>
	public void OpenScaffold()
	{
		Open();
		_showScaffold = true;
	}

	public void Draw()
	{
		if (!_open)
			return;

		ImGui.SetNextWindowSize(new Num.Vector2(620, 0), ImGuiCond.Appearing);
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

			VoltageEditorUtils.SmallVerticalSpace();
			ImGuiSafe.TextSafe("Include globs, one per line");
			Gui.InputTextMultiline("##include", ref _include, 4096, new Num.Vector2(-1, 60));
			ImGuiSafe.TextSafe("Exclude globs, one per line");
			Gui.InputTextMultiline("##exclude", ref _exclude, 4096, new Num.Vector2(-1, 60));

			VoltageEditorUtils.SmallVerticalSpace();
			DrawRules();
			DrawScaffold();

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

	private void DrawRules()
	{
		if (!Gui.CollapsingHeader("Rules", ImGuiTreeNodeFlags.DefaultOpen))
			return;

		ImGuiSafe.TextDisabledSafe("A rule routes its extensions through an importer and processor, or copies or skips them; it overrides the built-in table.");
		for (var i = 0; i < _rules.Count; i++)
		{
			var rule = _rules[i];
			ImGui.PushID(i);
			Gui.InputText("Name", ref rule.Name, 64);
			Gui.InputText("Extensions", ref rule.Extensions, 256);
			if (ImGui.IsItemHovered())
				ImGuiSafe.SetTooltipSafe("Comma separated, e.g. .lvl, .map");
			Gui.Combo("Action", ref rule.Action, Actions, Actions.Length);
			if (rule.Action == 0)
			{
				Gui.InputText("Importer", ref rule.Importer, 128);
				Gui.InputText("Processor", ref rule.Processor, 128);
				Gui.InputText("Parameters", ref rule.Parameters, 512);
				if (ImGui.IsItemHovered())
					ImGuiSafe.SetTooltipSafe("Comma separated Key=Value pairs passed as processor parameters");
				Gui.InputText("Assembly", ref rule.Assembly, 512);
				if (ImGui.IsItemHovered())
					ImGuiSafe.SetTooltipSafe("Project-relative DLL with the importer and processor; empty for MonoGame's built-in ones. A DLL inside a one-csproj folder is rebuilt when its sources change.");
			}
			if (Gui.Button("Remove rule"))
			{
				_rules.RemoveAt(i);
				ImGui.PopID();
				break;
			}
			ImGui.Separator();
			ImGui.PopID();
		}
		if (Gui.Button("Add rule"))
			_rules.Add(new RuleDraft());
	}

	private void DrawScaffold()
	{
		ImGui.SetNextItemOpen(_showScaffold, ImGuiCond.Once);
		if (!Gui.CollapsingHeader("New pipeline extension"))
			return;

		ImGuiSafe.TextDisabledSafe("Writes Pipeline/<Name> with an importer, processor and writer, Scripts/Content/<Name>Asset.cs with the runtime type and reader, and a rule for the extension.");
		Gui.InputText("Name", ref _scaffoldName, 64);
		Gui.InputText("Extension", ref _scaffoldExtension, 16);
		if (Gui.Button("Scaffold extension"))
		{
			try
			{
				var result = AssetBuildScaffold.Create(ProjectManager.Instance.CurrentProject, _scaffoldName, _scaffoldExtension);
				_message = $"Created {result.Folder}";
				Open();
				_showScaffold = true;
			}
			catch (Exception ex)
			{
				_message = ex.Message;
			}
		}
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
			s.TextureFormat = Formats[Math.Clamp(_format, 0, Formats.Length - 1)];
			s.Platform = AssetBuildSettingsStore.IsValidPlatform(_platform.Trim()) ? AssetBuildSettingsStore.MgcbPlatform(_platform) : "DesktopGL";
			_platform = s.Platform;
			s.Include = Lines(_include);
			s.Exclude = Lines(_exclude);
			var rules = _rules.Where(r => r.Name.Trim().Length > 0).Select(r => new ProjectSettings.AssetBuildRule
			{
				Name = r.Name.Trim(),
				Extensions = Split(r.Extensions),
				Action = Actions[Math.Clamp(r.Action, 0, Actions.Length - 1)],
				Importer = r.Importer.Trim(),
				Processor = r.Processor.Trim(),
				Parameters = Split(r.Parameters),
				Assembly = r.Assembly.Trim()
			}).ToList();
			var project = ProjectManager.Instance.CurrentProject;
			foreach (var rule in rules)
			{
				var problem = AssetBuildRuleValidation.Validate(rule, project.ProjectPath, out _);
				if (problem != null)
				{
					_message = $"Rule {rule.Name}: {problem}";
					return;
				}
			}
			s.Rules = rules;
			AssetBuildSettingsStore.Save(project);
			_message = "Saved";
		}
		catch (Exception ex)
		{
			_message = ex.Message;
		}
	}

	private static List<string> Lines(string text) =>
		text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

	private static List<string> Split(string text) =>
		text.Split(',', '\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
