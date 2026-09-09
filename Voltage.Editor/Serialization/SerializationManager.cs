using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Voltage.Data;
using Voltage.Editor.Interfaces;
using Voltage.Editor.ProjectFile;
using Voltage.Editor.SceneFile;
using Voltage.Editor.Utils;
using Voltage.Editor.Undo.Core;
using Voltage.Persistence;
using Voltage.Serialization;
using Voltage.Serialization.Registries;
using Voltage.Utils;
using Voltage.Utils.Extensions;
using PrefabData = Voltage.Data.PrefabData;

namespace Voltage.Editor.Serialization;

/// <summary>
/// Manages all Scene/Entity/Prefab data loading and saving operations.
/// Central point for serialization/deserialization.
/// </summary>
public partial class SerializationManager : GlobalManager
{
	private static SerializationManager _instance;

	public static SerializationManager Instance
	{
		get
		{
			if (_instance == null)
			{
				_instance = Core.GetGlobalManager<SerializationManager>();
				if (_instance == null)
				{
					_instance = new SerializationManager();
					Core.RegisterGlobalManager(_instance);
				}
			}

			return _instance;
		}
	}

	public bool HasExitedEditorMode { get; private set; } = false;

	#region Events

	/// <summary>
	/// Invoked when a scene needs to be saved asynchronously.
	/// </summary>
	public event Func<Task> OnSaveSceneAsync;

	/// <summary>
	/// Invoked when a prefab is created and needs to be saved.
	/// </summary>
	public event Func<Entity, bool, Task<bool>> OnPrefabCreated;

	/// <summary>
	/// Invoked when a prefab needs to be loaded by name.
	/// </summary>
	public event Func<string, PrefabData> OnPrefabLoadRequested;

	/// <summary>
	/// Invoked when entity data needs to be loaded onto an entity.
	/// </summary>
	public event Action<Entity, object> OnLoadEntityData;

	#endregion

	public SerializationManager()
	{
		_instance = this;
		Core.Emitter.AddObserver(CoreEvents.SceneChanged, OnSceneChanged);
		Core.OnSwitchEditMode += ChangedToPlayMode;

		OnSaveSceneAsync += SaveSceneChangesAsync;
		// Wire each event to its real handler. (These previously subscribed the events to
		// themselves — a copy-paste bug — so OnPrefabLoadRequested/OnLoadEntityData never fired,
		// making prefab instantiation produce empty, nameless, component-less entities.)
		OnPrefabCreated += SavePrefabDataAsync;
		OnPrefabLoadRequested += name => LoadPrefabData(name) ?? new PrefabData();
		OnLoadEntityData += (entity, data) =>
		{
			if (data is PrefabData prefabData)
				LoadPrefabEntityData(entity, prefabData);
		};
	}

	#region Event Invokers

	public void InvokeSaveSceneChanges()
	{
		if (Core.Scene == null)
		{
			Debug.Error("No active scene to save!");
			return;
		}

		if (!SceneManager.Instance.HasLoadedScene)
		{
			Debug.Error("No Scene has been loaded yet!");
			return;
		}

		if (!Core.IsEditMode)
		{
			NotificationSystem.ShowTimedNotification("Scene Data can't be saved in Play/Pause Mode");
			return;
		}

		Core.Schedule(0f, false, this, async _ =>
		{
			try
			{
				if (OnSaveSceneAsync != null)
				{
					await OnSaveSceneAsync.Invoke();
				}
				else
				{
					Debug.Error("OnSaveSceneAsync has no subscribers!");
				}
			}
			catch (Exception ex)
			{
				Debug.Error($"Failed to save scene: {ex.Message}");
				Debug.Error($"Stack trace: {ex.StackTrace}");
			}
		});
	}

	/// <summary>
	/// Invokes the prefab creation event.
	/// </summary>
	public async Task<bool> InvokePrefabCreated(Entity prefabEntity, bool overrideExistingPrefab)
	{
		if (OnPrefabCreated != null)
		{
			return await OnPrefabCreated.Invoke(prefabEntity, overrideExistingPrefab);
		}

		return false;
	}

	/// <summary>
	/// Invokes the prefab load request event.
	/// </summary>
	public PrefabData InvokePrefabLoadRequested(string prefabName)
	{
		if (OnPrefabLoadRequested != null)
		{
			return OnPrefabLoadRequested.Invoke(prefabName);
		}

		return new PrefabData();
	}

	/// <summary>
	/// Invokes the entity data load event.
	/// </summary>
	public void InvokeLoadEntityData(Entity entity, object entityData)
	{
		OnLoadEntityData?.Invoke(entity, entityData);
	}

	#endregion

	#region Event Handlers

	private void ChangedToPlayMode(bool isEditMode)
	{
		if(!isEditMode)
			HasExitedEditorMode = true;
	}

	private void OnSceneChanged()
	{
		HasExitedEditorMode = false;
	}

	#endregion

	#region Scene Save/Load Methods

	/// <summary>
	/// Saves the current scene changes.
	/// </summary>
	public async Task SaveSceneChangesAsync()
	{
		await SaveSceneDataAsync(Core.Scene, HasExitedEditorMode);

		if (HasExitedEditorMode)
		{
			NotificationSystem.ShowTimedNotification(
				"WARNING. Only saved EntityData, without Transform and Component data. Must Reset the Scene to save current state!");
		}
		else
		{
			NotificationSystem.ShowTimedNotification(
				$"Successfully saved {Core.Scene.SceneData?.Name ?? "Scene"} data");
		}

		EditorChangeTracker.ClearOnSave();
	}

	/// <summary>
	/// Collects all serialized components from an entity, including both the live
	/// Components list and the pending ComponentsToAdd list.
	/// </summary>
	private static IEnumerable<Component> GetAllSerializedComponents(Entity entity)
	{
		return entity.Components
			.Concat(entity.ComponentsToAdd)
			.Where(c => c.IsSerialized);
	}

	/// <summary>The entry a save would write for this component, or null when it carries no data.</summary>
	internal static ComponentDataEntry? SerializeComponentEntry(Component component)
	{
		var entry = SerializeComponent(component);
		return string.IsNullOrEmpty(entry.Json) ? null : entry;
	}

