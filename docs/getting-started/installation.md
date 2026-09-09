---
title: Installation
sidebar_position: 1
description: Build the editor from source and open your first project.
---

# Installation

Voltage is built from source. There is no installer yet; the editor is a single executable that the .NET SDK produces in a couple of minutes.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer.
- Windows, macOS or Linux with an OpenGL 3 capable GPU driver. The editor and games use MonoGame DesktopGL (SDL2 + OpenGL).
- To publish games as NativeAOT binaries, the platform's native toolchain: on Windows the Visual Studio C++ build tools, on Linux `clang` and `zlib1g-dev`, on macOS the Xcode command-line tools.

## Build the editor

```bash
git clone https://github.com/VoltageEngine/VoltageEngine.git
cd VoltageEngine
dotnet build Voltage.Editor/Voltage.Editor.csproj -c Editor-Debug
```

The executable lands in `Voltage.Editor/bin/Editor-Debug/win-x64/Voltage.Editor.exe` (the runtime identifier folder matches your platform). `Editor-Release` builds an optimized editor to `bin/Editor-Release`.

The `Editor-*` configurations define the `EDITOR` preprocessor symbol, which adds the inspector, serialization manager and hot-reload surfaces to the engine. Plain `Debug` and `Release` build the engine the way a published game sees it.

## Run it

Start the editor with no arguments to get the project picker, or pass a `.voltage` file to open a project directly:

```bash
Voltage.Editor/bin/Editor-Debug/win-x64/Voltage.Editor.exe C:/Games/MyGame/MyGame.voltage
```

`File > New Project` creates a fresh game project with the [standard folder layout](../editor/projects.md), including its own `.csproj` that references the engine assemblies the editor copies into `EngineLibs/`.

## The command-line client

`Voltage.Cli` builds to `voltage`, a client for the [Editor Gateway](../gateway/README.md) that lets scripts and AI agents drive a running editor:

```bash
dotnet build Voltage.Cli/Voltage.Cli.csproj -c Editor-Debug
dotnet Voltage.Cli/bin/Editor-Debug/net8.0/voltage.dll status
```

## Next

Follow [Your First Game](first-game.md) to create a project, a scene and a component.
