using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Nez.Data;
using Nez.ECS;
using Nez.Editor;
using Nez.ImGuiTools.UndoActions;
using Nez.Persistence;
using Nez.Utils;
using Nez.Utils.Coroutines;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static Nez.Entity;

namespace Nez.ImGuiTools.SceneGraphPanes;

public class EntityPane
{
	#region Fields and Properties

    private const int MIN_ENTITIES_FOR_CLIPPER = 100;
    private Entity _previousEntity;
	public List<Entity> SelectedEntities => _selectedEntities;
	private ImGuiManager _imGuiManager;
	private List<Entity> _copiedEntities = new();
	private List<Entity> _selectedEntities = new();
	private Entity _lastRangeSelectEntity;

	public void SetSelectedEntity(Entity entity, bool ctrlDown, bool shiftDown = false)
	{
		if (entity == null && !ctrlDown && !shiftDown)
			return;

		var hierarchyList = _imGuiManager.SceneGraphWindow.BuildHierarchyList();

		if (shiftDown && _lastRangeSelectEntity != null)
		{
			int startIdx = hierarchyList.IndexOf(_lastRangeSelectEntity);
			int endIdx = hierarchyList.IndexOf(entity);
			if (startIdx != -1 && endIdx != -1)
			{
				int minIdx = Math.Min(startIdx, endIdx);
				int maxIdx = Math.Max(startIdx, endIdx);
				if (ctrlDown)
				{
					// Add range to current selection
					for (int i = minIdx; i <= maxIdx; i++)
					{
						if (!_selectedEntities.Contains(hierarchyList[i]))
							_selectedEntities.Add(hierarchyList[i]);
					}
				}
				else
				{
					// Replace selection with range
					_selectedEntities.Clear();
					for (int i = minIdx; i <= maxIdx; i++)
						_selectedEntities.Add(hierarchyList[i]);
				}
			}
		}
		else if (ctrlDown)
		{
			if (_selectedEntities.Contains(entity))
				_selectedEntities.Remove(entity);
			else
				_selectedEntities.Add(entity);
			_lastRangeSelectEntity = entity;
		}
		else
		{
			_selectedEntities.Clear();
			_selectedEntities.Add(entity);
			_lastRangeSelectEntity = entity;
		}
	}
	#endregion

	#region Main Draw Entry Point

	/// <summary>
	/// Main entry point for drawing the entity pane UI and gizmos.
	/// </summary>
	public unsafe void Draw()
	{
		if (_imGuiManager == null)
			_imGuiManager = Core.GetGlobalManager<ImGuiManager>();

		// Draw entity tree (with clipper for large lists)
		if (Core.Scene.Entities.Count > MIN_ENTITIES_FOR_CLIPPER)
		{
			var clipperPtr = ImGuiNative.ImGuiListClipper_ImGuiListClipper();
			var clipper = new ImGuiListClipperPtr(clipperPtr);

			clipper.Begin(Core.Scene.Entities.Count, -1);

			while (clipper.Step())
				for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
					DrawEntity(Core.Scene.Entities[i]);

			ImGuiNative.ImGuiListClipper_destroy(clipperPtr);
		}
		else
		{
			for (var i = 0; i < Core.Scene.Entities.Count; i++)
				DrawEntity(Core.Scene.Entities[i]);
		}

		NezImGui.MediumVerticalSpace();
		EntityDuplicationAndDeletion();
	}
	#endregion

    #region Entity Tree Rendering and Interaction