	/// <summary>
	/// Serializes a single component into a ComponentDataEntry.
	/// If the component's Data property returns null but the component's runtime type
	/// comes from a DynamicScripts assembly, this is likely a source-generator failure
	/// and a diagnostic warning is logged.
	/// </summary>
	private static ComponentDataEntry SerializeComponent(Component component)
	{
		var data = component.Data;

		if (data != null)
		{
			// For script-generated component data, don't write the JSON "$type" property.
			// We already store the component data's type in `DataTypeName` (no assembly info).
			// Leaving "$type" in the JSON can lock deserialization to an old DynamicScripts
			// assembly after a recompile, so omit it for script types.
			var isDynamicScriptType = data.GetType().Assembly.GetName().Name
				?.StartsWith("DynamicScripts") == true;

			var componentJsonSettings = new JsonSettings
			{
				PrettyPrint = true,
				TypeNameHandling = isDynamicScriptType ? TypeNameHandling.None : TypeNameHandling.Auto,
				PreserveReferencesHandling = false
			};

			var json = Json.ToJson(data, componentJsonSettings);

			var entry = new ComponentDataEntry
			{
				ComponentId = ComponentIdRegistry.GetIdForType(component.GetType()),
				ComponentTypeName = component.GetType().FullName,
				ComponentName = component.Name,
				DataTypeName = data.GetType().FullName,
				Json = json
			};

			return entry;
		}

		// Data is null — check if this is unexpected (script component that should
		// have had a source-generated Data override but doesn't).
		var componentAssembly = component.GetType().Assembly;
		var assemblyName = componentAssembly.GetName().Name;
		if (assemblyName != null && assemblyName.StartsWith("DynamicScripts"))
		{
			var hasDataOverride = component.GetType().GetProperty("Data",
				System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
				System.Reflection.BindingFlags.DeclaredOnly) != null;

			if (!hasDataOverride)
			{
				Debug.Warn($"[Serialization] Component '{component.Name}' ({component.GetType().FullName}) " +
				           $"has no Data override. The source generator may not have run. " +
				           $"Ensure the class is marked 'partial' and Voltage.SourceGenerators.dll is present. " +
				           $"Assembly: {componentAssembly.GetName().Name}");
			}
			else
			{
				Debug.Warn($"[Serialization] Component '{component.Name}' ({component.GetType().FullName}) " +
				           $"has a Data override but it returned null. Check the generated Data getter.");
			}
		}

		return new ComponentDataEntry
		{
			ComponentId = ComponentIdRegistry.GetIdForType(component.GetType()),
			ComponentTypeName = component.GetType().FullName,
			ComponentName = component.Name,
			DataTypeName = null,
			Json = null
		};
	}

