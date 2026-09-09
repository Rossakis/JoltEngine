---
title: The Component Model and Lifecycle
sidebar_position: 1
---

# The Component Model and Lifecycle

Derive from `Component` to attach behavior to an entity:

```csharp
public partial class PlayerController : Component, IUpdatable
{
    public float MoveSpeed = 200f;

    public override void OnAddedToEntity()
    {
        // Called immediately when AddComponent is called.
        // Entity.Transform is available. Other components may not be ready yet.
    }

    public override void OnEnabled()
    {
        // Called after OnAddedToEntity when the entity is enabled.
    }

    public override void OnStart()
    {
        // Called after OnEnabled. Entity.Scene is set. Safe to query other components.
    }

    public void Update()
    {
        // Called every frame while in Play mode and not paused.
        var dir = Vector2.Zero;
        if (Input.IsKeyDown(Keys.Left))  dir.X -= 1f;
        if (Input.IsKeyDown(Keys.Right)) dir.X += 1f;
        Entity.Position += dir * MoveSpeed * Time.DeltaTime;
    }

    public override void OnRemovedFromEntity()
    {
        // Cleanup. Called when the component is removed or the entity is destroyed.
    }

    public override void OnDisabled()
    {
        // Called when the entity or this component is disabled.
    }
}
```

**The `partial` keyword is required** on any component that serializes fields (see [Serialization and NativeAOT](../engine/serialization-and-aot.md)). Omitting it means the source generator cannot emit the `Data` property, so no fields will be saved or loaded.

**Lifecycle order summary:**

```
AddComponent(c) called
  → c.OnAddedToEntity()          (immediate, before added to live list)
  → c.OnEnabled()                (if enabled)
  → c.OnStart()                  (after enabled; Entity.Scene is set)
  ...per-frame...
  → c.Update()                   (IUpdatable only; skipped while paused unless IUpdatableInPauseMode)
  → c.OnEntityTransformChanged() (when parent transform changes)
RemoveComponent(c) / entity.Destroy()
  → c.OnRemovedFromEntity()
```

**Common Entity API:**

```csharp
// Add / get / remove components
entity.AddComponent(new SpriteRenderer());
var sr = entity.GetComponent<SpriteRenderer>();
bool found = entity.TryGetComponent<SpriteRenderer>(out var sr);
entity.RemoveComponent<SpriteRenderer>();

// Hierarchy traversal
var childSprite = entity.GetComponentInChildren<SpriteRenderer>();
var parentHealth = entity.GetComponentInParent<HealthComponent>();

// From within a Component, shortcut methods delegate to Entity:
var sr = GetComponent<SpriteRenderer>();
```

**Entity properties:**

```csharp
entity.Name
entity.Tag              // int tag for scene-wide queries
entity.Enabled          // enables/disables the entity and all its components
entity.UpdateOrder      // sort order within the scene's entity list
entity.Position         // world-space shortcut to entity.Transform.Position
entity.LocalPosition    // local-space position
entity.Rotation         // radians
entity.Scale
entity.Parent           // Transform of the parent entity
entity.Destroy()        // queues destruction at end of frame
entity.Destroy(float)   // destroy after N seconds (coroutine internally)
```

**Pause-mode behavior:** Components that implement `IUpdatableInPauseMode` continue receiving `Update` calls while `Core.IsPauseMode` is true. Regular gameplay components are frozen. Use this for HUD, menus, or any component that must remain responsive while the game is paused.

```csharp
public partial class PauseMenuUI : Component, IUpdatable, IUpdatableInPauseMode
{
    public void Update() { /* runs even while paused */ }
}
```
