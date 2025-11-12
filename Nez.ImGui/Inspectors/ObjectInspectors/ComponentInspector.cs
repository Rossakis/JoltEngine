using System;
using System.Collections.Generic;
using ImGuiNET;
using Nez.ImGuiTools.Inspectors.CustomInspectors;
using Nez.ImGuiTools.TypeInspectors;
using Nez.ImGuiTools.UndoActions;
using Nez.Persistence;
using Nez.Sprites;
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

			// Special handling for SpriteRenderer (like Transform)
			if (component.GetType().FullName == typeof(SpriteRenderer).FullName)
			{
				// For SpriteRenderer, create a mix of standard + custom inspectors
				_inspectors = TypeInspectorUtils.GetInspectableProperties(component);
				
				// Add the custom file inspector as an additional inspector
				var fileInspector = new SpriteRendererFileInspector();
				fileInspector.SetTarget((SpriteRenderer)component, typeof(SpriteRenderer).GetProperty("Sprite"));
				fileInspector.Initialize();
				_inspectors.Add(fileInspector);
			}
			else if(component.GetType().FullName == typeof(SpriteAnimator).FullName)
			{
				// For SpriteAnimator, create a mix of standard + custom inspectors
				_inspectors = TypeInspectorUtils.GetInspectableProperties(component);
				
				// Add the custom file inspector as an additional inspector
				var fileInspector = new SpriteAnimatorFileInspector();
				fileInspector.SetTarget((SpriteAnimator)component, typeof(SpriteAnimator).GetProperty("TextureFilePath"));
				fileInspector.Initialize();
				_inspectors.Add(fileInspector);
			}
			else
			{
				_inspectors = TypeInspectorUtils.GetInspectableProperties(component);
			}

			var typeName = _component.GetType().IsGenericType
				? $"{_component.GetType().BaseType.Name}<{_component.GetType().GetGenericArguments()[0].Name}>"
				: _component.GetType().Name;

			// If the component's name is null or empty, treat it as the type name
			var compName = string.IsNullOrEmpty(_component.Name) ? typeName : _component.Name;

			// Show only type if name matches type, otherwise show "Name (Type)"
			if (compName == typeName)
				_name = typeName;
			else
				_name = $"{compName} ({typeName})";

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
				//Copy - FIXED: Clone the component immediately when copying
				if (ImGui.Selectable("Copy Component")) 
				{
					// Clone the component RIGHT NOW to capture its current state
					try
					{
						var clonedComponent = _component.Clone();
						_imGuiManager.SceneGraphWindow.CopiedComponent = clonedComponent;
						System.Console.WriteLine($"Copied component: {_component.GetType().Name}");
					}
					catch (Exception ex)
					{
						// Fallback: use JSON serialization if Clone() fails
						try
						{
							var jsonSettings = new JsonSettings
							{
								PrettyPrint = false,
								TypeNameHandling = TypeNameHandling.Auto,
								PreserveReferencesHandling = false
							};
							
							var sourceData = _component.Data;
							if (sourceData != null)
							{
								// Create a new component instance
								var componentType = _component.GetType();
								var clonedComponent = (Component)Activator.CreateInstance(componentType);
								clonedComponent.Name = _component.Name;
								clonedComponent.Enabled = _component.Enabled;
								
								// Clone the data using JSON
								var json = Json.ToJson(sourceData, jsonSettings);
								var clonedData = (ComponentData)Json.FromJson(json, sourceData.GetType());
								clonedComponent.Data = clonedData;
								
								_imGuiManager.SceneGraphWindow.CopiedComponent = clonedComponent;
								System.Console.WriteLine($"Copied component via JSON fallback: {_component.GetType().Name}");
							}
						}
						catch (Exception jsonEx)
						{
							System.Console.WriteLine($"Failed to copy component {_component.GetType().Name}: {ex.Message}. JSON fallback also failed: {jsonEx.Message}");
						}
					}
				}

				NezImGui.SmallVerticalSpace();

				//Paste - Simplified since we now have a true copy
				var copiedComponent = _imGuiManager.SceneGraphWindow.CopiedComponent;
				var canPaste = copiedComponent != null && copiedComponent.GetType() == _component.GetType();
				
				if (!canPaste)
				{
					ImGui.BeginDisabled();
				}

				var pasteText = canPaste ? "Paste Component Values" : 
									   (copiedComponent != null ? $"Can't paste {copiedComponent.GetType().Name} into {_component.GetType().Name}" : "No component copied");
				
				if (ImGui.Selectable(pasteText) && canPaste)
				{
					PasteComponentValues(copiedComponent, _component);
				}

				if (!canPaste)
				{
					ImGui.EndDisabled();
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

		/// <summary>
		/// Pastes component data from source to target component of the same type.
		/// Uses JSON serialization for reliable deep cloning.
		/// </summary>
		private void PasteComponentValues(Component sourceComponent, Component targetComponent)
		{
			if (sourceComponent == null || targetComponent == null)
				return;

			if (sourceComponent.GetType() != targetComponent.GetType())
			{
				System.Console.WriteLine($"Cannot paste {sourceComponent.GetType().Name} into {targetComponent.GetType().Name} - types must match");
				return;
			}

			try
			{
				// Store old data for undo
				var oldData = targetComponent.Data;

				// Get the source component's data
				var sourceData = sourceComponent.Data;

				if (sourceData == null)
				{
					System.Console.WriteLine("Source component has no data to copy");
					return;
				}

				// DEEP CLONE using JSON serialization (most reliable approach)
				ComponentData clonedData;
				try
				{
					var jsonSettings = new JsonSettings
					{
						PrettyPrint = false,
						TypeNameHandling = TypeNameHandling.Auto,
						PreserveReferencesHandling = false
					};

					var json = Json.ToJson(sourceData, jsonSettings);
					clonedData = (ComponentData)Json.FromJson(json, sourceData.GetType());
				}
				catch (Exception ex)
				{
					System.Console.WriteLine($"Failed to clone component data via JSON: {ex.Message}");
					return;
				}

				// Create undo action BEFORE making changes
				EditorChangeTracker.PushUndo(
					new ComponentDataChangeAction(
						targetComponent,
						oldData,
						clonedData, // Use the cloned data, not the original
						$"Paste {targetComponent.GetType().Name} values"
					),
					targetComponent.Entity,
					$"Paste {targetComponent.GetType().Name} values"
				);

				// Apply the cloned data to the target component
				targetComponent.Data = clonedData;

				System.Console.WriteLine($"Successfully pasted {sourceComponent.GetType().Name} values");
			}
			catch (Exception ex)
			{
				System.Console.WriteLine($"Failed to paste component values: {ex.Message}");
			}
		}
	}
}