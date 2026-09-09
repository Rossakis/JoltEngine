using System;
using System.Collections.Generic;
using ImGuiNET;
using Voltage.Sprites;
using Voltage.Utils.Extensions;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.Inspectors.Attributes;
using Voltage.Editor.Inspectors.CustomInspectors;
using Voltage.Editor.Inspectors.TypeInspectors;
using Voltage.Editor.Undo;
using Voltage.Editor.Undo.ComponentActions;
using Voltage.Editor.Utils;
using Voltage.Persistence;
using Num = System.Numerics;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.PropertyActions;
using Voltage.Editor.Windows;

namespace Voltage.Editor.Inspectors.ObjectInspectors
{
	public class ComponentInspector : AbstractComponentInspector
	{
		private ImGuiManager _imGuiManager;
		public override Entity Entity => _component.Entity;
		public override Component Component => _component;

		private readonly Component _component;
		private readonly string _name;

		// Basically buttons for components, like "Apply Sprite Changes" on SpriteRenderer, or "Reset Transform" on Transform, etc.
		private readonly List<Action> _componentDelegateMethods = new List<Action>(); 

		// Separate lists for regular and read-only struct inspectors
		private readonly List<AbstractTypeInspector> _regularInspectors = new List<AbstractTypeInspector>();
		private readonly List<AbstractTypeInspector> _readOnlyStructInspectors = new List<AbstractTypeInspector>();
		private bool _isReadOnlyStructsOpen = false;

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

			// Remove the auto-generated "Enabled" inspector — we draw it manually with entity-disabled guard
			_inspectors.RemoveAll(i => i.Name == nameof(Component.Enabled));

			SeparateReadOnlyStructs();

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

		/// <summary>
		/// Separates read-only struct inspectors from regular inspectors
		/// </summary>
		private void SeparateReadOnlyStructs()
		{
			_regularInspectors.Clear();
			_readOnlyStructInspectors.Clear();

			foreach (var inspector in _inspectors)
			{
				// Check if this is a StructInspector using GetType() instead of pattern matching
				if (inspector.GetType() == typeof(TypeInspectors.StructInspector))
				{
					var structInspector = inspector as TypeInspectors.StructInspector;
					
					if (structInspector.MemberInfo != null)
					{
						bool isReadOnly = false;
						
						if (structInspector.MemberInfo is System.Reflection.FieldInfo fieldInfo)
						{
							isReadOnly = fieldInfo.IsInitOnly;
						}
						else if (structInspector.MemberInfo is System.Reflection.PropertyInfo propInfo)
						{
							bool hasPublicSetter = propInfo.CanWrite && (propInfo.SetMethod?.IsPublic ?? false);
							isReadOnly = !hasPublicSetter;
						}

						if (isReadOnly)
						{
							_readOnlyStructInspectors.Add(inspector);
						}
						else
						{
							_regularInspectors.Add(inspector);
						}
					}
					else
					{
						_regularInspectors.Add(inspector);
					}
				}
				else
				{
					_regularInspectors.Add(inspector);
				}
			}
		}

		public override unsafe void Draw()
		{
			if(_imGuiManager == null)
				_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

			ImGui.PushID(_scopeId);

			// Highlight non-serialized (code-created) components with a light green header
			var isRuntimeOnly = !_component.IsSerialized;
			if (isRuntimeOnly)
			{
				ImGui.PushStyleColor(ImGuiCol.Header, new Num.Vector4(0.2f, 0.45f, 0.2f, 0.6f));
				ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Num.Vector4(0.25f, 0.5f, 0.25f, 0.7f));
				ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Num.Vector4(0.3f, 0.55f, 0.3f, 0.8f));
			}

			var isHeaderOpen = Gui.CollapsingHeader(isRuntimeOnly ? $"{_name}  [Runtime Only]" : _name);

			if (isRuntimeOnly)
				ImGui.PopStyleColor(3);

			if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
			{
				ComponentReferenceTypeInspector.DraggedComponent = _component;
				byte dummy = 1;
				ImGui.SetDragDropPayload(ComponentReferenceTypeInspector.DragDropPayloadId, new IntPtr(&dummy), sizeof(byte));
				ImGuiSafe.TextSafe(_component.ToString());
				ImGui.EndDragDropSource();
			}

			// Show tooltip for runtime-only components
			if (isRuntimeOnly && ImGui.IsItemHovered())
			{
				ImGui.BeginTooltip();
				ImGui.TextColored(new Num.Vector4(0.5f, 1f, 0.5f, 1f), "Runtime Only Component");
				ImGui.Text("This component was added through code and will not be saved.");
				ImGui.Text("It will be recreated automatically when the script runs.");
				ImGui.EndTooltip();
			}

