---
title: Scenes and Prefabs
sidebar_position: 4
---

# Scenes and Prefabs

**Scenes** are `.vscene` files stored in `Data/Scenes/`. Each scene is a JSON document containing entity transforms, component data, and a list of `SceneComponent` entries. The scene's display name comes from `SceneData.Name`; the key used for loading is the file name without extension (`Scene.LevelName`).

Load a scene from code:

```csharp
Scene.LoadLevel("Level2");
```

Reload the current scene (e.g. for a "restart" button):

```csharp
Scene.ReloadCurrentLevel();
```

**Prefabs** are `.vprefab` files stored in `Data/Prefabs/`. A prefab captures one entity (with its components and child entities) as a reusable template. To create a prefab, right-click an entity in the Scene Graph and choose *Save as Prefab*. To instantiate a prefab from code at runtime:

```csharp
Entity enemy = Core.Scene.LoadPrefab("Enemies/Slime.vprefab", spawnPosition);
```

**Prefab overrides** work at the component level. When a scene entity was created from a prefab, loading the scene re-instantiates the prefab and then overwrites individual component data entries from the scene's delta. There is no field-level override system.

**Important caveats:**
- `Entity.Transform` (position/rotation/scale) is **not** part of the prefab delta. Position is supplied at instantiation via the `position` parameter of `LoadPrefab`.
- A prefab instance in a scene stores both its source prefab's stable GUID (`OriginalPrefabGuid`) and name (`OriginalPrefabName`). At runtime the source prefab is resolved **GUID-first** via the baked **asset manifest** (`Data/assets.manifest`) — so renaming or moving a `.vprefab` does not break the scenes that use it — falling back to name only when the GUID is absent. (`Entity.OriginalPrefabGuid` is still `Guid.Empty` for prefabs instantiated by *path* via `Scene.LoadPrefab`, which carry no GUID context.)
