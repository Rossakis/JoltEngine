using ImGuiNET;
using Microsoft.Xna.Framework.Input;
using Nez.ECS;
using Nez.Editor;
using Nez.ImGuiTools.SceneGraphPanes;
using Nez.ImGuiTools.UndoActions;
using Nez.ImGuiTools.Utils;
using Nez.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Num = System.Numerics;

namespace Nez.ImGuiTools;

public class SceneGraphWindow
{
	/// <summary>
	/// A copy of a component that can be pasted to another entity
	/// </summary>
	public Component CopiedComponent { get; set; }
	public EntityPane EntityPane => _entityPane;
	private PostProcessorsPane _postProcessorsPane = new();
	private RenderersPane _renderersPane = new();
	private EntityPane _entityPane = new();
	private ImGuiManager _imGuiManager;
	public float SceneGraphWidth => _sceneGraphWidth;
	public float SceneGraphPosY { get; set; }
	public bool IsOpen { get; private set; }

	private string _entityFilterName;

	private float _sceneGraphWidth = 420f;
	private readonly float _minSceneGraphWidth = 1f;
	private readonly float _maxSceneGraphWidth = Screen.MonitorWidth;

	// Key Hold duration params
	private float _upKeyHoldTime = 0f;
	private float _downKeyHoldTime = 0f;
	private double _lastRepeatTime = 0f;
	private const float RepeatDelay = 0.3f; // seconds before repeat starts
	private const float RepeatRate = 0.08f; // seconds between repeats
	public HashSet<Entity> ExpandedEntities = new();

	// TiledMap (tmx) File Picker\
	public bool ShowTmxFilePicker { get => _showTmxFilePicker; set => _showTmxFilePicker = value; }
	private bool _showTmxFilePicker = false;
	public static event Action<string> OnTmxFileSelected;

	// Prefab caching
	private List<string> _cachedPrefabNames = new();
	private bool _prefabCacheInitialized = false;

	// Prefab deletion
	private bool _showDeletePrefabConfirmation = false;
	private string _prefabToDelete = "";
	
	//Aseprite
	public AsepriteFilePicker AsepriteFilePicker;
	public static event Action<AsepriteFilePicker.AsepriteSelection> OnAsepriteImageSelected;

	public void OnSceneChanged()
	{
		_postProcessorsPane.OnSceneChanged();
		_renderersPane.OnSceneChanged();
		
		AsepriteFilePicker = new AsepriteFilePicker(
			this,
			"aseprite-image-loader",
			System.IO.Path.Combine(Environment.CurrentDirectory, "Content"), 
			false
		);
	}

	/// <summary>
	/// Refreshes the prefab cache by scanning the prefabs directory.
	/// Called on first use and when new prefabs are created.
	/// </summary>
	public void RefreshPrefabCache()
	{
		if(_prefabCacheInitialized)
			return;

		_cachedPrefabNames.Clear();
		
		var prefabsDirectory = "Content/Data/Prefabs";
		if (Directory.Exists(prefabsDirectory))
		{
			var prefabFiles = Directory.GetFiles(prefabsDirectory, "*.json");
			foreach (var file in prefabFiles)
			{
				var fileName = Path.GetFileNameWithoutExtension(file);
				_cachedPrefabNames.Add(fileName);
			}
		}
		
		_prefabCacheInitialized = true;
	}

	/// <summary>
	/// Adds a new prefab name to the cache without rescanning the directory.
	/// Called when a new prefab is successfully created.
	/// </summary>
	public void AddPrefabToCache(string prefabName)
	{
		if (!_cachedPrefabNames.Contains(prefabName))
		{
			_cachedPrefabNames.Add(prefabName);
		}
	}

