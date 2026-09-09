---
title: SceneComponents
sidebar_position: 2
---

# SceneComponents

`SceneComponent` is a scene-scoped behavior — it lives on the scene rather than on an entity. Good for game-wide managers (spawn systems, wave controllers, audio mixers):

```csharp
public partial class WaveManager : SceneComponent
{
    public int CurrentWave = 1;

    public override void OnStart()
    {
        // Scene and all entities are ready.
    }

    public override void Update()
    {
        // Called each frame before entity updates.
        // Frozen while Core.IsPauseMode is true (same as regular components).
    }
}
```

Add from code or from the editor's Scene Component panel:

```csharp
var wm = Core.Scene.AddSceneComponent<WaveManager>();
var wm = Core.Scene.GetSceneComponent<WaveManager>();
Core.Scene.RemoveSceneComponent<WaveManager>();
```

`SceneComponent` must also be declared `partial` if it has serializable public fields.
