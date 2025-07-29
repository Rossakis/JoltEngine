using ImGuiNET;
using Nez.ImGuiTools.ObjectInspectors;
using Nez.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Num = System.Numerics;

namespace Nez.ImGuiTools;

public class MainEntityInspector
{
	public Entity Entity { get; private set; }
	public float Width { get; set; } = 500f; // Persist this separately
	public float MainInspectorWidth => _mainInspectorWidth;
	private static float _mainInspectorWidth = 500f;
	private float _minInspectorWidth = 1f;
	private float _maxInspectorWidth = Screen.MonitorWidth;
	public bool IsOpen { get; set; } = true; // Separate open/close flag
	public float MainInspectorPosY { get; private set; }

	private readonly string _windowId = "MAIN_INSPECTOR_WINDOW";
	private TransformInspector _transformInspector;
	private List<IComponentInspector> _componentInspectors = new();
	private string _componentNameFilter;

	private bool _isEditingUpdateInterval = false;
	private uint _updateIntervalEditStartValue;
	private bool _isEditingName = false;
	private string _nameEditStartValue;

	public MainEntityInspector(Entity entity = null)
	{
		Entity = entity;
		_componentInspectors.Clear();

		if (Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			for (var i = 0; i < entity.Components.Count; i++)
				_componentInspectors.Add(new ComponentInspector(entity.Components[i]));
		}
	}

	public void SetEntity(Entity entity)
	{
		Entity = entity;
		_componentInspectors.Clear();
		_transformInspector = null;
		if (Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			for (var i = 0; i < entity.Components.Count; i++)
				_componentInspectors.Add(new ComponentInspector(entity.Components[i]));
		}
	}

	public void Draw()
	{
		if (!IsOpen)
			return;

		var topMargin = 20f * ImGui.GetIO().FontGlobalScale;

		ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 0.0f);
		ImGui.PushStyleColor(ImGuiCol.ResizeGrip, new Num.Vector4(0, 0, 0, 0));

		var windowPosX = Screen.Width - _mainInspectorWidth;
		var windowHeight = Screen.Height - topMargin;
		MainInspectorPosY = topMargin;

		ImGui.SetNextWindowPos(new Num.Vector2(windowPosX, MainInspectorPosY), ImGuiCond.Always);

		// Only set window size on first use, not every frame
		ImGui.SetNextWindowSize(new Num.Vector2(_mainInspectorWidth, windowHeight), ImGuiCond.FirstUseEver);

		var open = IsOpen;
		var windowTitle = $"Main Inspector##{_windowId}"; // constant title

		if (ImGui.Begin(windowTitle, ref open, ImGuiWindowFlags.None))
		{
			var entityName = Entity != null ? Entity.Name : "";
			ImGui.SetWindowFontScale(1.5f); // Double the font size for header effect
			ImGui.Text(entityName);
			ImGui.SetWindowFontScale(1.0f); // Reset to default

			NezImGui.BigVerticalSpace();

			// Always update width, regardless of entity selection
			var currentWidth = ImGui.GetWindowSize().X;
			if (Math.Abs(currentWidth - _mainInspectorWidth) > 0.01f)
				_mainInspectorWidth = Math.Clamp(currentWidth, _minInspectorWidth, _maxInspectorWidth);

			if (Entity == null)
			{
				ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "No entity selected.");
			}
			else
			{
				// Draw main entity UI
				var type = Entity.Type.ToString();
				ImGui.InputText("InstanceType", ref type, 30);

				// Enabled
				{
					bool oldEnabled = Entity.Enabled;
					bool enabled = oldEnabled;
					if (ImGui.Checkbox("Enabled", ref enabled) && enabled != oldEnabled)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.Enabled)),
								oldEnabled,
								enabled,
								$"{Entity.Name}.Enabled"
							),
							Entity,
							$"{Entity.Name}.Enabled"
						);
						Entity.SetEnabled(enabled);
					}
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


				// UpdateOrder
				{
					int oldUpdateOrder = Entity.UpdateOrder;
					int updateOrder = oldUpdateOrder;
					if (ImGui.InputInt("Update Order", ref updateOrder) && updateOrder != oldUpdateOrder)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.UpdateOrder)),
								oldUpdateOrder,
								updateOrder,
								$"{Entity.Name}.UpdateOrder"
							),
							Entity,
							$"{Entity.Name}.UpdateOrder"
						);
						Entity.SetUpdateOrder(updateOrder);
					}
				}

				// UpdateInterval
				{
					int updateInterval = (int)Entity.UpdateInterval;

					// Draw the slider first
					bool changed = ImGui.SliderInt("Update Interval", ref updateInterval, 1, 100);

					// Start of edit session: store the initial value
					if (ImGui.IsItemActive() && !_isEditingUpdateInterval)
					{
						_isEditingUpdateInterval = true;
						_updateIntervalEditStartValue = Entity.UpdateInterval;
					}

					// Apply the value while dragging
					if (changed)
						Entity.UpdateInterval = (uint)updateInterval;

					// End of edit session: push undo if value changed
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

				// Tag
				{
					int oldTag = Entity.Tag;
					int tag = oldTag;
					if (ImGui.InputInt("Tag", ref tag) && tag != oldTag)
					{
						EditorChangeTracker.PushUndo(
							new GenericValueChangeAction(
								Entity,
								typeof(Entity).GetProperty(nameof(Entity.Tag)),
								oldTag,
								tag,
								$"{Entity.Name}.Tag"
							),
							Entity,
							$"{Entity.Name}.Tag"
						);
						Entity.Tag = tag;
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
			}
		}

		ImGui.End();
		ImGui.PopStyleVar();
		ImGui.PopStyleColor();

		if (!open)
			Nez.Core.GetGlobalManager<ImGuiManager>().CloseMainEntityInspector();
	}

	private void DrawComponentSelectorPopup()
	{
		if (Entity == null) return;

		EntityInspector.DrawComponentSelector(Entity, _componentNameFilter);
	}
}