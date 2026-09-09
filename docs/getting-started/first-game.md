---
title: Your First Game
sidebar_position: 2
description: Create a project, add a scene and an entity, and write a component that shows a sprite.
---

# Your First Game

This walkthrough creates a new project, adds a scene, and writes a component that loads a PNG sprite.

## Create a New Project

1. Launch the Voltage Editor.
2. Choose `File > New Project`. Fill in a project name and directory and click *Create*.
3. The editor creates the folder structure described in [Projects and Folder Layout](../editor/projects.md) and opens the project.

## Create a Scene

1. In the **Asset Browser**, navigate to `Data/Scenes/`.
2. Right-click the folder and choose *New Scene*. Name it `GameScene`.
3. Double-click `GameScene.vscene` to open it. The Scene Graph shows a default Camera entity.

## Add an Entity

1. In the **Scene Graph**, right-click the root and choose *Create Entity*. Name it `Hero`.
2. The Inspector shows the entity's `Transform`. Set **Position** to `(0, 0)`.

## Write a Component

In the `Scripts/` folder create a new file `HeroVisuals.cs`:

```csharp
using Voltage;
using Voltage.Sprites;

public partial class HeroVisuals : Component
{
    public override void OnStart()
    {
        var sprite = AddComponent(new SpriteRenderer());
        sprite.LoadPngFile("Content/Characters/hero.png");
    }
}
```

Place `hero.png` in the project's `Content/Characters/` folder.

Save the file. The editor detects the change, recompiles the scripts, and reloads the scene.

## Attach the Component

1. Select the `Hero` entity in the Scene Graph.
2. In the Inspector, click *Add Component* and choose `HeroVisuals` from the list.
3. Press `Ctrl+S` to save the scene.

## Press Play

Click the **Play** button in the Editor Tools bar (or press `F1`). The scene enters Play mode. `OnStart` runs, `LoadPngFile` loads the texture, and the sprite appears at the origin of the game viewport.

Press `F1` again to return to Edit mode. The scene reloads automatically if `Core.ResetSceneAutomatically` is true (the default).
