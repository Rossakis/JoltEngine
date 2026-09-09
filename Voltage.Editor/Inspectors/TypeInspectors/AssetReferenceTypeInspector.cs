using System;
using System.IO;
using ImGuiNET;
using Voltage.Editor.Utils;
using Voltage.Editor.Windows;
using Voltage.Serialization;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Inspectors.TypeInspectors;

/// <summary>
/// Inspector for a <see cref="AssetReference"/> field. Renders a drop slot; drag any asset from the
/// Asset Browser onto it to assign it, or click the button to open a searchable picker popup.
/// The reference stores the asset's stable <c>.meta</c> GUID (plus a project-relative hint path),
/// so it survives renaming/moving the file.
/// </summary>
public class AssetReferenceTypeInspector : AbstractTypeInspector
{
	private bool _showPicker;
	private string _pickerSearch = string.Empty;

	private AssetSlotFilter _filter;
	private AssetSlotFilter Filter => _filter ??= AssetSlotFilter.For(MemberInfo);

	public override void DrawMutable()
	{
		var current = (AssetReference)_getter(_target);

		ImGuiSafe.TextSafe(_name);
		ImGui.SameLine();

		var label = current.IsValid
			? (current.AssetName ?? current.AssetPath)
			: $"None ({Filter.DisplayTypeName})";

		var buttonColor = current.IsValid
			? new Num.Vector4(0.25f, 0.45f, 0.55f, 0.9f)
			: new Num.Vector4(0.22f, 0.22f, 0.22f, 0.9f);

		ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, buttonColor with { W = 1f });

		// Single click reveals in the Asset Browser; double click opens the picker.
		var pressed = Gui.Button($"{label}##assetref_{_scopeId}", new Num.Vector2(-1, 0));
		var doubleClicked = ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

		if (doubleClicked || (pressed && !current.IsValid))
			_showPicker = true;
		else if (pressed)
			AssetBrowserWindow.PingAsset(current.ResolvePath());

		ImGui.PopStyleColor(2);

		if (current.IsValid && ImGui.IsItemHovered())
		{
			ImGui.BeginTooltip();
			ImGuiSafe.TextSafe($"Path: {current.AssetPath}");
			ImGuiSafe.TextSafe($"GUID: {current.AssetGuid}");
			ImGuiSafe.TextSafe("Click to reveal in the Asset Browser — double-click to change.");
			ImGui.EndTooltip();
		}
		else
		{
			HandleTooltip();
		}

		// Accept a drag from the Asset Browser (VOLTAGE_ASSET_REF). The dragged editor-side
		// AssetReference carries the GUID + project-relative hint path.
		if (ImGui.BeginDragDropTarget())
		{
			var dragged = AssetBrowserWindow.DraggedReference;
			var draggedOk = dragged.IsEmpty || !Filter.IsConstrained ||
							Filter.Accepts(Voltage.Editor.Assets.AssetDatabase.Instance?.Resolve(dragged), dragged.Guid);

			var payload = draggedOk
				? ImGui.AcceptDragDropPayload(AssetBrowserWindow.DragDropPayloadId)
				: default;
			bool accepted;
			unsafe { accepted = payload.NativePtr != null; }

			if (accepted && !AssetBrowserWindow.DraggedReference.IsEmpty)
			{
				var dr = AssetBrowserWindow.DraggedReference;
				AssetBrowserWindow.DraggedReference = Voltage.Editor.Assets.AssetReference.Empty;

				var assetRef = new AssetReference
				{
					AssetGuid = dr.Guid,
					AssetPath = dr.HintPath,
					AssetName = Path.GetFileNameWithoutExtension(dr.HintPath),
				};
				SetValueWithUndo(assetRef, $"Assign {_name}");
			}

			ImGui.EndDragDropTarget();
		}

		// Right-click: clear.
		if (current.IsValid && Gui.BeginPopupContextItem($"assetref_ctx_{_scopeId}"))
		{
			if (Gui.Selectable("Clear"))
				SetValueWithUndo(default(AssetReference), $"Clear {_name}");
			Gui.EndPopup();
		}

		if (_showPicker)
		{
			ImGui.OpenPopup($"assetref_picker_{_scopeId}");
			_pickerSearch = string.Empty;
			_showPicker = false;
		}

