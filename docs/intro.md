---
title: Introduction
sidebar_position: 1
description: What Voltage is, how the engine and editor fit together, and what it does not do.
---

# Introduction

Voltage is a standalone 2D game engine and editor for C# developers. It ships as a single executable that opens a Godot/Unity-style editor, a dockable ImGui interface sitting in front of a live MonoGame render window, and publishes finished games as self-contained NativeAOT binaries with no runtime dependency.

![Voltage Editor](https://github.com/user-attachments/assets/3cff3193-5f86-44ae-8bb9-545ba088f21c)

## What Voltage Is

Voltage is a **standalone editor + runtime engine** for 2D pixel-art games written in C#. The editor is a single executable; game code lives in a separate C# project that the editor compiles, hot-reloads, and eventually publishes.

At runtime the world is structured as **Scenes containing Entities containing Components** — a traditional component-based model similar to Unity's MonoBehaviour system rather than a strict ECS. Entities carry a `Transform` (position, rotation, scale, parent/child hierarchy). Components hold data and behavior. `SceneComponent`s attach to the scene itself rather than to any entity and are useful for scene-wide managers.

## Engine vs Editor Split

| Layer | Project | Role |
|---|---|---|
| `Voltage.Engine` | `Voltage.Engine.dll` | Core runtime: `Core`, `Scene`, `Entity`, `Component`, content loading, rendering, physics, audio. Compiles with or without the `EDITOR` preprocessor symbol. |
| `Voltage.Editor` | `Voltage.Editor.exe` | ImGui editor shell, project/asset management, hot-reload, serialization manager, game builder. Depends on `Voltage.Engine` with `EDITOR` defined. |
| `Voltage.SourceGenerators` | Roslyn analyzer | Generates AOT-safe serialization code for every `partial Component` and `partial SceneComponent` subclass. |

Game code (your scripts) lives in a separate game project that references `Voltage.Engine` but not `Voltage.Editor`. The editor compiles your scripts into a `DynamicScripts` assembly and loads it at runtime; a published game includes only your compiled scripts + the engine.

## Tech Stack

- **Language / runtime:** C# / .NET (modern)
- **Windowing / graphics:** MonoGame.Framework.DesktopGL 3.8.5.1 (SDL2 backend, OpenGL renderer)
- **Editor UI:** ImGui.NET (immediate-mode GUI)
- **Publishing:** .NET NativeAOT + trimming
- **Serialization:** Custom `Voltage.Persistence.Json` with AOT-safe deserializers; no reflection at runtime in published builds
- **Asset formats:** `.png`, `.ase`/`.aseprite`, `.tmx` (Tiled), `.wav`, `.ogg` (via MonoGame)

## Comparison to Other Engines

| Concept | Voltage | Unity analog | Godot analog |
|---|---|---|---|
| Script attachment | `Component` on `Entity` | `MonoBehaviour` on `GameObject` | `Node` script |
| Scene-wide logic | `SceneComponent` | `MonoBehaviour` on scene root | `AutoLoad` singleton |
| Scene file | `.vscene` | `.unity` | `.tscn` |
| Prefab file | `.vprefab` | `.prefab` | `.tscn` (instanced) |
| Asset GUID | `.meta` sidecar (editor-only) | `.meta` sidecar | `uid://` embedded |
| Game loop entry | `Core : Game` | Hidden by engine | Hidden by engine |

## Notable Limitations

- The `.meta` / GUID asset-reference system is **editor-time only**. Published games resolve assets by file path.
- Prefab overrides are at the **component level** only (the entire component's data is replaced). Field-level property overrides (like Unity's) are not supported.
- `Entity.Transform` position/rotation/scale are **not** part of prefab delta data; position is supplied at instantiation time via `Scene.LoadPrefab(path, position)`.
- There is no visual prefab editing mode; prefabs are authored by saving an entity as a prefab from the Scene Graph.
