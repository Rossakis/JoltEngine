using ImGuiNET;
using Nez.ImGuiTools.ObjectInspectors;
using Nez.ImGuiTools.TypeInspectors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nez.ImGuiTools.Inspectors.ObjectInspectors
{
	/// <summary>
	/// MultiComponentInspector is used to inspect and edit components that are common across multiple entities.
	/// It allows synchronized editing of the same component type on different entities.
	/// </summary>
	public class MultiComponentInspector : IComponentInspector
	{
		private Type _componentType;
		private List<Entity> _entities;
		private List<Component> _components;
		private List<AbstractTypeInspector> _fieldInspectors = new();

		public Entity Entity => null; // Not a single entity
		public Component Component => null; // Not a single component

		public MultiComponentInspector(Type componentType, List<Entity> entities)
		{
			_componentType = componentType;
			_entities = entities;
			_components = entities
				.SelectMany(e => e.Components)
				.Where(c => c.GetType().FullName == componentType.FullName)
				.ToList();

			// Use the first component as the reference for fields/properties
			var referenceComponent = _components.FirstOrDefault();
			if (referenceComponent != null)
			{
				_fieldInspectors = TypeInspectorUtils.GetInspectableProperties(referenceComponent);
			}
		}

		public void Draw()
		{
			ImGui.PushID(_componentType.FullName);
			var headerOpen = ImGui.CollapsingHeader(_componentType.Name);

			if (headerOpen)
			{
				foreach (var inspector in _fieldInspectors)
				{
					// Draw the field/property for the first component, but propagate changes to all
					var beforeValue = inspector.GetValue();
					inspector.Draw();

					var afterValue = inspector.GetValue();
					if (!Equals(beforeValue, afterValue))
					{
						// 3) Propagate changes to all selected entities' components
						foreach (var comp in _components.Skip(1))
						{
							var field = inspector.GetFieldInfo();
							var prop = inspector.GetPropertyInfo();

							if (field != null)
								field.SetValue(comp, afterValue);
							else if (prop != null && prop.CanWrite)
								prop.SetValue(comp, afterValue);
						}
					}
				}
			}

			ImGui.PopID();
		}
	}
}
