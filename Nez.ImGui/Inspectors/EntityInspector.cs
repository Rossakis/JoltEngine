using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Nez.ImGuiTools.ObjectInspectors;
using Nez.Utils;
using Num = System.Numerics;


namespace Nez.ImGuiTools;

public class EntityInspector
{
	public Entity Entity { get; }

	public bool IsMainInspector { get; set; }
	public float MainInspectorWidth => _mainInspectorWidth;

	private string _entityWindowId = "entity-" + NezImGui.GetScopeId().ToString();
	private bool _shouldFocusWindow;
	private string _componentNameFilter;
	private TransformInspector _transformInspector;
	private List<IComponentInspector> _componentInspectors = new();

	private float _mainInspectorWidth = 500f;
	private float _minInspectorWidth = 1f;
	private float _maxInspectorWidth = Screen.MonitorWidth;

	public EntityInspector(Entity entity = null)
	{
		Entity = entity;
		if (Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			for (var i = 0; i < entity.Components.Count; i++)
				_componentInspectors.Add(new ComponentInspector(entity.Components[i]));
		}
	}

	public void Draw()
	{
		var inspectorName = IsMainInspector ? "MAIN Entity Inspector" : $"Entity Inspector";

		if (IsMainInspector)
		{
			var topMargin = 20f;
			var rightMargin = 0f;
			var windowPosX = Screen.Width - _mainInspectorWidth - rightMargin;
			var windowPosY = topMargin;
			var windowHeight = Screen.Height - topMargin;

			ImGui.SetNextWindowPos(new Num.Vector2(windowPosX, windowPosY), ImGuiCond.Always);
			ImGui.SetNextWindowSize(new Num.Vector2(_mainInspectorWidth, windowHeight), ImGuiCond.FirstUseEver);
		}
		else
		{
			ImGui.SetNextWindowSize(new Num.Vector2(335, 400), ImGuiCond.FirstUseEver);
			ImGui.SetNextWindowSizeConstraints(new Num.Vector2(335, 200), new Num.Vector2(800, 800));
		}

		var open = true;
		if (ImGui.Begin($"{inspectorName}###{_entityWindowId}", ref open))
		{
			if (Entity == null)
			{
				ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "No entity selected.");
				ImGui.End();
				return;
			}

			if (IsMainInspector)
			{
				var currentWidth = ImGui.GetWindowSize().X;
				if (Math.Abs(currentWidth - _mainInspectorWidth) > 0.01f)
					_mainInspectorWidth = Math.Clamp(currentWidth, _minInspectorWidth, _maxInspectorWidth);
			}

			var enabled = Entity.Enabled;
			if (ImGui.Checkbox("Enabled", ref enabled))
				Entity.Enabled = enabled;

			ImGui.InputText("Name", ref Entity.Name, 25);

			var updateOrder = Entity.UpdateOrder;
			if (ImGui.InputInt("Update Order", ref updateOrder))
				Entity.SetUpdateOrder(updateOrder);

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

			ImGui.End();
		}

		if (!open)
			Core.GetGlobalManager<ImGuiManager>().StopInspectingEntity(this);
	}

	private void DrawComponentSelectorPopup()
	{
		if (Entity == null)
			return;

		if (ImGui.BeginPopup("component-selector"))
		{
			ImGui.InputText("###ComponentFilter", ref _componentNameFilter, 25);
			ImGui.Separator();

			var isNezType = false;
			var isColliderType = false;
			foreach (var subclassType in InspectorCache.GetAllComponentSubclassTypes())
				if (string.IsNullOrEmpty(_componentNameFilter) ||
				    subclassType.Name.ToLower().Contains(_componentNameFilter.ToLower()))
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
						Entity.AddComponent(Activator.CreateInstance(subclassType) as Component);
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

	public void SetWidth(float width)
	{
		_mainInspectorWidth = Math.Clamp(width, _minInspectorWidth, _maxInspectorWidth);
	}
}