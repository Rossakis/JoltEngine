using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Nez.ImGuiTools.ObjectInspectors;
using Nez.ImGuiTools.UndoActions;
using Nez.Utils;
using Num = System.Numerics;


namespace Nez.ImGuiTools;

public class EntityInspector
{
	public Entity Entity { get; }

	private string _entityWindowId = "entity-" + NezImGui.GetScopeId().ToString();
	private bool _shouldFocusWindow;
	private string _componentNameFilter;
	private TransformInspector _transformInspector;
	private List<IComponentInspector> _componentInspectors = new();

	private ImGuiManager _imGuiManager;

	private int _normalEntityInspector_PosOffset;

	// Undo/redo support for edit sessions
	private bool _isEditingName = false;
	private string _nameEditStartValue;

	private bool _isEditingUpdateOrder = false;
	private int _updateOrderEditStartValue;

	private bool _isEditingUpdateInterval = false;
	private uint _updateIntervalEditStartValue;

	private bool _isEditingTag = false;
	private int _tagEditStartValue;

	public EntityInspector(Entity entity, int NormalInspector_PosOffset = 0)
	{
		Entity = entity;
		_normalEntityInspector_PosOffset = NormalInspector_PosOffset;

		ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = false;

		_transformInspector = new TransformInspector(Entity.Transform);
		for (var i = 0; i < entity.Components.Count; i++)
			_componentInspectors.Add(ComponentInspectorFactory.CreateInspector(entity.Components[i])); // Use factory here
	}