	/// <summary>
	/// Saves scene data to file asynchronously.
	/// </summary>
	public async Task SaveSceneDataAsync(Scene scene, bool ignoreEntityTransform)
	{
		ComponentDataSerializationBootstrap.EnsureInitialized();
		var filePath = SceneManager.Instance.CurrentScenePath;

		if (string.IsNullOrEmpty(filePath))
		{
			Debug.Error("Cannot save scene: No file path is set");
			throw new InvalidOperationException("Cannot save scene without a file path. Create a scene file first.");
		}

		// Validate the save target belongs to the current project to prevent
		// cross-project contamination when projects are switched.
		if (!ProjectManager.Instance.IsPathInCurrentProject(filePath))
		{
			Debug.Error(
				$"Cannot save scene: target path '{filePath}' does not belong to the current project '{ProjectManager.Instance.CurrentProject?.ProjectName}'.");
			throw new InvalidOperationException(
				$"Scene save path mismatch. Target '{filePath}' is outside the current project. " +
				"This usually means the project was changed without clearing the scene state.");
		}

		SceneData oldSceneData = null;
		var oldDataExists = File.Exists(filePath);

		if (oldDataExists)
		{
			try
			{
				oldSceneData = AotDeserializers.DeserializeSceneData(File.ReadAllText(filePath));
			}
			catch
			{
				oldSceneData = null;
			}
		}

		var newSceneData = new SceneData();

		// Preserve scene metadata
		if (scene.SceneData != null)
		{
			newSceneData.Name = scene.SceneData.Name;
			newSceneData.FilePath = filePath;
			newSceneData.CreatedAt = scene.SceneData.CreatedAt;
			newSceneData.ModifiedAt = DateTime.Now;
			newSceneData.FormatVersion = Voltage.Data.SceneData.CurrentFormatVersion;
			newSceneData.TiledMapFileName = scene.SceneData.TiledMapFileName;
			newSceneData.EditorData = scene.SceneData.EditorData;
		}
		else if (oldSceneData != null)
		{
			newSceneData.Name = oldSceneData.Name;
			newSceneData.FilePath = filePath;
			newSceneData.CreatedAt = oldSceneData.CreatedAt;
			newSceneData.ModifiedAt = DateTime.Now;
			newSceneData.FormatVersion = Voltage.Data.SceneData.CurrentFormatVersion;
			newSceneData.TiledMapFileName = oldSceneData.TiledMapFileName;
			newSceneData.EditorData = oldSceneData.EditorData;
		}
		else
		{
			newSceneData.Name = Path.GetFileNameWithoutExtension(filePath);
			newSceneData.FilePath = filePath;
			newSceneData.CreatedAt = DateTime.Now;
			newSceneData.ModifiedAt = DateTime.Now;
			newSceneData.FormatVersion = Voltage.Data.SceneData.CurrentFormatVersion;
		}

		var oldEntitiesByName = new Dictionary<string, SceneData.SceneEntityData>(StringComparer.OrdinalIgnoreCase);

		if (oldSceneData?.Entities != null)
		{
			foreach (var entity in oldSceneData.Entities)
				oldEntitiesByName[entity.Name] = entity;
		}

		for (int i = 0; i < scene.Entities.Count; i++)
		{
			var entity = scene.Entities[i];
			SceneData.SceneEntityData oldEntityData;
			var hasOldData = oldEntitiesByName.TryGetValue(entity.Name, out oldEntityData);

			Vector2 positionToSave;
			float rotationToSave;
			Vector2 scaleToSave;

			if (ignoreEntityTransform && hasOldData)
			{
				positionToSave = oldEntityData.Position;
				rotationToSave = oldEntityData.Rotation;
				scaleToSave = oldEntityData.Scale;
			}
			else
			{
				// Use current transform values (either in Edit Mode or no old data exists)
				if (entity.Transform.Parent != null)
				{
					// Entity has a parent - save LOCAL transform values
					positionToSave = entity.Transform.LocalPosition;
					rotationToSave = entity.Transform.LocalRotation;
					scaleToSave = entity.Transform.LocalScale;
				}
				else
				{
					// Entity has no parent - save WORLD transform values
					positionToSave = entity.Transform.Position;
					rotationToSave = entity.Transform.Rotation;
					scaleToSave = entity.Transform.Scale;
				}
			}

			var sceneEntityData = new SceneData.SceneEntityData
			{
				Id = hasOldData && oldEntityData.Id != Guid.Empty ? oldEntityData.Id : Guid.NewGuid(),
				InstanceType = entity.Type,
				Name = entity.Name,
				Position = positionToSave,
				Rotation = rotationToSave,
				Scale = scaleToSave,
				ParentEntityName = ignoreEntityTransform && hasOldData
					? oldEntityData.ParentEntityName
					: entity.Transform.Parent?.Entity?.Name,
				ParentId = ignoreEntityTransform && hasOldData
					? oldEntityData.ParentId
					: (entity.Transform.Parent?.Entity != null && oldSceneData != null
						? oldSceneData.Entities.FirstOrDefault(e => string.Equals(e.Name,
							entity.Transform.Parent.Entity.Name, StringComparison.OrdinalIgnoreCase))?.Id
						: null),
				Enabled = entity.Enabled,
				UpdateOrder = entity.UpdateOrder,
				Tag = entity.Tag,
				IsSelectableInEditor = entity.CanBeSelected,
				DebugRenderEnabled = entity.DebugRenderEnabled,
				OriginalPrefabName = entity.OriginalPrefabName,
				// Phase 3+: persist the stable prefab GUID (was previously omitted here).
				OriginalPrefabGuid = entity.OriginalPrefabGuid != Guid.Empty
					? entity.OriginalPrefabGuid
					: (Guid?)null
			};

			var entityData = entity.GetEntityData();

			// Component data handling based on PlayMode
			if (ignoreEntityTransform && hasOldData && oldEntityData.EntityData != null)
			{
				// Play-mode save: use old data as base, but update ONLY IPlayModeSaveableComponent
				// components. Preserve any Phase 4b override metadata from the old data so
				// round-tripping through play mode does not strip the delta.
				var oldEntityDataClone = oldEntityData.EntityData.Clone();

				if (entityData != null)
				{
					var updatedComponentDataList = new List<ComponentDataEntry>();

					if (oldEntityDataClone.ComponentDataList != null)
					{
						foreach (var oldEntry in oldEntityDataClone.ComponentDataList)
						{
							var isSaveableComponent = Type.GetType(oldEntry.ComponentTypeName) is { } componentType &&
							                          typeof(IPlayModeSaveableComponent)
								                          .IsAssignableFrom(componentType);

							if (!isSaveableComponent)
							{
								updatedComponentDataList.Add(oldEntry);
							}
						}
					}

					foreach (var component in GetAllSerializedComponents(entity))
					{
						if (component is IPlayModeSaveableComponent && component.Data != null)
						{
							updatedComponentDataList.Add(SerializeComponent(component));
						}
					}

					oldEntityDataClone.ComponentDataList = updatedComponentDataList;
				}

				sceneEntityData.EntityData = oldEntityDataClone;
				// Preserve Phase 4b override metadata from the on-disk entry (play-mode save should
				// not strip it — we are not re-diffing against the prefab here).
				sceneEntityData.PrefabOverrides          = oldEntityData.PrefabOverrides;
				sceneEntityData.RemovedPrefabComponents  = oldEntityData.RemovedPrefabComponents;
			}
			else
			{
				if (entityData != null)
				{
					// Components whose type is unknown in this editor session (plugin not installed,
					// script missing) have no live instance to serialize — capture their raw entries
					// BEFORE overwriting the list, or a teammate without the plugin would silently
					// destroy them on every save. They round-trip byte-identical; only the explicit
					// Remove on the inspector's missing-component card ever drops them.
					var preservedEntries = Scene.CollectUnresolvableEntries(entityData.ComponentDataList);

					var currentEntries = new List<ComponentDataEntry>();
					foreach (var component in GetAllSerializedComponents(entity))
						currentEntries.Add(SerializeComponent(component));

					currentEntries.AddRange(preservedEntries);

					// Prefab instances are saved self-contained (full component data) so a scene
					// reload never depends on resolving the source .vprefab. OriginalPrefabName/Guid
					// keep the prefab link; PrefabOverrides stays null (full-data entry).
					entityData.ComponentDataList = currentEntries;
					sceneEntityData.EntityData   = entityData;
					sceneEntityData.PrefabOverrides = null;
				}
			}

			newSceneData.Entities.Add(sceneEntityData);
		}

		// Build SceneComponent data — only include components marked as serialized
		newSceneData.SceneComponents.Clear();

		for (var i = 0; i < scene._sceneComponents.Length; i++)
		{
			var sc = scene._sceneComponents.Buffer[i];
			if (!sc.IsSerialized)
				continue;

			var isEngineType = sc.GetType().Assembly == typeof(Component).Assembly;

			var scEntry = new SceneComponentDataEntry
			{
				ComponentId        = ComponentIdRegistry.GetIdForType(sc.GetType()),
				ComponentTypeName = sc.GetType().FullName,
				ComponentName     = sc.Name
			};

			if (sc.Data != null)
			{
				var isDynamicScriptType = sc.Data.GetType().Assembly.GetName().Name
					?.StartsWith("DynamicScripts") == true;

				var scJsonSettings = new JsonSettings
				{
					PrettyPrint = true,
					TypeNameHandling = (isEngineType && !isDynamicScriptType)
						? TypeNameHandling.Auto
						: TypeNameHandling.None,
					PreserveReferencesHandling = false
				};

				scEntry.DataTypeName = sc.Data.GetType().FullName;
				scEntry.Json = Json.ToJson(sc.Data, scJsonSettings);
			}

			newSceneData.SceneComponents.Add(scEntry);
		}

		// Preserve scene components whose type is unknown in this session (plugin/script missing) —
		// same data-loss guard as entity components: they have no live instance, so the rebuild above
		// would silently drop them from the saved scene.
		if (oldSceneData?.SceneComponents != null)
		{
			foreach (var oldEntry in oldSceneData.SceneComponents)
			{
				var hasIdentity = !string.IsNullOrEmpty(oldEntry.ComponentId) || !string.IsNullOrEmpty(oldEntry.ComponentTypeName);
				var resolvable =
					(!string.IsNullOrEmpty(oldEntry.ComponentId) && ComponentIdRegistry.TryGetType(oldEntry.ComponentId, out _)) ||
					(!string.IsNullOrEmpty(oldEntry.ComponentTypeName) && ResolveType(oldEntry.ComponentTypeName) != null);

				if (hasIdentity && !resolvable)
					newSceneData.SceneComponents.Add(oldEntry);
			}
		}

		var settings = new JsonSettings
		{
			PrettyPrint = true,
			TypeNameHandling = TypeNameHandling.Auto,
			PreserveReferencesHandling = false
		};

		try
		{
			var jsonData = Json.ToJson(newSceneData, settings);
			await File.WriteAllTextAsync(filePath, jsonData);
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to save scene: {ex.Message}");
		}
	}

	/// <summary>
	/// Loads scene data from a .vscene file path.
	/// </summary>
	/// <param name="sceneFilePath">Full path to the .vscene file</param>
	/// <returns>Deserialized SceneData or null if failed</returns>
	public SceneData LoadSceneData(string sceneFilePath)
	{
		if (string.IsNullOrWhiteSpace(sceneFilePath))
		{
			Debug.Error("Scene file path cannot be null or empty");
			return null;
		}

		if (!File.Exists(sceneFilePath))
		{
			Debug.Error($"Scene file not found: {sceneFilePath}");
			return null;
		}

		try
		{
			var fileContent = File.ReadAllText(sceneFilePath);
			var sceneData = AotDeserializers.DeserializeSceneData(fileContent);

			if (sceneData == null)
			{
				throw new Exception($"Failed to deserialize scene data from: {sceneFilePath}");
			}
			
			sceneData.FilePath = sceneFilePath;

			Debug.Log($"Successfully loaded scene data: {sceneData.Name} from {sceneFilePath}");
			return sceneData;
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to load scene data from '{sceneFilePath}': {ex.Message}");
			Debug.Error($"Stack trace: {ex.StackTrace}");
			return null;
		}
	}

