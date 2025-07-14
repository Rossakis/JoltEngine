using System;
using ImGuiNET;
using Nez.ECS;

namespace Nez.ImGuiTools.SceneGraphPanes;

public class EntityPane
{
	public static Collider
		_selectedEntityCollider; //Used for rendering a collider box for the currently selected entity

	/// <summary>
	/// if this number of entites is exceeded we switch to a list clipper to keep things fast
	/// </summary>
	private const int MIN_ENTITIES_FOR_CLIPPER = 100;

	private string _newEntityName = "";

	private Entity _previousEntity; //Used for rendering a collider box for the currently selected entity
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

	public unsafe void Draw()
	{
		if(_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

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
	}

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

		if (entity.Transform.ChildCount > 0)
			treeNodeOpened = ImGui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.OpenOnArrow | flags);
		else
			treeNodeOpened = ImGui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.OpenOnArrow | flags);

		NezImGui.ShowContextMenuTooltip();

		// context menu for entity commands
		ImGui.OpenPopupOnItemClick("entityContextMenu", ImGuiPopupFlags.MouseButtonRight);
		DrawEntityContextMenuPopup(entity);

		if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
		    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
				Core.GetGlobalManager<ImGuiManager>().OpenMainEntityInspector(entity);

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

				Core.Scene.Camera.Position = entity.Transform.Position;
			}


		if (_selectedEntityCollider != null && Core.IsEditMode)
			Debug.DrawHollowRect(_selectedEntityCollider.Bounds, Debug.Colors.SelectedByInspectorEntity);

		if (treeNodeOpened)
		{
			for (var i = 0; i < entity.Transform.ChildCount; i++)
				DrawEntity(entity.Transform.GetChild(i).Entity, false);

			ImGui.TreePop();
		}

		ImGui.PopID();

	}

	private void DrawEntityContextMenuPopup(Entity entity)
	{
		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		if (ImGui.BeginPopup("entityContextMenu"))
		{
			if (_imGuiManager.SceneGraphWindow.CopiedComponent != null && ImGui.Selectable("Paste Component"))
			{
				//Component Commands
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

			//Entity Commands
			if (ImGui.Selectable("Move Camera to " + entity.Name))
				if (Core.Scene.Entities.Count > 0 && Core.IsEditMode)
					Core.Scene.Camera.Position = entity.Transform.Position;

			// Check for parameterless constructor (and no non-parameterless children)
			var hasParameterlessCtor = entity.GetType().GetConstructor(Type.EmptyTypes) != null;
			
			bool canClone = hasParameterlessCtor 
			                && !InspectorCache.HasNonParameterlessChildEntity(entity) 
			                && entity.Type != Entity.InstanceType.HardCoded;

			string reason = null;
			if (!canClone)
			{
				if (entity.Type == Entity.InstanceType.HardCoded)
					reason = "Can't clone a Hard-Coded Entity!";
				else if (!hasParameterlessCtor)
					reason = "Can't clone a Non-parameterless Entity!";
				else if (InspectorCache.HasNonParameterlessChildEntity(entity))
					reason = "Can't clone Entity with Non-parameterless children!";
			}

			if (canClone)
			{
				if (ImGui.Selectable("Clone Entity " + entity.Name))
				{
					var typeName = entity.GetType().Name;
					if (EntityFactoryRegistry.TryCreate(typeName, out var clone))
					{
						clone.Type = entity.Type;
						clone.Name = Core.Scene.GetUniqueEntityName(typeName);
						clone.Transform.Position = Core.Scene.Camera.Position;
						EntityFactoryRegistry.InvokeEntityCreated(clone);
					}
					else
					{
						// Fallback to old behavior if not registered
						var fallbackClone = entity.Clone(Core.Scene.Camera.Position);
						entity.Scene.AddEntity(fallbackClone);
					}
				}
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
}