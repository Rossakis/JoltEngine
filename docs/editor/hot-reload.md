---
title: Hot-Reload Script Workflow
sidebar_position: 6
---

# Hot-Reload Script Workflow

The editor watches the project's `Scripts/` folder using `ScriptWatcher`. When a `.cs` file changes, `ScriptManager` recompiles all scripts in the folder into a transient `DynamicScripts` assembly. If compilation succeeds and `AutoReloadSceneOnChange` is enabled, the scene is reloaded so the new code takes effect immediately.

Key settings (persisted per user, not per project):

| Setting | Default | Meaning |
|---|---|---|
| `EnableHotReload` | `true` | Auto-compile on file save |
| `AutoReloadSceneOnChange` | `true` | Reload scene after successful compile |
| `CompileOnStartup` | `true` | Compile scripts when a project is first opened |

The hot-reload assembly is for **edit-time only**. When you build the game (see [Serialization and NativeAOT](../engine/serialization-and-aot.md)), the scripts are compiled ahead-of-time as part of the NativeAOT publish step — the source generator runs at that point to emit reflection-free deserialization code.