	/// <summary>
	/// Loads scene data by scene name (searches in the project's Scenes folder).
	/// </summary>
	/// <param name="sceneName">Name of the scene (without .vscene extension)</param>
	/// <returns>Deserialized SceneData or null if failed</returns>
	public SceneData LoadSceneDataByName(string sceneName)
	{
		if (string.IsNullOrWhiteSpace(sceneName))
		{
			Debug.Error("Scene name cannot be null or empty");
			return null;
		}

		if (!ProjectManager.Instance.HasActiveProject)
		{
			Debug.Error("No active project. Cannot load scene.");
			return null;
		}

		var sceneFilePath = Path.Combine(
			ProjectManager.Instance.CurrentProject.ScenesFolder,
			$"{sceneName}.vscene"
		);

		return LoadSceneData(sceneFilePath);
	}

	#endregion

	#region Entity Data Loading

	/// <summary>
	/// Creates a component instance by type name.
	/// Uses ComponentAotFactory first (NativeAOT-safe), then falls back to reflection in editor.
	/// </summary>
	private static Component CreateComponentInstance(string componentTypeName, string componentId = null)
	{
		// GUID-first resolution: the stable [ComponentId] identity survives class/namespace renames.
		if (!string.IsNullOrEmpty(componentId) &&
		    ComponentIdRegistry.TryGetType(componentId, out var guidType) &&
		    guidType?.FullName != null)
		{
			if (ComponentAotFactory.IsRegistered(guidType.FullName))
				return (Component)ComponentAotFactory.Create(guidType.FullName);
			return (Component)Activator.CreateInstance(guidType);
		}

		if (string.IsNullOrEmpty(componentTypeName))
			return null;

		// AOT path: try the pre-registered factory first
		if (ComponentAotFactory.IsRegistered(componentTypeName))
		{
			return (Component)ComponentAotFactory.Create(componentTypeName);
		}

		// Editor fallback: resolve via reflection (allows hot-reloaded script types)
		var componentType = ResolveType(componentTypeName);
		if (componentType == null)
		{
			Debug.Error($"Could not find component type: {componentTypeName}");
			return null;
		}

		return (Component)Activator.CreateInstance(componentType);
	}

	/// <summary>
	/// Public façade for <see cref="CreateComponentInstance"/> used by the Phase 4b revert UI
	/// (<see cref="Voltage.Editor.Windows.EntityInspectorWindow"/>).
	/// </summary>
	public static Component CreateComponentInstancePublic(string componentTypeName, string componentId = null)
		=> CreateComponentInstance(componentTypeName, componentId);

	/// <summary>
	/// Applies a single <see cref="ComponentDataEntry"/> to a live <see cref="Component"/> by
	/// deserializing the entry's JSON via the AOT/reflection path used everywhere else.
	/// Called by the Phase 4b per-component revert UI.
	/// </summary>
	public void ApplyComponentEntry(Component component, ComponentDataEntry entry)
	{
		if (component == null || string.IsNullOrEmpty(entry.DataTypeName) || string.IsNullOrEmpty(entry.Json))
			return;

		// Re-use the same deserialization path as TryAssignComponentDataFromEntityData,
		// but applied directly rather than scanning the EntityData list.
		try
		{
			ComponentData data = null;
			var typeName = entry.DataTypeName;

			if (!ComponentDataAotDeserializer.IsRegistered(typeName) &&
			    TypeRenameRegistry.TryResolve(typeName, out var renamed))
				typeName = renamed.FullName ?? typeName;

			if (ComponentDataAotDeserializer.IsRegistered(typeName))
			{
				var clean = StripTypeFieldFromJson(entry.Json);
				data = ComponentDataAotDeserializer.TryDeserialize(typeName, clean);
			}
			else
			{
				var dataType = ResolveType(entry.DataTypeName);
				if (dataType != null)
				{
					var clean = StripTypeFieldFromJson(entry.Json);
					data = (ComponentData)Json.FromJson(clean, dataType);
				}
			}

			if (data != null)
				component.Data = data;
		}
		catch (Exception ex)
		{
			Debug.Error($"[SerializationManager] ApplyComponentEntry failed for '{component.Name}': {ex.Message}");
		}
	}

	/// <summary>
	/// Loads predefined entity data onto an entity.
	/// </summary>
	public void LoadPredefinedEntityData(Entity newEntity, SceneData.SceneEntityData entityData)
	{
		newEntity.Name = entityData.Name;
		newEntity.SetTag(entityData.Tag);
		newEntity.Enabled = entityData.Enabled;
		newEntity.UpdateOrder = entityData.UpdateOrder;
		newEntity.DebugRenderEnabled = entityData.DebugRenderEnabled;
		newEntity.Type = entityData.InstanceType;

		if (newEntity.Type == Entity.InstanceType.SerializedPrefab)
		{
			newEntity.OriginalPrefabName = entityData.OriginalPrefabName;
			newEntity.OriginalPrefabGuid = entityData.OriginalPrefabGuid ?? Guid.Empty;
		}
		else
		{
			newEntity.OriginalPrefabName = null;
		}

		// Handle transform and parent assignment
		Entity parentEntity = null;
		if (entityData.ParentId.HasValue && newEntity.Scene?.SceneData?.Entities != null)
		{
			var parentSceneData =
				newEntity.Scene.SceneData.Entities.FirstOrDefault(e => e.Id == entityData.ParentId.Value);
			if (parentSceneData != null)
				parentEntity = newEntity.Scene.FindEntity(parentSceneData.Name);
		}

		if (parentEntity == null && !string.IsNullOrEmpty(entityData.ParentEntityName))
			parentEntity = newEntity.Scene?.FindEntity(entityData.ParentEntityName);

		if (parentEntity != null)
		{
			newEntity.Transform.SetParent(parentEntity.Transform);
			newEntity.Transform.SetLocalPosition(entityData.Position);
			newEntity.Transform.SetLocalRotation(entityData.Rotation);
			newEntity.Transform.SetLocalScale(entityData.Scale);
		}
		else
		{
			if (entityData.ParentId.HasValue)
				newEntity.SetData("_PendingParentId", entityData.ParentId.Value);
			else if (!string.IsNullOrEmpty(entityData.ParentEntityName))
				newEntity.SetData("_PendingParentName", entityData.ParentEntityName);

			newEntity.SetData("_PendingLocalPosition", entityData.Position);
			newEntity.SetData("_PendingLocalRotation", entityData.Rotation);
			newEntity.SetData("_PendingLocalScale", entityData.Scale);

			newEntity.Transform.Position = entityData.Position;
			newEntity.Transform.Rotation = entityData.Rotation;
			newEntity.Transform.Scale = entityData.Scale;
		}

		if (entityData.EntityData != null)
		{
			// Clone directly — no JSON round-trip needed. The EntityData was
			// already deserialized by AotDeserializers.
			var clonedEntityData = entityData.EntityData.Clone();

			newEntity.SetEntityData(clonedEntityData);

			// Instantiate components from ComponentDataList, but skip any that
			// already exist on the entity (e.g. Camera on SceneRequired entities).
			if (clonedEntityData.ComponentDataList != null)
			{
				foreach (var componentEntry in clonedEntityData.ComponentDataList)
				{
					bool alreadyExists = false;
					foreach (var existing in newEntity.Components)
					{
						if (existing.Name == componentEntry.ComponentName)
						{
							alreadyExists = true;
							break;
						}
					}

					if (!alreadyExists)
					{
						foreach (var existing in newEntity.ComponentsToAdd)
						{
							if (existing.Name == componentEntry.ComponentName)
							{
								alreadyExists = true;
								break;
							}
						}
					}

					if (alreadyExists)
						continue;

					try
					{
						var component = CreateComponentInstance(componentEntry.ComponentTypeName, componentEntry.ComponentId);
						if (component == null)
						{
							Debug.Error($"Could not create component: {componentEntry.ComponentTypeName}");
							continue;
						}

						component.Name = componentEntry.ComponentName;
						component.SetSerialized(true);
						newEntity.AddComponent(component, true);
					}
					catch (Exception ex)
					{
						Debug.Error(
							$"Failed to instantiate component {componentEntry.ComponentTypeName}: {ex.Message}");
					}
				}
			}

			var processedComponents = new HashSet<string>();

			// Assign data to pending components
			foreach (var comp in newEntity.ComponentsToAdd)
			{
				if (TryAssignComponentDataFromEntityData(newEntity, comp))
				{
					var componentId = $"{comp.GetType().FullName}:{comp.Name}";
					processedComponents.Add(componentId);
				}
			}

			// Also assign data to already-live components (e.g. Camera on SceneRequired)
			foreach (var comp in newEntity.Components)
			{
				var componentId = $"{comp.GetType().FullName}:{comp.Name}";
				if (!processedComponents.Contains(componentId))
				{
					if (TryAssignComponentDataFromEntityData(newEntity, comp))
						processedComponents.Add(componentId);
				}
			}

			// Register callback for components added later
			newEntity.OnComponentAdded<Component>(comp =>
			{
				var componentId = $"{comp.GetType().FullName}:{comp.Name}";

				if (!processedComponents.Contains(componentId))
				{
					TryAssignComponentDataFromEntityData(newEntity, comp);
				}
			});
		}
	}

