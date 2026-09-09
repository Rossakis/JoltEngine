using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Voltage.Editor.Hotkeys;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Voltage.Editor.DebugUtils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Editor.Undo.EntityActions;
using Voltage.Editor.Serialization;
using Voltage.Editor.Utils;
using Voltage.Persistence;
using Voltage.Serialization;

namespace Voltage.Editor.Inspectors.SceneGraphPanes;

public class EntityPane
{
	#region Fields and Properties

    private const int MIN_ENTITIES_FOR_CLIPPER = 100;
    private Entity _previousEntity;

	// Inner drops allowed here: dropping ON a row reparents, and opens no seam.
	private readonly Voltage.Editor.Utils.DragReorderStrip _dragReorder = new(DragDropPayloadType);


	// One entry per drawn line. The clipper must index this, not Scene.Entities, which skips
	// children and collapsed subtrees.
	private readonly List<Row> _rows = new();

	private readonly struct Row
	{
		public readonly Entity Entity;
		public readonly int Depth;

		public Row(Entity entity, int depth)
		{
			Entity = entity;
			Depth = depth;
		}
	}

	private string _entitySearch = string.Empty;

	public List<Entity> SelectedEntities => _selectedEntities;
	private ImGuiManager _imGuiManager;
	private List<Entity> _selectedEntities = new();
	private Entity _lastRangeSelectEntity;

	// Drag-drop state
	private const string DragDropPayloadType = "ENTITY_DRAG";
	private bool _isDragging;
	private Entity _dragDropTarget;
	private bool _dragDropTargetIsRoot; // dropping onto root = unparent

	// When clicking an already-selected entity with no modifier, we
	// wait for mouse release without drag before collapsing the selection.
	private Entity _pendingSelectEntity;

	// Inline rename state. When _renamingEntity is set, that row shows an editable text field.
	private Entity _renamingEntity;
	private string _renameBuffer = string.Empty;
	private string _renameStartValue = string.Empty;
	private bool _renameFocusPending;

	// Delayed click-to-rename (file-explorer / Unity style): clicking an already-selected entity arms a
	// rename candidate; if no double-click or drag arrives within the double-click window, rename starts.
	private Entity _renameCandidateEntity;
	private double _renameCandidateTime;

	public void SetSelectedEntity(Entity entity, bool ctrlDown, bool shiftDown = false)
	{
		if (entity == null && !ctrlDown && !shiftDown)
			return;

		var hierarchyList = _imGuiManager.SceneGraphWindow.BuildHierarchyList();

		if (shiftDown && _lastRangeSelectEntity != null)
		{
			int startIdx = hierarchyList.IndexOf(_lastRangeSelectEntity);
			int endIdx = hierarchyList.IndexOf(entity);
			if (startIdx != -1 && endIdx != -1)
			{
				int minIdx = Math.Min(startIdx, endIdx);
				int maxIdx = Math.Max(startIdx, endIdx);
				if (ctrlDown)
				{
					// Add range to current selection
					for (int i = minIdx; i <= maxIdx; i++)
					{
						if (!_selectedEntities.Contains(hierarchyList[i]))
							_selectedEntities.Add(hierarchyList[i]);
					}
				}
				else
				{
					// Replace selection with range
					_selectedEntities.Clear();
					for (int i = minIdx; i <= maxIdx; i++)
						_selectedEntities.Add(hierarchyList[i]);
				}
			}
		}
		else if (ctrlDown)
		{
			if (_selectedEntities.Contains(entity))
				_selectedEntities.Remove(entity);
			else
				_selectedEntities.Add(entity);
			_lastRangeSelectEntity = entity;
		}
		else
		{
			_selectedEntities.Clear();
			_selectedEntities.Add(entity);
			_lastRangeSelectEntity = entity;
		}
	}
	#endregion

	#region Search

	public bool IsFiltering => !string.IsNullOrWhiteSpace(_entitySearch);

	/// <summary>Name filter for the entity tree. Matches show with the parents leading to them, force-opened.</summary>
	public void DrawSearchBar()
	{
		var clearWidth = ImGui.GetFrameHeight();

		var inputWidth = Math.Max(60f,
			ImGui.GetContentRegionAvail().X - clearWidth - ImGui.GetStyle().ItemSpacing.X);

		ImGui.SetNextItemWidth(inputWidth);
		Gui.InputTextWithHint("##EntitySearch", "Search entities...", ref _entitySearch, 128);

		ImGui.SameLine();

		ImGui.BeginDisabled(!IsFiltering);
		if (Gui.Button("x##ClearEntitySearch", new System.Numerics.Vector2(clearWidth, 0)))
			_entitySearch = string.Empty;
		ImGui.EndDisabled();

		if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			ImGui.SetTooltip("Clear the search.");

		if (IsFiltering)
			ImGuiSafe.TextDisabledSafe($"{_rows.Count} row(s) match \"{_entitySearch.Trim()}\"");
	}

	#endregion

	#region Main Draw Entry Point

	/// <summary>Flattens the hierarchy into the rows that will be drawn: roots plus the descendants of expanded entities.</summary>
	private void BuildVisibleRows()
	{
		_rows.Clear();

		var entities = Core.Scene?.Entities;
		if (entities == null)
			return;

		var filter = IsFiltering ? _entitySearch.Trim() : null;

		for (var i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (entity.Transform.Parent == null)
				AppendRow(entity, 0, filter);
		}
	}

	/// <summary>Appends the entity and its visible descendants. False when a filter is active and nothing below matched, in which case its rows are rolled back.</summary>
	private bool AppendRow(Entity entity, int depth, string filter)
	{
		var start = _rows.Count;
		_rows.Add(new Row(entity, depth));

		var expanded = filter != null || _imGuiManager.SceneGraphWindow.ExpandedEntities.Contains(entity);
		var keptChild = false;

		if (expanded)
		{
			for (var i = 0; i < entity.Transform.ChildCount; i++)
				keptChild |= AppendRow(entity.Transform.GetChild(i).Entity, depth + 1, filter);
		}

		if (filter == null)
			return true;

		var selfMatch = entity.Name != null &&
			entity.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

		if (selfMatch || keptChild)
			return true;

		_rows.RemoveRange(start, _rows.Count - start);
		return false;
	}

