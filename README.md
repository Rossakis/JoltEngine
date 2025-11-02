# JoltEngine

JoltEngine is a MonoGame-based runtime and in-game editor built on top of Nez, inspired by Unity Engine. It ships with an ImGui-powered authoring workflow (Nez.ImGui) and pragmatic data-driven systems for Entities, Components, Scenes, and Prefabs.

> [!WARNING]
> Status: Experimental. Using JoltEngine standalone is not recommended yet.
> There are interlinked dependencies between the original game project (JoltMono) and this engine (relative project references, shared data contracts, editor hooks). Until these are fully decoupled, you may hit broken references and missing runtime glue unless you mirror the original folder layout and wire game-specific managers. Proceed only if you are comfortable resolving engine–game coupling yourself.

This repository contains the engine/editor only. Reference it from your game project to get a fast edit–play iteration loop.

<img width="3840" height="2160" alt="Jolt - Editor Showoff" src="https://github.com/user-attachments/assets/c63658a0-0e4e-4695-91da-daceb4c3359c" />


## Key Features

- MonoGame + Nez runtime
  - Entities/Components/Scenes, rendering, input, content pipeline
- In-game ImGui editor
  - Scene graph, inspectors, popups, notifications
  - Edit Mode / Play Mode toggling (F1/F2)
- Data-driven workflow
  - SceneData, EntityData, Prefabs (JSON)
  - Entity factory/registry for type-safe creation without reflection
- Content-aware UX
  - Tiled (.tmx) file picker (rooted under Content)
  - Auto-generated content paths (T4) for strong-typed asset references
- Undo/Redo support
  - Entity create/delete and other common actions
- Prefab management
  - Cached listing, instantiate via popup, delete with confirmation
- Debug Tab
  - Use the Debug.Log() method to output information straight into the editor.
## Projects in this repo

- Nez.Portable: Core runtime (ECS, rendering, utilities)
- Nez.ImGui: Editor UI and integration (scene graph, inspectors, popups, file pickers)

## Editor Capabilities

- Scene Graph window
  - Post Processors pane: list/configure scene post processors
  - Renderers pane: list/configure renderers
  - Entities pane (double-click to inspect)
    - Select, multi-select, clone, delete
    - Expand/collapse hierarchy
    - Copy a component from one entity and paste to another
    - Keyboard navigation with Up/Down, repeat behavior, and auto-expand of parents/children
    - Click empty space to clear selection
  - Save Scene button
  - Add Entity popup
    - Entity types sourced from EntityFactoryRegistry
    - Prefabs from Content/Data/Prefabs (cached)
      - Left-click to instantiate
      - Right-click context menu: Create Instance, Delete Prefab (with confirmation)
  - TMX File Picker
    - Rooted under Content; returns a relative path (Content/…/file.tmx)
- Main Entity Inspector
  - Edit components at runtime
  - Save Entity defaults (per-type)
- Notifications
  - Timed feedback for success/failure and dangerous actions
- Mode toggling
  - Edit mode shows editor controls
  - Play mode for runtime validation

## Data Model and Serialization

- Entity.InstanceType
  - HardCoded: created via code only (singletons, tightly coupled entities)
  - Dynamic: created at edit time; persisted per-scene
  - Prefab: reusable archetypes saved to JSON (Content/Data/Prefabs)
- Serialization
  - Use public fields for data (not properties) to ensure persistence
- Save semantics
  - Save Scene: stores instance data for Dynamic/Prefab instances (positions, enable flags, per-instance data)
  - Save Entity: stores shared defaults for an entity type

## Entity Factory and Events

- Register your entity types once and create by name:
``` EntityFactoryRegistry.Register("Platform", () => new PlatformEntity());```

- Creation helpers and events:
  - TryCreate/Create("TypeName")
  - OnEntityCreated(Entity entity)
  - OnDataLoadingStarted(Entity entity, SceneData.SceneEntityData data)
- The editor’s “Add Entity” popup is populated from the registry.

## Animation Event Editor

Author and preview timeline events on your sprite animations. Use them to trigger gameplay hooks (SFX/VFX, state changes, component toggles) in sync with frames.

- Per-clip timeline event tracks
- Add/rename/move/delete events
- Preview playback with event firing
- Runtime hook-up via Animator/SpriteAnimator subscriptions

Example:
```// Subscribe to events authored in the Animation Event Editor Animator.SubscribeToEvent("OnHit", "ReturnToIdle", () => Animator.Play("Idle", SpriteAnimator.LoopMode.Once) );```


## Folder Conventions

- Content root: Content/
- Prefabs: Content/Data/Prefabs/*.json
- Scenes: Content/Data/Scene/*.json
- TMX maps: any path under Content/ (picker blocks traversal outside Content)

## Integration (from your game project)

1) Add a project reference to JoltEngine (Nez and Nez.ImGui).
2) In your Game/Core initialization:
- Set Content.RootDirectory = "Content".
- Register global managers (e.g., ImGuiManager).
- Initialize your data loaders.
- Register entities in EntityFactoryRegistry.
3) Run in Debug to use the in-game editor.

## Requirements

- .NET 8 SDK
- MonoGame 3.8.x
- ImGui.NET
- A standard MonoGame Content folder named “Content”

## Tips

- Keep SceneData/EntityData schemas engine-agnostic to simplify future engine swaps.
- For trimming/AOT, keep a registry of component types used via reflection to prevent trimming.
- Editor best practice: after Play → back to Edit, reload the scene before Save Scene to avoid persisting gameplay-mutated state.

## Roadmap (ideas)

- Deeper Undo/Redo coverage (component edits, reparenting)
- Custom component drawers and inspectors
- Additional built-in effects with editor controls

## License

JoltEngine builds on Nez and MonoGame. See their respective licenses. Your game code remains yours.