		DrawPickerPopup(current);
	}

	private void DrawPickerPopup(AssetReference current)
	{
		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(420, 460), ImGuiCond.Appearing);

		bool open = true;
		if (!Gui.BeginPopupModal($"assetref_picker_{_scopeId}", ref open, ImGuiWindowFlags.NoResize))
			return;

		ImGuiSafe.TextColoredSafe(new Num.Vector4(0.3f, 0.8f, 1f, 1f), $"{_name}  ({Filter.DisplayTypeName})");
		ImGui.Separator();

		ImGui.SetNextItemWidth(-1);
		Gui.InputTextWithHint("##assetsearch", "Search...", ref _pickerSearch, 128);
		ImGui.Separator();

		if (Gui.Selectable($"  None ({Filter.DisplayTypeName})", !current.IsValid))
		{
			SetValueWithUndo(default(AssetReference), $"Clear {_name}");
			ImGui.CloseCurrentPopup();
		}

		ImGui.Separator();

		var db = Voltage.Editor.Assets.AssetDatabase.Instance;
		if (db != null)
		{
			string lower = _pickerSearch.ToLowerInvariant();
			bool filtering = !string.IsNullOrEmpty(_pickerSearch);
			DrawAssetNodes(db.RootNodes, current, lower, filtering);
		}

		ImGui.Separator();
		VoltageEditorUtils.SmallVerticalSpace();
		if (VoltageEditorUtils.CenteredButton("Cancel", 0.5f))
			ImGui.CloseCurrentPopup();

		Gui.EndPopup();
	}

	private void DrawAssetNodes(
		System.Collections.Generic.IReadOnlyList<Voltage.Editor.Assets.AssetFolderNode> nodes,
		AssetReference current,
		string lower,
		bool filtering)
	{
		foreach (var folder in nodes)
		{
			// When filtering, recurse without drawing folder headers so matching files surface at top level.
			if (filtering)
			{
				DrawFilteredFiles(folder, current, lower);
				DrawAssetNodes(folder.ChildFolders, current, lower, filtering);
				continue;
			}

			bool hasVisibleContent = FolderHasAcceptableFiles(folder);
			if (!hasVisibleContent)
				continue;

			bool open = Gui.TreeNodeEx(folder.Label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
			if (open)
			{
				foreach (var item in folder.Files)
					DrawAssetItem(item, current);

				DrawAssetNodes(folder.ChildFolders, current, lower, filtering);
				ImGui.TreePop();
			}
		}
	}

	private void DrawFilteredFiles(Voltage.Editor.Assets.AssetFolderNode folder, AssetReference current, string lower)
	{
		foreach (var item in folder.Files)
		{
			if (item.FileName.ToLowerInvariant().Contains(lower))
				DrawAssetItem(item, current);
		}
	}

	private void DrawAssetItem(Voltage.Editor.Assets.AssetItem item, AssetReference current)
	{
		var db    = Voltage.Editor.Assets.AssetDatabase.Instance;
		var edRef = db?.GetReference(item.AbsolutePath) ?? Assets.AssetReference.Empty;

		if (Filter.IsConstrained && !Filter.Accepts(item.AbsolutePath, edRef.Guid))
			return;
		bool isCurrent = current.IsValid && current.AssetGuid != Guid.Empty && current.AssetGuid == edRef.Guid;

		if (Gui.Selectable($"  {item.FileName}", isCurrent))
		{
			var assetRef = new AssetReference
			{
				AssetGuid = edRef.Guid,
				AssetPath = edRef.HintPath,
				AssetName = Path.GetFileNameWithoutExtension(item.FileName),
			};
			SetValueWithUndo(assetRef, $"Assign {_name} = {item.FileName}");
			ImGui.CloseCurrentPopup();
		}

		if (ImGui.IsItemHovered())
		{
			ImGui.BeginTooltip();
			ImGuiSafe.TextSafe(item.AbsolutePath);
			ImGui.EndTooltip();
		}
	}

	private bool FolderHasAcceptableFiles(Voltage.Editor.Assets.AssetFolderNode folder)
	{
		if (!Filter.IsConstrained)
			return FolderHasFiles(folder);

		var db = Voltage.Editor.Assets.AssetDatabase.Instance;
		foreach (var file in folder.Files)
		{
			var guid = db?.GetReference(file.AbsolutePath).Guid ?? Guid.Empty;
			if (Filter.Accepts(file.AbsolutePath, guid))
				return true;
		}

		foreach (var child in folder.ChildFolders)
			if (FolderHasAcceptableFiles(child))
				return true;

		return false;
	}

	internal static bool FolderHasFilesInternal(Voltage.Editor.Assets.AssetFolderNode folder) => FolderHasFiles(folder);

	private static bool FolderHasFiles(Voltage.Editor.Assets.AssetFolderNode folder)
	{
		if (folder.Files.Count > 0) return true;
		foreach (var child in folder.ChildFolders)
			if (FolderHasFiles(child)) return true;
		return false;
	}


	/// <summary>
	/// Shared helper used by <see cref="ListInspector"/> to build an <see cref="AssetReference"/>
	/// from a drag-drop payload already accepted by the caller.
	/// </summary>
	internal static AssetReference BuildFromDrag()
	{
		var dr = AssetBrowserWindow.DraggedReference;
		AssetBrowserWindow.DraggedReference = Voltage.Editor.Assets.AssetReference.Empty;

		return new AssetReference
		{
			AssetGuid = dr.Guid,
			AssetPath = dr.HintPath,
			AssetName = Path.GetFileNameWithoutExtension(dr.HintPath),
		};
	}
}
