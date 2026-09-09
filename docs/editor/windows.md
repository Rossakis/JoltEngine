---
title: Main Windows
sidebar_position: 2
---

# Main Windows

| Window | Purpose |
|---|---|
| **Scene Graph** | Entity hierarchy for the active scene. Create, rename, reparent, duplicate, and delete entities. Select an entity to open its inspector. |
| **Inspector** | Edits the selected entity's `Transform` and component fields. Component data is read and written through each component's `Data` property. |
| **Asset Browser** | File tree rooted at `Content/`, `Scripts/`, `Effects/`, `Data/Scenes/`, and `Data/Prefabs/`. Drag a texture onto a `SpriteRenderer` field in the inspector to assign it. Right-click for Copy / Paste / Duplicate / Delete. Keyboard: `Ctrl+C`, `Ctrl+V`, `Ctrl+D`, `Delete`. |
| **Game Viewport** | The live MonoGame render target displayed as an ImGui image. The editor UI is rendered on top; the game content renders into the same window as a passthrough. |
| **Editor Tools bar** | Cursor/zoom mode toggle, audio mute toggle, and the Play / Stop / Pause / Reset button cluster. |

Layouts and theme can be changed from the **View** menu.
