---
title: Content Loading
sidebar_position: 3
---

# Content Loading

Voltage uses a **path-based content system** rather than asset-reference objects. `VoltageContentManager` resolves paths against `VoltageContentManager.ContentRoot`, which the editor sets to the open project's root directory. In a published game it defaults to `AppContext.BaseDirectory`.

Paths are typically relative to the project root, e.g. `"Content/Characters/Hero.png"`.

The scene's `Content` manager is the right one for scene-lifetime assets; `Core.Content` is for assets that should persist across scene transitions.

```csharp
// Load a PNG texture
Texture2D tex = Entity.Scene.Content.LoadTexture("Content/Tiles/Grass.png");

// Load an Aseprite file (returns AsepriteFile)
AsepriteFile ase = Entity.Scene.Content.LoadAsepriteFile("Content/Characters/Hero.aseprite");

// Load a sound effect
SoundEffect sfx = Entity.Scene.Content.LoadSoundEffect("Content/Audio/Jump.wav");

// Load a Tiled map
TmxMap map = Entity.Scene.Content.LoadTiledMap("Content/Maps/Level1.tmx");
```

All loaders cache by path — loading the same path twice returns the same object. Assets are disposed when the `VoltageContentManager` is disposed (at scene end for `scene.Content`, never for `Core.Content`).

**`SpriteRenderer` convenience loaders** — these are the methods to call when loading a sprite from code inside a component. They internally use the scene's content manager and also update the component's serialized data so the path survives a save/load cycle:

```csharp
// PNG
spriteRenderer.LoadPngFile("Content/Characters/Hero.png");

// Aseprite — optional layer name and 0-based frame index
spriteRenderer.LoadAsepriteFile("Content/Characters/Hero.aseprite");
spriteRenderer.LoadAsepriteFile("Content/Characters/Hero.aseprite", layerName: "Body", frameNumber: 0);

// Tiled map image layer
spriteRenderer.LoadTmxFile("Content/Maps/Level1.tmx");
spriteRenderer.LoadTmxFile("Content/Maps/Level1.tmx", imageLayerName: "Background");
```

Do not use `Core.Content` or `scene.Content` directly to load a sprite and then assign it to a `SpriteRenderer` — the path will not be serialized and the sprite will disappear on scene reload.
