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

	private bool _isEditingUpdateInterval = false;
	private uint _updateIntervalEditStartValue;
	private bool _isEditingName = false;
	private string _nameEditStartValue;

	// Prefab creation popup fields
	private string _prefabName = "";
	private ImGuiManager _imguiManager;

	public MainEntityInspector(Entity entity = null)
	{
		Entity = entity;
		_componentInspectors.Clear();

		if (Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			for (var i = 0; i < entity.Components.Count; i++)
				_componentInspectors.Add(ComponentInspectorFactory.CreateInspector(entity.Components[i])); // Use factory here
		}
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
		_componentInspectors.Clear();
		_transformInspector = null;
		if (Entity != null)
		{
			_transformInspector = new TransformInspector(Entity.Transform);
			RefreshComponentInspectors(); // Use the new method
		}
	}

	public void Draw()
	{
		if (!IsOpen)
			return;

		if(_imguiManager == null)
			_imguiManager = Core.GetGlobalManager<ImGuiManager>();

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

				if (NezImGui.CenteredButton("Create Prefab", 0.6f))
				{
					_prefabName = Entity.Name + "_Prefab";
					ImGui.OpenPopup("prefab-creator");
				}
				
				DrawPrefabCreatorPopup();
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
	private async void CreatePrefabFromEntity(string prefabName)
	{
		if (Entity == null || _imguiManager?.SceneGraphWindow?.EntityPane == null)
			return;

		// Use the existing DuplicateEntity method from EntityPane
		var newPrefab = _imguiManager.SceneGraphWindow.EntityPane.DuplicateEntity(Entity, prefabName, Entity.InstanceType.Prefab);
		
		if (newPrefab != null)
		{
			// Save the prefab using the async event system
			bool saveSuccessful = await _imguiManager.InvokePrefabCreated(newPrefab);
			
			if (saveSuccessful)
			{
				// Add the new prefab to the cache so it appears in the entity selector
				_imguiManager.SceneGraphWindow.AddPrefabToCache(newPrefab.Name);
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
}