	public void Show(ref bool isOpen)
	{
		IsOpen = isOpen;

		if (Core.Scene == null || !isOpen)
			return;

		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		// var topMargin = 20f * ImGui.GetIO().FontGlobalScale;
		var rightMargin = 10f;
		var leftMargin = 0f;
		var windowHeight = Screen.Height - SceneGraphPosY;
		SceneGraphPosY = _imGuiManager.MainWindowPositionY;

		// Calculate left edge so right edge is always at Screen.Width - rightMargin

		ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 0.0f); // makes grip almost invisible
		ImGui.PushStyleColor(ImGuiCol.ResizeGrip, new Num.Vector4(0, 0, 0, 0)); // transparent grip

		ImGui.SetNextWindowPos(new Num.Vector2(0, SceneGraphPosY), ImGuiCond.Always);
		ImGui.SetNextWindowSize(new Num.Vector2(_sceneGraphWidth, windowHeight), ImGuiCond.FirstUseEver);

		var windowFlags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;

		if (ImGui.Begin("Scene Graph", ref isOpen, windowFlags))
		{
			// Update width after user resizes
			var currentWidth = ImGui.GetWindowSize().X;
			if (Math.Abs(currentWidth - _sceneGraphWidth) > 0.01f)
				_sceneGraphWidth = Math.Clamp(currentWidth, _minSceneGraphWidth, _maxSceneGraphWidth);

			NezImGui.SmallVerticalSpace();
			if (Core.IsEditMode)
			{
				ImGui.TextWrapped("Press F1/F2 to switch to Play mode.");
				NezImGui.SmallVerticalSpace();

				if (NezImGui.CenteredButton("Edit Mode", 0.8f))
					Core.InvokeSwitchEditMode(false); 

				NezImGui.SmallVerticalSpace();
				if (NezImGui.CenteredButton("Reset Scene", 0.8f))
					Core.InvokeResetScene();
			}
			else
			{
				ImGui.TextWrapped("Press F1/F2 to switch to Edit mode.");
				NezImGui.SmallVerticalSpace();

				if (NezImGui.CenteredButton("Play Mode", 0.8f))
					Core.InvokeSwitchEditMode(true);
			}

			NezImGui.MediumVerticalSpace();
			if (ImGui.CollapsingHeader("Post Processors"))
				_postProcessorsPane.Draw();

			if (ImGui.CollapsingHeader("Renderers"))
				_renderersPane.Draw();

			if (ImGui.CollapsingHeader("Entities (double-click label to inspect)", ImGuiTreeNodeFlags.DefaultOpen))
				_entityPane.Draw();

			NezImGui.MediumVerticalSpace();
			if (NezImGui.CenteredButton("Save Scene", 0.7f))
				_imGuiManager.InvokeSaveSceneChanges();

			NezImGui.MediumVerticalSpace();
			if (NezImGui.CenteredButton("Add Entity", 0.6f))
			{
				_entityFilterName = "";
				ImGui.OpenPopup("entity-selector");
			}

			if (_showTmxFilePicker)
			{
				ImGui.OpenPopup("tmx-file-picker");
				_showTmxFilePicker = false;
			}

			if (AsepriteFilePicker.IsOpen)
			{
				ImGui.OpenPopup(AsepriteFilePicker.PopupId);
			}

			// Show Copied Component
			NezImGui.MediumVerticalSpace();
			if (CopiedComponent != null)
			{
				NezImGui.VeryBigVerticalSpace();
				ImGui.TextWrapped($"Component Copied: {CopiedComponent.GetType().Name}");

				NezImGui.SmallVerticalSpace();
				if (NezImGui.CenteredButton("Clear Copied Component", 0.8f))
					CopiedComponent = null;
			}

			DrawTmxFilePickerPopup();
			DrawEntitySelectorPopup();

			AsepriteFilePicker.AsepriteSelection asepriteSelection = AsepriteFilePicker.Draw();
			if (asepriteSelection != null)
			{
				OnAsepriteImageSelected?.Invoke(asepriteSelection);
			}

			ImGui.End();
			ImGui.PopStyleVar();
			ImGui.PopStyleColor();
		}