			// context menu has to be outside the isHeaderOpen block so it works open or closed
			if (Gui.BeginPopupContextItem())
			{
				if (Gui.Selectable("Copy Component")) 
				{
					try
					{
						var clonedComponent = _component.Clone();
						_imGuiManager.SceneGraphWindow.CopiedComponent = clonedComponent;
					}
					catch (Exception ex)
					{
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
								var componentType = _component.GetType();
								var clonedComponent = (Component)Activator.CreateInstance(componentType);
								clonedComponent.Name = _component.Name;
								clonedComponent.Enabled = _component.Enabled;
								
								// Clone the data using JSON
								var json = Json.ToJson(sourceData, jsonSettings);
								var clonedData = (ComponentData)Json.FromJson(json, sourceData.GetType());
								clonedComponent.Data = clonedData;
								
								_imGuiManager.SceneGraphWindow.CopiedComponent = clonedComponent;
								Debug.Error($"Copied component via JSON fallback: {_component.GetType().Name}");
							}
						}
						catch (Exception jsonEx)
						{ 
							Debug.Error($"Failed to copy component {_component.GetType().Name}: {ex.Message}. JSON fallback also failed: {jsonEx.Message}");
						}
					}
				}

				VoltageEditorUtils.SmallVerticalSpace();

				//Paste - Simplified since we now have a true copy
				var copiedComponent = _imGuiManager.SceneGraphWindow.CopiedComponent;
				var canPaste = copiedComponent != null && copiedComponent.GetType() == _component.GetType();
				
				if (!canPaste)
				{
					ImGui.BeginDisabled();
				}

				var pasteText = canPaste ? "Paste Component Values" : 
									   (copiedComponent != null ? $"Can't paste {copiedComponent.GetType().Name} into {_component.GetType().Name}" : "No component copied");
				
				if (Gui.Selectable(pasteText) && canPaste)
				{
					PasteComponentValues(copiedComponent, _component);
				}

				if (!canPaste)
				{
					ImGui.EndDisabled();
				}

				ImGui.Separator();
				VoltageEditorUtils.SmallVerticalSpace();

				if (Gui.Selectable("Remove Component"))
				{
					_component.RemoveComponent();
				}

				Gui.EndPopup();
			}

			if (isHeaderOpen)
			{
				DrawEnabledCheckbox();

				VoltageEditorUtils.SmallVerticalSpace();

				// Draw regular inspectors
				for (var i = _regularInspectors.Count - 1; i >= 0; i--)
				{
					if (_regularInspectors[i].IsTargetDestroyed)
					{
						_regularInspectors.RemoveAt(i);
						continue;
					}

					_regularInspectors[i].Draw();
				}

				// Draw read-only structs section if there are any
				if (_readOnlyStructInspectors.Count > 0)
				{
					VoltageEditorUtils.SmallVerticalSpace();
					
					ImGui.PushStyleColor(ImGuiCol.Header, new Num.Vector4(0.3f, 0.3f, 0.4f, 0.6f));
					ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Num.Vector4(0.35f, 0.35f, 0.45f, 0.7f));
					ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Num.Vector4(0.4f, 0.4f, 0.5f, 0.8f));
					
					// Collapsing header that starts closed by default
					_isReadOnlyStructsOpen = Gui.CollapsingHeader(
						"Read Only", 
						_isReadOnlyStructsOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None
					);
					
					ImGui.PopStyleColor(3);

					if (_isReadOnlyStructsOpen)
					{
						// Apply dimmed appearance to read-only section
						ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * 0.7f);
						
						for (var i = _readOnlyStructInspectors.Count - 1; i >= 0; i--)
						{
							if (_readOnlyStructInspectors[i].IsTargetDestroyed)
							{
								_readOnlyStructInspectors.RemoveAt(i);
								continue;
							}

							_readOnlyStructInspectors[i].Draw();
						}
						
						ImGui.PopStyleVar();
					}
				}
				
				foreach (var action in _componentDelegateMethods)
					action();
			}

			ImGui.PopID();
		}

		/// <summary>
		/// Draws the component Enabled checkbox. If the user tries to enable a component
		/// on a disabled Entity, shows a notification and blocks the change.
		/// </summary>
		private void DrawEnabledCheckbox()
		{
			bool oldEnabled = _component.Enabled;
			bool enabled = oldEnabled;

			if (Gui.Checkbox("Enabled", ref enabled) && enabled != oldEnabled)
			{
				// Block enabling a component on a disabled entity
				if (enabled && _component.Entity != null && !_component.Entity.Enabled)
				{
					NotificationSystem.ShowTimedNotification(
						$"Can't enable '{_component.Name}' — the Entity '{_component.Entity.Name}' is disabled!"
					);
					return;
				}

				EditorChangeTracker.PushUndo(
					new GenericValueChangeAction(
						_component.Entity,
						(obj, val) => _component.SetEnabled((bool)val),
						oldEnabled,
						enabled,
						$"{_component}.Enabled"
					),
					_component.Entity,
					$"{_component}.Enabled"
				);

				_component.SetEnabled(enabled);
			}
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
				Debug.Error($"Cannot paste {sourceComponent.GetType().Name} into {targetComponent.GetType().Name} - types must match");
				return;
			}

			try
			{
				var oldData = targetComponent.Data;// Store old data for undo
				var sourceData = sourceComponent.Data;

				if (sourceData == null)
				{
					Debug.Error("Source component has no data to copy");
					return;
				}

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
					Debug.Error($"Failed to clone component data via JSON: {ex.Message}");
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

				targetComponent.Data = clonedData;
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to paste component values: {ex.Message}");
			}
		}
	}
}