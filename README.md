# Voltage Engine

Voltage is a standalone 2D game engine and editor for C# developers. It ships as a single executable that opens a Godot/Unity-style editor, a dockable ImGui interface sitting in front of a live MonoGame render window, and publishes finished games as self-contained, NativeAOT-compiled binaries with no runtime dependency.

<img width="3831" height="2066" alt="Voltage Editor" src="https://github.com/user-attachments/assets/3cff3193-5f86-44ae-8bb9-545ba088f21c" />

## Documentation

The manual lives at **https://voltageengine.github.io/VoltageEngine/** and is written in the [`docs/`](docs/) folder of this repository, so every page is readable on GitHub as well.

| Start here | Then |
|---|---|
| [Introduction](docs/intro.md) | [Editor guide](docs/editor/projects.md) |
| [Installation](docs/getting-started/installation.md) | [Scripting guide](docs/scripting/components.md) |
| [Your first game](docs/getting-started/first-game.md) | [Serialization and NativeAOT](docs/engine/serialization-and-aot.md) |
| [Editor Gateway, CLI and MCP](docs/gateway/README.md) | [Plugins](docs/plugins/README.md), [Asset file formats](docs/assets/README.md) |

## Quick start

```bash
git clone https://github.com/VoltageEngine/VoltageEngine.git
cd VoltageEngine
dotnet build Voltage.Editor/Voltage.Editor.csproj -c Editor-Debug
Voltage.Editor/bin/Editor-Debug/win-x64/Voltage.Editor.exe
```

Requires the .NET 8 SDK. `File > New Project` creates a game project with its own `.csproj`; save a `.cs` file under `Scripts/` and the editor recompiles and reloads the scene.

## Features

- **Editor**: scene graph, inspector, asset browser with `.meta` GUIDs, prefabs with component-level overrides, tile painting, timeline, data assets, undo history, dockable layouts.
- **Scripting**: `Component` and `SceneComponent` classes with a Unity-like lifecycle, hot reload on save, source-generated serialization with no runtime reflection.
- **Runtime**: MonoGame DesktopGL rendering, deferred lighting, physics, a bussed audio mixer with spatial sound and zones, Aseprite and Tiled import.
- **Publishing**: `dotnet publish` with NativeAOT and trimming from inside the editor, per platform.
- **Automation**: a loopback [gateway](docs/gateway/README.md) and the `voltage` CLI let scripts and AI agents drive the editor, with an MCP server built in.
- **Plugins**: versioned packages from bundled, path, git or zip sources, locked per project.

## Repository layout

| Project | Role |
|---|---|
| `Voltage.Engine` | Runtime: `Core`, scenes, entities, components, content, rendering, physics, audio. |
| `Voltage.Editor` | The ImGui editor, project and asset management, hot reload, game builder, gateway. |
| `Voltage.Cli` | `voltage`, the gateway client and MCP server. |
| `Voltage.SourceGenerators` | Roslyn generator for AOT-safe component serialization. |
| `Voltage.Persistence` | The JSON layer scenes and prefabs are written with. |
| `docs/` | The manual; `Voltage.github.io/` builds it into the documentation site. |

## Working on the docs

```bash
cd Voltage.github.io
npm install
npm run start
```

Pages are Markdown files under `docs/`; the site reads them in place. `npm run build` checks for broken links.

## Contributing and licence

See [CONTRIBUTING.md](CONTRIBUTING.md). The engine is released under the [MIT licence](LICENSE); the bundled UI assets carry their own [licence](UI_LICENSE).
