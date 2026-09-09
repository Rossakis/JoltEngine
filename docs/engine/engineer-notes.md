---
title: Engine Engineer Notes
sidebar_position: 2
---

# Engine Engineer Notes

This section covers internals that game developers using the prebuilt editor do not need to know.

## Core Lifecycle

`Core : Game` is the MonoGame `Game` subclass. It owns:
- The active `Scene` and the queued `_nextScene` (swap happens at end of `Update`).
- `GlobalManager` instances (coroutine manager, tween manager, timer manager, render target).
- The `Emitter<CoreEvents>` event bus for engine-level events (`SceneChanged`, `GraphicsDeviceReset`, `OrientationChanged`, `Exiting`).
- Static events `OnSwitchEditMode`, `OnSwitchPauseMode`, `OnSwitchAudio`, `OnResetScene` for mode changes.

In `EDITOR` builds the title bar is updated with FPS and memory each second, and `drawCalls` is tracked via `GraphicsDevice.Metrics`.

`Core.IsEditMode` is set to `true` on construction in `EDITOR` builds and `false` in standalone builds. The editor sets it to `false` when Play is pressed.

## Scene Resolution Policy

`Scene.SetDesignResolution(width, height, SceneResolutionPolicy)` sets the internal render target size and the final blit rectangle. Policies:

| Policy | Behavior |
|---|---|
| `None` | Render target = screen size. No scaling. |
| `ExactFit` | Fixed render target; stretched to fill screen. May distort. |
| `ShowAll` | Letterboxed to preserve aspect ratio. |
| `ShowAllPixelPerfect` | Integer-only scale. |
| `NoBorder` | Crops to fill screen. No distortion. |
| `FixedHeight` / `FixedWidth` | Fixes one axis; the other adapts to device aspect ratio. |
| `BestFit` | Bleed-area based; gives a safe zone. |

`Scene.SetDefaultDesignResolution` sets the policy for all future scenes.

## Persistence Layer

`Voltage.Persistence.Json` (custom) is used for all scene and component serialization. It is not `System.Text.Json` or `Newtonsoft.Json`, though it shares some naming. `JsonSettings` controls `PrettyPrint`, `TypeNameHandling`, and `PreserveReferencesHandling`.

`AotDeserializers.DeserializeSceneData` and `DeserializePrefabData` are the scene-load entry points in published builds. They call registered `ComponentDataAotDeserializer` functions that were registered by the source generator's `[ModuleInitializer]` methods.

`ComponentIdRegistry` maps a stable `[ComponentId]` alias to the component `Type` that currently carries it (and back). It is populated by the generated `ComponentDataAotBootstrap.AutoRegister` and is the primary, rename-proof way scenes resolve components: load resolves the stored id to the current type, ignoring a stale type name — so class renames and namespace moves need no editor reconciliation. The same bootstrap runs both in the NativeAOT build and against the editor's dynamically-compiled scripts, so the mapping is identical in both — no reflection. Component identity is stamped into source by `ComponentIdStamper` during editor compilation, and the `[ComponentId]` attribute itself is emitted by the generator (so user scripts compile even against a stale engine reference). Scripts therefore use no `.meta` sidecar — only non-code assets do, since code is the one asset type that can carry its own identity in-source.

`TypeRenameRegistry` maps old fully-qualified type names to current `Type` objects. It is populated by the generated `ComponentDataAotBootstrap.AutoRegister` based on `[FormerlyKnownAs]` attributes, and serves as the fallback for scenes saved before a component had a `[ComponentId]`.

**Identity model — everything resolves by a stable id, names are hints.** The engine is consistent across reference kinds:
- **Entities / Transforms** are referenced by `EntityReference.EntityPersistentId` (a `Guid`) — already rename-proof.
- **Components** (cross-entity `ComponentReference`) are referenced by the entity's `PersistentId` **plus** the component's stable `[ComponentId]` (resolved via `ComponentIdRegistry`), with `ComponentTypeName` as the fallback hint — so renaming a component class keeps cross-entity references valid without `[FormerlyKnownAs]`.
- **Assets** (prefabs, scenes, textures) are referenced by their `.meta` GUID, resolved in the editor via `AssetDatabase` and at runtime via the baked `Voltage.Assets.AssetManifest` (`Data/assets.manifest`). Renaming/moving an asset never changes its GUID, so references survive in editor *and* published builds.

## Entity Instance Types

| `InstanceType` | Meaning |
|---|---|
| `NonSerialized` | Code-only. Not saved. Cannot be selected in editor. |
| `Serialized` | Default editor entity. Saved in `.vscene`. |
| `SerializedPrefab` | Instantiated from a `.vprefab`. Saved as a delta against the prefab. |
| `SceneRequired` | Always present; cannot be deleted. Currently used for the main Camera entity. |

## Adding a New Serializable Engine Component

1. Declare the component `partial`.
2. If the serialization is complex (e.g., `SpriteRenderer`'s file-type union), write a manual `ComponentData` subclass and override `Data` yourself. The generator skips partial classes that already have a `Data` override.
3. If using manual `Data`, register a deserializer in `ComponentDataAotBootstrap` or via a `[ModuleInitializer]` so NativeAOT builds can deserialize it.
4. Add the component type to any AOT reflection annotations (`[DynamicDependency]`) if its data type is referenced only via a string name.
