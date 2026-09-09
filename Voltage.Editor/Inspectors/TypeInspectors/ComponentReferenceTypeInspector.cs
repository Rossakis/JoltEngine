using System;
using ImGuiNET;
using Voltage.Editor.Utils;
using Voltage.Utils;
using Num = System.Numerics;

namespace Voltage.Editor.Inspectors.TypeInspectors;

public class ComponentReferenceTypeInspector : AbstractTypeInspector
{
	public const string DragDropPayloadId = "VOLTAGE_COMPONENT_REF";
	public static Component DraggedComponent;

	private bool _showPicker;
	private string _pickerSearch = string.Empty;

	public override void DrawMutable()
	{
		var current = _getter(_target) as Component;
		var fieldType = _valueType; // e.g. SpriteAnimator

		ImGuiSafe.TextSafe(_name);
		ImGui.SameLine();

		var label = current != null
			? current.ToString()
			: $"None ({fieldType.Name})";

		var buttonColor = current != null
			? new Num.Vector4(0.2f, 0.35f, 0.55f, 0.9f)
			: new Num.Vector4(0.22f, 0.22f, 0.22f, 0.9f);

		ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
		ImGui.PushStyleColor(ImGuiCol.ButtonHovered, buttonColor with { W = 1f });

		Gui.Button($"{label}##compref_{_scopeId}", new Num.Vector2(-1, 0));
		if (ImGui.IsItemHovered())
		{
			// Single click highlights the referenced component's entity; double click opens the picker.
			if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				_showPicker = true;
			else if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && current?.Entity != null)
				EntityReferenceTypeInspector.PingEntityInSceneGraph(current.Entity);
		}

		ImGui.PopStyleColor(2);
		HandleTooltip();

		if (ImGui.BeginDragDropTarget())
		{
			var payload = ImGui.AcceptDragDropPayload(DragDropPayloadId);

			bool accepted;
			unsafe { accepted = payload.NativePtr != null; } 

			if (accepted && DraggedComponent != null)
			{
				if (fieldType.IsAssignableFrom(DraggedComponent.GetType()))
					SetValueWithUndo(DraggedComponent, $"Assign {_name}");
				else
					Debug.Warn($"[ComponentRef] Cannot assign {DraggedComponent.GetType().Name} to field of type {fieldType.Name}.");

				DraggedComponent = null;
			}

			// Also accept an entity dragged from the Scene Graph: assign that entity's component of
			// the field's type (the first match), if it has one.
			var entPayload = ImGui.AcceptDragDropPayload(EntityReferenceTypeInspector.DragDropPayloadId);
			bool entAccepted;
			unsafe { entAccepted = entPayload.NativePtr != null; }

			if (entAccepted && EntityReferenceTypeInspector.DraggedEntity != null)
			{
				var dragged = EntityReferenceTypeInspector.DraggedEntity;
				Component match = null;
				for (int i = 0; i < dragged.Components.Count; i++)
				{
					if (fieldType.IsAssignableFrom(dragged.Components[i].GetType()))
					{
						match = dragged.Components[i];
						break;
					}
				}

				if (match != null)
					SetValueWithUndo(match, $"Assign {_name} = {match}");
				else
					Debug.Warn($"[ComponentRef] Entity '{dragged.Name}' has no {fieldType.Name} component to assign.");

				EntityReferenceTypeInspector.DraggedEntity = null;
			}

			ImGui.EndDragDropTarget();
		}

		// Clear with right-click
		if (current != null && Gui.BeginPopupContextItem($"compref_ctx_{_scopeId}"))
		{
			if (Gui.Selectable("Clear"))
				SetValueWithUndo(null, $"Clear {_name}");

			Gui.EndPopup();
		}

		if (_showPicker)
		{
			ImGui.OpenPopup($"compref_picker_{_scopeId}");
			_pickerSearch = string.Empty;
			_showPicker = false;
		}