	/// <summary>
	/// Main entry point for drawing the entity pane UI and gizmos.
	/// </summary>
	public unsafe void Draw()
	{
		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		BuildVisibleRows();

		_dragReorder.BeginFrame(_selectedEntities.Count, ImGui.GetTextLineHeightWithSpacing());

		// The clipper needs uniform row heights, and a sliding strip is not one. Rows are only all
		// submitted during a drag, which is also when they all must exist to be drop targets.
		if (_rows.Count > MIN_ENTITIES_FOR_CLIPPER && !_dragReorder.IsDragging)
		{
			var clipperPtr = ImGuiNative.ImGuiListClipper_ImGuiListClipper();
			var clipper = new ImGuiListClipperPtr(clipperPtr);

			clipper.Begin(_rows.Count, -1);

			while (clipper.Step())
				for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
					DrawEntityRow(_rows[i], i);

			ImGuiNative.ImGuiListClipper_destroy(clipperPtr);
		}
		else
		{
			for (var i = 0; i < _rows.Count; i++)
			{
				DrawReorderStrip(i);
				DrawEntityRow(_rows[i], i);
			}

			DrawReorderStrip(_rows.Count);
		}

		_dragReorder.AutoScroll();

		if (_rows.Count == 0 && IsFiltering)
			ImGuiSafe.TextDisabledSafe($"No entity matches \"{_entitySearch}\".");


		// Unparent
		Gui.InvisibleButton("##drop_root", new System.Numerics.Vector2(-1, 1));
		if (ImGui.IsItemHovered())
			ImGui.SetTooltip("Drop here to unparent");

		if (ImGui.BeginDragDropTarget())
		{
			unsafe
			{
				var payload = ImGui.AcceptDragDropPayload(DragDropPayloadType,
					ImGuiDragDropFlags.AcceptNoDrawDefaultRect);

				if (payload.NativePtr != null)
					ReparentSelectedEntities(null, -1);
			}
			ImGui.EndDragDropTarget();
		}

		VoltageEditorUtils.MediumVerticalSpace();
		EntityDuplicationAndDeletion();

		PromotePendingRename();

		DrawPaneContextMenu();
		HandleCreateEntityShortcut();
	}

	#region Entity Creation

