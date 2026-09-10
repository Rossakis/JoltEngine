---
title: Asset Build
sidebar_position: 7
description: Compile the Content folder into MonoGame .xnb files for published games, with the same loaders resolving compiled or raw assets.
---

# Asset Build

A published game normally ships the `Content/` folder as raw files and decodes them at load time: PNGs through `Texture2D.FromStream`, Aseprite files through the engine's own parser, audio through NVorbis and NLayer. The asset build compiles that folder with the MonoGame Content Builder (MGCB) into `.xnb` files, so a build loads pre-decoded textures and pre-flattened Aseprite frames instead. It is off by default and changes nothing in the editor, which keeps loading raw files.

## Turning it on

`Build > Asset Build` in the menu bar holds the switch and the tool:

| Item | What it does |
|---|---|
| **Compile assets on build** | Stores `AssetBuild.Enabled` in `ProjectSettings.json`. When on, every game build runs the asset build before copying content. |
| **Build assets now** | Runs the asset build alone into `<project>/bin/AssetBuild/<platform>` (the standalone tool's output, never used by a game build) and shows a report window with counts, timings and errors. The folder is emptied before every run. |
| **Clean asset build** | Deletes that output and the intermediate folder. |
| **Settings** | Platform, compression, texture options, include and exclude globs, rules, and the extension scaffold. |
| **New pipeline extension** | Opens the settings window at the scaffold section (see Extending the asset build). |
| **Show last report** | Reopens the report of the last run, including one started by a game build. |
| **Open output folder** | Opens the last output in the file browser. |

The same switch is exposed to the gateway as `assetbuild.settings` and the tool as `assetbuild.run` (`platform`, `clean`, `copyRaw`, `output` inside the project); `assetbuild.status` and `assetbuild.clean` round it out, and `build.game compileAssets=` overrides the stored setting for one build.

## What gets compiled

| Source | Compiled as | Runtime type | Notes |
|---|---|---|---|
| `.png .jpg .bmp .tga` | `TextureImporter` + `TextureProcessor` | `Texture2D` | `PremultiplyAlpha` follows the setting; `ColorKeyEnabled` off; mipmaps off; `TextureFormat` from the setting (`Color` or `Compressed`). |
| `.aseprite .ase` | `AsepriteImporter` + `AsepriteProcessor` (Voltage.Pipeline) | `AsepriteFile` | Frames, layers, tags, slices and per-layer pixel data are flattened at build time, so the runtime skips cel decompression and compositing. |
| `.wav` | `WavImporter` + `SoundEffectProcessor` | `SoundEffect` | Only when `CompileAudio` is on; PCM `.xnb` files are larger than the source. |
| `.ogg .mp3` | copied raw | `SoundEffect` | Decoded at load time as today; `CompileAudio` converts them through `SoundEffectProcessor` when on. |
| `.fnt` + texture | `BitmapFontImporter` + `BitmapFontProcessor` (Voltage.Pipeline) | `BitmapFont` | Uses the engine's existing `BitmapFontReader`. |
| `.vtileset .vasset .vtimeline .vprefab .vscene .json .tmx .tsx .meta` | copied raw | as today | JSON and Tiled formats stay as files; the AOT-safe readers need no change. |
| `.fx .mgfxo` | untouched | `Effect` | Effects keep the separate `mgfxc` path from the Effects menu. |
| everything else | copied raw | | |

Files matched by an exclude glob, and files under `Content/Voltage` (engine content), are never compiled.

## How a build finds compiled assets

The asset build writes `Content/content.index` next to the compiled files: one line per compiled asset, `source path<TAB>asset name`, where the source path is the project-relative path the scene data and scripts use (`Content/Characters/Hero.aseprite`) and the asset name is the MGCB name relative to the content root, without extension (`Characters/Hero`). When two sources would share a name (`Hero.png` and `Hero.aseprite`), the later one gets its extension appended (`Characters/Hero_aseprite`).

`VoltageContentManager` reads the index once at startup when it exists. Every `Load*` method, `LoadByType<T>` behind `AssetReference`, and therefore every `SpriteRenderer`, `SpriteAnimator`, `TilemapRenderer`, audio component and legacy script call such as `Content.LoadTexture("Content/UI/Cursor.png")` or `AsepriteUtils.LoadAsepriteAnimation(entity, "Content/Characters/Hero.aseprite", "Idle")` first asks the index for a compiled name and loads the `.xnb` through the base `ContentManager` when one exists. Anything without an entry loads the raw file exactly as before, so a project can compile a subset, or nothing, and still run. The editor never has an index, so it never changes behaviour.

A compiled source is never shipped: the build output holds either the `.xnb` or the raw file, and files without a compiled counterpart are always copied. Game builds start from an empty output folder every time, and a standalone run empties `bin/AssetBuild/<platform>` first and removes any `.xnb` the current run did not produce, so renamed or deleted sources leave nothing behind.

## Extending the asset build

Rules decide what happens to an extension before the built-in table does, so a project can route its own file types through custom importers, or change how a built-in type is handled (route `.png` through another processor, or `copy` or `skip` a type). A rule has a name, extensions, an action (`compile`, `copy` or `skip`), the importer and processor class names, optional `Key=Value` processor parameters, and optionally the project-relative DLL that holds the classes; rules with no DLL use MonoGame's built-in importers. Rules live under `AssetBuild.Rules` in `ProjectSettings.json`, in the settings window, and on the gateway as `assetbuild.rules`, `assetbuild.rule.set` and `assetbuild.rule.remove`. Editor plugins register rules in code with `AssetBuildRules.Register`; a project rule for the same extension wins.

`Build > Asset Build > New pipeline extension` (or `assetbuild.scaffold name=Level extension=.lvl`) writes a working starting point:

- `Pipeline/Level/Level.csproj` with `LevelImporter`, `LevelProcessor` and `LevelWriter`, a .NET 8 class library referencing `MonoGame.Framework.Content.Pipeline`. The game project excludes `Pipeline/**` from its own compile (new projects carry the exclusion; the scaffold adds it to older ones).
- `Scripts/Content/LevelAsset.cs` with the runtime `LevelAsset` type, its `LevelReader`, and a module initializer that registers a raw-file loader through `VoltageContentManager.RegisterLoader`, so `[AssetType(typeof(LevelAsset))] AssetReference` fields load in the editor and in raw builds too.
- The rule `Level` for `.lvl` pointing at `Pipeline/Level/bin/Release/net8.0/Level.dll`.

When a rule's DLL sits in a folder with exactly one `.csproj`, the asset build runs `dotnet build -c Release` on it whenever a source is newer than the DLL, before generating the response file. The scaffolded project references `Voltage.Pipeline` through the `VoltagePipelineDir` MSBuild property, which the editor passes on that command line, so the csproj carries no machine-specific path; building it by hand needs `-p:VoltagePipelineDir=<editor folder>/Pipeline/`. Rules are validated wherever they come from: the DLL must sit inside the project, importer and processor must be class names, parameters must be `Key=Value`, and none of them may contain `;` or line breaks; a rule that fails a check fails its files with the reason. Extension projects must stay on .NET 8, which is what `mgcb` loads. Only the runtime file compiles with the game scripts; the pipeline project is never loaded by the editor or the game.

## Tooling

The build uses the `dotnet-mgcb` tool at the same version as the engine's MonoGame package, installed into a tool manifest inside the project (`<project>/.config/dotnet-tools.json`) with `dotnet tool restore`, which the editor runs the first time and whenever the version changes. Custom importers live in `Voltage.Pipeline.dll`, which ships beside the editor and is passed to MGCB with `/reference`. The generated response file is written to `<project>/obj/AssetBuild/<platform>.rsp` and can be inspected or run by hand with `dotnet mgcb /@:<file>`; it deliberately does not use the `.mgcb` extension, which the game's own MonoGame builder task would pick up during publish.

Requirements: the .NET SDK on the path (already needed for game builds). No Visual Studio components are needed.

## Diagnostics

The report window and `assetbuild.run` list every file with its outcome: compiled, copied, skipped by a glob, or failed with MGCB's message; a file that MGCB accepted but produced no `.xnb` for is reported as failed too. `assetbuild.status` reports the tool version, whether the manifest is restored, the last output and its index size. `log.tail` carries the same lines with the `[AssetBuild]` prefix.
