using System;
using System.Collections.Generic;
using ImGuiNET;
using Nez.ImGuiTools.TypeInspectors;
using Nez.Utils.Extensions;


namespace Nez.ImGuiTools.ObjectInspectors
{
	public class ComponentInspector : AbstractComponentInspector
	{
		private ImGuiManager _imGuiManager;
		public override Entity Entity => _component.Entity;
		public override Component Component => _component;

		Component _component;
		string _name;
		List<Action> _componentDelegateMethods = new List<Action>();

		public ComponentInspector(Component component)
		{
			_component = component;
			_inspectors = TypeInspectorUtils.GetInspectableProperties(component);

			if (_component.GetType().IsGenericType)
			{
				var genericType = _component.GetType().GetGenericArguments()[0].Name;
				_name = $"{_component.GetType().BaseType.Name}<{genericType}>";
			}
			else
			{
				if(_component.Name != null)
					_name = _component.Name + $" ({_component.GetType().Name})";
				else
					_name = _component.GetType().Name;
			}

			var methods = TypeInspectorUtils.GetAllMethodsWithAttribute<InspectorDelegateAttribute>(_component.GetType());
			foreach (var method in methods)
			{
				// only allow zero param methods
				if (method.GetParameters().Length == 0)
					_componentDelegateMethods.Add((Action) Delegate.CreateDelegate(typeof(Action), _component, method));
			}
		}

		public override void Draw()
		{
			if(_imGuiManager == null)
				_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

			ImGui.PushID(_scopeId);
			var isHeaderOpen = ImGui.CollapsingHeader(_name);

			// context menu has to be outside the isHeaderOpen block so it works open or closed
			if (ImGui.BeginPopupContextItem())
			{
				//Copy
				if (ImGui.Selectable("Copy Component")) 
				{
					_imGuiManager.SceneGraphWindow.CopiedComponent = _component;
				}

				NezImGui.SmallVerticalSpace();

				//Paste
				if(_imGuiManager.SceneGraphWindow.CopiedComponent != null && ImGui.Selectable("Paste Component"))
				{
					var temp = _component;
					_component.Entity.RemoveComponent(_component);
					temp.Entity.AddComponent(_imGuiManager.SceneGraphWindow.CopiedComponent);
				}

				ImGui.Separator();
				NezImGui.SmallVerticalSpace();

				if (ImGui.Selectable("Remove Component"))
				{
					_component.RemoveComponent();
				}

				ImGui.EndPopup();
			}

			if (isHeaderOpen)
			{
				var enabled = _component.Enabled;
				if (ImGui.Checkbox("Enabled", ref enabled))
					_component.SetEnabled(enabled);

				for (var i = _inspectors.Count - 1; i >= 0; i--)
				{
					if (_inspectors[i].IsTargetDestroyed)
					{
						_inspectors.RemoveAt(i);
						continue;
					}

					_inspectors[i].Draw();
				}

				foreach (var action in _componentDelegateMethods)
					action();
			}

			ImGui.PopID();
		}
	}
}