		DrawPickerPopup(current, fieldType);
	}

	private void DrawPickerPopup(Component current, Type fieldType)
	{
		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(400, 450), ImGuiCond.Appearing);

		bool open = true;
		if (!Gui.BeginPopupModal($"compref_picker_{_scopeId}", ref open, ImGuiWindowFlags.NoResize))
			return;

		ImGuiSafe.TextColoredSafe(new Num.Vector4(0.3f, 0.8f, 1f, 1f), $"Select {fieldType.Name}");
		ImGui.Separator();

		ImGui.SetNextItemWidth(-1);
		Gui.InputTextWithHint("##refsearch", "Search...", ref _pickerSearch, 128);
		ImGui.Separator();

		if (Gui.Selectable($"  None ({fieldType.Name})", current == null))
		{
			SetValueWithUndo(null, $"Clear {_name}");
			ImGui.CloseCurrentPopup();
		}

		ImGui.Separator();

		var scene = Core.Scene;
		if (scene != null)
		{
			bool filtering = !string.IsNullOrEmpty(_pickerSearch);
			if (filtering)
			{
				string lower = _pickerSearch.ToLowerInvariant();
				for (int e = 0; e < scene.Entities.Count; e++)
				{
					var entity = scene.Entities[e];
					if (!entity.Name.ToLowerInvariant().Contains(lower))
						continue;

					for (int c = 0; c < entity.Components.Count; c++)
					{
						var comp = entity.Components[c];
						if (!fieldType.IsAssignableFrom(comp.GetType()))
							continue;

						ImGui.PushID((int)entity.Id * 1000 + c);
						if (Gui.Selectable($"  {entity.Name}  /  {comp}", comp == current))
						{
							SetValueWithUndo(comp, $"Assign {_name} = {comp}");
							ImGui.CloseCurrentPopup();
						}
						ImGui.PopID();
					}
				}
			}
			else
			{
				for (int e = 0; e < scene.Entities.Count; e++)
				{
					var entity = scene.Entities[e];
					if (entity.Transform.Parent == null)
						DrawComponentTreeNode(entity, current, fieldType);
				}
			}
		}

		ImGui.Separator();
		VoltageEditorUtils.SmallVerticalSpace();
		if (VoltageEditorUtils.CenteredButton("Cancel", 0.5f))
			ImGui.CloseCurrentPopup();

		Gui.EndPopup();
	}

	private void DrawComponentTreeNode(Entity entity, Component current, Type fieldType)
	{
		bool entityHasMatch = EntitySubtreeHasMatch(entity, fieldType);
		if (!entityHasMatch)
			return;

		bool hasChildren = entity.Transform.ChildCount > 0;

		ImGui.PushID((int)entity.Id);

		bool nodeOpen = hasChildren
			? Gui.TreeNodeEx(entity.Name,
				ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth)
			: Gui.TreeNodeEx(entity.Name,
				ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen
				| ImGuiTreeNodeFlags.SpanAvailWidth);

		if (hasChildren ? nodeOpen : true)
		{
			for (int c = 0; c < entity.Components.Count; c++)
			{
				var comp = entity.Components[c];
				if (!fieldType.IsAssignableFrom(comp.GetType()))
					continue;

				ImGui.PushID(c);
				ImGui.Indent();
				if (Gui.Selectable($"{entity.Name}  /  {comp}", comp == current))
				{
					SetValueWithUndo(comp, $"Assign {_name} = {comp}");
					ImGui.CloseCurrentPopup();
				}
				ImGui.Unindent();
				ImGui.PopID();
			}

			if (hasChildren)
			{
				for (int i = 0; i < entity.Transform.ChildCount; i++)
					DrawComponentTreeNode(entity.Transform.GetChild(i).Entity, current, fieldType);

				ImGui.TreePop();
			}
		}

		ImGui.PopID();
	}

	private static bool EntitySubtreeHasMatch(Entity entity, Type fieldType)
	{
		for (int c = 0; c < entity.Components.Count; c++)
		{
			if (fieldType.IsAssignableFrom(entity.Components[c].GetType()))
				return true;
		}
		for (int i = 0; i < entity.Transform.ChildCount; i++)
		{
			if (EntitySubtreeHasMatch(entity.Transform.GetChild(i).Entity, fieldType))
				return true;
		}
		return false;
	}
}