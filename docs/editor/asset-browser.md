---
title: The Asset Browser and .meta GUIDs
sidebar_position: 5
---

# The Asset Browser and .meta GUIDs

Every file in a watched asset folder gets a `.meta` sidecar file the first time it is seen by the editor. The sidecar contains a stable `Guid` that survives renames and moves within the project. The editor uses this GUID to resolve prefab references when a `.vprefab` file is renamed or moved.

**The `.meta`/GUID system is editor-time only.** Published games do not read `.meta` files. Asset references in scenes and prefabs are stored as project-relative file paths, and `VoltageContentManager` resolves them against `VoltageContentManager.ContentRoot` at runtime.

Do not delete `.meta` files while the project is open. If a `.meta` file is missing the editor regenerates it with a new GUID, which will break any prefab references that pointed to the old GUID.