    /// <summary>
    /// Draws a single entity node in the tree, handles selection, context menu, and inspector opening.
    /// </summary>
    private void DrawEntity(Entity entity, bool onlyDrawRoots = true)
    {
        if (onlyDrawRoots && entity.Transform.Parent != null)
            return;

        bool isSelected = _selectedEntities.Contains(entity);
        ImGui.PushID((int)entity.Id);
        bool treeNodeOpened = false;
        var flags = isSelected ? ImGuiTreeNodeFlags.Selected : 0;
        bool isExpanded = _imGuiManager.SceneGraphWindow.ExpandedEntities.Contains(entity);
        if (entity.Transform.ChildCount > 0)
            ImGui.SetNextItemOpen(isExpanded, ImGuiCond.Always);

		// Set special color for entities based on type
		bool isPrefab = entity.Type == Entity.InstanceType.Prefab;
		bool isHardCoded = entity.Type == Entity.InstanceType.HardCoded;

		if (isPrefab)
		{
			// Orange color for prefab entities
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1.0f, 0.6f, 0.2f, 1.0f));
		}
		else if (isHardCoded)
		{
			// Green color for hardcoded entities
			ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.2f, 1.0f, 0.2f, 1.0f));
		}

		// Draw tree node
		if (entity.Transform.ChildCount > 0)
			treeNodeOpened = ImGui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.OpenOnArrow | flags);
		else
			treeNodeOpened = ImGui.TreeNodeEx($"{entity.Name} ({entity.Transform.ChildCount})###{entity.Id}",
				ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.OpenOnArrow | flags);

		if (isPrefab || isHardCoded)
		{
			ImGui.PopStyleColor();
		}

		if (entity.Transform.ChildCount > 0)
		{
			if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
			    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X <= ImGui.GetTreeNodeToLabelSpacing())
			{
				if (isExpanded)
					_imGuiManager.SceneGraphWindow.ExpandedEntities.Remove(entity);
				else
					_imGuiManager.SceneGraphWindow.ExpandedEntities.Add(entity);
			}
		}
		NezImGui.ShowContextMenuTooltip();

		ImGui.OpenPopupOnItemClick("entityContextMenu", ImGuiPopupFlags.MouseButtonRight);
		DrawEntityContextMenuPopup(entity);

		if (ImGui.IsItemClicked(ImGuiMouseButton.Left) &&
			ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
		{
			bool ctrlDown = Input.IsKeyDown(Keys.LeftControl) || Input.IsKeyDown(Keys.RightControl) || ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeySuper;
			bool shiftDown = Input.IsKeyDown(Keys.LeftShift) || Input.IsKeyDown(Keys.RightShift) || ImGui.GetIO().KeyShift;
			SetSelectedEntity(entity, ctrlDown, shiftDown);
			//if (!ctrlDown)// && !shiftDown)
				_imGuiManager.OpenMainEntityInspector(entity);

			ImGui.SetWindowFocus();
		}

		if (ImGui.IsMouseClicked(0) && ImGui.IsItemClicked() &&
		    ImGui.GetMousePos().X - ImGui.GetItemRectMin().X > ImGui.GetTreeNodeToLabelSpacing())
            if (Core.Scene.Entities.Count > 0 && Core.IsEditMode)
            {
                if (_previousEntity == null || !_previousEntity.Equals(entity))
                {
                    _previousEntity = entity;
                }

                _imGuiManager.CursorSelectionManager.SetCameraTargetPosition(entity.Transform.Position);
            }

		// Recursively draw children
		if (treeNodeOpened)
		{
			for (var i = 0; i < entity.Transform.ChildCount; i++)
				DrawEntity(entity.Transform.GetChild(i).Entity, false);

			ImGui.TreePop();
		}

		ImGui.PopID();
	}

    #endregion

    #region Entity Context Menu

    /// <summary>
    /// Draws the context menu popup for entity actions (copy, clone, destroy, etc).
    /// </summary>
    private void DrawEntityContextMenuPopup(Entity entity)
    {
        if (_imGuiManager == null)
            _imGuiManager = Core.GetGlobalManager<ImGuiManager>();

        if (ImGui.BeginPopup("entityContextMenu"))
        {
            if (_imGuiManager.SceneGraphWindow.CopiedComponent != null && ImGui.Selectable("Paste Component"))
            {
                var copiedComponent = _imGuiManager.SceneGraphWindow.CopiedComponent;
                
                // Find existing component of the same type
                var existingComponent = entity.Components.FirstOrDefault(c => c.GetType() == copiedComponent.GetType());
                
                if (existingComponent != null)
                {
                    // Use JSON serialization for reliable deep cloning
                    try
                    {
                        var jsonSettings = new JsonSettings
                        {
                            PrettyPrint = false,
                            TypeNameHandling = TypeNameHandling.Auto,
                            PreserveReferencesHandling = false
                        };
                        
                        var sourceData = copiedComponent.Data;
                        if (sourceData != null)
                        {
                            // Store old data for undo
                            var oldData = existingComponent.Data;
                            
                            // Clone the source data
                            var json = Json.ToJson(sourceData, jsonSettings);
                            var clonedData = (ComponentData)Json.FromJson(json, sourceData.GetType());
                            
                            // Create undo action
                            EditorChangeTracker.PushUndo(
                                new ComponentDataChangeAction(
                                    existingComponent,
                                    oldData,
                                    clonedData,
                                    $"Paste {copiedComponent.GetType().Name} to {entity.Name}"
                            ),
                            entity,
                            $"Paste {copiedComponent.GetType().Name} to {entity.Name}"
                        );
                        
                        // Apply the cloned data
                        existingComponent.Data = clonedData;
                        
                        System.Console.WriteLine($"Pasted {copiedComponent.GetType().Name} values to {entity.Name}");
						}
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"Failed to paste component data: {ex.Message}");
                        
                        // Fallback: use component replacement
                        var clonedComponent = copiedComponent.Clone();
                        clonedComponent.Name = existingComponent.Name;
                        entity.ReplaceComponent(clonedComponent);
                        
                        System.Console.WriteLine($"Used fallback paste method for {copiedComponent.GetType().Name}");
                    }
                    
                    // Refresh the inspector to show the updated component
                    _imGuiManager.RefreshMainEntityInspector();
                }
                else
                {
                    // No existing component of this type, add a clone
                    var clonedComponent = copiedComponent.Clone();
                    entity.AddComponent(clonedComponent);
                    
                    // Refresh the inspector to show the new component
                    _imGuiManager.RefreshMainEntityInspector();
                }
            }

            if (ImGui.Selectable($"Open {entity.Name} in separate window"))
                Core.GetGlobalManager<ImGuiManager>().OpenSeparateEntityInspector(entity);

            // Entity Commands
            if (ImGui.Selectable("Move Camera to " + entity.Name))
                if (Core.Scene.Entities.Count > 0 && Core.IsEditMode)
                    _imGuiManager.CursorSelectionManager.SetCameraTargetPosition(entity.Transform.Position);

            // Clone logic
            string reason = null;
            if (entity.Type == Entity.InstanceType.HardCoded)
            {
                reason = "Can't duplicate HardCoded entities!";
            }

            if (reason == null)
            {
                if (ImGui.Selectable("Duplicate Entity " + entity.Name))
                    DuplicateEntity(entity);
            }
            else
            {
                ImGui.BeginDisabled(true);
                ImGui.Selectable(reason);
                ImGui.EndDisabled();
            }

            if (ImGui.Selectable("Destroy Entity"))
            {
                // Push undo BEFORE destroying, so the entity is still valid
                EditorChangeTracker.PushUndo(
                    new EntityCreateDeleteUndoAction(entity.Scene, entity, wasCreated: false, $"Delete Entity {entity.Name}"),
                    entity,
                    $"Delete Entity {entity.Name}"
                );
                entity.Destroy();
            }

            if (ImGui.Selectable("Create Child Entity", false, ImGuiSelectableFlags.DontClosePopups))
                ImGui.OpenPopup("create-new-entity");

            ImGui.EndPopup();
        }
    }
	#endregion


	#region Copy and Paste Logic
	/// <summary>
	/// Handles copy/paste/duplicate shortcuts for entities.
	/// </summary>
	///
	private void EntityDuplicationAndDeletion()
    {
	    if (ImGui.IsAnyItemActive() || ImGui.IsAnyItemFocused())
		    return;

		// Handle Copy/Paste/Duplicate Shortcuts
		bool gameCtrlDown = Input.IsKeyDown(Keys.LeftControl) || Input.IsKeyDown(Keys.RightControl);
	    bool imguiCtrlDown = ImGui.GetIO().KeyCtrl;

	    bool ShouldBlockDuplication(Entity entity)
	    {
	        if (entity != null && entity.Type == Entity.InstanceType.HardCoded)
	        {
	            NotificationSystem.ShowTimedNotification("Cannot duplicate HardCoded entities.");
	            return true; 
	        }
	        return false;
	    }

	    // Ctrl+D: Duplicate selected
	    if (Core.IsEditMode && gameCtrlDown && Input.IsKeyPressed(Keys.D) && _selectedEntities.Count > 0)
	    {
	        var entitiesToDuplicate = _selectedEntities.Where(e => !ShouldBlockDuplication(e)).ToList();
	        if (entitiesToDuplicate.Count > 1)
	        {
	            DuplicateEntities(entitiesToDuplicate);
	        }
	        else
	        {
	            foreach (var entity in entitiesToDuplicate)
	                DuplicateEntity(entity);
	        }
	    }
	    else if (imguiCtrlDown && ImGui.IsKeyPressed(ImGuiKey.D) && _selectedEntities.Count > 0)
	    {
	        var entitiesToDuplicate = _selectedEntities.Where(e => !ShouldBlockDuplication(e)).ToList();
	        if (entitiesToDuplicate.Count > 1)
	        {
	            DuplicateEntities(entitiesToDuplicate);
	        }
	        else
	        {
	            foreach (var entity in entitiesToDuplicate)
	                DuplicateEntity(entity);
	        }
	    }

	    // Ctrl+C: Copy all selected entities
	    if (Core.IsEditMode && gameCtrlDown && Input.IsKeyPressed(Keys.C) && _selectedEntities.Count > 0)
	    {
	        _copiedEntities = _selectedEntities.ToList();
	    }
	    else if (imguiCtrlDown && ImGui.IsKeyPressed(ImGuiKey.C) && _selectedEntities.Count > 0)
	    {
	        _copiedEntities = _selectedEntities.ToList();
	    }

	    // Ctrl+V: Paste (duplicate all copied entities)
	    if (Core.IsEditMode && gameCtrlDown && Input.IsKeyPressed(Keys.V) && _copiedEntities.Count > 0)
	    {
	        var entitiesToDuplicate = _copiedEntities.Where(e => !ShouldBlockDuplication(e)).ToList();
	        if (entitiesToDuplicate.Count > 1)
	        {
	            DuplicateEntities(entitiesToDuplicate);
	        }
	        else
	        {
	            foreach (var entity in entitiesToDuplicate)
	                DuplicateEntity(entity);
	        }
	    }
	    else if (imguiCtrlDown && ImGui.IsKeyPressed(ImGuiKey.V) && _copiedEntities.Count > 0)
	    {
	        var entitiesToDuplicate = _copiedEntities.Where(e => !ShouldBlockDuplication(e)).ToList();
	        if (entitiesToDuplicate.Count > 1)
	        {
	            DuplicateEntities(entitiesToDuplicate);
	        }
	        else
	        {
	            foreach (var entity in entitiesToDuplicate)
	                DuplicateEntity(entity);
	        }
	    }

	    // Delete: Remove all selected entities with Undo/Redo support
	    if (Core.IsEditMode && _selectedEntities.Count > 0 &&
	        (Input.IsKeyPressed(Keys.Delete) || ImGui.IsKeyPressed(ImGuiKey.Delete)))
	    {
	        var entitiesToDelete = _selectedEntities.ToList();

	        // Push a single undo for all entities
	        EditorChangeTracker.PushUndo(
	            new MultiEntityDeleteUndoAction(Core.Scene, entitiesToDelete, 
	                $"Deleted: {string.Join(", ", entitiesToDelete.Select(e => e.Name))}"),
	            entitiesToDelete.FirstOrDefault(),
	            $"Deleted: {string.Join(", ", entitiesToDelete.Select(e => e.Name))}"
	        );

	        foreach (var entity in entitiesToDelete)
	            entity.Destroy();

	        DeselectAllEntities();
	    }
	}

	/// <summary>
	/// Duplicates the given entity and adds it to the scene.
	/// If the entity is HardCoded, the clone will be of type Dynamic.
	/// Uses JSON serialization for reliable component copying.
	/// </summary>
	public Entity DuplicateEntity(Entity entity, string customName = null)
	{
		if (entity == null || entity.Scene == null)
			return null;

		var typeName = entity.GetType().Name;
		if (EntityFactoryRegistry.TryCreate(typeName, out var clone))
		{
			// Use unique name for each clone
			string baseName = customName ?? entity.Name;
			clone.Name = Core.Scene.GetUniqueEntityName(baseName, clone);

			// Set up the clone with basic properties first
			clone.Transform.Position = entity.Transform.Position;
			clone.Transform.Rotation = entity.Rotation;
			clone.Transform.Scale = entity.Scale;
			clone.SetTag(entity.Tag);
			clone.Enabled = entity.Enabled;
			clone.DebugRenderEnabled = entity.DebugRenderEnabled;
			clone.UpdateInterval = entity.UpdateInterval;
			clone.UpdateOrder = entity.UpdateOrder;

			if(entity.Type == InstanceType.HardCoded || entity.Type == InstanceType.Dynamic)
				clone.Type = InstanceType.Dynamic;
			else
			{
				clone.Type = InstanceType.Prefab;
				clone.OriginalPrefabName = entity.OriginalPrefabName;
			}

			// IMPORTANT: Copy all components from the source entity BEFORE invoking EntityCreated
			// This ensures we preserve the original component data before OnAddedToScene runs
			foreach (var sourceComponent in entity.Components)
			{
				// Check if clone already has a component of this type (from constructor)
				var existingComponent = clone.Components.FirstOrDefault(c => 
					c.GetType() == sourceComponent.GetType() && c.Name == sourceComponent.Name);
				
				if (existingComponent != null)
				{
					// Component already exists - copy the data from source to existing
					if (sourceComponent.Data != null)
					{
						try
						{
							var componentJsonSettings = new JsonSettings
							{
								PrettyPrint = false,
								TypeNameHandling = TypeNameHandling.Auto,
								PreserveReferencesHandling = false
							};
							
							// Serialize the source component data to JSON
							var json = Json.ToJson(sourceComponent.Data, componentJsonSettings);
						
							// Deserialize back to a new instance (deep clone)
							var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
							
							// Apply the cloned data to the existing component
							existingComponent.Data = clonedData;
							
							// Also copy other component properties
							existingComponent.Enabled = sourceComponent.Enabled;
						}
						catch (Exception ex)
						{
							System.Console.WriteLine($"Failed to copy data to existing component {sourceComponent.GetType().Name}: {ex.Message}");
						}
					}
				}
				else
				{
					// Component doesn't exist - create a new one
					var componentType = sourceComponent.GetType();
					Component clonedComponent;
					
					try
					{
						clonedComponent = (Component)Activator.CreateInstance(componentType);
					}
					catch (Exception ex)
					{
						System.Console.WriteLine($"Failed to create component {componentType.Name}: {ex.Message}");
						continue;
					}

					// Copy basic component properties
					clonedComponent.Name = sourceComponent.Name;
					clonedComponent.Enabled = sourceComponent.Enabled;

					// Add the component first so it gets properly initialized
					clone.AddComponent(clonedComponent);

					// Copy component data using JSON serialization
					if (sourceComponent.Data != null)
					{
						try
						{
							var componentJsonSettings = new JsonSettings
							{
								PrettyPrint = false,
								TypeNameHandling = TypeNameHandling.Auto,
								PreserveReferencesHandling = false
							};
						
							// Serialize the source component data to JSON
							var json = Json.ToJson(sourceComponent.Data, componentJsonSettings);
						
							// Deserialize back to a new instance (deep clone)
							var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
							
							// Apply the cloned data to the new component
							clonedComponent.Data = clonedData;
						}
						catch (Exception ex)
						{
							System.Console.WriteLine($"Failed to copy data for component {sourceComponent.GetType().Name}: {ex.Message}");
							
							// Fallback: try the Clone method if JSON fails
							try
							{
								var fallbackClone = sourceComponent.Clone();
								if (fallbackClone != null && fallbackClone.Data != null)
								{
									clonedComponent.Data = fallbackClone.Data;
								}
							}
							catch (Exception cloneEx)
							{
								System.Console.WriteLine($"Clone() fallback also failed for {sourceComponent.GetType().Name}: {cloneEx.Message}");
							}
						}
					}
				}
			}

			// NOW invoke entity creation - this will call OnAddedToScene
			// The components we copied above should be preserved
			EntityFactoryRegistry.InvokeEntityCreated(clone);

			// After OnAddedToScene, copy any component data again for components that might have been reset
			// This is a safety measure for components that get reinitialized in OnAddedToScene
			foreach (var sourceComponent in entity.Components)
			{
				var targetComponent = clone.Components.FirstOrDefault(c => 
					c.GetType() == sourceComponent.GetType() && c.Name == sourceComponent.Name);
				
				if (targetComponent != null && sourceComponent.Data != null)
				{
					try
					{
						var componentJsonSettings = new JsonSettings
						{
							PrettyPrint = false,
							TypeNameHandling = TypeNameHandling.Auto,
							PreserveReferencesHandling = false
						};
						
						var json = Json.ToJson(sourceComponent.Data, componentJsonSettings);
						var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
						targetComponent.Data = clonedData;
					}
					catch (Exception ex)
					{
						System.Console.WriteLine($"Failed post-creation data copy for {sourceComponent.GetType().Name}: {ex.Message}");
					}
				}
			}

			// Copy children if any exist, but SKIP HardCoded entities
			for (var i = 0; i < entity.Transform.ChildCount; i++)
			{
				var childEntity = entity.Transform.GetChild(i).Entity;
				
				// Skip HardCoded children - they should be created by the parent entity's initialization logic
				if (childEntity.Type == Entity.InstanceType.HardCoded)
				{
					continue;
				}
				
				// Only duplicate Dynamic and Prefab children
				var clonedChild = DuplicateEntity(childEntity, null);
				if (clonedChild != null)
				{
					clonedChild.Transform.SetParent(clone.Transform);
				}
			}

			// Undo/Redo support for entity creation
			EditorChangeTracker.PushUndo(
				new EntityCreateDeleteUndoAction(entity.Scene, clone, wasCreated: true, $"Created: Entity {clone.Name}"),
				clone,
				$"Created: {clone.Name}"
			);

			_imGuiManager.MainEntityInspector.DelayedSetEntity(clone);
			
			return clone;
		}
		else
		{
			throw new InvalidOperationException(
				$"EntityFactoryRegistry: Entity type '{typeName}' is not registered in the factory. " +
				$"Did you forget to call EntityFactoryRegistry.Register(\"{typeName}\", ...)?");
		}
	}

    /// <summary>
    /// Duplicates the given entities and adds them to the scene.
    /// Uses JSON serialization for reliable component copying.
    /// </summary>
    public List<Entity> DuplicateEntities(IEnumerable<Entity> entitiesToDuplicate)
    {
        var clones = new List<Entity>();

        // First, create all clones and assign unique names considering both scene and pending clones
        foreach (var entity in entitiesToDuplicate)
        {
            var typeName = entity.GetType().Name;
            if (EntityFactoryRegistry.TryCreate(typeName, out var clone))
            {
                clone.Name = Core.Scene.GetUniqueEntityName(entity.Name, clone, clones);
                clone.Transform.Position = entity.Transform.Position;
                clone.Transform.Rotation = entity.Rotation;
                clone.Transform.Scale = entity.Scale;
                clone.SetTag(entity.Tag);
                clone.Enabled = entity.Enabled;
                clone.DebugRenderEnabled = entity.DebugRenderEnabled;
                clone.UpdateInterval = entity.UpdateInterval;
                clone.UpdateOrder = entity.UpdateOrder;

                if (entity.Type == InstanceType.HardCoded || entity.Type == InstanceType.Dynamic)
                    clone.Type = InstanceType.Dynamic;
                else
                {
                    clone.Type = InstanceType.Prefab;
                    clone.OriginalPrefabName = entity.OriginalPrefabName;
                }

                // Copy all components from the source entity BEFORE invoking EntityCreated
                foreach (var sourceComponent in entity.Components)
                {
                    var existingComponent = clone.Components.FirstOrDefault(c =>
                        c.GetType() == sourceComponent.GetType() && c.Name == sourceComponent.Name);

                    if (existingComponent != null)
                    {
                        if (sourceComponent.Data != null)
                        {
                            try
                            {
                                var componentJsonSettings = new JsonSettings
                                {
                                    PrettyPrint = false,
                                    TypeNameHandling = TypeNameHandling.Auto,
                                    PreserveReferencesHandling = false
                                };

                                var json = Json.ToJson(sourceComponent.Data, componentJsonSettings);
                                var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
                                existingComponent.Data = clonedData;
                                existingComponent.Enabled = sourceComponent.Enabled;
                            }
                            catch (Exception ex)
                            {
                                System.Console.WriteLine($"Failed to copy data to existing component {sourceComponent.GetType().Name}: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        var componentType = sourceComponent.GetType();
                        Component clonedComponent;
                        try
                        {
                            clonedComponent = (Component)Activator.CreateInstance(componentType);
                        }
                        catch (Exception ex)
                        {
                            System.Console.WriteLine($"Failed to create component {componentType.Name}: {ex.Message}");
                            continue;
                        }

                        clonedComponent.Name = sourceComponent.Name;
                        clonedComponent.Enabled = sourceComponent.Enabled;
                        clone.AddComponent(clonedComponent);

                        if (sourceComponent.Data != null)
                        {
                            try
                            {
                                var componentJsonSettings = new JsonSettings
                                {
                                    PrettyPrint = false,
                                    TypeNameHandling = TypeNameHandling.Auto,
                                    PreserveReferencesHandling = false
                                };

                                var json = Json.ToJson(sourceComponent.Data, componentJsonSettings);
                                var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
                                clonedComponent.Data = clonedData;
                            }
                            catch (Exception ex)
                            {
                                System.Console.WriteLine($"Failed to copy data for component {sourceComponent.GetType().Name}: {ex.Message}");
                                try
                                {
                                    var fallbackClone = sourceComponent.Clone();
                                    if (fallbackClone != null && fallbackClone.Data != null)
                                    {
                                        clonedComponent.Data = fallbackClone.Data;
                                    }
                                }
                                catch (Exception cloneEx)
                                {
                                    System.Console.WriteLine($"Clone() fallback also failed for {sourceComponent.GetType().Name}: {cloneEx.Message}");
                                }
                            }
                        }
                    }
                }

                // Call entity.InitParams() basically 
                EntityFactoryRegistry.InvokeEntityCreated(clone);

                // Post-creation component data copy (for components that may be reset)
                foreach (var sourceComponent in entity.Components)
                {
                    var targetComponent = clone.Components.FirstOrDefault(c =>
                        c.GetType() == sourceComponent.GetType() && c.Name == sourceComponent.Name);

                    if (targetComponent != null && sourceComponent.Data != null)
                    {
                        try
                        {
                            var componentJsonSettings = new JsonSettings
                            {
                                PrettyPrint = false,
                                TypeNameHandling = TypeNameHandling.Auto,
                                PreserveReferencesHandling = false
                            };

                            var json = Json.ToJson(sourceComponent.Data, componentJsonSettings);
                            var clonedData = (ComponentData)Json.FromJson(json, sourceComponent.Data.GetType());
                            targetComponent.Data = clonedData;
                        }
                        catch (Exception ex)
                        {
                            Debug.Log(Debug.LogType.Warn, $"Failed post-creation data copy for {sourceComponent.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                // Copy children if any exist, but SKIP HardCoded entities
                for (var i = 0; i < entity.Transform.ChildCount; i++)
                {
                    var childEntity = entity.Transform.GetChild(i).Entity;
                    if (childEntity.Type == Entity.InstanceType.HardCoded)
                        continue;

                    var clonedChild = DuplicateEntity(childEntity, null);
                    if (clonedChild != null)
                    {
                        clonedChild.Transform.SetParent(clone.Transform);
                    }
                }

                clones.Add(clone);
            }
        }

        // Add all clones to the scene after naming
        foreach (var clone in clones)
            Core.Scene.AddEntity(clone);

        return clones;
	}
	#endregion

    public void DeselectAllEntities()
    {
        _selectedEntities.Clear();
        _lastRangeSelectEntity = null; // Reset anchor
        _imGuiManager.ClearHighlightCache();
	}

	public Vector2 GetSelectedEntitiesCenter()
    {
        if (SelectedEntities.Count == 0)
            return Core.Scene.Camera.Position;        

        Vector2 sum = Vector2.Zero;

        foreach (var e in SelectedEntities)
            sum += e.Transform.Position;

        return sum / SelectedEntities.Count;
    }
}