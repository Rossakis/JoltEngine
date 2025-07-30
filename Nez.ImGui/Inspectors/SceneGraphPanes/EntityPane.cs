using System;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Nez.ECS;
using Nez.Utils;

namespace Nez.ImGuiTools.SceneGraphPanes;

public class EntityPane
{
	#region Fields and Properties
	public bool IsDraggingGizmo => _draggingX || _draggingY;
	public static Collider _selectedEntityCollider; // Used for rendering a collider box for the currently selected entity

    private const int MIN_ENTITIES_FOR_CLIPPER = 100;

    private Entity _previousEntity; // Used for rendering a collider box for the currently selected entity
    private Entity _selectedEntity;

    public Entity SelectedEntity
    {
        get => _selectedEntity;
        set
        {
            _selectedEntity = value;
            _selectedEntityCollider = _selectedEntity?.GetComponent<Collider>();
        }
    }

    private ImGuiManager _imGuiManager;

    private bool _draggingX = false;
    private bool _draggingY = false;
    private Vector2 _dragStartEntityPos;
    private Vector2 _dragStartWorldMouse;
    private Vector2 _dragStartUndoPosition;

    private Entity _copiedEntity; // For Ctrl+C/Ctrl+V

	#endregion

	#region Main Draw Entry Point

	/// <summary>
	/// Main entry point for drawing the entity pane UI and gizmos.
	/// </summary>
	public unsafe void Draw()
    {
		if (_imGuiManager == null)
            _imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		// Draw entity tree (with clipper for large lists)
		if (Core.Scene.Entities.Count > MIN_ENTITIES_FOR_CLIPPER)
        {
            var clipperPtr = ImGuiNative.ImGuiListClipper_ImGuiListClipper();
            var clipper = new ImGuiListClipperPtr(clipperPtr);

            clipper.Begin(Core.Scene.Entities.Count, -1);

            while (clipper.Step())
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    DrawEntity(Core.Scene.Entities[i]);

            ImGuiNative.ImGuiListClipper_destroy(clipperPtr);
        }
        else
        {
            for (var i = 0; i < Core.Scene.Entities.Count; i++)
                DrawEntity(Core.Scene.Entities[i]);
        }

        NezImGui.MediumVerticalSpace();

        // Draw gizmo for selected entity (arrows)
        DrawSelectedEntityGizmo();
    }
	#endregion


	#region Gizmo Rendering (Arrows for Entity Manipulation)

	private Vector2 prevCameraPos = Vector2.Zero;