	/// <summary>
	/// Helper to deep-clone a ComponentDataEntry.
	/// </summary>
	private ComponentDataEntry CloneComponentDataEntry(ComponentDataEntry entry)
	{
		var json = Json.ToJson(entry, true);
		return Json.FromJson<ComponentDataEntry>(json);
	}

	/// <summary>
	/// Resolves a type by its full name, searching all loaded assemblies if Type.GetType() fails.
	/// When a LatestScriptAssembly is set, it is checked first to ensure newly compiled script types
	/// take priority over stale types from previously loaded assemblies.
	/// On a final miss, consults <see cref="TypeRenameRegistry"/> so that component
	/// classes whose fully-qualified name changed (class or namespace rename) still resolve correctly
	/// from serialized scene/prefab data.
	/// </summary>
	private static Type ResolveType(string typeName)
	{
		if (string.IsNullOrEmpty(typeName))
			return null;

		var type = Type.GetType(typeName);
		if (type != null)
			return type;

		// Check the latest script assembly first to ensure newly compiled types take priority
		// over stale types from old assemblies that are still loaded in the AppDomain.
		if (Core.LatestScriptAssembly != null)
		{
			type = Core.LatestScriptAssembly.GetType(typeName);
			if (type != null)
				return type;
		}

		// Fall back to searching all loaded assemblies but skip stale DynamicScripts
		// assemblies. Only the LatestScriptAssembly is authoritative for script types.
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			var assemblyName = assembly.GetName().Name;
			if (assemblyName != null && assemblyName.StartsWith("DynamicScripts"))
				continue;

			type = assembly.GetType(typeName);
			if (type != null)
				return type;
		}

		// Last resort: check the rename registry (populated by [FormerlyKnownAs] at module init).
		// This is the only path that changes behavior for renamed types — all direct lookups
		// above are identical to the pre-Phase-4a code.
		if (TypeRenameRegistry.TryResolve(typeName, out var renamedType))
		{
			Debug.Warn($"[Editor] Type '{typeName}' resolved via TypeRenameRegistry → '{renamedType.FullName}'. " +
				"Re-save the scene/prefab to update the stored type name.");
			return renamedType;
		}

