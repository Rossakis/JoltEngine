using System;
using System.Linq;
using System.Threading;
using ImGuiNET;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Builders;

/// <summary>Shows the last asset build: per-file outcome, counts, elapsed time and errors.</summary>
public sealed class AssetBuildReportWindow
{
	private bool _open;
	private CancellationTokenSource _cancel;
	private string _filter = "";

	/// <summary>Starts a standalone asset build into the project's Build/Assets folder and shows it.</summary>
	public void BuildNow()
	{
		var project = ProjectManager.Instance.CurrentProject;
		if (project == null || AssetBuildService.IsRunning)
			return;
		var settings = AssetBuildSettingsStore.Get();
		_cancel = new CancellationTokenSource();
		_open = true;
		_ = AssetBuildService.RunAsync(project, settings, AssetBuildSettingsStore.DefaultOutputDirectory(project, settings.Platform), null, false, true, _cancel.Token);
	}

	public void Show() => _open = true;

	public void Draw()
	{
		if (!_open)
			return;

		ImGui.SetNextWindowSize(new Num.Vector2(760, 460), ImGuiCond.Appearing);
		if (Gui.Begin("Asset Build", ref _open, ImGuiWindowFlags.NoCollapse))
		{
			var report = AssetBuildService.LastReport;
			if (report == null)
				ImGuiSafe.TextSafe("No asset build has run yet.");
			else
				DrawReport(report);
		}
		Gui.End();
	}

	private void DrawReport(AssetBuildReport report)
	{
		string status, output;
		int compiled, copied, skipped, failed, total;
		double elapsed;
		bool running, success;
		string[] errors;
		AssetBuildItem[] items;
		lock (report.Lock)
		{
			status = report.Status;
			output = report.OutputDir;
			running = report.Running;
			success = report.Success;
			elapsed = report.Elapsed.TotalSeconds;
			items = report.Items.ToArray();
			errors = report.Errors.ToArray();
			compiled = items.Count(i => i.Outcome == "compiled");
			copied = items.Count(i => i.Outcome == "copied");
			skipped = items.Count(i => i.Outcome == "skipped");
			failed = items.Count(i => i.Outcome == "failed");
			total = items.Length;
		}

		ImGuiSafe.TextSafe(running ? $"Running: {status}" : success ? $"Done in {elapsed:0.0}s" : $"Failed after {elapsed:0.0}s");
		ImGuiSafe.TextSafe($"{compiled} compiled, {copied} copied, {skipped} skipped, {failed} failed of {total}");
		if (!string.IsNullOrEmpty(output))
			ImGuiSafe.TextDisabledSafe(output);

		if (running)
		{
			if (Gui.Button("Cancel"))
				_cancel?.Cancel();
		}
		else if (Gui.Button("Rebuild"))
			BuildNow();
		ImGui.SameLine();
		ImGui.SetNextItemWidth(220);
		Gui.InputTextWithHint("##filter", "filter", ref _filter, 256);

		foreach (var error in errors.Take(10))
			ImGuiSafe.TextColoredSafe(new Num.Vector4(1f, 0.4f, 0.4f, 1f), error);

		VoltageEditorUtils.SmallVerticalSpace();
		if (ImGui.BeginTable("assetbuild-items", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
		{
			ImGui.TableSetupScrollFreeze(0, 1);
			ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Outcome", ImGuiTableColumnFlags.WidthFixed, 80);
			ImGui.TableSetupColumn("Asset", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableHeadersRow();
			foreach (var item in items)
			{
				if (_filter.Length > 0 && !item.RelativePath.Contains(_filter, StringComparison.OrdinalIgnoreCase))
					continue;
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGuiSafe.TextSafe(item.RelativePath);
				ImGui.TableNextColumn();
				ImGuiSafe.TextColoredSafe(OutcomeColor(item.Outcome), item.Outcome ?? "");
				ImGui.TableNextColumn();
				ImGuiSafe.TextSafe(item.AssetName ?? "");
				ImGui.TableNextColumn();
				ImGuiSafe.TextSafe(item.Error ?? item.Reason ?? (item.Processor ?? ""));
			}
			ImGui.EndTable();
		}
	}

	private static Num.Vector4 OutcomeColor(string outcome) => outcome switch
	{
		"compiled" => new Num.Vector4(0.5f, 1f, 0.5f, 1f),
		"failed" => new Num.Vector4(1f, 0.4f, 0.4f, 1f),
		"skipped" => new Num.Vector4(0.6f, 0.6f, 0.6f, 1f),
		"pending" => new Num.Vector4(1f, 0.85f, 0.4f, 1f),
		_ => new Num.Vector4(0.85f, 0.85f, 0.85f, 1f)
	};
}
