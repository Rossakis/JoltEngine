using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using Voltage.Editor.Hotkeys;
using Microsoft.Xna.Framework;
using Voltage.Editor.DebugUtils;
using Voltage.Utils;
using Voltage.Editor.ImGuiCore;
using Voltage.Editor.FilePickers;
using Voltage.Editor.Inspectors.SceneGraphPanes;
using Voltage.Editor.Undo;
using Voltage.Editor.Utils;
using Num = System.Numerics;
using Voltage.Editor.Interfaces;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.Serialization;
using Voltage.Editor.Undo.Core;
using Voltage.Editor.Undo.EntityActions;
using Voltage.Editor.Assets;
using Voltage.Serialization;
using PrefabData = Voltage.Data.PrefabData;

namespace Voltage.Editor.Windows;

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
	private SceneComponentsPane _sceneComponentsPane = new();
	private ImGuiManager _imGuiManager;
	public float SceneGraphWidth => _sceneGraphWidth;
	public float SceneGraphPosY { get; set; }
	public bool IsOpen { get; private set; }

	private float _sceneGraphWidth = 420f;
	private readonly float _minSceneGraphWidth = 1f;
	private readonly float _maxSceneGraphWidth = Screen.MonitorWidth;

	// Key Hold duration params
	private float _upKeyHoldTime = 0f;
	private float _downKeyHoldTime = 0f;
	private double _lastRepeatTime = 0f;
	private const float RepeatDelay = 0.3f;
	private const float RepeatRate = 0.08f;
	public HashSet<Entity> ExpandedEntities = new();

	// SerializedPrefab caching
	private List<string> _cachedPrefabNames = new();
	private bool _prefabCacheInitialized = false;

	// SerializedPrefab deletion
	private bool _showDeletePrefabConfirmation = false;
	private string _prefabToDelete = "";

	// File Pickers
	public TmxFilePicker TmxFilePicker;
	public AsepriteFilePicker AsepriteFilePicker;
	
	// Events
	public static event Action<TmxFilePicker.TmxSelection> OnTmxFileSelected;
	public static event Action<AsepriteFilePicker.AsepriteSelection> OnAsepriteImageSelected;

	/// <summary>
	/// Called by <see cref="ImGuiManager.OpenSceneComponentInspector"/> to bring the
	/// Scene Graph window to the foreground so the user can see the selected component.
	/// </summary>
	public void FocusSceneComponentInspector()
	{
		// The inspector is drawn inline; just set next-frame focus on the scene graph window.
		ImGui.SetWindowFocus("Scene Graph ###SceneGraphWindow");
	}

	public void OnSceneChanged()
	{
		_postProcessorsPane.OnSceneChanged();
		_renderersPane.OnSceneChanged();
		_sceneComponentsPane.OnSceneChanged();
		
		TmxFilePicker = new TmxFilePicker(
			this,
			"tmx-file-picker",
			Path.Combine(Environment.CurrentDirectory, "Content")
		);
		
		AsepriteFilePicker = new AsepriteFilePicker(
			this,
			"aseprite-image-loader",
			Path.Combine(Environment.CurrentDirectory, "Content"), 
			false
		);
	}

	/// <summary>
	/// Refreshes the prefab cache by scanning the prefabs directory and its subdirectories.
	/// Called on first use and when new prefabs are created.
	/// </summary>
	public void RefreshPrefabCache()
	{
		if(_prefabCacheInitialized)
			return;

		_cachedPrefabNames.Clear();
		
		var prefabsDirectory = ProjectManager.Instance.CurrentProject.PrefabsFolder;
		if (Directory.Exists(prefabsDirectory))
		{
			// Search through all EntityType subdirectories
			var entityTypeDirectories = Directory.GetDirectories(prefabsDirectory);
			
			foreach (var entityTypeDir in entityTypeDirectories)
			{
				// Native engine format is .vprefab; also scan .prefab for legacy files.
				var prefabFiles = Directory.GetFiles(entityTypeDir, "*.vprefab")
					.Concat(Directory.GetFiles(entityTypeDir, "*.prefab")).ToArray();
				foreach (var file in prefabFiles)
				{
					var fileName = Path.GetFileNameWithoutExtension(file);
					_cachedPrefabNames.Add(fileName);
				}
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

	public bool Show(bool isOpen)
	{
		IsOpen = isOpen;

		if (Core.Scene == null || !isOpen)
			return false;

		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 0.0f);
		ImGui.PushStyleColor(ImGuiCol.ResizeGrip, new Num.Vector4(0, 0, 0, 0));
		
		var windowFlags = ImGuiWindowFlags.None; 

		if (Gui.Begin("Scene Graph ###SceneGraphWindow", ref isOpen, windowFlags))
		{
			// Update width after user resizes
			var currentWidth = ImGui.GetWindowSize().X;
			if (Math.Abs(currentWidth - _sceneGraphWidth) > 0.01f)
				_sceneGraphWidth = Math.Clamp(currentWidth, _minSceneGraphWidth, _maxSceneGraphWidth);

			// Above the scrolling child, so it stays pinned no matter how far the panes below are scrolled.
			_entityPane.DrawSearchBar();
			ImGui.Separator();

			// Wrap all content in a child so BeginDragDropTarget() after EndChild() binds
			// to the child item — making the entire scene-graph area a valid drop zone.
			// Child is borderless/transparent so it is invisible to the user.
			var childSize = ImGui.GetContentRegionAvail();
			// border: false — child is invisible; the window provides the border.
			ImGui.BeginChild("##SceneGraphContent", childSize, false, ImGuiWindowFlags.None);

			// Prefab edit scene: a "Go Back" control at the very top returns to the previous game scene.
			if (_imGuiManager != null && _imGuiManager.IsInPrefabEditScene)
			{
				if (Gui.Button("< Go Back", new Num.Vector2(100, 0)))
					_imGuiManager.ExitPrefabEditScene();

				ImGui.SameLine();
				ImGuiSafe.TextColoredSafe(new Num.Vector4(0.8f, 0.6f, 1f, 1f),
					$"Editing Prefab: {_imGuiManager.PrefabEditName}");

				VoltageEditorUtils.SmallVerticalSpace();
				ImGui.Separator();
			}

			VoltageEditorUtils.MediumVerticalSpace();
			if (Gui.CollapsingHeader("Scene Components"))
				_sceneComponentsPane.Draw();

			if (Gui.CollapsingHeader("Post Processors"))
				_postProcessorsPane.Draw();

			if (Gui.CollapsingHeader("Renderers"))
				_renderersPane.Draw();

			if (Gui.CollapsingHeader("Entities (double-click label to inspect)", ImGuiTreeNodeFlags.DefaultOpen))
				_entityPane.Draw();

			VoltageEditorUtils.MediumVerticalSpace();
			if (_imGuiManager != null && _imGuiManager.IsInPrefabEditScene)
			{
				// A prefab edit scene works strictly on the prefab: save writes back to the .vprefab
				// (never a scene file), and the copies button mirrors the inspector action.
				if (VoltageEditorUtils.CenteredButton("Save Prefab", 0.7f))
					_imGuiManager.SavePrefabEditToOriginal();

				VoltageEditorUtils.SmallVerticalSpace();
				if (VoltageEditorUtils.CenteredButton("Apply to SerializedPrefab Copies", 0.7f))
					_imGuiManager.ApplyPrefabEditToCopies();
			}
			else
			{
				if (VoltageEditorUtils.CenteredButton("Save Scene", 0.7f))
					SerializationManager.Instance.InvokeSaveSceneChanges();
			}

			VoltageEditorUtils.MediumVerticalSpace();

			VoltageEditorUtils.MediumVerticalSpace();
			if (CopiedComponent != null)
			{
				VoltageEditorUtils.VeryBigVerticalSpace();
				ImGuiSafe.TextWrappedSafe($"Component Copied: {CopiedComponent.GetType().Name}");

				VoltageEditorUtils.SmallVerticalSpace();
				if (VoltageEditorUtils.CenteredButton("Clear Copied Component", 0.8f))
					CopiedComponent = null;
			}

			if (TmxFilePicker.IsOpen)
			{
				TmxFilePicker.TmxSelection tmxSelection = TmxFilePicker.Draw();
				if (tmxSelection != null)
				{
					OnTmxFileSelected?.Invoke(tmxSelection);
				}
			}

			if (AsepriteFilePicker.IsOpen)
			{
				AsepriteFilePicker.AsepriteSelection asepriteSelection = AsepriteFilePicker.Draw();
				if (asepriteSelection != null)
				{
					OnAsepriteImageSelected?.Invoke(asepriteSelection);
				}
			}

			ImGui.EndChild(); // ##SceneGraphContent

			// BeginDragDropTarget() immediately after EndChild() binds to the child item,
			// covering the whole scene-graph area — clicks on tree nodes pass through normally.
			if (ImGui.BeginDragDropTarget())
			{
				var payload = ImGui.AcceptDragDropPayload(AssetBrowserWindow.DragDropPayloadId);
				bool accepted;
				unsafe { accepted = payload.NativePtr != null; }

				if (accepted && !AssetBrowserWindow.DraggedReference.IsEmpty)
				{
					var reference = AssetBrowserWindow.DraggedReference;
					AssetBrowserWindow.DraggedReference = Voltage.Editor.Assets.AssetReference.Empty;

					// Resolve the item descriptor via the hint-path extension so we can
					// look up the correct DropFactory without re-scanning the database.
					var ext        = System.IO.Path.GetExtension(reference.HintPath);
					var descriptor = Voltage.Editor.Assets.AssetTypeRegistry.Resolve(ext);

					// Invoke the per-kind factory registered in AssetTypeRegistry.
					descriptor.DropFactory?.Invoke(reference);
				}

				ImGui.EndDragDropTarget();
			}
		}

		// Must run even when Begin() returns false (collapsed/clipped), or the ImGui stacks stay unbalanced.
		Gui.End();
		ImGui.PopStyleVar();
		ImGui.PopStyleColor();

		// Draw delete confirmation popup outside of the main window
		DrawDeletePrefabConfirmationPopup();

		HandleEntitySelectionNavigation();

		if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
		    && !ImGui.IsAnyItemHovered()
		    && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
		{
			EntityPane.DeselectAllEntities();
		}

		return isOpen;
	}

	/// <summary>
	/// Draws the delete prefab confirmation popup.
	/// </summary>
	private void DrawDeletePrefabConfirmationPopup()
	{
		if (_showDeletePrefabConfirmation)
		{
			ImGui.OpenPopup("delete-prefab-confirmation");
			_showDeletePrefabConfirmation = false;
		}

		var center = new Num.Vector2(Screen.Width * 0.45f, Screen.Height * 0.7f);
		ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Num.Vector2(0.5f, 0.5f));

		bool open = true;
		if (Gui.BeginPopupModal("delete-prefab-confirmation", ref open, ImGuiWindowFlags.AlwaysAutoResize))
		{
			ImGui.Text("Delete SerializedPrefab");
			ImGui.Separator();
			
			ImGuiSafe.TextWrappedSafe($"Are you sure you want to delete the '{_prefabToDelete}' prefab completely?");
			ImGui.TextColored(new Num.Vector4(1.0f, 0.6f, 0.2f, 1.0f), "This action cannot be undone!");

			VoltageEditorUtils.MediumVerticalSpace();

			var buttonWidth = 80f;
			var spacing = 10f;
			var totalButtonWidth = buttonWidth * 2 + spacing;
			var windowWidth = ImGui.GetWindowSize().X;
			var centerStart = (windowWidth - totalButtonWidth) * 0.5f;
			
			ImGui.SetCursorPosX(centerStart);
			
			if (Gui.Button("Yes", new Num.Vector2(buttonWidth, 0)))
			{
				DeletePrefab(_prefabToDelete);
			}
			
			ImGui.SameLine(); 
			
			if (Gui.Button("No", new Num.Vector2(buttonWidth, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			Gui.EndPopup();
		}
	}

	/// <summary>
	/// Deletes a prefab file and updates the prefab cache.
	/// Searches in EntityType subdirectories.
	/// </summary>
	private void DeletePrefab(string prefabName)
	{
		try
		{
			var prefabsDirectory = ProjectManager.Instance.CurrentProject.PrefabsFolder;
			bool fileDeleted = false;

			if (Directory.Exists(prefabsDirectory))
			{
				var entityTypeDirectories = Directory.GetDirectories(prefabsDirectory);
				
				foreach (var entityTypeDir in entityTypeDirectories)
				{
					// Try .vprefab first (native format), then .prefab (legacy).
					var prefabFilePath = Path.Combine(entityTypeDir, $"{prefabName}.vprefab");
					if (!File.Exists(prefabFilePath))
						prefabFilePath = Path.Combine(entityTypeDir, $"{prefabName}.prefab");

					if (File.Exists(prefabFilePath))
					{
						File.Delete(prefabFilePath);
						fileDeleted = true;
						break;
					}
				}
			}

			if (fileDeleted)
			{
				_cachedPrefabNames.Remove(prefabName);
				
				_prefabCacheInitialized = false;
				
				ImGui.CloseCurrentPopup();
			}
			else
			{
				var errorMsg = $"SerializedPrefab file not found: {prefabName}";
				Debug.Error(errorMsg);
			}
		}
		catch (Exception ex)
		{
			var errorMsg = $"Failed to delete prefab {prefabName}: {ex.Message}";
			Debug.Error(errorMsg);
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

	/// <summary>
	/// World-space center of the current editor camera view, so new entities land in the
	/// middle of the viewport rather than its top-left corner.
	/// </summary>
	private static Vector2 GetCameraCenterWorld()
	{
		var rt = Core.Scene.SceneRenderTargetSize;
		return Core.Scene.Camera.ScreenToWorldPoint(new Vector2(rt.X * 0.5f, rt.Y * 0.5f));
	}

	/// <summary>
	/// Creates a new entity from a prefab by name.
	/// Used by the in-editor right-click popup list, where the name is already
	/// guaranteed to be resolvable via the serialization manager's name scan.
	/// </summary>
	/// <param name="prefabName">File name without extension (backward-compat key).</param>
	/// <param name="prefabGuid">
	/// Stable GUID from the prefab's <c>.meta</c> sidecar.  Pass <see cref="Guid.Empty"/>
	/// (or omit) when the drop path is not involved.
	/// </param>
	internal void CreateEntityFromPrefab(string prefabName, Guid prefabGuid = default, Vector2? worldPosition = null)
	{
		try
		{
			if (prefabGuid == Guid.Empty)
				prefabGuid = ResolvePrefabGuidByName(prefabName);

			var prefabData = SerializationManager.Instance.InvokePrefabLoadRequested(prefabName);
			InstantiateEntityFromPrefabData(prefabData, prefabName, prefabGuid, worldPosition);
		}
		catch (Exception ex)
		{
			EditorDebug.Log($"Error creating entity from prefab '{prefabName}': {ex.Message}");
		}
	}

	private static Guid ResolvePrefabGuidByName(string prefabName)
	{
		var db = Assets.AssetDatabase.Instance;
		if (db == null)
			return Guid.Empty;

		var prefabsDir = ProjectManager.Instance.CurrentProject?.PrefabsFolder;
		if (string.IsNullOrEmpty(prefabsDir) || !System.IO.Directory.Exists(prefabsDir))
			return Guid.Empty;

		var direct = System.IO.Path.Combine(prefabsDir, $"{prefabName}.vprefab");
		if (System.IO.File.Exists(direct))
			return db.GetReference(direct).Guid;

		foreach (var sub in System.IO.Directory.GetDirectories(prefabsDir))
		{
			var candidate = System.IO.Path.Combine(sub, $"{prefabName}.vprefab");
			if (System.IO.File.Exists(candidate))
				return db.GetReference(candidate).Guid;
		}

		return Guid.Empty;
	}

	/// <summary>
	/// Creates a new entity from a preloaded <see cref="PrefabData"/>.
	/// Used by the asset-drop path where the absolute file path is already resolved,
	/// so we bypass the name-based scan entirely.
	/// </summary>
	internal Entity CreateEntityFromPrefabData(PrefabData prefabData, string prefabName, Guid prefabGuid = default, Vector2? worldPosition = null)
	{
		try
		{
			return InstantiateEntityFromPrefabData(prefabData, prefabName, prefabGuid, worldPosition);
		}
		catch (Exception ex)
		{
			EditorDebug.Log($"Error instantiating prefab '{prefabName}' from preloaded data: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Core instantiation logic shared by both overloads above. Returns the created entity (or null).
	/// </summary>
	private Entity InstantiateEntityFromPrefabData(PrefabData prefabData, string prefabName, Guid prefabGuid, Vector2? worldPosition)
	{
		if (prefabData.EntityData == null)
		{
			EditorDebug.Log($"Null SerializedPrefab EntityData for '{prefabName}'.");
			return null;
		}

		var entity = Core.Scene.SimpleCreateEntity(prefabData.Name, Entity.InstanceType.SerializedPrefab);
		entity.Type = Entity.InstanceType.SerializedPrefab;
		entity.Transform.Position = worldPosition ?? GetCameraCenterWorld();

		// LoadPrefabEntityData instantiates components + children and performs the reference
		// remap + resolve internally (it owns the old->new id map built at child creation).
		SerializationManager.Instance.InvokeLoadEntityData(entity, prefabData);
		entity.Name = Core.Scene.GetUniqueEntityName(prefabData.Name, entity);
		entity.OriginalPrefabName = prefabName;
		entity.OriginalPrefabGuid = prefabGuid;

		EditorChangeTracker.PushUndo(
			new EntityCreateDeleteUndoAction(Core.Scene, entity, wasCreated: true,
				$"Create Entity from SerializedPrefab {entity.Name}"),
			entity,
			$"Create Entity from SerializedPrefab {entity.Name}"
		);

		_imGuiManager.SceneGraphWindow.EntityPane.SetSelectedEntity(entity, false);
		_imGuiManager.MainEntityInspectorWindow.DelayedSetEntity(entity);

		EditorDebug.Log($"Created entity '{entity.Name}' from prefab '{prefabName}'.");
		return entity;
	}

	#region Entity Selection Navigation
	private void HandleEntitySelectionNavigation()
	{
		var selectedEntity = _imGuiManager.SceneGraphWindow.EntityPane.SelectedEntities.FirstOrDefault();

		if (!Core.IsEditMode || selectedEntity == null || !ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow))
			return; 

		var hierarchyList = BuildHierarchyList();
		var currentEntity = _imGuiManager?.MainEntityInspectorWindow?.Entity;
		if (currentEntity == null || hierarchyList.Count == 0)
			return;

		bool upPressed = EditorHotkeys.Pressed(EditorHotkeys.SelectPrevEntity);
		bool downPressed = EditorHotkeys.Pressed(EditorHotkeys.SelectNextEntity);
		bool upHeld = ImGui.IsKeyDown(ImGuiKey.UpArrow);
		bool downHeld = ImGui.IsKeyDown(ImGuiKey.DownArrow);

		double now = ImGui.GetTime();

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
			return null;

		Entity prev = hierarchyList[idx - 1];

		if (current.Transform.Parent != null && prev == current.Transform.Parent.Entity)
			return prev;

		if (prev.Transform.ChildCount > 0)
			return GetLastDescendant(prev);

		return prev;
	}

	private Entity NavigateDown(Entity current, List<Entity> hierarchyList)
	{
		int idx = hierarchyList.IndexOf(current);
		if (idx < 0 || idx >= hierarchyList.Count - 1)
			return null;
		return hierarchyList[idx + 1];
	}

	private void ExpandParentsAndChildren(Entity entity)
	{
		var parent = entity.Transform.Parent;
		while (parent != null)
		{
			ExpandedEntities.Add(parent.Entity);
			parent = parent.Parent;
		}

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