	/// <summary>
	/// Draws the X/Y axis arrows for the selected entity and handles drag interaction.
	/// </summary>
	private void DrawSelectedEntityGizmo()
	{
		if (_selectedEntity == null || !Core.IsEditMode)
			return;

		var camera = Core.Scene.Camera;
		float baseLength = 30f;
		float minLength = 10f;
		float maxLength = 100f;
		float axisLength = baseLength / MathF.Max(camera.RawZoom, 0.01f);
		axisLength = Math.Clamp(axisLength, minLength, maxLength);

		float baseWidth = 4f;
		float maxWidth = 16f;
		float scaledWidth = baseWidth;
		if (camera.RawZoom > 1f)
			scaledWidth = MathF.Min(baseWidth * camera.RawZoom, maxWidth);

		var entityPos = _selectedEntity.Transform.Position;
		var screenPos = camera.WorldToScreenPoint(entityPos);
		var axisEndX = camera.WorldToScreenPoint(entityPos + new Vector2(axisLength, 0));
		var axisEndY = camera.WorldToScreenPoint(entityPos + new Vector2(0, -axisLength));

		Color xColor = Color.Red;
		Color yColor = Color.LimeGreen;

		var mousePos = Input.ScaledMousePosition;

		bool xHovered = IsMouseNearLine(mousePos, screenPos, axisEndX);
		bool yHovered = IsMouseNearLine(mousePos, screenPos, axisEndY);

		if (_draggingX)
			xColor = Color.Yellow;
		else if (xHovered)
			xColor = Color.Orange;

		if (_draggingY)
			yColor = Color.Yellow;
		else if (yHovered)
			yColor = Color.Orange;

		Debug.DrawArrow(entityPos, entityPos + new Vector2(axisLength, 0), scaledWidth, scaledWidth, xColor);
		Debug.DrawArrow(entityPos, entityPos + new Vector2(0, -axisLength), scaledWidth, scaledWidth, yColor);

		if (prevCameraPos == Vector2.Zero)
			prevCameraPos = camera.Position;

		// Start dragging if not already dragging
		if (!_draggingX && !_draggingY)
		{
			if ((xHovered && yHovered && Input.LeftMouseButtonPressed) ||
			    (xHovered && Input.LeftMouseButtonPressed) ||
			    (yHovered && Input.LeftMouseButtonPressed))
			{
				if (xHovered && yHovered)
				{
					_draggingX = true;
					_draggingY = true;
				}
				else if (xHovered)
				{
					_draggingX = true;
				}
				else if (yHovered)
				{
					_draggingY = true;
				}

				_dragStartEntityPos = entityPos;
				_dragStartWorldMouse = camera.ScreenToWorldPoint(mousePos);

				// Undo/Redo: Start of drag session
				_dragStartUndoPosition = _selectedEntity.Transform.Position;
			}
		}

		// Keep dragging as long as mouse is held down 
		if ((_draggingX || _draggingY) && Input.LeftMouseButtonDown)
		{
			var worldMouse = camera.ScreenToWorldPoint(mousePos);
			var delta = worldMouse - _dragStartWorldMouse;

			if (_draggingX && _draggingY)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
				_selectedEntity.Transform.Position = _dragStartEntityPos + delta;
			}
			else if (_draggingX)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
				_selectedEntity.Transform.Position = new Vector2(_dragStartEntityPos.X + delta.X,
					_selectedEntity.Transform.Position.Y);
			}
			else if (_draggingY)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
				_selectedEntity.Transform.Position = new Vector2(_selectedEntity.Transform.Position.X,
					_dragStartEntityPos.Y + delta.Y);
			}
		}

		// Undo/Redo: End of drag session
		if ((_draggingX || _draggingY) && !Input.LeftMouseButtonDown)
		{
			_draggingX = false;
			_draggingY = false;

			var endPos = _selectedEntity.Transform.Position;
			if (_dragStartUndoPosition != endPos)
			{
				EditorChangeTracker.PushUndo(
					new GenericValueChangeAction(
						_selectedEntity.Transform,
						(obj, val) => ((Transform)obj).SetPosition((Vector2)val),
						_dragStartUndoPosition,
						endPos,
						$"{_selectedEntity.Name}.Transform.Position"
					),
					_selectedEntity,
					$"{_selectedEntity.Name}.Transform.Position"
				);
			}
		}

		prevCameraPos = camera.Position;
	}

	/// <summary>
    /// Utility to check if mouse is near a line segment.
    /// </summary>
    private bool IsMouseNearLine(Vector2 mouse, Vector2 a, Vector2 b, float threshold = 10f)
    {
        var ap = mouse - a;
        var ab = b - a;
        float abLen = ab.Length();
        float t = Math.Clamp(Vector2.Dot(ap, ab) / (abLen * abLen), 0, 1);
        var closest = a + ab * t;
        return (mouse - closest).Length() < threshold;
    }

    #endregion

    #region Entity Tree Rendering and Interaction

    /// <summary>
    /// Draws a single entity node in the tree, handles selection, context menu, and inspector opening.
    /// </summary>
    private void DrawEntity(Entity entity, bool onlyDrawRoots = true)
    {
        if (onlyDrawRoots && entity.Transform.Parent != null)
            return;

        bool isSelected = entity == _selectedEntity;
        ImGui.PushID((int)entity.Id);
        bool treeNodeOpened;
        var flags = isSelected ? ImGuiTreeNodeFlags.Selected : 0;
        bool isExpanded = _imGuiManager.SceneGraphWindow.ExpandedEntities.Contains(entity);
        if (entity.Transform.ChildCount > 0)
            ImGui.SetNextItemOpen(isExpanded, ImGuiCond.Always);

		// Draw tree node
		if (entity.Transform.ChildCount > 0)
			treeNodeOpened = ImGui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.OpenOnArrow | flags);
		else
			treeNodeOpened = ImGui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.OpenOnArrow | flags);

		if (entity.Transform.ChildCount > 0)
		{
			// Check if the arrow was clicked (not the label)
			if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
			    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X <= ImGui.GetTreeNodeToLabelSpacing())
			{
				if (isExpanded)
					_imGuiManager.SceneGraphWindow.ExpandedEntities.Remove(entity);
				else
					_imGuiManager.SceneGraphWindow.ExpandedEntities.Add(entity);
			}
		}
		NezImGui.ShowContextMenuTooltip();

		// Context menu for entity commands
		ImGui.OpenPopupOnItemClick("entityContextMenu", ImGuiPopupFlags.MouseButtonRight);
		DrawEntityContextMenuPopup(entity);

		// Handle selection and inspector opening
		if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
		    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
		{
			_imGuiManager.OpenMainEntityInspector(entity);
			SelectedEntity = entity;
			ImGui.SetWindowFocus();
		}

		// Move camera to the entity for inspection
		if (ImGui.IsMouseClicked(0) && ImGui.IsItemClicked() &&
		    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
            if (Core.Scene.Entities.Count > 0 && Core.IsEditMode)
            {
                if (_previousEntity == null || !_previousEntity.Equals(entity))
                {
                    _previousEntity = entity;
                    _selectedEntityCollider = entity.GetComponent<Collider>();
                }

                _imGuiManager.SetCameraTargetPosition(entity.Transform.Position);
            }

		// Recursively draw children
		if (treeNodeOpened)
		{
			for (var i = 0; i < entity.Transform.ChildCount; i++)
				DrawEntity(entity.Transform.GetChild(i).Entity, false);

			ImGui.TreePop();
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

        if (ImGui.BeginPopup("entityContextMenu"))
        {
            if (_imGuiManager.SceneGraphWindow.CopiedComponent != null && ImGui.Selectable("Paste Component"))
            {
                NezImGui.SmallVerticalSpace();

                var index = -1;
                for (var i = 0; i < entity.Components.Count; i++)
                    if (entity.Components[i].GetType() == _imGuiManager.SceneGraphWindow.CopiedComponent.GetType())
                    {
                        index = i;
                        break;
                    }

                if (index > -1)
                {
                    var temp = _imGuiManager.SceneGraphWindow.CopiedComponent;
                    entity.RemoveComponent(entity.Components[index]);
                    entity.AddComponent(temp);
                }
            }

            if (ImGui.Selectable($"Open {entity.Name} in separate window"))
                Core.GetGlobalManager<ImGuiManager>().OpenSeparateEntityInspector(entity);

            // Entity Commands
            if (ImGui.Selectable("Move Camera to " + entity.Name))
                if (Core.Scene.Entities.Count > 0 && Core.IsEditMode)
	                _imGuiManager.SetCameraTargetPosition(entity.Transform.Position);

			// Clone logic
			var hasParameterlessCtor = entity.GetType().GetConstructor(Type.EmptyTypes) != null;
            bool canClone = hasParameterlessCtor
                            && !InspectorCache.HasNonParameterlessChildEntity(entity);

            string reason = null;
            if (!canClone)
            {
				if (!hasParameterlessCtor)
                    reason = "Can't duplicate a Non-parameterless Entity!";
                else if (InspectorCache.HasNonParameterlessChildEntity(entity))
                    reason = "Can't duplicate Entity with Non-parameterless children!";
            }

            if (canClone)
            {
                if (ImGui.Selectable("Duplicate Entity " + entity.Name))
	                DuplicateEntity(entity);
            }
            else
            {
                ImGui.BeginDisabled(true);
                ImGui.Selectable(reason);
                ImGui.EndDisabled();
            }

            if (ImGui.Selectable("Destroy Entity"))
                entity.Destroy();

            if (ImGui.Selectable("Create Child Entity", false, ImGuiSelectableFlags.DontClosePopups))
                ImGui.OpenPopup("create-new-entity");

            ImGui.EndPopup();
        }
    }
	#endregion

	#region Copy and Paste Logic

	/// <summary>
	/// Handles copy/paste/duplicate shortcuts for entities.
	/// </summary>
	public void HandleCopyAndPaste()
    {
	    // Handle Copy/Paste/Duplicate Shortcuts
	    bool gameCtrlDown = Input.IsKeyDown(Keys.LeftControl) || Input.IsKeyDown(Keys.RightControl);

		// Ctrl+C: Copy (we can only copy in EditMode in game view)
		if (Core.IsEditMode && gameCtrlDown && Input.IsKeyPressed(Keys.C) && _copiedEntity != _selectedEntity)
	    {
		    System.Console.WriteLine("Copy game");
		    _copiedEntity = _selectedEntity;
	    }

	    // Ctrl+V: Paste (duplicate)
	    if (Core.IsEditMode && gameCtrlDown && Input.IsKeyPressed(Keys.V) && _copiedEntity != null)
	    {
		    System.Console.WriteLine("Paste game");
		    DuplicateEntity(_copiedEntity);
	    }

	    // Ctrl+D: Duplicate selected
	    if (Core.IsEditMode && gameCtrlDown && Input.IsKeyPressed(Keys.D) && _selectedEntity != null)
	    {
		    System.Console.WriteLine("Duplicate game");
		    DuplicateEntity(_selectedEntity);
	    }
	}

	/// <summary>
	/// Duplicates the given entity and adds it to the scene.
	/// If the entity is HardCoded, the clone will be of type Dynamic.
	/// </summary>
	private void DuplicateEntity(Entity entity)
    {
		if (entity == null || entity.Scene == null)
			return;

		var typeName = entity.GetType().Name;
		if (EntityFactoryRegistry.TryCreate(typeName, out var clone))
		{
			clone.Type = Entity.InstanceType.Dynamic;
			clone.Name = Core.Scene.GetUniqueEntityName(typeName);
			clone.Transform.Position = Core.Scene.Camera.Position;
			EntityFactoryRegistry.InvokeEntityCreated(clone);
			entity.Scene.AddEntity(clone);

			// Optionally select the new entity
			SelectedEntity = clone;
			_imGuiManager.OpenMainEntityInspector(clone);
		}
		else
		{
			// Fallback: use deep clone if factory is not available
			var fallbackClone = entity.Clone(Core.Scene.Camera.Position);
			fallbackClone.Type = Entity.InstanceType.Dynamic;
			fallbackClone.Name = Core.Scene.GetUniqueEntityName(entity.Name);
			entity.Scene.AddEntity(fallbackClone);

			SelectedEntity = fallbackClone;
			_imGuiManager.OpenMainEntityInspector(fallbackClone);
		}
	}
    #endregion

}