	/// <summary>
	/// Right-click context menu for the entity pane's empty space. Offers a single "Add Entity"
	/// item that immediately creates a new empty entity in the current scene (no dialog).
	/// Scoped to the current window so it only appears when right-clicking inside the pane and
	/// not over an entity row (which has its own context menu).
	/// </summary>
	private void DrawPaneContextMenu()
	{
		if (Gui.BeginPopupContextWindow("entityPaneContextMenu",
			ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
		{
			if (Gui.MenuItem("Add Entity", EditorHotkeys.MenuLabel(EditorHotkeys.NewEntity)))
				CreateEntity();

			// Also here, so pasting into an empty scene does not require an existing entity to right-click.
			ImGui.BeginDisabled(!EntityClipboard.HasContent);
			if (Gui.MenuItem("Paste Entity", EditorHotkeys.MenuLabel(EditorHotkeys.PasteEntity)))
				PasteEntities();
			ImGui.EndDisabled();

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				ImGui.SetTooltip("Pastes into the current scene - copy in one scene, switch, then paste here.");

			Gui.EndPopup();
		}
	}

	/// <summary>
	/// Shift+N (edge-triggered) creates a single new empty entity while the pane is focused/hovered.
	/// Debounced via ImGui.IsKeyPressed so holding the keys does not spam entities, and suppressed
	/// while a text field is capturing keyboard input (e.g. inline rename).
	/// </summary>
	private void HandleCreateEntityShortcut()
	{
		if (!Core.IsEditMode)
			return;

		// Only act when the pane's window is the interaction target.
		if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
			!ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
			return;

		// Never fire while typing (inline rename, prefab filter, etc.).
		if (ImGui.GetIO().WantTextInput || ImGui.GetIO().WantCaptureKeyboard)
			return;

		if (EditorHotkeys.Pressed(EditorHotkeys.NewEntity))
			CreateEntity();
	}

	/// <summary>
	/// Creates a single new empty entity in the current scene at the camera centre, selects it,
	/// opens it in the main inspector, and pushes an undoable <see cref="EntityCreateDeleteUndoAction"/>.
	/// Shared by the pane context menu and the Shift+N shortcut so all creation paths are identical.
	/// </summary>
	private void CreateEntity()
	{
		if (Core.Scene == null)
			return;

		var entity = new Entity("Entity", Entity.InstanceType.Serialized);
		entity.Type = Entity.InstanceType.Serialized;
		entity.Name = Core.Scene.GetUniqueEntityName("Entity", entity);
		entity.Transform.Position = GetCameraCenterWorld();

		Core.Scene.AddEntity(entity);

		EditorChangeTracker.PushUndo(
			new EntityCreateDeleteUndoAction(Core.Scene, entity, wasCreated: true,
				$"Create Entity {entity.Name}"),
			entity,
			$"Create Entity {entity.Name}"
		);

		SetSelectedEntity(entity, false);
		_imGuiManager.MainEntityInspectorWindow.DelayedSetEntity(entity);
	}

	/// <summary>
	/// World-space center of the current editor camera view, so new entities land in the
	/// middle of the viewport rather than its top-left corner.
	/// </summary>
	private static Vector2 GetCameraCenterWorld()
	{
		var rt = Core.Scene.SceneRenderTargetSize;
		return Core.Scene.Camera.ScreenToWorldPoint(new Vector2(rt.X * 0.5f, rt.Y * 0.5f));
	}

	#endregion

	/// <summary>
	/// Promotes an armed rename candidate into an actual inline rename once the double-click window has
	/// elapsed without a double-click (which would have cancelled it) or a drag. Waiting for the window —
	/// and for the mouse button to be released — distinguishes a slow "click… click" rename from a fast
	/// double-click and from the start of a drag.
	/// </summary>
	private void PromotePendingRename()
	{
		if (_renameCandidateEntity == null)
			return;

		if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
			return; // still holding (or mid double-click / drag) — wait

		double window = ImGui.GetIO().MouseDoubleClickTime + 0.02; // small epsilon past the dbl-click window
		if (ImGui.GetTime() - _renameCandidateTime < window)
			return;

		// Only rename if it is still the sole selection (selection may have moved on).
		if (_selectedEntities.Count == 1 && _selectedEntities[0] == _renameCandidateEntity)
			BeginEntityRename(_renameCandidateEntity);

		_renameCandidateEntity = null;
	}

	/// <summary>Enters inline-rename mode for <paramref name="entity"/>, seeding the buffer with its name.</summary>
	private void BeginEntityRename(Entity entity)
	{
		if (entity == null)
			return;

		_renamingEntity = entity;
		_renameBuffer = entity.Name;
		_renameStartValue = entity.Name;
		_renameFocusPending = true;
		_renameCandidateEntity = null;
	}

	/// <summary>
	/// Draws the inline rename text field for the entity currently being renamed. Enter (or clicking away)
	/// commits; Escape cancels; an empty/whitespace name commits as a no-op.
	/// </summary>
	private void DrawEntityRenameInput(Entity entity)
	{
		if (_renameFocusPending)
		{
			ImGui.SetKeyboardFocusHere();
			_renameFocusPending = false;
		}

		ImGui.SetNextItemWidth(-1);
		bool enter = Gui.InputText($"##entrename_{entity.Id}", ref _renameBuffer, 64,
			ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);

		// Escape must be checked before deactivation: pressing Escape also deactivates the field.
		bool escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false);
		bool deactivated = ImGui.IsItemDeactivated();

		if (enter || (deactivated && !escape))
		{
			CommitEntityRename(entity, _renameBuffer);
			_renamingEntity = null;
		}
		else if (escape)
		{
			_renamingEntity = null; // cancel — no change
		}
	}

	/// <summary>
	/// Applies the new name to <paramref name="entity"/> (no-op for empty/unchanged), pushing an undo entry
	/// that mirrors the EntityInspector's name-edit so rename is undoable from the hierarchy too.
	/// </summary>
	private void CommitEntityRename(Entity entity, string newName)
	{
		newName = newName?.Trim();
		if (string.IsNullOrEmpty(newName) || newName == _renameStartValue)
			return;

		entity.Name = newName;

		EditorChangeTracker.PushUndo(
			new Voltage.Editor.Undo.PropertyActions.GenericValueChangeAction(
				entity,
				typeof(Entity).GetProperty(nameof(Entity.Name)),
				_renameStartValue,
				entity.Name,
				$"{_renameStartValue}.Name"),
			entity,
			$"Rename {_renameStartValue} → {entity.Name}");

		_imGuiManager?.RefreshMainEntityInspector();
	}

	/// <summary>
	/// Reparents all currently selected entities to <paramref name="newParent"/> at <paramref name="insertIndex"/>.
	/// Passing null for newParent unparents them (makes them scene roots).
	/// Passing -1 for insertIndex appends at the end.
	/// Skips entities that would create a cycle (i.e., dropping onto a descendant).
	/// </summary>
	private void ReparentSelectedEntities(Transform newParent, int insertIndex = -1)
	{
		var entries = new List<(Entity entity, Transform oldParent, int oldIndex, Transform newParent, int newIndex)>();

		// Tree order, not click order: a multi-entity drop should not come out backwards.
		var moving = OrderedSelection();

		// Inserting everything at one index would reverse the group.
		var placed = 0;

		foreach (var entity in moving)
		{
			// Skip if newParent is the entity itself or a descendant of it (cycle guard)
			if (newParent != null && IsDescendantOf(newParent, entity.Transform))
				continue;

			// Skip SceneRequired entities — they must stay at root level as designed
			if (entity.Type == Entity.InstanceType.SceneRequired)
				continue;

			// An entity travels with its ancestor; reparenting it too would flatten the subtree.
			if (HasSelectedAncestor(entity))
				continue;

			// Capture old position
			int oldIndex = entity.Transform.Parent != null
				? entity.Transform.Parent.Children.IndexOf(entity.Transform)
				: Core.Scene.Entities.EntityFastList.IndexOf(entity);

			int actualInsert = insertIndex;
			if (actualInsert >= 0)
			{
				if (newParent == entity.Transform.Parent)
				{
					if (oldIndex >= 0 && oldIndex < actualInsert)
						actualInsert--;
				}

				actualInsert += placed;
				placed++;
			}

			entries.Add((entity, entity.Transform.Parent, oldIndex, newParent, actualInsert));
		}

		if (entries.Count == 0)
			return;

		foreach (var entry in entries)
		{
			// Capture world transform before the parent changes so we can reapply it as local
			var worldPos = entry.entity.Transform.Position;
			var worldRot = entry.entity.Transform.Rotation;
			var worldScale = entry.entity.Transform.Scale;

			entry.entity.Transform.SetParentAt(entry.newParent, entry.newIndex);

			if (entry.newParent == null)
			{
				// newIndex is a final position (-1 appends); MoveRootTo converts it for MoveEntityToIndex.
				var rootIndex = entry.newIndex >= 0 ? entry.newIndex : Core.Scene.Entities.Count - 1;
				EntityReparentUndoAction.MoveRootTo(entry.entity, rootIndex);
			}

			// Force-recalculate locals from world values, bypassing equality guards
			entry.entity.Transform.RecomputeLocalsFromWorld(worldPos, worldRot, worldScale);
		}

		EditorChangeTracker.PushUndo(
			new EntityReparentUndoAction(entries, $"Reparent {string.Join(", ", entries.Select(e => e.entity.Name))}"),
			entries[0].entity,
			$"Reparent {string.Join(", ", entries.Select(e => e.entity.Name))}"
		);
	}

	/// <summary>The selected entities in the order the tree shows them, with anything not on screen appended.</summary>
	private List<Entity> OrderedSelection()
	{
		var ordered = new List<Entity>(_selectedEntities.Count);

		foreach (var row in _rows)
		{
			if (_selectedEntities.Contains(row.Entity) && !ordered.Contains(row.Entity))
				ordered.Add(row.Entity);
		}

		foreach (var entity in _selectedEntities)
		{
			if (!ordered.Contains(entity))
				ordered.Add(entity);
		}

		return ordered;
	}

	/// <summary>True when an ancestor of this entity is also selected, and so is already carrying it.</summary>
	private bool HasSelectedAncestor(Entity entity)
	{
		for (var t = entity?.Transform?.Parent; t != null; t = t.Parent)
		{
			if (t.Entity != null && _selectedEntities.Contains(t.Entity))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Returns true if <paramref name="potentialDescendant"/> is equal to or a descendant of <paramref name="ancestor"/>.
	/// </summary>
	private static bool IsDescendantOf(Transform potentialDescendant, Transform ancestor)
	{
		var t = potentialDescendant;
		while (t != null)
		{
			if (t == ancestor)
				return true;
			t = t.Parent;
		}
		return false;
	}
	#endregion

    #region Entity Tree Rendering and Interaction

    // Draws one row. Children are separate rows from BuildVisibleRows, so every row is one line
    // of uniform height, which the clipper requires.
    private void DrawEntityRow(Row row, int rowIndex)
    {
        var entity = row.Entity;

        var indent = row.Depth * ImGui.GetStyle().IndentSpacing;
        if (indent > 0f)
            ImGui.Indent(indent);

        try
        {
            DrawEntityRowContent(entity, rowIndex);
        }
        finally
        {
            if (indent > 0f)
                ImGui.Unindent(indent);
        }
    }

	/// <summary>The landing strip for a drop between rows. Seams open only for reorders, never for a reparent or the unparent zone.</summary>
	private void DrawReorderStrip(int slot)
	{
		if (!_dragReorder.TryGetGap(slot, out var height))
			return;

		if (_dragReorder.DrawStrip(slot, height, false))
			DropIntoSeam(slot);
	}

	/// <summary>Drops the selection into the seam above the slot, as that row's siblings. The seam's meaning comes from the row below it, never from the row the cursor armed it from: one gap can sit between two different parents.</summary>
	private void DropIntoSeam(int slot)
	{
		// The rows are about to be rebuilt, so a kept gap would hang open in the new tree.
		_dragReorder.Reset();

		if (slot < 0 || slot >= _rows.Count)
		{
			ReparentSelectedEntities(null, -1);
			return;
		}

		var below = _rows[slot].Entity;
		var parent = below.Transform.Parent;

		var index = parent != null
			? parent.Children.IndexOf(below.Transform)
			: Core.Scene.Entities.EntityFastList.IndexOf(below);

		ReparentSelectedEntities(parent, index);
	}

    private void DrawEntityRowContent(Entity entity, int rowIndex)
    {
        bool isSelected = _selectedEntities.Contains(entity);
        ImGui.PushID((int)entity.Id);

        // While this entity is being renamed, show an inline editable field in place of its tree row.
        if (_renamingEntity == entity)
        {
            DrawEntityRenameInput(entity);
            ImGui.PopID();
            return;
        }

        var flags = ImGuiTreeNodeFlags.NoTreePushOnOpen | (isSelected ? ImGuiTreeNodeFlags.Selected : 0);
        bool isExpanded = IsFiltering || _imGuiManager.SceneGraphWindow.ExpandedEntities.Contains(entity);
        if (entity.Transform.ChildCount > 0)
            ImGui.SetNextItemOpen(isExpanded, ImGuiCond.Always);

		// Set special color for entities based on type
		bool isPrefab = entity.Type == Entity.InstanceType.SerializedPrefab;
		bool isNonSerialized = entity.Type == Entity.InstanceType.NonSerialized;
		bool isSceneRequired = entity.Type == Entity.InstanceType.SceneRequired;

		if (isSceneRequired)
		{
			// Light orange
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1.0f, 0.8f, 0.4f, 1.0f));
		}
		else if (isPrefab)
		{
			// Orange 
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1.0f, 0.6f, 0.2f, 1.0f));
		}
		else if (isNonSerialized)
		{
			// Green
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.2f, 1.0f, 0.2f, 1.0f));
		}

		// Draw tree node
		if (entity.Transform.ChildCount > 0)
			Gui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.OpenOnArrow | flags);
		else
			Gui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.OpenOnArrow | flags);

		if (isSceneRequired || isPrefab || isNonSerialized)
		{
			ImGui.PopStyleColor();
		}

		if (entity.Transform.ChildCount > 0)
		{
			if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
			    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X <= ImGui.GetTreeNodeToLabelSpacing())
			{
				var expandedSet = _imGuiManager.SceneGraphWindow.ExpandedEntities;
				if (!expandedSet.Remove(entity))
					expandedSet.Add(entity);
			}
		}
		VoltageEditorUtils.ShowContextMenuTooltip();

		ImGui.OpenPopupOnItemClick("entityContextMenu", ImGuiPopupFlags.MouseButtonRight);
		DrawEntityContextMenuPopup(entity);

		if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
			ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
		{
			bool ctrlDown = Input.IsKeyDown(Keys.LeftControl) || Input.IsKeyDown(Keys.RightControl) || ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeySuper;
			bool shiftDown = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift) || ImGui.GetIO().KeyShift;

			if (ctrlDown || shiftDown)
			{
				// Modifier-held multi-select applies immediately (this gesture never starts a reference
				// drag, so there is no focus-steal to guard against).
				SetSelectedEntity(entity, ctrlDown, shiftDown);
				_imGuiManager.OpenMainEntityInspector(entity);
				ImGui.SetWindowFocus();
				_pendingSelectEntity = null;
			}
			else
			{
				// Plain click — might be the start of a drag 
				bool alreadySoleSelected = _selectedEntities.Count == 1 && _selectedEntities[0] == entity;

				if (alreadySoleSelected)
				{
					_renameCandidateEntity = entity;
					_renameCandidateTime = ImGui.GetTime();
				}

				_pendingSelectEntity = entity;
			}

			// A fast double-click is an activation gesture, never a rename — cancel any armed candidate.
			if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				_renameCandidateEntity = null;
		}

		// Commit the deferred selection + inspector focus on mouse release, but only if this was a click
		// and not a drag. If a drag started, the drag source cleared _pendingSelectEntity already.
		if (_pendingSelectEntity == entity &&
			ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
			!ImGui.IsMouseDragging(ImGuiMouseButton.Left))
		{
			SetSelectedEntity(entity, false, false);
			_imGuiManager.OpenMainEntityInspector(entity);
			ImGui.SetWindowFocus();
			_pendingSelectEntity = null;
		}

		if (ImGui.IsMouseClicked(0) && ImGui.IsItemClicked() &&
			ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
			if (Core.Scene.Entities.Count > 0 && Core.IsEditMode)
			{
				if (_previousEntity == null || !_previousEntity.Equals(entity))
				{
					_previousEntity = entity;
				}

				_imGuiManager.CursorSelectionManager.SetCameraTargetPosition(entity.Transform.Position);
			}

		// Drag source
		if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover | ImGuiDragDropFlags.SourceNoHoldToOpenOthers))
		{
			// Clear any pending deferred single-select so the full multi-selection is intact when dropped
			_pendingSelectEntity = null;
			// A drag is not a rename gesture — cancel any armed delayed rename.
			_renameCandidateEntity = null;

			if (!_selectedEntities.Contains(entity))
			{
				_selectedEntities.Clear();
				_selectedEntities.Add(entity);
			}
			// Publish the dragged entity so reference-field inspectors (Entity/Transform/Component)
			// can accept this same ENTITY_DRAG payload and assign it.
			Voltage.Editor.Inspectors.TypeInspectors.EntityReferenceTypeInspector.DraggedEntity = entity;

			unsafe
			{
				uint id = entity.Id;
				ImGui.SetDragDropPayload(DragDropPayloadType, (nint)(&id), sizeof(uint));
			}

			_dragReorder.MarkDragStarted();
			var names = _selectedEntities.Count == 1
				? $"Move: {_selectedEntities[0].Name}"
				: $"Move {_selectedEntities.Count} entities";
			ImGuiSafe.TextSafe(names);
			ImGui.EndDragDropSource();
		}

		// Outer thirds reorder and open a seam; the middle reparents and deliberately does not.
		var zone = _dragReorder.HandleRow(rowIndex, allowInner: true, out var dropped);

		if (dropped)
		{
			if (zone == Voltage.Editor.Utils.DragReorderStrip.RowZone.Inner)
			{
				_dragReorder.Reset();
				ReparentSelectedEntities(entity.Transform);
			}
			else if (zone != Voltage.Editor.Utils.DragReorderStrip.RowZone.None)
			{
				DropIntoSeam(zone == Voltage.Editor.Utils.DragReorderStrip.RowZone.Below ? rowIndex + 1 : rowIndex);
			}
		}

		ImGui.PopID();
	}

    #endregion

    #region Entity Context Menu

    /// <summary>
    /// Draws the context menu popup for entity actions (copy, clone, destroy, etc).
    /// </summary>
    private void DrawEntityContextMenuPopup(Entity entity)
    {
        if (_imGuiManager == null)
            _imGuiManager = Core.GetGlobalManager<ImGuiManager>();

        if (Gui.BeginPopup("entityContextMenu"))
        {
            var blocksCopy = entity.Type == Entity.InstanceType.NonSerialized
                             || entity.Type == Entity.InstanceType.SceneRequired;

            // Right-clicking outside the selection acts on the clicked entity alone, matching the Asset Browser.
            var copyTargets = _selectedEntities.Contains(entity) ? _selectedEntities.Count : 1;
            var copyLabel = copyTargets > 1 ? $"Copy ({copyTargets} entities)" : "Copy";

            ImGui.BeginDisabled(blocksCopy);
            if (Gui.MenuItem(copyLabel, EditorHotkeys.MenuLabel(EditorHotkeys.CopyEntity)))
            {
                if (!_selectedEntities.Contains(entity))
                    SetSelectedEntity(entity, ctrlDown: false);

                CopySelectedEntities();
            }
            ImGui.EndDisabled();

            ImGui.BeginDisabled(!EntityClipboard.HasContent);
            if (Gui.MenuItem("Paste", EditorHotkeys.MenuLabel(EditorHotkeys.PasteEntity)))
                PasteEntities();
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Pastes into the current scene - copy in one scene, switch, then paste here.");

            ImGui.Separator();

            if (_imGuiManager.SceneGraphWindow.CopiedComponent != null && Gui.Selectable("Paste Component"))
            {
                var copiedComponent = _imGuiManager.SceneGraphWindow.CopiedComponent;
                var existingComponent = entity.Components.FirstOrDefault(c => c.GetType() == copiedComponent.GetType());
                
                if (existingComponent != null)
                {
                    // Use JSON serialization for reliable deep cloning
                    try
                    {
                        var jsonSettings = new JsonSettings
                        {
                            PrettyPrint = false,
                            TypeNameHandling = TypeNameHandling.Auto,
                            PreserveReferencesHandling = false
                        };
                        
                        var sourceData = copiedComponent.Data;
                        if (sourceData != null)
                        {
                            var oldData = existingComponent.Data; // for Undo
                            var json = Json.ToJson(sourceData, jsonSettings);
                            var clonedData = (ComponentData)Json.FromJson(json, sourceData.GetType());
                            
                            EditorChangeTracker.PushUndo(
                                new ComponentDataChangeAction(
                                    existingComponent,
                                    oldData,
                                    clonedData,
                                    $"Paste {copiedComponent.GetType().Name} to {entity.Name}"
                            ),
                            entity,
                            $"Paste {copiedComponent.GetType().Name} to {entity.Name}"
                        );
                        
                        existingComponent.Data = clonedData;
						}
                    }
                    catch (Exception ex)
                    {
                        var clonedComponent = copiedComponent.Clone();
                        clonedComponent.Name = existingComponent.Name;
                        entity.ReplaceComponent(clonedComponent);
                        Debug.Error($"Failed to paste component data: {ex.Message}. Used fallback paste method for {copiedComponent.GetType().Name}");
                    }
                    
                    _imGuiManager.RefreshMainEntityInspector();
                }
                else
                {
                    var clonedComponent = copiedComponent.Clone();
                    entity.AddComponent(clonedComponent);
                    _imGuiManager.RefreshMainEntityInspector();
                }
            }

            if (Gui.Selectable("Rename"))
                BeginEntityRename(entity);

            if (Gui.Selectable("Open in separate window"))
                Core.GetGlobalManager<ImGuiManager>().OpenSeparateEntityInspector(entity);

            // Entity Commands
            if (Gui.Selectable("Move Camera Here"))
                if (Core.Scene.Entities.Count > 0 && Core.IsEditMode)
                    _imGuiManager.CursorSelectionManager.SetCameraTargetPosition(entity.Transform.Position);

            
            string reason = null;
            if (entity.Type == Entity.InstanceType.NonSerialized)
            {
                reason = "Can't duplicate NonSerialized entities!";
            }
            else if (entity.Type == Entity.InstanceType.SceneRequired)
            {
                reason = "Can't duplicate SceneRequired entities!";
            }

            if (reason == null)
            {
                if (Gui.MenuItem("Duplicate",
                        EditorHotkeys.MenuLabel(EditorHotkeys.DuplicateEntity)))
                    DuplicateEntity(entity);
            }
            else
            {
                ImGui.BeginDisabled(true);
                Gui.Selectable(reason);
                ImGui.EndDisabled();
            }


            if (entity.Type == Entity.InstanceType.SceneRequired)
            {
                ImGui.BeginDisabled(true);
                Gui.Selectable("Can't delete SceneRequired entities!");
                ImGui.EndDisabled();
            }
            else
            {
                if (Gui.MenuItem("Destroy", EditorHotkeys.MenuLabel(EditorHotkeys.DeleteEntity)))
                {
                    // Push undo BEFORE destroying, so the entity is still valid
                    EditorChangeTracker.PushUndo(
                        new EntityCreateDeleteUndoAction(entity.Scene, entity, wasCreated: false, $"Delete Entity {entity.Name}"),
                        entity,
                        $"Delete Entity {entity.Name}"
                    );
                    entity.Destroy();
                }
            }

			if (Gui.Selectable("Create Child", false, ImGuiSelectableFlags.DontClosePopups))
			{
				var child = new Entity("Child Entity");
				child.Transform.SetParent(entity.Transform);
				child.Type = Entity.InstanceType.Serialized;
				entity.Scene.AddEntity(child);

				EditorChangeTracker.PushUndo(
					new EntityCreateDeleteUndoAction(entity.Scene, child, wasCreated: true, $"Create Child Entity under {entity.Name}"),
					child,
					$"Create Child Entity under {entity.Name}"
				);

				_imGuiManager.MainEntityInspectorWindow.DelayedSetEntity(child);
			}

			// Add an empty Parent (multi-selection only)
			if (_selectedEntities.Count > 1 && Gui.Selectable("Add an empty Parent"))
			{
				var entitiesToGroup = _selectedEntities
					.Where(e => e.Type != Entity.InstanceType.SceneRequired)
					.ToList();

				if (entitiesToGroup.Count > 0)
				{
					// Compute center position of all selected entities
					var center = Vector2.Zero;
					foreach (var e in entitiesToGroup)
						center += e.Transform.Position;
					center /= entitiesToGroup.Count;

					var parentEntity = new Entity("Parent");
					parentEntity.Type = Entity.InstanceType.Serialized;
					parentEntity.Transform.Position = center;
					entity.Scene.AddEntity(parentEntity);

					var reparentEntries = new List<(Entity entity, Transform oldParent, int oldIndex, Transform newParent, int newIndex)>();
					for (int i = 0; i < entitiesToGroup.Count; i++)
					{
						var e = entitiesToGroup[i];
						int oldIdx = e.Transform.Parent != null
							? e.Transform.Parent.Children.IndexOf(e.Transform)
							: Core.Scene.Entities.EntityFastList.IndexOf(e);
						reparentEntries.Add((e, e.Transform.Parent, oldIdx, parentEntity.Transform, i));
					}

					foreach (var e in entitiesToGroup)
					{
						var worldPos = e.Transform.Position;
						var worldRot = e.Transform.Rotation;
						var worldScale = e.Transform.Scale;

						e.Transform.SetParent(parentEntity.Transform);
						e.Transform.RecomputeLocalsFromWorld(worldPos, worldRot, worldScale);
					}

					// Push a composite undo: parent creation + reparenting
					EditorChangeTracker.PushUndo(
						new EntityCreateDeleteUndoAction(entity.Scene, parentEntity, wasCreated: true, $"Add empty Parent for selection"),
						parentEntity,
						$"Add empty Parent"
					);
					EditorChangeTracker.PushUndo(
						new EntityReparentUndoAction(reparentEntries, $"Group under {parentEntity.Name}"),
						parentEntity,
						$"Group under {parentEntity.Name}"
					);

					_imGuiManager.MainEntityInspectorWindow.DelayedSetEntity(parentEntity);
					_selectedEntities.Clear();
					_selectedEntities.Add(parentEntity);
				}
			}

			Gui.EndPopup();
        }
    }
	#endregion

	#region Copy and Paste Logic
	/// <summary>
	/// Handles copy/paste/duplicate shortcuts for entities.
	/// </summary>
	///
	/// <summary>
	/// Copies the selection as DATA. Unlike duplication this survives a scene switch, so the paste can land in a
	/// different scene - or, via the OS clipboard, a different editor instance.
	/// </summary>
	public void CopySelectedEntities()
	{
		var copyable = _selectedEntities
			.Where(e => e != null && e.Type != Entity.InstanceType.NonSerialized
			                      && e.Type != Entity.InstanceType.SceneRequired)
			.ToList();

		if (copyable.Count == 0)
		{
			Debug.Error("Cannot copy NonSerialized or SceneRequired entities.");
			return;
		}

		var count = EntityClipboard.Copy(copyable);
		if (count > 0)
			EditorDebug.Log($"Copied {count} entit{(count == 1 ? "y" : "ies")}.", "Entity");
	}

	/// <summary>Pastes the clipboard into the current scene and selects the result.</summary>
	public void PasteEntities()
	{
		if (Core.Scene == null)
			return;

		// Offset so a same-scene paste is visible instead of landing exactly on the original.
		var roots = EntityClipboard.Paste(new Vector2(PasteOffset, PasteOffset));
		if (roots.Count == 0)
			return;

		var description = $"Pasted: {string.Join(", ", roots.Select(e => e.Name))}";

		// Creation, so undo must DETACH - the inverse of MultiEntityDeleteUndoAction. One composite step
		// so a paste of several roots reverts in a single Ctrl+Z.
		var actions = roots
			.Select(root => (EditorChangeTracker.IEditorAction)
				new EntityCreateDeleteUndoAction(Core.Scene, root, wasCreated: true, description))
			.ToList();

		EditorChangeTracker.PushUndo(new CompositeUndoAction(actions, description), roots[0], description);

		DeselectAllEntities();
		foreach (var root in roots)
			SetSelectedEntity(root, ctrlDown: true);
	}

	private const float PasteOffset = 16f;

	private void EntityDuplicationAndDeletion()
    {
	    if (ImGui.IsAnyItemActive() || ImGui.IsAnyItemFocused())
		    return;

	    // Scoped to this window so the Asset Browser's identical shortcuts do not both fire.
	    if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
		    return;

	    bool ShouldBlockDuplication(Entity entity)
	    {
	        if (entity != null && (entity.Type == Entity.InstanceType.NonSerialized
	                               || entity.Type == Entity.InstanceType.SceneRequired))
	        {
	            Debug.Error($"Cannot duplicate {entity.Type} entities.");
	            return true; 
	        }
	        return false;
	    }

	    bool ShouldBlockDeletion(Entity entity)
	    {
	        return entity != null && entity.Type == Entity.InstanceType.SceneRequired;
	    }

	    if (Core.IsEditMode && EditorHotkeys.Pressed(EditorHotkeys.DuplicateEntity) && _selectedEntities.Count > 0)
	    {
	        var entitiesToDuplicate = _selectedEntities.Where(e => !ShouldBlockDuplication(e)).ToList();
	        if (entitiesToDuplicate.Count > 1)
	        {
	            DuplicateEntities(entitiesToDuplicate);
	        }
	        else
	        {
	            foreach (var entity in entitiesToDuplicate)
	                DuplicateEntity(entity);
	        }
	    }

	    if (Core.IsEditMode && EditorHotkeys.Pressed(EditorHotkeys.CopyEntity) && _selectedEntities.Count > 0)
	        CopySelectedEntities();

	    if (Core.IsEditMode && EditorHotkeys.Pressed(EditorHotkeys.PasteEntity))
	        PasteEntities();

	    if (Core.IsEditMode && _selectedEntities.Count > 0 && EditorHotkeys.Pressed(EditorHotkeys.DeleteEntity))
	    {
	        var entitiesToDelete = _selectedEntities.Where(e => !ShouldBlockDeletion(e)).ToList();

	        if (entitiesToDelete.Count > 0)
	        {
	            // Push a single undo for all entities
	            EditorChangeTracker.PushUndo(
	                new MultiEntityDeleteUndoAction(Core.Scene, entitiesToDelete, 
	                    $"Deleted: {string.Join(", ", entitiesToDelete.Select(e => e.Name))}"),
	                entitiesToDelete.FirstOrDefault(),
	                $"Deleted: {string.Join(", ", entitiesToDelete.Select(e => e.Name))}"
	            );

	            foreach (var entity in entitiesToDelete)
	                entity.Destroy();
	        }

	        DeselectAllEntities();
	    }
	}

	/// <summary>
	/// Duplicates the given entity and its subtree, remaps intra-subtree references so
	/// EntityReference/ComponentReference fields that pointed at siblings or children now
	/// point at the corresponding clones, then resolves all references. Returns the root clone.
	/// </summary>
	public Entity DuplicateEntity(Entity entity, string customName = null)
	{
		if (entity == null || entity.Scene == null)
			return null;

		var idMap = new Dictionary<Guid, Guid>();
		var clone = DuplicateEntityInternal(entity, customName, idMap);
		if (clone == null)
			return null;

		ComponentReferenceResolver.RemapEntitySubtree(clone, idMap);
		ComponentReferenceResolver.ResolveEntitySubtree(clone, Core.Scene);

		EditorChangeTracker.PushUndo(
			new EntityCreateDeleteUndoAction(entity.Scene, clone, wasCreated: true, $"Created: Entity {clone.Name}"),
			clone,
			$"Created: {clone.Name}"
		);

		_imGuiManager.MainEntityInspectorWindow.DelayedSetEntity(clone);
		return clone;
	}

	/// <summary>
	/// Internal recursive clone builder. Populates <paramref name="idMap"/> with
	/// sourceId → cloneId pairs for every entity in the subtree, but does NOT remap or resolve.
	/// Pass <paramref name="pendingClones"/> when cloning multiple roots simultaneously so
	/// name uniqueness accounts for the other in-flight clones.
	/// </summary>
	private Entity DuplicateEntityInternal(Entity entity, string customName, Dictionary<Guid, Guid> idMap,
		List<Entity> pendingClones = null)
	{
		var clone = new Entity("Entity");
		string baseName = customName ?? entity.Name;
		clone.Name = Core.Scene.GetUniqueEntityName(baseName, clone, pendingClones);

		clone.Transform.Position = entity.Transform.Position;
		clone.Transform.Rotation = entity.Rotation;
		clone.Transform.Scale = entity.Scale;
		clone.SetTag(entity.Tag);
		clone.Enabled = entity.Enabled;
		clone.DebugRenderEnabled = entity.DebugRenderEnabled;
		clone.UpdateInterval = entity.UpdateInterval;
		clone.UpdateOrder = entity.UpdateOrder;

		if (entity.Type == Entity.InstanceType.NonSerialized || entity.Type == Entity.InstanceType.Serialized
		    || entity.Type == Entity.InstanceType.SceneRequired)
			clone.Type = Entity.InstanceType.Serialized;
		else
		{
			clone.Type = Entity.InstanceType.SerializedPrefab;
			clone.OriginalPrefabName = entity.OriginalPrefabName;
		}

		idMap[entity.PersistentId] = clone.PersistentId;

		foreach (var sourceComponent in entity.Components)
		{
			if (!sourceComponent.IsSerialized)
				continue;

			var componentType = sourceComponent.GetType();
			Component clonedComponent;
			try
			{
				clonedComponent = (Component)Activator.CreateInstance(componentType);
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to create component {componentType.Name}: {ex.Message}");
				continue;
			}

			clonedComponent.Name = sourceComponent.Name;
			clonedComponent.Enabled = sourceComponent.Enabled;
			clone.AddComponent(clonedComponent);
			clonedComponent.SetSerialized(true);

			if (sourceComponent.Data != null)
			{
				try
				{
					var componentJsonSettings = new JsonSettings
					{
						PrettyPrint = false,
						TypeNameHandling = TypeNameHandling.Auto,
						PreserveReferencesHandling = false
					};

					var json = Json.ToJson(sourceComponent.Data, componentJsonSettings);
					var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
					clonedComponent._pendingLoadedData = clonedData;
					clonedComponent.Data = clonedData;
				}
				catch (Exception ex)
				{
					Debug.Warn($"Failed to copy data for component {sourceComponent.GetType().Name}: {ex.Message}");
					try
					{
						var fallbackClone = sourceComponent.Clone();
						if (fallbackClone?.Data != null)
						{
							clonedComponent._pendingLoadedData = fallbackClone.Data;
							clonedComponent.Data = fallbackClone.Data;
						}
					}
					catch (Exception cloneEx)
					{
						Debug.Error($"Clone() fallback also failed for {sourceComponent.GetType().Name}: {cloneEx.Message}");
					}
				}
			}
		}

		Core.Scene.AddEntity(clone);

		for (var i = 0; i < entity.Transform.ChildCount; i++)
		{
			var childEntity = entity.Transform.GetChild(i).Entity;
			if (childEntity.Type == Entity.InstanceType.NonSerialized)
				continue;

			var clonedChild = DuplicateEntityInternal(childEntity, null, idMap);
			if (clonedChild != null)
			{
				var cWorldPos = childEntity.Transform.Position;
				var cWorldRot = childEntity.Transform.Rotation;
				var cWorldScale = childEntity.Transform.Scale;
				clonedChild.Transform.SetParent(clone.Transform);
				clonedChild.Transform.RecomputeLocalsFromWorld(cWorldPos, cWorldRot, cWorldScale);
			}
		}

		return clone;
	}

    /// <summary>
    /// Duplicates multiple entities, remapping intra-subtree references on each clone
    /// before resolving. Each top-level entity gets its own remap pass over its own subtree.
    /// </summary>
    public List<Entity> DuplicateEntities(IEnumerable<Entity> entitiesToDuplicate)
    {
        var clones = new List<Entity>();

        foreach (var entity in entitiesToDuplicate)
        {
			// Same guard DuplicateEntity has: cloning a torn-down entity yields an empty husk.
			if (entity == null || entity.Scene == null)
				continue;

			var idMap = new Dictionary<Guid, Guid>();
			var clone = DuplicateEntityInternal(entity, null, idMap, clones);
			if (clone == null)
				continue;

			ComponentReferenceResolver.RemapEntitySubtree(clone, idMap);
			ComponentReferenceResolver.ResolveEntitySubtree(clone, Core.Scene);

            clones.Add(clone);
        }

        return clones;
	}
	#endregion

    public void DeselectAllEntities()
    {
        _selectedEntities.Clear();
        _lastRangeSelectEntity = null; // Reset anchor
        _imGuiManager.ClearHighlightCache();
	}

	public Vector2 GetSelectedEntitiesCenter()
    {
        if (SelectedEntities.Count == 0)
            return Core.Scene.Camera.Position;        

        Vector2 sum = Vector2.Zero;

        foreach (var e in SelectedEntities)
            sum += e.Transform.Position;

        return sum / SelectedEntities.Count;
    }
}