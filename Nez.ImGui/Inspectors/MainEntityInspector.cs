using ImGuiNET;
using Nez.ImGuiTools.ObjectInspectors;
using Nez.Utils;
using System;
using System.Collections.Generic;
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

				var enabled = Entity.Enabled;
				if (ImGui.Checkbox("Enabled", ref enabled))
					Entity.Enabled = enabled;

				ImGui.InputText("Name", ref Entity.Name, 25);

				// TODO:
				int oldUpdateOrder = Entity.UpdateOrder;
				int updateOrder = Entity.UpdateOrder;
				if (ImGui.InputInt("Update Order", ref updateOrder))
				{
					// Get the PropertyInfo for the UpdateOrder property
					var propertyInfo = typeof(Entity).GetProperty(nameof(Entity.UpdateOrder));

					// Create the undo/redo action
					var action = new GenericValueChangeAction(
						Entity,
						propertyInfo,
						oldUpdateOrder,
						updateOrder,
						$"{Entity.Name}.UpdateOrder"
					);

					// Push to the tracker (also marks as dirty)
					EditorChangeTracker.PushUndo(action, Entity, $"{Entity.Name}.UpdateOrder");

					// Actually apply the change
					Entity.SetUpdateOrder(updateOrder);
				}

				var updateInterval = (int)Entity.UpdateInterval;
				if (ImGui.SliderInt("Update Interval", ref updateInterval, 1, 100))
					Entity.UpdateInterval = (uint)updateInterval;

				var tag = Entity.Tag;
				if (ImGui.InputInt("Tag", ref tag))
					Entity.Tag = tag;

				var debugEnabled = Entity.DebugRenderEnabled;
				if (ImGui.Checkbox("Debug Render Enabled", ref debugEnabled))
					Entity.DebugRenderEnabled = debugEnabled;

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