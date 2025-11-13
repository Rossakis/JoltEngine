using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using ImGuiNET;
using Nez.ImGuiTools.ObjectInspectors;
using Nez.ImGuiTools.UndoActions;
using Nez.Utils;
using Nez.Editor;
using Nez.Persistence;
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

	// Prefab creation popup fields
	private string _prefabName = "";

	// Prefab apply confirmation popup fields
	private bool _showApplyToPrefabCopiesConfirmation = false;
	private List<Entity> _prefabCopiesToModify = new();

	// Add these fields for "Apply to Original Prefab" confirmation
	private bool _showApplyToOriginalPrefabConfirmation = false;

	public EntityInspector(Entity entity, int NormalInspector_PosOffset = 0)
	{
		Entity = entity;
		_normalEntityInspector_PosOffset = NormalInspector_PosOffset;

		ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = false;

		_transformInspector = new TransformInspector(Entity.Transform);
		RefreshComponentInspectors();
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

			// Draw main entity UI
			var type = Entity.Type.ToString();
			ImGui.InputText("InstanceType", ref type, 30, ImGuiInputTextFlags.ReadOnly);

			// Show OriginalPrefabName for Prefab entities (readonly)
			if (Entity.Type == Entity.InstanceType.Prefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
			{
				var originalPrefabName = Entity.OriginalPrefabName;
				ImGui.InputText("Original Prefab Name", ref originalPrefabName, 50, ImGuiInputTextFlags.ReadOnly);
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
				Entity.SetEnabled(enabled);
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
					Entity.Name = name; // This will automatically ensure uniqueness

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

			// UpdateInterval (edit session)
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

			NezImGui.MediumVerticalSpace();

			// IsSelectableInEditor
			{
				bool oldSelectable = Entity.IsSelectableInEditor;
				bool isSelectable = oldSelectable;
				if (ImGui.Checkbox("Can Be Selected", ref isSelectable) && isSelectable != oldSelectable)
				{
					EditorChangeTracker.PushUndo(
						new GenericValueChangeAction(
							Entity,
							typeof(Entity).GetProperty(nameof(Entity.IsSelectableInEditor)),
							oldSelectable,
							isSelectable,
							$"{Entity.Name}.IsSelectableInEditor"
						),
						Entity,
						$"{Entity.Name}.IsSelectableInEditor"
					);
					Entity.IsSelectableInEditor = isSelectable;
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("If FALSE, you won't be able select this \n Entity with your cursor in the Editor.");
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

			// Create Prefab button
			if (Entity.Type != Entity.InstanceType.HardCoded && NezImGui.CenteredButton("Create Prefab", 0.6f))
			{
				_prefabName = Entity.Name + "_Prefab";
				ImGui.OpenPopup("prefab-creator");
			}

			// Apply to Prefab Copies button for prefab entities
			if (Entity.Type == Entity.InstanceType.Prefab && !string.IsNullOrEmpty(Entity.OriginalPrefabName))
			{
				NezImGui.MediumVerticalSpace();
				if (NezImGui.CenteredButton("Apply to Prefab Copies", 0.8f))
				{
					ShowApplyToPrefabCopiesConfirmation();
				}
			}

			// Apply to Original Prefab button for prefab entities
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

			ImGui.End();
		}

		if (!open)
			_imGuiManager.CloseEntityInspector(this);
	}

	/// <summary>
	/// Draws the prefab creation popup with name input and create/cancel buttons.
	/// </summary>
	private void DrawPrefabCreatorPopup()
	{
		var center = new Num.Vector2(Screen.Width * 0.5f, Screen.Height * 0.4f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

		bool open = true;
		if (ImGui.BeginPopupModal("prefab-creator", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.Text("Create Prefab from Entity");
			ImGui.Separator();
			
			ImGui.Text("Prefab Name:");
			ImGui.InputText("##PrefabName", ref _prefabName, 50);

			var correctedName = CorrectPrefabName(_prefabName.Trim(), Entity.GetType().Name);
			bool prefabExists = CheckPrefabExists(correctedName);
			
			if (prefabExists)
			{
				ImGui.TextColored(new Num.Vector4(1.0f, 0.2f, 0.2f, 1.0f), $"Warning: Prefab '{correctedName}' already exists!");
			}

			NezImGui.MediumVerticalSpace();

			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
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
	private bool CheckPrefabExists(string prefabName)
	{
		var prefabFilePath = $"Content/Data/Prefabs/{prefabName}.json";
		return File.Exists(prefabFilePath);
	}

	/// <summary>
	/// Creates a prefab from the current entity.
	/// </summary>
	private async void CreatePrefabFromEntity(string prefabName)
	{
		if (Entity == null || _imGuiManager?.SceneGraphWindow?.EntityPane == null)
			return;

		var newPrefab = _imGuiManager.SceneGraphWindow.EntityPane.DuplicateEntity(Entity, prefabName);
		
		if (newPrefab != null)
		{
			bool saveSuccessful = await _imGuiManager.InvokePrefabCreated(newPrefab, false);
			
			if (saveSuccessful)
			{
				_imGuiManager.SceneGraphWindow.AddPrefabToCache(newPrefab.Name);
				NotificationSystem.ShowTimedNotification($"Successfully created and saved prefab: {newPrefab.Name}");
			}
			else
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
	/// Corrects the prefab name to follow the convention.
	/// </summary>
	private string CorrectPrefabName(string inputName, string entityTypeName)
	{
		if (string.IsNullOrWhiteSpace(inputName))
			return $"{entityTypeName}_Prefab";

		var separators = new char[] { '_', '-', '&', '#', '@'};
		var expectedPrefix = entityTypeName;
		
		if (inputName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
		{
			if (inputName.Length > expectedPrefix.Length)
			{
				var nextChar = inputName[expectedPrefix.Length];
				if (separators.Contains(nextChar))
				{
					return inputName;
				}
			}
		}

		var cleanedName = inputName.TrimStart(separators);
		
		if (string.IsNullOrWhiteSpace(cleanedName))
			cleanedName = "Prefab";

		return $"{entityTypeName}_{cleanedName}";
	}

	/// <summary>
	/// Shows the confirmation popup for applying prefab changes to copies.
	/// </summary>
	private void ShowApplyToPrefabCopiesConfirmation()
	{
		if (Entity == null || Entity.Type != Entity.InstanceType.Prefab || string.IsNullOrEmpty(Entity.OriginalPrefabName))
			return;

		_prefabCopiesToModify = Core.Scene.Entities
			.Where(e => e != Entity &&
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
			_showApplyToPrefabCopiesConfirmation = false;
		}

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
			
			ImGui.TextColored(new Num.Vector4(1.0f, 0.8f, 0.2f, 1.0f), $"Prefab: {Entity.OriginalPrefabName}");
			ImGui.TextColored(new Num.Vector4(0.8f, 0.8f, 0.8f, 1.0f), $"Entities to be modified ({_prefabCopiesToModify.Count}):");

			if (ImGui.BeginChild("EntityList", new Num.Vector2(0, Math.Min(200, _prefabCopiesToModify.Count * 25 + 20)), true))
			{
				ImGui.Dummy(new Num.Vector2(0, 5));
				foreach (var prefabCopy in _prefabCopiesToModify)
				{
					ImGui.BulletText($"{prefabCopy.Name}");
					ImGui.Dummy(new Num.Vector2(0, 2));
				}
				ImGui.Dummy(new Num.Vector2(0, 5));
			}
			ImGui.EndChild();

			NezImGui.MediumVerticalSpace();
			
			ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "This action can be undone with Ctrl+Z");

			NezImGui.MediumVerticalSpace();

			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = (buttonWidth * 2) + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			if (ImGui.Button("OK", new Num.Vector2(buttonWidth, 0)))
			{
				ApplyToPrefabCopies();
				ImGui.CloseCurrentPopup();
			}
			
			ImGui.SameLine();
			
			if (ImGui.Button("Cancel", new Num.Vector2(buttonWidth, 0)))
			{
				_prefabCopiesToModify.Clear();
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}
	}

	/// <summary>
	/// Applies the current prefab entity's component data to all other entities in the scene.
	/// </summary>
	private void ApplyToPrefabCopies()
	{
		if (Entity == null || Entity.Type != Entity.InstanceType.Prefab || string.IsNullOrEmpty(Entity.OriginalPrefabName))
			return;

		var prefabCopies = _prefabCopiesToModify;
		
		if (prefabCopies.Count == 0)
		{
			NotificationSystem.ShowTimedNotification($"No other copies of prefab '{Entity.OriginalPrefabName}' found in scene.");
			return;
		}

		var undoActions = new List<EditorChangeTracker.IEditorAction>();

		foreach (var targetEntity in prefabCopies)
		{
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

			bool hasChanges = false;
			foreach (var sourceComponent in Entity.Components)
			{
				if (sourceComponent.Data == null)
					continue;

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
						
						var json = Json.ToJson(sourceComponent.Data, jsonSettings);
						var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
						
						targetComponent.Data = clonedData;
						hasChanges = true;
					}
					catch (Exception ex)
					{
						System.Console.WriteLine($"Failed to copy component data: {sourceComponent.Name} - {ex.Message}");
					}
				}
			}

			if (hasChanges)
			{
				undoActions.Add(new PrefabCopyUndoAction(targetEntity, oldComponentData, Entity.OriginalPrefabName));
			}
		}

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
	/// Draws the confirmation popup for applying changes to the original prefab.
	/// </summary>
	private void DrawApplyToOriginalPrefabConfirmationPopup()
	{
		if (_showApplyToOriginalPrefabConfirmation)
		{
			ImGui.OpenPopup("apply-to-original-prefab-confirmation");
			_showApplyToOriginalPrefabConfirmation = false;
		}

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

	/// <summary>
	/// Undo-enabled ApplyToOriginalPrefab
	/// </summary>
	private async void ApplyToOriginalPrefabWithUndo()
	{
		if (Entity != null && Entity.Type == Entity.InstanceType.Prefab &&
		    !string.IsNullOrEmpty(Entity.OriginalPrefabName))
		{
			bool saveSuccessful = await Core.GetGlobalManager<ImGuiManager>().InvokePrefabCreated(Entity, true);

			if (saveSuccessful)
			{
				NotificationSystem.ShowTimedNotification(
					$"Applied changes to original prefab: {Entity.OriginalPrefabName}");
			}
			else
			{
				NotificationSystem.ShowTimedNotification(
					$"Failed to apply changes to prefab: {Entity.OriginalPrefabName}");
			}
		}
	}

	public void SetWindowFocus()
	{
		_shouldFocusWindow = true;
	}
}