		return null;
	}

	/// <summary>
	/// Tries to assign component data from entity data using Voltage.Persistence.Json.
	/// The generated Data property setter handles field assignment; the JSON layer just
	/// needs to deserialize the ComponentData subclass via the type name in the entry.
	/// </summary>
	[DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor,
		typeof(List<SceneData.SceneEntityData>))]
	[DynamicDependency(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor, typeof(ComponentData))]
	private bool TryAssignComponentDataFromEntityData(Entity entity, Component component)
	{
		ComponentDataSerializationBootstrap.EnsureInitialized();
		var entityData = entity.GetEntityData();

		if (entityData == null || entityData.ComponentDataList == null)
			return false;

		for (int i = entityData.ComponentDataList.Count - 1; i >= 0; i--)
		{
			var entry = entityData.ComponentDataList[i];

			if (component.Name == entry.ComponentName)
			{
				// Data-less component
				if (string.IsNullOrWhiteSpace(entry.DataTypeName))
				{
					entityData.ComponentDataList.RemoveAt(i);
					entity.SetEntityData(entityData);
					return true;
				}

				try
				{
					ComponentData data = null;

					// Prefer the AOT deserializer: it keys by FullName only, so it is
					// immune to the stale assembly name baked into the JSON $type field
					// by TypeNameHandling.Auto after a script recompile.
					var aotDataTypeName = entry.DataTypeName;

					// If the stored DataTypeName is unknown to the AOT registry, check whether
					// it is a renamed type and use the current name instead (Phase 4a rename path).
					if (!ComponentDataAotDeserializer.IsRegistered(aotDataTypeName) &&
					    TypeRenameRegistry.TryResolve(aotDataTypeName, out var renamedDataType))
					{
						aotDataTypeName = renamedDataType.FullName ?? aotDataTypeName;
					}

					if (ComponentDataAotDeserializer.IsRegistered(aotDataTypeName))
					{
						// Strip any stale $type from the JSON before passing to the AOT
						// deserializer. The AOT deserializer uses a strongly-typed
						// Json.FromJson<T>, so a stale $type would still win if left in.
						var cleanJson = StripTypeFieldFromJson(entry.Json);
						data = ComponentDataAotDeserializer.TryDeserialize(aotDataTypeName, cleanJson);
					}
					else
					{
						var dataType = ResolveType(entry.DataTypeName);
						if (dataType != null)
						{
							// Strip the stale $type so the JSON deserializer binds to
							// the freshly resolved dataType rather than the old assembly type.
							var cleanJson = StripTypeFieldFromJson(entry.Json);
							data = (ComponentData)Json.FromJson(cleanJson, dataType);
						}
						else
						{
							Debug.Warn(
								$"Could not resolve component data type '{entry.DataTypeName}' for component '{component.Name}'. " +
								"The data will be skipped. Re-save the scene to clear stale entries.");
							entityData.ComponentDataList.RemoveAt(i);
							entity.SetEntityData(entityData);
							return true;
						}
					}

					component._pendingLoadedData = data;
					component.Data = data;

					entityData.ComponentDataList.RemoveAt(i);
					entity.SetEntityData(entityData);
					return true;
				}
				catch (Exception ex)
				{
					Debug.Error($"Error loading component data for {component.Name}: {ex.Message}");
					return false;
				}
			}
		}

		return false;
	}

	#endregion

	#region Prefab Methods

	// Phase 4b helpers — prefab diff / delta computation

	/// <summary>
	/// Resolves the source prefab <c>.vprefab</c> file for a <see cref="Entity.InstanceType.SerializedPrefab"/>
	/// entity and returns its deserialized <see cref="PrefabData"/>, or null when the prefab cannot be found.
	/// <para>Resolution order: AssetDatabase GUID lookup (editor) → name-based search under PrefabsFolder.</para>
	/// </summary>
	private PrefabData? TryLoadSourcePrefabForDiff(string prefabName, Guid prefabGuid)
	{
		if (string.IsNullOrEmpty(prefabName))
			return null;

		string resolvedPath = null;

		// 1. GUID-based lookup via AssetDatabase (editor path — survives renames).
		if (prefabGuid != Guid.Empty && Assets.AssetDatabase.Instance != null)
			resolvedPath = Assets.AssetDatabase.Instance.GetPath(prefabGuid);

		// 2. Name-based fallback — scan PrefabsFolder subdirectories.
		if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
		{
			var prefabsDir = ProjectManager.Instance.CurrentProject?.PrefabsFolder;
			if (!string.IsNullOrEmpty(prefabsDir) && Directory.Exists(prefabsDir))
			{
				// Direct match at root.
				var direct = Path.Combine(prefabsDir, prefabName + ".vprefab");
				if (File.Exists(direct))
				{
					resolvedPath = direct;
				}
				else
				{
					foreach (var sub in Directory.GetDirectories(prefabsDir))
					{
						var candidate = Path.Combine(sub, prefabName + ".vprefab");
						if (File.Exists(candidate)) { resolvedPath = candidate; break; }
						candidate = Path.Combine(sub, prefabName + ".prefab");
						if (File.Exists(candidate)) { resolvedPath = candidate; break; }
					}
				}
			}
		}

		if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
			return null;

		try
		{
			var json = File.ReadAllText(resolvedPath);
			var pd = AotDeserializers.DeserializePrefabData(json);
			return pd.Name != null ? pd : (PrefabData?)null;
		}
		catch (Exception ex)
		{
			Debug.Warn($"[SerializationManager] Could not load source prefab '{resolvedPath}': {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Computes the Phase 4b override delta between the source prefab's component list and the
	/// current instance's serialized component entries.
	/// </summary>
	/// <param name="prefabEntityData">Source prefab component list (may be null for an empty prefab).</param>
	/// <param name="currentEntries">Fully serialized current instance component entries.</param>
	/// <param name="overrideNames">
	/// Output: component names that differ from the prefab (data changed) or are new (not in prefab).
	/// These are stored in <see cref="SceneData.SceneEntityData.PrefabOverrides"/>.
	/// </param>
	/// <param name="removedNames">
	/// Output: component names that exist in the prefab but were removed on this instance.
	/// </param>
	/// <param name="overrideEntries">
	/// Output: the subset of <paramref name="currentEntries"/> whose names appear in <paramref name="overrideNames"/>.
	/// Only the overriding data is stored in <see cref="SceneData.SceneEntityData.EntityData"/>; the rest
	/// is re-sourced from the prefab at load time.
	/// </param>
	private static void BuildPrefabOverrideDelta(
		EntityData prefabEntityData,
		List<ComponentDataEntry> currentEntries,
		out HashSet<string> overrideNames,
		out List<string> removedNames,
		out List<ComponentDataEntry> overrideEntries)
	{
		overrideNames  = new HashSet<string>(StringComparer.Ordinal);
		removedNames   = new List<string>();
		overrideEntries = new List<ComponentDataEntry>();

		// Build lookup of prefab component entries by name.
		var prefabByName = new Dictionary<string, ComponentDataEntry>(StringComparer.Ordinal);
		if (prefabEntityData?.ComponentDataList != null)
		{
			foreach (var e in prefabEntityData.ComponentDataList)
				prefabByName[e.ComponentName] = e;
		}

		// Build lookup of current (instance) entries by name.
		var currentByName = new Dictionary<string, ComponentDataEntry>(StringComparer.Ordinal);
		foreach (var e in currentEntries)
			currentByName[e.ComponentName] = e;

		// Detect changed / added components.
		foreach (var current in currentEntries)
		{
			if (!prefabByName.TryGetValue(current.ComponentName, out var prefabEntry))
			{
				// Added on instance — always an override.
				overrideNames.Add(current.ComponentName);
				overrideEntries.Add(current);
			}
			else if (!ComponentDataJsonEquals(prefabEntry, current))
			{
				// Differs from prefab — override.
				overrideNames.Add(current.ComponentName);
				overrideEntries.Add(current);
			}
			// Identical to prefab — inherited, do not include in overrideEntries.
		}

		// Detect removed components (in prefab but not in current instance).
		foreach (var name in prefabByName.Keys)
		{
			if (!currentByName.ContainsKey(name))
				removedNames.Add(name);
		}
	}

	/// <summary>
	/// Returns true when two <see cref="ComponentDataEntry"/> values represent the same component
	/// data, using a normalised JSON string comparison.  Two entries that were both serialised
	/// from the same data should produce identical JSON; we normalise whitespace to absorb
	/// pretty-print differences (the prefab is always pretty-printed; instance data also).
	/// </summary>
	private static bool ComponentDataJsonEquals(ComponentDataEntry a, ComponentDataEntry b)
	{
		// If both are data-less, they are equal.
		if (string.IsNullOrEmpty(a.Json) && string.IsNullOrEmpty(b.Json))
			return string.Equals(a.ComponentTypeName, b.ComponentTypeName, StringComparison.Ordinal) &&
			       string.Equals(a.DataTypeName, b.DataTypeName, StringComparison.Ordinal);

		// Type mismatch — definitely different.
		if (!string.Equals(a.ComponentTypeName, b.ComponentTypeName, StringComparison.Ordinal) ||
		    !string.Equals(a.DataTypeName, b.DataTypeName, StringComparison.Ordinal))
			return false;

		// Normalise whitespace before comparing JSON strings to absorb pretty-print differences.
		var normA = NormaliseJson(a.Json);
		var normB = NormaliseJson(b.Json);
		return string.Equals(normA, normB, StringComparison.Ordinal);
	}

	/// <summary>
	/// Collapses all runs of whitespace (space, tab, newline) in a JSON string to a single space
	/// and trims the result.  This is intentionally naive — it only normalises inter-token
	/// whitespace and does not handle whitespace inside string values.  Since component data
	/// field values are almost always numbers, booleans, or short strings without embedded
	/// whitespace sequences, this is safe for the diff comparison use-case.
	/// </summary>
	private static string NormaliseJson(string json)
	{
		if (string.IsNullOrEmpty(json))
			return string.Empty;

		return System.Text.RegularExpressions.Regex.Replace(json, @"\s+", " ").Trim();
	}

	/// <summary>
	/// Saves a prefab entity to a JSON file.
	/// </summary>
	public async Task<bool> SavePrefabDataAsync(Entity prefabEntity, bool overrideExistingPrefab = false)
	{
		if (prefabEntity == null)
		{
			Debug.Error("Entity is null");
			return false;
		}

		if (prefabEntity.Type != Entity.InstanceType.SerializedPrefab)
		{
			Debug.Error("Entity is not a SerializedPrefab type!");
			return false;
		}

		try
		{
			var prefabsDirectory = $"{ProjectManager.Instance.CurrentProject.PrefabsFolder}";
			Directory.CreateDirectory(prefabsDirectory);

			var prefabFileName = !string.IsNullOrEmpty(prefabEntity.OriginalPrefabName)
				? prefabEntity.OriginalPrefabName
				: prefabEntity.Name;

			var sourceFilePath = CrossPlatformPath.Combine(prefabsDirectory, $"{prefabFileName}.vprefab");

			if (File.Exists(sourceFilePath) && !overrideExistingPrefab)
			{
				Debug.Error($"SerializedPrefab with name '{prefabEntity.Name}' already exists!");
				return false;
			}

			var prefabData = new PrefabData
			{
				Id = prefabEntity.PersistentId,
				InstanceType = prefabEntity.Type,
				Name = prefabEntity.Name,
				Rotation = prefabEntity.Transform.Rotation,
				Scale = prefabEntity.Transform.Scale,
				Enabled = prefabEntity.Enabled,
				UpdateOrder = prefabEntity.UpdateOrder,
				Tag = prefabEntity.Tag,
				DebugRenderEnabled = prefabEntity.DebugRenderEnabled
			};

			var entityData = prefabEntity.GetEntityData().Clone();
			entityData.ComponentDataList.Clear();

			foreach (var component in GetAllSerializedComponents(prefabEntity))
			{
				entityData.ComponentDataList.Add(SerializeComponent(component));
			}

			prefabData.ChildEntities.Clear();
			foreach (var child in prefabEntity.Transform.Children)
			{
				var childEntity = child.Entity;
				if (childEntity.Type != Entity.InstanceType.NonSerialized)
				{
					var childEntityData = childEntity.GetEntityData().Clone();
					childEntityData.ComponentDataList.Clear();
					foreach (var childComp in GetAllSerializedComponents(childEntity))
						childEntityData.ComponentDataList.Add(SerializeComponent(childComp));

					var childData = new SceneData.SceneEntityData
					{
						// Persist the child's stable id so the prefab-instantiation remap can map
						// it to the new instance child's id and rewrite references that point to it.
						Id = childEntity.PersistentId,
						InstanceType = childEntity.Type,
						Name = childEntity.Name,
						Position = childEntity.Transform.LocalPosition,
						Rotation = childEntity.Transform.LocalRotation,
						Scale = childEntity.Transform.LocalScale,
						ParentEntityName = prefabEntity.Name,
						Enabled = childEntity.Enabled,
						UpdateOrder = childEntity.UpdateOrder,
						Tag = childEntity.Tag,
						DebugRenderEnabled = childEntity.DebugRenderEnabled,
						OriginalPrefabName = childEntity.OriginalPrefabName,
						EntityData = childEntityData
					};
					prefabData.ChildEntities.Add(childData);
				}
			}

			prefabData.EntityData = entityData;

			var settings = new JsonSettings
			{
				PrettyPrint = true,
				TypeNameHandling = TypeNameHandling.Auto,
				PreserveReferencesHandling = false
			};

			var jsonData = Json.ToJson(prefabData, settings);
			await File.WriteAllTextAsync(sourceFilePath, jsonData);

			if (Assets.AssetDatabase.Instance != null)
				prefabEntity.OriginalPrefabGuid = Assets.AssetDatabase.Instance.GetReference(sourceFilePath).Guid;

			return true;
		}
		catch (Exception ex)
		{
			Debug.Error($"Failed to save prefab {prefabEntity.Name}: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// Loads a prefab from an absolute file path.
	/// Used by the asset-drop path where the absolute path is already known, avoiding
	/// the name-based directory scan in <see cref="LoadPrefabData"/> that fails when
	/// the prefab is not in a one-level subdirectory of PrefabsFolder.
	/// </summary>
	public PrefabData? LoadPrefabDataFromPath(string absolutePath)
	{
		try
		{
			if (!File.Exists(absolutePath))
				throw new Exception($"Prefab file not found at path: {absolutePath}");

			var jsonContent = File.ReadAllText(absolutePath);

			if (string.IsNullOrWhiteSpace(jsonContent))
				throw new Exception($"Prefab file is empty: {absolutePath}");

			var prefabData = AotDeserializers.DeserializePrefabData(jsonContent);

			if (prefabData.Name == null)
				throw new Exception($"Invalid prefab data (missing Name) at: {absolutePath}");

			return prefabData;
		}
		catch (Exception ex)
		{
			throw new Exception($"Failed to load prefab from path '{absolutePath}': {ex.Message}");
		}
	}

	/// <summary>
	/// Loads a prefab from JSON file by name.
	/// </summary>
	public PrefabData? LoadPrefabData(string prefabName)
	{
		try
		{
			var prefabsDirectory = ProjectManager.Instance.CurrentProject.PrefabsFolder;
			Directory.CreateDirectory(prefabsDirectory);

			var entityTypeDirectories = Directory.GetDirectories(prefabsDirectory);

			foreach (var entityTypeDir in entityTypeDirectories)
			{
				var prefabFilePath = Path.Combine(entityTypeDir, $"{prefabName}.vprefab");

				if (File.Exists(prefabFilePath))
				{
					var jsonContent = File.ReadAllText(prefabFilePath);

					if (string.IsNullOrWhiteSpace(jsonContent))
					{
						throw new Exception($"SerializedPrefab file is empty: {prefabFilePath}");
					}

					var prefabData = AotDeserializers.DeserializePrefabData(jsonContent);

					if (prefabData.Name == null)
					{
						throw new Exception($"Invalid prefab data format for: {prefabName} - missing Name");
					}

					return prefabData;
				}
			}

			throw new Exception($"SerializedPrefab file not found: {prefabName}");
		}
		catch (Exception ex)
		{
			throw new Exception($"Failed to load prefab {prefabName}: {ex.Message}");
		}
	}

	/// <summary>
	/// Loads prefab entity data onto an entity.
	/// </summary>
	public void LoadPrefabEntityData(Entity newEntity, PrefabData prefabData)
	{
		newEntity.Name = prefabData.Name;
		newEntity.SetTag(prefabData.Tag);
		newEntity.Enabled = prefabData.Enabled;
		newEntity.UpdateOrder = prefabData.UpdateOrder;
		newEntity.DebugRenderEnabled = prefabData.DebugRenderEnabled;
		newEntity.Transform.Rotation = prefabData.Rotation;
		newEntity.Transform.Scale = prefabData.Scale;

		// old prefab id -> new instance id, built structurally (not by name, since instance
		// children get uniquified names). Used to remap intra-prefab references after load.
		var idMap = new Dictionary<Guid, Guid>();
		if (prefabData.Id != Guid.Empty)
			idMap[prefabData.Id] = newEntity.PersistentId;

		if (prefabData.EntityData != null)
		{
			var clonedEntityData = prefabData.EntityData.Clone();
			newEntity.SetEntityData(clonedEntityData);

			// Instantiate components from the prefab's ComponentDataList. SetEntityData only
			// stores the data; without this loop the root entity comes out component-less.
			if (clonedEntityData.ComponentDataList != null)
			{
				foreach (var componentEntry in clonedEntityData.ComponentDataList)
				{
					bool alreadyExists =
						newEntity.Components.Any(c => c.Name == componentEntry.ComponentName) ||
						newEntity.ComponentsToAdd.Any(c => c.Name == componentEntry.ComponentName);

					if (alreadyExists)
						continue;

					try
					{
						var component = CreateComponentInstance(componentEntry.ComponentTypeName, componentEntry.ComponentId);
						if (component == null)
						{
							Debug.Error($"Could not create component: {componentEntry.ComponentTypeName}");
							continue;
						}

						component.Name = componentEntry.ComponentName;
						component.SetSerialized(true);
						newEntity.AddComponent(component, true);
					}
					catch (Exception ex)
					{
						Debug.Error($"Failed to instantiate component {componentEntry.ComponentTypeName}: {ex.Message}");
					}
				}
			}

			var processedComponents = new HashSet<string>();

			foreach (var comp in newEntity.ComponentsToAdd)
			{
				if (TryAssignComponentDataFromEntityData(newEntity, comp))
				{
					var componentId = $"{comp.GetType().FullName}:{comp.Name}";
					processedComponents.Add(componentId);
				}
			}

			newEntity.OnComponentAdded<Component>(comp =>
			{
				var componentId = $"{comp.GetType().FullName}:{comp.Name}";

				if (!processedComponents.Contains(componentId))
				{
					TryAssignComponentDataFromEntityData(newEntity, comp);
				}
			});
		}

		// Create child entities
		if (prefabData.ChildEntities != null)
		{
			foreach (var childData in prefabData.ChildEntities)
			{
				if (childData.InstanceType == Entity.InstanceType.NonSerialized)
					continue;

				if (IsDuplicateChild(newEntity.Scene, childData))
					continue;

				var childEntity = new Entity();
				LoadPredefinedEntityData(childEntity, childData);
				newEntity.Scene.AddEntity(childEntity);
				childEntity.Transform.SetParent(newEntity.Transform);
				// childData stores LOCAL transform (see SavePrefabDataAsync); apply it after
				// parenting so the child follows the instance instead of keeping a stale world pos.
				childEntity.Transform.SetLocalPosition(childData.Position);
				childEntity.Transform.SetLocalRotation(childData.Rotation);
				childEntity.Transform.SetLocalScale(childData.Scale);

				if (childData.Id != Guid.Empty)
					idMap[childData.Id] = childEntity.PersistentId;
			}
		}

		Voltage.Serialization.ComponentReferenceResolver.RemapEntitySubtree(newEntity, idMap);
		Voltage.Serialization.ComponentReferenceResolver.ResolveEntitySubtree(newEntity, newEntity.Scene);
	}

	private bool IsDuplicateChild(Scene scene, SceneData.SceneEntityData childData)
	{
		var existing = scene.FindEntity(childData.Name);
		if (existing != null && existing.Transform.Parent?.Entity?.Name == childData.ParentEntityName)
			return true;
		return false;
	}

	#endregion

	/// <summary>
	/// Merge-replaces the components, transform, and children of a SceneRequired entity
	/// with data from a prefab. The entity identity (name, type, scene membership) is preserved.
	/// </summary>
	public void MergeReplacePrefabOntoSceneRequired(Entity target, PrefabData prefabData)
	{
		if (target == null)
			return;

		if (target.Type != Entity.InstanceType.SceneRequired)
		{
			Debug.Error($"MergeReplacePrefabOntoSceneRequired called on non-SceneRequired entity '{target.Name}'.");
			return;
		}

		target.Transform.Rotation = prefabData.Rotation;
		target.Transform.Scale = prefabData.Scale;

		// Remove all existing serialized components (non-serialized ones like Camera stay)
		var componentsToRemove = new List<Component>();
		foreach (var comp in target.Components)
		{
			if (comp.IsSerialized)
				componentsToRemove.Add(comp);
		}

		foreach (var comp in componentsToRemove)
			target.RemoveComponent(comp);

		// Load new components from prefab EntityData
		if (prefabData.EntityData != null)
		{
			var clonedEntityData = prefabData.EntityData.Clone();
			target.SetEntityData(clonedEntityData);

			if (clonedEntityData.ComponentDataList != null)
			{
				foreach (var componentEntry in clonedEntityData.ComponentDataList)
				{
					try
					{
						var component = CreateComponentInstance(componentEntry.ComponentTypeName, componentEntry.ComponentId);
						if (component == null)
						{
							Debug.Error($"Could not create component: {componentEntry.ComponentTypeName}");
							continue;
						}

						component.Name = componentEntry.ComponentName;
						component.SetSerialized(true);
						target.AddComponent(component, true);
					}
					catch (Exception ex)
					{
						Debug.Error(
							$"Failed to instantiate component {componentEntry.ComponentTypeName}: {ex.Message}");
					}
				}
			}

			var processedComponents = new HashSet<string>();

			foreach (var comp in target.ComponentsToAdd)
			{
				if (TryAssignComponentDataFromEntityData(target, comp))
				{
					var componentId = $"{comp.GetType().FullName}:{comp.Name}";
					processedComponents.Add(componentId);
				}
			}

			// Also assign data to pre-existing components (e.g. Camera)
			foreach (var comp in target.Components)
			{
				var componentId = $"{comp.GetType().FullName}:{comp.Name}";
				if (!processedComponents.Contains(componentId))
				{
					if (TryAssignComponentDataFromEntityData(target, comp))
						processedComponents.Add(componentId);
				}
			}
		}

		// Destroy existing serialized children
		for (var i = target.Transform.ChildCount - 1; i >= 0; i--)
		{
			var child = target.Transform.GetChild(i).Entity;
			if (child.Type != Entity.InstanceType.NonSerialized)
				child.Destroy();
		}

		// Create children from prefab
		if (prefabData.ChildEntities != null)
		{
			foreach (var childData in prefabData.ChildEntities)
			{
				if (childData.InstanceType == Entity.InstanceType.NonSerialized)
					continue;

				var childEntity = new Entity();
				LoadPredefinedEntityData(childEntity, childData);
				childEntity.Transform.SetParent(target.Transform);
				target.Scene.AddEntity(childEntity);
			}
		}
	}

	/// <summary>
	/// Removes the top-level "$type" property from a JSON object string.
	/// This prevents a stale assembly-qualified type name (written by a previous
	/// TypeNameHandling.Auto serialization) from overriding the explicitly
	/// supplied target type during deserialization after a script recompile.
	/// </summary>
	private static string StripTypeFieldFromJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json) || !json.Contains("\"$type\""))
			return json;

		return System.Text.RegularExpressions.Regex.Replace(
			json,
			@"^\s*\{\s*""\$type""\s*:\s*""[^""]*""\s*,\s*",
			"{ ",
			System.Text.RegularExpressions.RegexOptions.Singleline);
	}
}