---
title: Play, Pause, Reset, and Audio
sidebar_position: 3
---

# Play, Pause, Reset, and Audio

The Editor Tools bar contains a four-button cluster that controls the game loop:

| Button | Keyboard | What it does |
|---|---|---|
| **Play** | `F1` | Enters Play mode (`Core.IsEditMode = false`). Component `Update` methods start running. |
| **Stop** | `F1` (again) | Returns to Edit mode. If `Core.ResetSceneAutomatically` is true the scene is reloaded from disk, discarding Play-mode changes. |
| **Pause** | `F2` | Toggles `Core.IsPauseMode`. Components that do not implement `IUpdatableInPauseMode` stop updating. Only meaningful while in Play mode. |
| **Reset** | `F5` | Calls `Core.InvokeResetScene()`. Reloads the scene from disk without leaving Play mode. |

Additional shortcuts:

| Action | Keyboard |
|---|---|
| Reload scene (recompile scripts + reload) | `F6` |
| Save scene | `Ctrl+S` |
| Toggle fullscreen | `F11` |

The **audio mute** button in the toolbar calls `Core.InvokeSwitchAudio(bool)`, which sets `Core.IsAudioOn`, fires `Core.OnSwitchAudio`, and applies `SoundEffect.MasterVolume` as a global backstop. Components that implement `IAudioComponent` receive an `OnAudioStateChanged` callback.

> Scene data **cannot be saved** while in Play or Pause mode. `Ctrl+S` is ignored and a notification is shown.
