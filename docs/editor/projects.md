---
title: Projects and Folder Layout
sidebar_position: 1
---

# Projects and Folder Layout

The editor works with **Voltage projects** — a directory containing a `.voltage` metadata file alongside the game C# project. Open a project from `File > Load Project…` or create one with `File > New Project`.

A freshly created project has this layout (relative paths are stored in `ProjectMetadata`):

```
MyGame/
  MyGame.csproj              # game script project
  MyGame.voltage             # project metadata (JSON)
  ProjectSettings.json       # display resolution, vsync, startup scene
  Content/                   # textures, audio, tiled maps, fonts
  Scripts/                   # C# source files hot-reloaded by the editor
  Effects/                   # custom GLSL/HLSL shader source files
  Data/
    Scenes/                  # .vscene files
    Prefabs/                 # .vprefab files
  Build/                     # output of the Build system (generated)
```

The paths `ContentsFolder`, `ScriptsFolder`, `EffectsFolder`, `DataFolder`, `ScenesFolder`, and `PrefabsFolder` are all properties on `RuntimeGameProject` and are resolved from the metadata at load time.

`ProjectSettings.json` controls the startup scene name, target design resolution, and display settings applied on the first `Scene.LoadLevel` call.