		// Draw delete confirmation popup outside of the main window
		// This ensures it appears on top of everything
		if (_showDeletePrefabConfirmation)
		{
			DrawDeletePrefabConfirmationPopup();
		}

		HandleEntitySelectionNavigation();

		if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
		    && !ImGui.IsAnyItemHovered()
		    && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
		{
			EntityPane.DeselectAllEntities();
		}
	}

	private void DrawEntitySelectorPopup()
	{
		if (ImGui.BeginPopup("entity-selector"))
		{
			ImGui.InputText("###EntityFilter", ref _entityFilterName, 25);
			ImGui.Separator();

			RefreshPrefabCache();

			// Draw Entity Factory Types
			ImGui.TextColored(new Num.Vector4(0.8f, 0.8f, 1.0f, 1.0f), "Entity Types:");
			foreach (var typeName in EntityFactoryRegistry.GetRegisteredTypes())
			{
				if (string.IsNullOrEmpty(_entityFilterName) ||
				    typeName.ToLower().Contains(_entityFilterName.ToLower()))
				{
					if (ImGui.Selectable(typeName))
					{
						CreateEntityFromFactory(typeName);
						ImGui.CloseCurrentPopup();
					}
				}
			}

			if (_cachedPrefabNames.Count > 0)
			{
				ImGui.Separator();
				ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "Prefabs:");
				
				foreach (var prefabName in _cachedPrefabNames)
				{
					if (string.IsNullOrEmpty(_entityFilterName) ||
					    prefabName.ToLower().Contains(_entityFilterName.ToLower()))
					{
						// Handle left-click to create prefab - FIXED VERSION
						if (ImGui.Selectable($"{prefabName} [Prefab]"))
						{
							CreateEntityFromPrefab(prefabName);
							ImGui.CloseCurrentPopup();
						}
						
						// Handle right-click for context menu
						if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
						{
							ImGui.OpenPopup($"prefab-context-{prefabName}");
						}
						
						// Draw context menu for this prefab
						if (ImGui.BeginPopup($"prefab-context-{prefabName}"))
						{
							if (ImGui.Selectable("Create Prefab Instance"))
							{
								CreateEntityFromPrefab(prefabName);
								ImGui.CloseCurrentPopup();
							}
							
							ImGui.Separator();
							
							if (ImGui.Selectable("Delete Prefab"))
							{
								_prefabToDelete = prefabName;
								_showDeletePrefabConfirmation = true;
								ImGui.CloseCurrentPopup();
							}
							
							ImGui.EndPopup();
						}
					}
				}
			}

			ImGui.EndPopup();
		}
		
		// Draw delete confirmation popup outside of the entity selector
		DrawDeletePrefabConfirmationPopup();
	}

	/// <summary>
	/// Draws the delete prefab confirmation popup.
	/// </summary>
	private void DrawDeletePrefabConfirmationPopup()
	{
		if (_showDeletePrefabConfirmation)
		{
			ImGui.OpenPopup("delete-prefab-confirmation");
			_showDeletePrefabConfirmation = false; // Only open once
		}

		// Center the popup when it first appears
		var center = new Num.Vector2(Screen.Width * 0.45f, Screen.Height * 0.7f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

		bool open = true;
		if (ImGui.BeginPopupModal("delete-prefab-confirmation", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.Text("Delete Prefab");
			ImGui.Separator();
			
			ImGui.TextWrapped($"Are you sure you want to delete the '{_prefabToDelete}' prefab completely?");
			ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "This action cannot be undone!");

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
				DeletePrefab(_prefabToDelete);
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
	/// Deletes a prefab file and updates the prefab cache.
	/// </summary>
	/// <param name="prefabName">Name of the prefab to delete</param>
	private void DeletePrefab(string prefabName)
	{
		try
		{
			// Construct paths to both the source and output prefab files
			var sourceFilePath = $"{Environment.CurrentDirectory}/Content/Data/Prefabs/{prefabName}.json";
			var outputFilePath = $"Content/Data/Prefabs/{prefabName}.json";

			bool fileDeleted = false;

			// Delete the source file if it exists
			if (File.Exists(sourceFilePath))
			{
				File.Delete(sourceFilePath);
				fileDeleted = true;
			}

			// Delete the output file if it exists
			if (File.Exists(outputFilePath))
			{
				File.Delete(outputFilePath);
				fileDeleted = true;
			}

			if (fileDeleted)
			{
				// Remove from cache
				_cachedPrefabNames.Remove(prefabName);
				
				NotificationSystem.ShowTimedNotification($"Successfully deleted prefab: {prefabName}");
			}
			else
			{
				NotificationSystem.ShowTimedNotification($"Prefab file not found: {prefabName}");
			}
		}
		catch (Exception ex)
		{
			NotificationSystem.ShowTimedNotification($"Failed to delete prefab {prefabName}: {ex.Message}");
		}
	}

	/// <summary>
	/// Removes a prefab name from the cache without rescanning the directory.
	/// Called when a prefab is successfully deleted.
	/// </summary>
	public void RemovePrefabFromCache(string prefabName)
	{
		_cachedPrefabNames.Remove(prefabName);
	}

	#region Menu toolbar buttons

	private void DrawTmxFilePickerPopup()
	{
		bool isOpen = true;
		if (ImGui.BeginPopupModal("tmx-file-picker", ref isOpen, ImGuiWindowFlags.AlwaysAutoResize))
		{
			var picker = FilePicker.GetFilePicker(this, Path.Combine(Environment.CurrentDirectory, "Content"), ".tmx");
			picker.DontAllowTraverselBeyondRootFolder = true;
			if (picker.Draw())
			{
				var file = picker.SelectedFile;
				if (file.EndsWith(".tmx"))
				{
					string fullPath = file;
					string contentRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Content"));

					if (fullPath.StartsWith(contentRoot, StringComparison.OrdinalIgnoreCase))
					{
						string relativePath = "Content" + fullPath.Substring(contentRoot.Length).Replace('\\', '/');
						OnTmxFileSelected?.Invoke(relativePath);

						ImGui.CloseCurrentPopup();
					}
					else
					{
						ImGui.Text("Selected file is not inside Content folder!");
					}
				}
				else
				{
					ImGui.Text("Selected file is not a valid TMX file.");
				}
				FilePicker.RemoveFilePicker(this);
			}
			ImGui.EndPopup();
		}
	}
	#endregion

	/// <summary>
	/// Creates a new entity from a prefab.
	/// </summary>
	private void CreateEntityFromPrefab(string prefabName)
	{
		try
		{
			// Use the event system to load prefab data from the DataLoader
			var prefabData = _imGuiManager.InvokePrefabLoadRequested(prefabName);

			if (prefabData.EntityData == null)
			{
				NotificationSystem.ShowTimedNotification($"Null Prefab EntityData: {prefabName}");
				return;
			}

			if (prefabData.EntityType == null)
			{
				NotificationSystem.ShowTimedNotification($"Invalid prefab data format for: {prefabName} - EntityType property not found");
				return;
			}

			if (EntityFactoryRegistry.TryCreate(prefabData.EntityType, out var entity))
			{
				EntityFactoryRegistry.InvokeEntityCreated(entity);

				// Set as dynamic instance (instantiated from prefab, but not a prefab itself)
				entity.Type = Entity.InstanceType.Prefab;
				entity.Transform.Position = Core.Scene.Camera.Transform.Position;

				// Load the prefab data into the entity using the event system
				_imGuiManager.InvokeLoadEntityData(entity, prefabData);

				entity.Name = Core.Scene.GetUniqueEntityName(prefabData.Name, entity);
				entity.OriginalPrefabName = prefabName; // Store the prefab name for later

				// Undo/Redo support for entity creation
				EditorChangeTracker.PushUndo(
					new EntityCreateDeleteUndoAction(Core.Scene, entity, wasCreated: true,
						$"Create Entity from Prefab {entity.Name}"),
					entity,
					$"Create Entity from Prefab {entity.Name}"
				);

				_imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(entity, false);
				_imGuiManager.MainEntityInspector.DelayedSetEntity(entity);

				NotificationSystem.ShowTimedNotification($"Created entity from prefab: {prefabName}");
			}
			else
			{
				NotificationSystem.ShowTimedNotification(
					$"Failed to create entity from prefab: {prefabName}. Entity type '{prefabData.EntityType}' not registered.");
			}

		}
		catch (Exception ex)
		{
			NotificationSystem.ShowTimedNotification($"Error creating entity from prefab {prefabName}: {ex.Message}");
		}
	}

	/// <summary>
	/// Creates a new entity from the EntityFactoryRegistry.
	/// </summary>
	private void CreateEntityFromFactory(string typeName)
	{
		if (EntityFactoryRegistry.TryCreate(typeName, out var entity))
		{
			EntityFactoryRegistry.InvokeEntityCreated(entity);
			entity.Type = Entity.InstanceType.Dynamic;
			entity.Name = Core.Scene.GetUniqueEntityName(typeName, entity); 
			entity.Transform.Position = Core.Scene.Camera.Transform.Position;

			// Undo/Redo support for entity creation
			EditorChangeTracker.PushUndo(
				new EntityCreateDeleteUndoAction(Core.Scene, entity, wasCreated: true,
					$"Create Entity {entity.Name}"),
				entity,
				$"Create Entity {entity.Name}"
			);

			// Optionally select and open inspector
			var imGuiManager = Core.GetGlobalManager<ImGuiManager>();
			if (imGuiManager != null)
			{
				imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(entity, false);
				_imGuiManager.MainEntityInspector.DelayedSetEntity(entity);
			}
		}
	}

	#region Entity Selection Navigation
	private void HandleEntitySelectionNavigation()
	{
		// Use the first selected entity for navigation
		var selectedEntity = _imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.FirstOrDefault();

		if (!Core.IsEditMode || selectedEntity == null || !ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow))
			return; 

		var hierarchyList = BuildHierarchyList();
		var currentEntity = _imGuiManager?.MainEntityInspector?.Entity;
		if (currentEntity == null || hierarchyList.Count == 0)
			return;

		// Up / Down key navigation logic
		bool upPressed = ImGui.IsKeyPressed(ImGuiKey.UpArrow);
		bool downPressed = ImGui.IsKeyPressed(ImGuiKey.DownArrow);
		bool upHeld = ImGui.IsKeyDown(ImGuiKey.UpArrow);
		bool downHeld = ImGui.IsKeyDown(ImGuiKey.DownArrow);

		double now = ImGui.GetTime();

		// Up key logic
		if (upPressed)
		{
			_upKeyHoldTime = (float)now;
			var next = NavigateUp(currentEntity, hierarchyList);
			if (next != null)
			{
				_imGuiManager.OpenMainEntityInspector(next);
				EntityPane.SetSelectedEntity(next, false);
				ExpandParentsAndChildren(next);
				_imGuiManager.CursorSelectionManager.SetCameraTargetPosition(next.Transform.Position);
			}
			_lastRepeatTime = now;
		}
		else if (upHeld)
		{
			if (now - _upKeyHoldTime > RepeatDelay && now - _lastRepeatTime > RepeatRate)
			{
				var next = NavigateUp(currentEntity, hierarchyList);
				if (next != null)
				{
					_imGuiManager.OpenMainEntityInspector(next);
					EntityPane.SetSelectedEntity(next, false);
					ExpandParentsAndChildren(next);
					_imGuiManager.CursorSelectionManager.SetCameraTargetPosition(next.Transform.Position);
				}
				_lastRepeatTime = now;
			}
		}
		else if (!upHeld)
		{
			_upKeyHoldTime = 0f;
		}

		// Down key logic
		if (downPressed)
		{
			_downKeyHoldTime = (float)now;
			var next = NavigateDown(currentEntity, hierarchyList);
			if (next != null)
			{
				_imGuiManager.OpenMainEntityInspector(next);
				EntityPane.SetSelectedEntity(next, false);
				ExpandParentsAndChildren(next);
				_imGuiManager.CursorSelectionManager.SetCameraTargetPosition(next.Transform.Position); 
			}
			_lastRepeatTime = now;
		}
		else if (downHeld)
		{
			if (now - _downKeyHoldTime > RepeatDelay && now - _lastRepeatTime > RepeatRate)
			{
				var next = NavigateDown(currentEntity, hierarchyList);
				if (next != null)
				{
					_imGuiManager.OpenMainEntityInspector(next);
					EntityPane.SetSelectedEntity(next, false);
					ExpandParentsAndChildren(next);
					_imGuiManager.CursorSelectionManager.SetCameraTargetPosition(next.Transform.Position);
				}
				_lastRepeatTime = now;
			}
		}
		else if (!downHeld)
		{
			_downKeyHoldTime = 0f;
		}
	}

	public List<Entity> BuildHierarchyList()
	{
		var result = new List<Entity>();
		var entities = Core.Scene?.Entities;
		if (entities == null) return result;

		for (int i = 0; i < entities.Count; i++)
		{
			var entity = entities[i];
			if (entity.Transform.Parent == null)
				AddEntityAndChildren(entity, result);
		}
		return result;
	}

	private void AddEntityAndChildren(Entity entity, List<Entity> result)
	{
		result.Add(entity);
		for (int i = 0; i < entity.Transform.ChildCount; i++)
		{
			AddEntityAndChildren(entity.Transform.GetChild(i).Entity, result);
		}
	}

	private Entity GetLastDescendant(Entity entity)
	{
		while (entity.Transform.ChildCount > 0)
			entity = entity.Transform.GetChild(entity.Transform.ChildCount - 1).Entity;
		return entity;
	}

	private Entity NavigateUp(Entity current, List<Entity> hierarchyList)
	{
		int idx = hierarchyList.IndexOf(current);
		if (idx <= 0)
			return null; // Already at top

		Entity prev = hierarchyList[idx - 1];

		// If current is the first child of its parent, and prev is that parent, just select the parent
		if (current.Transform.Parent != null && prev == current.Transform.Parent.Entity)
			return prev;

		// Otherwise, if prev has children, descend into its last descendant
		if (prev.Transform.ChildCount > 0)
			return GetLastDescendant(prev);

		return prev;
	}

	private Entity NavigateDown(Entity current, List<Entity> hierarchyList)
	{
		int idx = hierarchyList.IndexOf(current);
		if (idx < 0 || idx >= hierarchyList.Count - 1)
			return null; // Already at bottom
		return hierarchyList[idx + 1];
	}

	private void ExpandParentsAndChildren(Entity entity)
	{
		// Expand all parents up to the root
		var parent = entity.Transform.Parent;
		while (parent != null)
		{
			ExpandedEntities.Add(parent.Entity);
			parent = parent.Parent;
		}

		// Expand all children (non-recursive using a stack)
		var stack = new Stack<Entity>();
		stack.Push(entity);

		while (stack.Count > 0)
		{
			var current = stack.Pop();
			ExpandedEntities.Add(current);

			for (int i = 0; i < current.Transform.ChildCount; i++)
			{
				var child = current.Transform.GetChild(i).Entity;
				if (!ExpandedEntities.Contains(child))
					stack.Push(child);
			}
		}
	}

	#endregion
}