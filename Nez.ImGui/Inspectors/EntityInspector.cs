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

	/// <summary>
	/// Main inspector is the one that persists to the right side of the screen, unlike the ones that are spawned wherever.
	/// </summary>
	public bool IsMainInspector { get; set; } 

	private string _entityWindowId = "entity-" + NezImGui.GetScopeId().ToString();
	private bool _shouldFocusWindow;
	private string _componentNameFilter;
	private TransformInspector _transformInspector;
	private List<IComponentInspector> _componentInspectors = new();

	private float _mainInspectorWidth = 700f;
	private float _minInspectorWidth = 400f;
	private float _maxInspectorWidth = Screen.MonitorWidth;

	public EntityInspector(Entity entity)
	{
		Entity = entity;
		_transformInspector = new TransformInspector(Entity.Transform);

		for (var i = 0; i < entity.Components.Count; i++)
			_componentInspectors.Add(new ComponentInspector(entity.Components[i]));
	}

	public void Draw()
	{
		// check to see if we are still alive
		if (Entity.IsDestroyed)
		{
			Core.GetGlobalManager<ImGuiManager>().StopInspectingEntity(this);
			return;
		}

		if (_shouldFocusWindow)
		{
			_shouldFocusWindow = false;
			ImGui.SetNextWindowFocus();
			ImGui.SetNextWindowCollapsed(false);
		}

		// every 60 frames we check for newly added Components and add them
		if (Time.FrameCount % 60 == 0)
			for (var i = 0; i < Entity.Components.Count; i++)
			{
				var component = Entity.Components[i];
				if (_componentInspectors
					    .Where(inspector => inspector.Component != null && inspector.Component == component)
					    .Count() == 0)
					_componentInspectors.Insert(0, new ComponentInspector(component));
			}

		ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None;

		string InspectorName = IsMainInspector ? "MAIN Entity Inspector" : $"Entity Inspector";
		if (IsMainInspector)
		{
			float topMargin = 33f;
			float rightMargin = 10f;

			// Calculate left edge so right edge is always at Screen.Width - rightMargin
			float windowPosX = Screen.Width - _mainInspectorWidth - rightMargin;
			float windowPosY = topMargin;
			float windowHeight = Screen.Height - topMargin;

			// Set position every frame, but size only once
			ImGui.SetNextWindowPos(new Num.Vector2(windowPosX, windowPosY), ImGuiCond.Always);
			ImGui.SetNextWindowSize(new Num.Vector2(_mainInspectorWidth, windowHeight), ImGuiCond.Once);
		}
		else
		{
			// Use a reasonable default size and let ImGui auto-size after first use
			ImGui.SetNextWindowSize(new Num.Vector2(335, 400), ImGuiCond.FirstUseEver);
			ImGui.SetNextWindowSizeConstraints(new Num.Vector2(335, 200), new Num.Vector2(800, 800)); // or whatever max you want
		}

		var open = true;
		if (ImGui.Begin($"{InspectorName}: {Entity.Name}###{_entityWindowId}", ref open, windowFlags))
		{
			if (IsMainInspector)
			{
				// Get the current window width and update _mainInspectorWidth
				float currentWidth = ImGui.GetWindowSize().X;
				if (Math.Abs(-_mainInspectorWidth) > 0.01f)
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

			// watch out for removed Components
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
					// stick a seperator in after custom Components and before Colliders
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

	/// <summary>
	/// sets this EntityInspector to be focused the next time it is drawn
	/// </summary>
	public void SetWindowFocus()
	{
		_shouldFocusWindow = true;
	}
}