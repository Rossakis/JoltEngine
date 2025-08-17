using ImGuiNET;
using Nez.ImGuiTools.ObjectInspectors;
using Nez.ImGuiTools.UndoActions;
using Nez.Utils;
using Nez.Utils.Coroutines;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Nez.Editor;
using Nez.ImGuiTools.Inspectors.ObjectInspectors;
using Nez.ImGuiTools.TypeInspectors;
using Nez.Persistence;
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
	public static float MainInspectorPosY => 20f * ImGui.GetIO().FontGlobalScale;
	public static float MainInspectorOffsetY => 24 * ImGui.GetIO().FontGlobalScale;
	private readonly string _windowId = "MAIN_INSPECTOR_WINDOW";
	private TransformInspector _transformInspector;
	private List<IComponentInspector> _componentInspectors = new();

	private bool _isEditingUpdateInterval = false;
	private uint _updateIntervalEditStartValue;
	private bool _isEditingName = false;
	private string _nameEditStartValue;

	// Prefab creation popup fields
	private string _prefabName = "";
	private ImGuiManager _imguiManager;

	// Prefab apply confirmation popup fields
	private bool _showApplyToPrefabCopiesConfirmation = false;
	private List<Entity> _prefabCopiesToModify = new();

	private ImGuiManager _imGuiManager;

	// Add these fields to MainEntityInspector
	private bool _showApplyToOriginalPrefabConfirmation = false;

	// New fields for multi-entity selection
	private List<Entity> _selectedEntities = new();

	public MainEntityInspector(Entity entity = null)
	{
		Entity = entity;
		_componentInspectors.Clear();

		// Get selected entities from ImGuiManager
		var imGuiManager = Core.GetGlobalManager<ImGuiManager>();
		if (imGuiManager != null)
			_selectedEntities = imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.ToList();
		else
			_selectedEntities = entity != null ? new List<Entity> { entity } : new List<Entity>();

		if (_selectedEntities.Count == 1 && Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			for (var i = 0; i < Entity.Components.Count; i++)
				_componentInspectors.Add(ComponentInspectorFactory.CreateInspector(Entity.Components[i])); // Use factory here
		}
		else if (_selectedEntities.Count > 1)
		{
			// For multiple selection, find common components
			var commonComponents = GetCommonComponents(_selectedEntities);
			_componentInspectors.Clear();
			foreach (var compType in commonComponents)
			{
				// Create a MultiComponentInspector for each common type
				var multiInspector = new MultiComponentInspector(compType, _selectedEntities);
				_componentInspectors.Add(multiInspector);
			}
		}
	}

	/// <summary>
	/// Finds the set of component types that are common to all selected entities.
	/// </summary>
	private List<Type> GetCommonComponents(List<Entity> entities)
	{
		if (entities == null || entities.Count == 0)
			return new List<Type>();

		var firstEntity = entities[0];
		var commonTypes = firstEntity.Components.Select(c => c.GetType().FullName).ToHashSet();

		foreach (var entity in entities.Skip(1))
		{
			var types = entity.Components.Select(c => c.GetType().FullName).ToHashSet();
			commonTypes.IntersectWith(types);
		}

		// Convert back to Type
		return firstEntity.Components
			.Where(c => commonTypes.Contains(c.GetType().FullName))
			.Select(c => c.GetType())
			.Distinct()
			.ToList();
	}

	/// <summary>
	/// Refreshes all component inspectors. Call this after components are added, removed, or replaced.
	/// </summary>
	public void RefreshComponentInspectors()
	{
		if (Entity == null)
			return;
			
		_componentInspectors.Clear();
		
		// Recreate all component inspectors
		for (var i = 0; i < Entity.Components.Count; i++)
		{
			_componentInspectors.Add(ComponentInspectorFactory.CreateInspector(Entity.Components[i]));
		}
	}

	public void SetEntity(Entity entity)
	{
		Entity = entity;
		var imGuiManager = Core.GetGlobalManager<ImGuiManager>();
		_selectedEntities = imGuiManager?.SceneGraphWindow.EntityPane.SelectedEntities.ToList() ?? (entity != null ? new List<Entity> { entity } : new List<Entity>());
		_componentInspectors.Clear();
		_transformInspector = null;

		if (_selectedEntities.Count == 1 && Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			RefreshComponentInspectors();
		}
		else if (_selectedEntities.Count > 1)
		{
			var commonComponents = GetCommonComponents(_selectedEntities);
			foreach (var compType in commonComponents)
			{
				var multiInspector = new MultiComponentInspector(compType, _selectedEntities);
				_componentInspectors.Add(multiInspector);
			}
		}
	}

	public void Draw(ImGuiWindowFlags windowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove)
	{
		if (!IsOpen)
			return;

		if (_imguiManager == null)
			_imguiManager = Core.GetGlobalManager<ImGuiManager>();

		_selectedEntities = _imguiManager.SceneGraphWindow.EntityPane.SelectedEntities.ToList();

		var windowPosX = Screen.Width - _mainInspectorWidth;
		var windowPosY = MainEntityInspector.MainInspectorPosY;
		var windowWidth = _mainInspectorWidth;
		var windowHeight = Screen.Height - MainInspectorPosY;

		ImGui.SetNextWindowPos(new Num.Vector2(windowPosX, windowPosY), ImGuiCond.Once);
		ImGui.SetNextWindowSize(new Num.Vector2(windowWidth, windowHeight), ImGuiCond.Once);

		var open = IsOpen;

		if (ImGui.Begin("##MainEntityInspector", ref open, windowFlags))
		{
			// 1) Show "Multiple Entities Selected" if more than one entity is selected
			if (_selectedEntities.Count > 1)
			{
				ImGui.SetWindowFontScale(1.5f);
				ImGui.Text("Multiple Entities Selected");
				ImGui.SetWindowFontScale(1.0f);
				NezImGui.BigVerticalSpace();

				// 2) Show common components
				foreach (var inspector in _componentInspectors)
				{
					inspector.Draw();
					NezImGui.MediumVerticalSpace();
				}
			}
			else if (_selectedEntities.Count == 1 && Entity != null)
			{
				// Original single-entity logic
				var entityName = Entity.Name;
				ImGui.SetWindowFontScale(1.5f);
				ImGui.Text(entityName);
				ImGui.SetWindowFontScale(1.0f);
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

					// Show OriginalPrefabName for Prefab entities (readonly)
					if (Entity.Type == Entity.InstanceType.Prefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
					{
						var originalPrefabName = Entity.OriginalPrefabName;
						ImGui.InputText("Original Prefab Name", ref originalPrefabName, 50, ImGuiInputTextFlags.ReadOnly);
					}

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

						if (_isEditingName && ImGui.IsItemDeactivatedAfterEdit())
						{
							_isEditingName = false;
							Entity.Name = name; 
							
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

					if(_transformInspector != null)
					{
						_transformInspector.Draw();
					}
					
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

					if (Entity.Type != Entity.InstanceType.HardCoded && NezImGui.CenteredButton("Create Prefab", 0.6f))
					{
						_prefabName = Entity.Name + "_Prefab";
						ImGui.OpenPopup("prefab-creator");
					}

					// Add "Apply to Prefab Copies" button for prefab entities
					if (Entity.Type == Entity.InstanceType.Prefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
					{
						NezImGui.MediumVerticalSpace();
						if (NezImGui.CenteredButton("Apply to Prefab Copies", 0.8f))
						{
							// Find prefab copies and show confirmation
							ShowApplyToPrefabCopiesConfirmation();
						}
					}
					
					if (Entity.Type == Entity.InstanceType.Prefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
					{
						NezImGui.MediumVerticalSpace();
						if (NezImGui.CenteredButton("Apply to Original Prefab", 0.8f))
						{
							_showApplyToOriginalPrefabConfirmation = true;
						}
					}
					
					DrawPrefabCreatorPopup();
					DrawApplyToPrefabCopiesConfirmationPopup();
					DrawApplyToOriginalPrefabConfirmationPopup();
				}
			}
			else
			{
				ImGui.TextColored(new Num.Vector4(1, 1, 0, 1), "No entity selected.");
			}

		}

		ImGui.End();
		ImGui.PopStyleVar();
		ImGui.PopStyleColor();

		if (!open)
			Core.GetGlobalManager<ImGuiManager>().CloseMainEntityInspector();
	}

	/// <summary>
	/// Draws the prefab creation popup with name input and create/cancel buttons.
	/// </summary>
	private void DrawPrefabCreatorPopup()
	{
		// Center the popup when it first appears
		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

		bool open = true;
		if (ImGui.BeginPopupModal("prefab-creator", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.Text("Create Prefab from Entity");
			ImGui.Separator();
			
			ImGui.Text("Prefab Name:");
			ImGui.InputText("##PrefabName", ref _prefabName, 50);

			// Check if prefab name already exists and show warning
			var correctedName = CorrectPrefabName(_prefabName.Trim(), Entity.GetType().Name);
			bool prefabExists = CheckPrefabExists(correctedName);
			
			if (prefabExists)
			{
				ImGui.TextColored(new Num.Vector4(1.0f, 0.2f, 0.2f, 1.0f), $"Warning: Prefab '{correctedName}' already exists!");
			}

			NezImGui.MediumVerticalSpace();

			// Center the buttons
			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			// Disable create button if prefab exists
			if (prefabExists)
				ImGui.BeginDisabled();
			
			if (ImGui.Button("Create", new Num.Vector2(buttonWidth, 0)))
			{
				if (!string.IsNullOrWhiteSpace(_prefabName))
				{
					CreatePrefabFromEntity(correctedName);
					ImGui.CloseCurrentPopup();
				}
			}
			
			if (prefabExists)
				ImGui.EndDisabled();
			
			ImGui.SameLine();
			
			if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}
	}

	/// <summary>
	/// Checks if a prefab with the given name already exists.
	/// </summary>
	/// <param name="prefabName">Name of the prefab to check</param>
	/// <returns>True if prefab exists, false otherwise</returns>
	private bool CheckPrefabExists(string prefabName)
	{
		var prefabFilePath = $"Content/Data/Prefabs/{prefabName}.json";
		return File.Exists(prefabFilePath);
	}

	/// <summary>
	/// Creates a prefab from the current entity using the DuplicateEntity method from EntityPane.
	/// Handles async saving and notifications.
	/// </summary>
	private async void CreatePrefabFromEntity(string prefabName, bool canOverride = false)
	{
		if (Entity == null || _imguiManager?.SceneGraphWindow?.EntityPane == null)
			return;

		// Use the existing DuplicateEntity method from EntityPane
		var newPrefab = _imguiManager.SceneGraphWindow.EntityPane.DuplicateEntity(Entity, prefabName);
		
		if (newPrefab != null)
		{
			// Save the prefab using the async event system
			bool saveSuccessful = await _imguiManager.InvokePrefabCreated(newPrefab, false);
			
			if (saveSuccessful)
			{
				// Add the new prefab to the cache so it appears in the entity selector
				_imguiManager.SceneGraphWindow.AddPrefabToCache(newPrefab.Name);
				NotificationSystem.ShowTimedNotification($"Successfully created and saved prefab: {newPrefab.Name}");
			}
			else if(!canOverride)
			{
				NotificationSystem.ShowTimedNotification($"Failed to save prefab: {newPrefab.Name} - Prefab with this name already exists!");
			}
		}
		else
		{
			NotificationSystem.ShowTimedNotification($"Failed to create prefab: {prefabName}");
		}
	}

	/// <summary>
	/// Corrects the prefab name to follow the convention: EntityTypeName_PrefabName
	/// If the name doesn't start with the entity type followed by a separator, it adds the prefix.
	/// </summary>
	/// <param name="inputName">The user-provided prefab name</param>
	/// <param name="entityTypeName">The entity's type name</param>
	/// <returns>Corrected prefab name</returns>
	private string CorrectPrefabName(string inputName, string entityTypeName)
	{
		if (string.IsNullOrWhiteSpace(inputName))
			return $"{entityTypeName}_Prefab";

		// Check if the name already starts with the entity type followed by a separator
		var separators = new char[] { '_', '-', '&', '#', '@'};
		var expectedPrefix = entityTypeName;
		
		// Check if it starts with EntityTypeName followed by any separator
		if (inputName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
		{
			// Check if the character after the entity type name is a valid separator
			if (inputName.Length > expectedPrefix.Length)
			{
				var nextChar = inputName[expectedPrefix.Length];
				if (separators.Contains(nextChar))
				{
					// Name is already correctly formatted
					return inputName;
				}
			}
		}

		// Name doesn't follow the convention, so add the prefix
		// Remove any leading separators from the input name
		var cleanedName = inputName.TrimStart(separators);
		
		// If the cleaned name is empty or just whitespace, use default
		if (string.IsNullOrWhiteSpace(cleanedName))
			cleanedName = "Prefab";

		return $"{entityTypeName}_{cleanedName}";
	}

	/// <summary>
	/// Applies the current prefab entity's component data to all other entities in the scene 
	/// that have the same OriginalPrefabName.
	/// </summary>
	private void ApplyToPrefabCopies()
	{
		if (Entity == null || Entity.Type != Entity.InstanceType.Prefab || string.IsNullOrEmpty(Entity.OriginalPrefabName))
			return;

		// Use the pre-found list of prefab copies
		var prefabCopies = _prefabCopiesToModify;
		
		if (prefabCopies.Count == 0)
		{
			NotificationSystem.ShowTimedNotification($"No other copies of prefab '{Entity.OriginalPrefabName}' found in scene.");
			return;
		}

		// Create undo action to track all changes
		var undoActions = new List<EditorChangeTracker.IEditorAction>();

		foreach (var targetEntity in prefabCopies)
		{
			// Store the entity's old component data for undo
			var oldComponentData = new Dictionary<string, ComponentData>();
			foreach (var component in targetEntity.Components)
			{
				if (component.Data != null)
				{
					try
					{
						var jsonSettings = new JsonSettings
						{
							PrettyPrint = false,
							TypeNameHandling = TypeNameHandling.Auto,
							PreserveReferencesHandling = false
						};
						
						// Deep clone the old data for undo
						var json = Json.ToJson(component.Data, jsonSettings);
						var clonedOldData = (ComponentData)Json.FromJson(json, component.Data.GetType());
						oldComponentData[component.Name] = clonedOldData;
					}
					catch (Exception ex)
					{
						System.Console.WriteLine($"Failed to backup component data for undo: {component.Name} - {ex.Message}");
					}
				}
			}

			// Apply component data from source entity to target entity
			bool hasChanges = false;
			foreach (var sourceComponent in Entity.Components)
			{
				if (sourceComponent.Data == null)
					continue;

				// Find matching component in target entity
				var targetComponent = targetEntity.Components.FirstOrDefault(c => 
					c.GetType() == sourceComponent.GetType() && c.Name == sourceComponent.Name);

				if (targetComponent != null)
				{
					try
					{
						var jsonSettings = new JsonSettings
						{
							PrettyPrint = false,
							TypeNameHandling = TypeNameHandling.Auto,
							PreserveReferencesHandling = false
						};
						
						// Deep clone the source component data
						var json = Json.ToJson(sourceComponent.Data, jsonSettings);
						var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
						
						// Apply the cloned data to the target component
						targetComponent.Data = clonedData;
						hasChanges = true;
					}
					catch (Exception ex)
					{
						System.Console.WriteLine($"Failed to copy component data: {sourceComponent.Name} - {ex.Message}");
					}
				}
			}

			// Only create undo action if there were actual changes
			if (hasChanges)
			{
				undoActions.Add(new PrefabCopyUndoAction(targetEntity, oldComponentData, Entity.OriginalPrefabName));
			}
		}

		// Create a composite undo action that can undo all changes at once
		if (undoActions.Count > 0)
		{
			var compositeUndo = new CompositePrefabApplyUndoAction(undoActions, Entity.OriginalPrefabName);
			EditorChangeTracker.PushUndo(
				compositeUndo,
				Entity,
				$"Apply '{Entity.OriginalPrefabName}' to {undoActions.Count} copies"
			);

			NotificationSystem.ShowTimedNotification($"Applied prefab '{Entity.OriginalPrefabName}' to {undoActions.Count} copies.");
		}
		else
		{
			NotificationSystem.ShowTimedNotification($"No changes were applied - all copies are already up to date.");
		}

		_prefabCopiesToModify.Clear();
	}

	/// <summary>
	/// Used when we're dealing with a newly loaded entity that might not be ready to be set immediately.
	/// </summary>
	/// <param name="entity"></param>
	/// <param name="time"></param>
	public void DelayedSetEntity(Entity entity, float time = 0.05f)
	{
		Core.StartCoroutine(ShowInspector(entity, time));
	}

	private IEnumerator ShowInspector(Entity entity, float time)
	{
		yield return Coroutine.WaitForSeconds(time);
		SetEntity(entity);
	}

	/// <summary>
	/// Shows the confirmation popup for applying prefab changes to copies.
	/// </summary>
	private void ShowApplyToPrefabCopiesConfirmation()
	{
		if (Entity == null || Entity.Type != Entity.InstanceType.Prefab || string.IsNullOrEmpty(Entity.OriginalPrefabName))
			return;

		// Find all entities in the scene that share the same OriginalPrefabName
		_prefabCopiesToModify = Core.Scene.Entities
			.Where(e => e != Entity && // Don't include the source entity
						e.Type == Entity.InstanceType.Prefab && 
						e.OriginalPrefabName == Entity.OriginalPrefabName)
			.ToList();

		if (_prefabCopiesToModify.Count == 0)
		{
			NotificationSystem.ShowTimedNotification($"No other copies of prefab '{Entity.OriginalPrefabName}' found in scene.");
			return;
		}

		_showApplyToPrefabCopiesConfirmation = true;
	}

	/// <summary>
	/// Draws the apply to prefab copies confirmation popup.
	/// </summary>
	private void DrawApplyToPrefabCopiesConfirmationPopup()
	{
		if (_showApplyToPrefabCopiesConfirmation)
		{
			ImGui.OpenPopup("apply-to-prefab-copies-confirmation");
			_showApplyToPrefabCopiesConfirmation = false; // Only open once
		}

		// Center the popup when it first appears
		var center = new Num.Vector2(Screen.Width * 0.45f, Screen.Height * 0.45f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(450, 0), ImGuiCond.Appearing);

		bool open = true;
		if (ImGui.BeginPopupModal("apply-to-prefab-copies-confirmation", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
		{
			ImGui.Text("Apply Changes to Prefab Copies");
			ImGui.Separator();
			
			ImGui.TextWrapped($"You are going to change prefab values for these entities:");
			
			NezImGui.SmallVerticalSpace();
			
			// Show the list of entities that will be affected
			ImGui.TextColored(new Num.Vector4(1.0f, 0.8f, 0.2f, 1.0f), $"Prefab: {Entity.OriginalPrefabName}");
			ImGui.TextColored(new Num.Vector4(0.8f, 0.8f, 0.8f, 1.0f), $"Entities to be modified ({_prefabCopiesToModify.Count}):");

			// Create a scrollable region for the entity list
			if (ImGui.BeginChild("EntityList", new Num.Vector2(0, Math.Min(200, _prefabCopiesToModify.Count * 80 + 20)), true))
			{
				foreach (var prefabCopy in _prefabCopiesToModify)
				{
					ImGui.BulletText($"{prefabCopy.Name}");
					ImGui.Dummy(new Num.Vector2(0, 2)); // spacing between items
				}
			}
			ImGui.EndChild();

			NezImGui.MediumVerticalSpace();
			
			ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "This action can be undone with Ctrl+Z");

			NezImGui.MediumVerticalSpace();

			// Center the buttons
			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			if (ImGui.Button("OK", new Num.Vector2(buttonWidth, 0)))
			{
				// Proceed with applying changes
				ApplyToPrefabCopies();
				ImGui.CloseCurrentPopup();
			}
			
			ImGui.SameLine();
			
			if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
			{
				// Clear the list and close popup
				_prefabCopiesToModify.Clear();
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}
	}

	/// <summary>
	/// Draws the confirmation popup for applying changes to the original prefab.
	/// </summary>
	private void DrawApplyToOriginalPrefabConfirmationPopup()
	{
		if (_showApplyToOriginalPrefabConfirmation)
		{
			ImGui.OpenPopup("apply-to-original-prefab-confirmation");
			_showApplyToOriginalPrefabConfirmation = false; // Only open once
		}

		// Center the popup when it first appears
		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.5f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));
		ImGui.SetNextWindowSize(new Num.Vector2(400, 0), ImGuiCond.Appearing);

		bool open = true;
		if (ImGui.BeginPopupModal("apply-to-original-prefab-confirmation", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
		{
			ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), $"Are you sure you want to override the data of '{Entity.OriginalPrefabName}'?");
			ImGui.Separator();
			ImGui.TextWrapped("This action will overwrite the original prefab file and cannot be undone outside of this session.");

			NezImGui.MediumVerticalSpace();

			// Center the buttons
			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);

			if (ImGui.Button("Yes", new Num.Vector2(buttonWidth, 0)))
			{
				ApplyToOriginalPrefabWithUndo();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();

			if (ImGui.Button("No", new Num.Vector2(buttonWidth, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}
	}

	// Undo-enabled ApplyToOriginalPrefab
	private async void ApplyToOriginalPrefabWithUndo()
	{
		// Save the current entity's data to its original prefab file
		if (Entity != null && Entity.Type == Entity.InstanceType.Prefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
		{
			// Save the prefab using the async event system
			bool saveSuccessful = await Core.GetGlobalManager<ImGuiManager>().InvokePrefabCreated(Entity, true);

			if (saveSuccessful)
			{
				NotificationSystem.ShowTimedNotification($"Applied changes to original prefab: {Entity.OriginalPrefabName}");
			}
			else
			{
				NotificationSystem.ShowTimedNotification($"Failed to apply changes to prefab: {Entity.OriginalPrefabName}");
			}
		}
	}
}