	public void Draw()
	{
		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		var topMargin = 20f;
		var windowHeight = Screen.Height - topMargin;

		var pos = new Num.Vector2(Screen.Width / 2f, Screen.Height / 2f) -
		          new Num.Vector2(_normalEntityInspector_PosOffset, -_normalEntityInspector_PosOffset);

		ImGui.SetNextWindowPos(pos, ImGuiCond.Once);
		ImGui.SetNextWindowSize(new Num.Vector2(_imGuiManager.MainEntityInspector.Width, windowHeight / 2f),
			ImGuiCond.FirstUseEver);

		var open = true;
		if (ImGui.Begin($"Inspector: {Entity.Name}###{_entityWindowId}", ref open))
		{
			if (Entity == null)
			{
				ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "No entity selected.");
				ImGui.End();
				return;
			}

			// Enabled (no edit session needed, checkbox is atomic)
			var enabled = Entity.Enabled;
			if (ImGui.Checkbox("Enabled", ref enabled) && enabled != Entity.Enabled)
			{
				EditorChangeTracker.PushUndo(
					new GenericValueChangeAction(
						Entity,
						typeof(Entity).GetProperty(nameof(Entity.Enabled)),
						Entity.Enabled,
						enabled,
						$"{Entity.Name}.Enabled"
					),
					Entity,
					$"{Entity.Name}.Enabled"
				);
				Entity.Enabled = enabled;
			}

			// Name (edit session)
			{
				string name = Entity.Name;
				bool changed = ImGui.InputText("Name", ref name, 25);

				if (ImGui.IsItemActive() && !_isEditingName)
				{
					_isEditingName = true;
					_nameEditStartValue = Entity.Name;
				}

				if (changed)
					Entity.Name = name;

				if (_isEditingName && ImGui.IsItemDeactivatedAfterEdit())
				{
					_isEditingName = false;
					if (Entity.Name != _nameEditStartValue)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.Name)),
								_nameEditStartValue,
								Entity.Name,
								$"{_nameEditStartValue}.Name"
							),
							Entity,
							$"{_nameEditStartValue}.Name"
						);
					}
				}
			}

			// UpdateOrder (edit session)
			{
				int updateOrder = Entity.UpdateOrder;
				bool changed = ImGui.InputInt("Update Order", ref updateOrder);

				if (ImGui.IsItemActive() && !_isEditingUpdateOrder)
				{
					_isEditingUpdateOrder = true;
					_updateOrderEditStartValue = Entity.UpdateOrder;
				}

				if (changed)
					Entity.SetUpdateOrder(updateOrder);

				if (_isEditingUpdateOrder && ImGui.IsItemDeactivatedAfterEdit())
				{
					_isEditingUpdateOrder = false;
					if (Entity.UpdateOrder != _updateOrderEditStartValue)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.UpdateOrder)),
								_updateOrderEditStartValue,
								Entity.UpdateOrder,
								$"{Entity.Name}.UpdateOrder"
							),
							Entity,
							$"{Entity.Name}.UpdateOrder"
						);
					}
				}
			}

			// UpdateInterval (edit session, already present)
			{
				int updateInterval = (int)Entity.UpdateInterval;
				bool changed = ImGui.SliderInt("Update Interval", ref updateInterval, 1, 100);

				if (ImGui.IsItemActive() && !_isEditingUpdateInterval)
				{
					_isEditingUpdateInterval = true;
					_updateIntervalEditStartValue = Entity.UpdateInterval;
				}

				if (changed)
					Entity.UpdateInterval = (uint)updateInterval;

				if (_isEditingUpdateInterval && ImGui.IsItemDeactivatedAfterEdit())
				{
					_isEditingUpdateInterval = false;
					if (Entity.UpdateInterval != _updateIntervalEditStartValue)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								(obj, val) => ((Entity)obj).UpdateInterval = (uint)val,
								_updateIntervalEditStartValue,
								Entity.UpdateInterval,
								$"{Entity.Name}.UpdateInterval"
							),
							Entity,
							$"{Entity.Name}.UpdateInterval"
						);
					}
				}
			}

			// Tag (edit session)
			{
				int tag = Entity.Tag;
				bool changed = ImGui.InputInt("Tag", ref tag);

				if (ImGui.IsItemActive() && !_isEditingTag)
				{
					_isEditingTag = true;
					_tagEditStartValue = Entity.Tag;
				}

				if (changed)
					Entity.Tag = tag;

				if (_isEditingTag && ImGui.IsItemDeactivatedAfterEdit())
				{
					_isEditingTag = false;
					if (Entity.Tag != _tagEditStartValue)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.Tag)),
								_tagEditStartValue,
								Entity.Tag,
								$"{Entity.Name}.Tag"
							),
							Entity,
							$"{Entity.Name}.Tag"
						);
					}
				}
			}

			// DebugRenderEnabled
			{
				bool oldDebugEnabled = Entity.DebugRenderEnabled;
				bool debugEnabled = oldDebugEnabled;
				if (ImGui.Checkbox("Debug Render Enabled", ref debugEnabled) && debugEnabled != oldDebugEnabled)
				{
					EditorChangeTracker.PushUndo(
						new GenericValueChangeAction(
							Entity,
							typeof(Entity).GetProperty(nameof(Entity.DebugRenderEnabled)),
							oldDebugEnabled,
							debugEnabled,
							$"{Entity.Name}.DebugRenderEnabled"
						),
						Entity,
						$"{Entity.Name}.DebugRenderEnabled"
					);
					Entity.DebugRenderEnabled = debugEnabled;
				}
			}

			NezImGui.MediumVerticalSpace();
			_transformInspector.Draw();
			NezImGui.MediumVerticalSpace();

			for (var i = _componentInspectors.Count - 1; i >= 0; i--)
			{
				if (_componentInspectors[i].Entity == null)
				{
					_componentInspectors.RemoveAt(i);
					continue;
				}

				_componentInspectors[i].Draw();
				NezImGui.MediumVerticalSpace();
			}

			if (NezImGui.CenteredButton("Add Component", 0.6f))
			{
				_componentNameFilter = "";
				ImGui.OpenPopup("component-selector");
			}

			DrawComponentSelectorPopup();

			ImGui.End();
		}

		if (!open)
			_imGuiManager.CloseEntityInspector(this);
	}

	private void DrawComponentSelectorPopup()
	{
		if (Entity == null)
			return;

		DrawComponentSelector(Entity, _componentNameFilter);
	}

	public static void DrawComponentSelector(Entity entity, string componentNameFilter)
	{
		if (ImGui.BeginPopup("component-selector"))
		{
			ImGui.InputText("###ComponentFilter", ref componentNameFilter, 25);
			ImGui.Separator();

			var isNezType = false;
			var isColliderType = false;
			foreach (var subclassType in InspectorCache.GetAllComponentSubclassTypes())
				if (string.IsNullOrEmpty(componentNameFilter) ||
				    subclassType.Name.ToLower().Contains(componentNameFilter.ToLower()))
				{
					if (!isNezType && subclassType.Namespace.StartsWith("Nez"))
					{
						isNezType = true;
						ImGui.Separator();
					}

					if (!isColliderType && typeof(Collider).IsAssignableFrom(subclassType))
					{
						isColliderType = true;
						ImGui.Separator();
					}

					if (ImGui.Selectable(subclassType.Name))
					{
						entity.AddComponent(Activator.CreateInstance(subclassType) as Component);
						ImGui.CloseCurrentPopup();
					}
				}

			ImGui.EndPopup();
		}
	}

	public void SetWindowFocus()
	{
		_shouldFocusWindow = true;
	}
}