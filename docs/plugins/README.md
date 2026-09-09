---
title: Plugins
sidebar_position: 1
description: Packages, sources, the lockfile, editor versus game loading, external SDKs, and authoring your own plugin.
---

# Plugins

Documentation for the engine's plugin system (packages, sources, the lockfile, editor vs. game loading,
external SDKs, and authoring your own).

A **plugin** is a self-contained folder with a `plugin.json` manifest at its root. It can contribute
gameplay code (DLLs or plain `.cs` sources), native libraries, content, and editor tooling. The bundled
**Farseer Physics** plugin is the reference example — the whole `FS*` component family ships as a plugin.

---

## 1. Architecture — how a plugin reaches the editor and the game

Plugins are **acquired** from a source, **cached** immutably per-user, **synced** into the project, and then
consumed by two very different hosts: the editor (which loads DLLs at runtime) and the game build (which
statically references them so they survive NativeAOT).

```mermaid
flowchart TD

  subgraph SRC["① SOURCES — declared per project in plugins.json"]
    direction LR
    BUN["Bundled<br/>ships with the editor<br/>&lt;editor&gt;/BundledPlugins/"]
    PATH["Path (dev)<br/>a folder on disk<br/>unpinned, re-synced every open"]
    GIT["Git<br/>URL + ref → pinned commit"]
    ZIP["Zip<br/>https URL"]
  end

  CACHE["PluginCache — immutable per-user cache<br/>keyed by id + version + sha256 content hash"]
  LOCK["plugins.lock.json<br/>pins Version · Commit · ContentHash<br/>(committed to git)"]

  subgraph PROJ["② PluginLibs/ — the project's payload folder (GITIGNORED)"]
    direction TB
    PAY["PluginLibs/&lt;id&gt;/<br/>lib · editor-lib · src · native · content"]
    GLUE["Generated build glue<br/>Plugins.g.props · PluginBootstrap.g.cs<br/>PluginTrimmerRoots.xml"]
  end

  subgraph HOST["③ TWO CONSUMERS"]
    direction LR
    ED["EDITOR<br/>Assembly.LoadFrom(editor-lib ?? lib)<br/>components appear in the Inspector<br/>IEditorPlugin → windows & menu items"]
    GAME["GAME BUILD<br/>&lt;Reference&gt; to lib/ (never LoadFrom —<br/>AOT images cannot load at runtime)<br/>+ bootstrap forces module initializers"]
  end

  BUN --> CACHE
  GIT --> CACHE
  ZIP --> CACHE
  PATH -. "dev: synced straight<br/>from the working folder" .-> PAY
  CACHE -->|PluginSync.SyncPlugin| PAY
  LOCK -. "verifies / short-circuits<br/>acquisition (offline-friendly)" .-> CACHE
  PAY --> GLUE
  PAY --> ED
  GLUE -->|"csproj Import (Exists-conditioned)"| GAME

  classDef src fill:#1c222b,stroke:#f2b544,stroke-width:1.5px,color:#e8ebef;
  classDef store fill:#15211f,stroke:#4fd6c9,stroke-width:1.5px,color:#e8ebef;
  classDef host fill:#241d12,stroke:#f2b544,stroke-width:1.5px,color:#f2b544;
  class BUN,PATH,GIT,ZIP src;
  class CACHE,PAY,GLUE,LOCK store;
  class ED,GAME host;
```

**The key asymmetry:** the editor can `Assembly.LoadFrom` a DLL at runtime; a published NativeAOT game
cannot. So the game gets plugins as *static MSBuild references* compiled into the image, plus a generated
bootstrap that forces each plugin's `[ModuleInitializer]` registrations to run.

---

## 2. Anatomy of a plugin package

The manifest sits at the package root and points at the folders it uses. Everything is optional except
`plugin.json` itself.

```
my-plugin/
├── plugin.json              # the manifest (required)
├── lib/                     # gameplay DLLs — net8.0, built WITHOUT the EDITOR symbol
├── editor-lib/              # optional EDITOR-flavored twins of lib/ (first-party engine modules only)
├── editor/                  # editor-plugin DLLs — reference Voltage.Editor.dll, implement IEditorPlugin
├── src/                     # source-form gameplay code, compiled together with the game's Scripts/
├── native/<rid>/            # per-RID native libraries (win-x64, osx-arm64, …)
└── content/                 # runtime content copied into the game build's Content folder
```

You ship **either** `lib/` (prebuilt) **or** `src/` (source-form). Source-form is simpler — the `.cs` files
just compile into the game — and its generated module initializers run natively, so it needs no bootstrap
entry. Prebuilt is what you want for closed-source or slow-to-compile code.

---

## 3. `plugin.json` — manifest reference

All JSON keys are **PascalCase** (`Voltage.Persistence.Json` matches field names directly).

### Top level

| Field | Type | Notes |
| --- | --- | --- |
| `SchemaVersion` | int | Currently `1`. |
| `Id` | string | **Required.** Stable, reverse-domain (`voltage.farseer`, `com.studio.fmod`). Never change it — it's the identity everywhere. |
| `Name` | string | Display name in the Plugin Manager. |
| `Version` | string | Semver (`1.2.0`). |
| `Description` | string | Shown in the Plugin Manager's Description column. |
| `Author` | string | Optional vendor name. |
| `Kinds` | string[] | **Required.** `"gameplay"` and/or `"editor"`. Decides which sections below must be present. |
| `EngineVersion` | string | Supported engine range — `"*"`, `">=0.1.0"`, `">=0.1.0 <0.2.0"`. A mismatch **warns only**; the plugin still loads. |
| `EditorPluginApiVersion` | int | Hard-checked for editor plugins — a mismatch means `Failed`. Ignored for gameplay-only plugins. |
| `Dependencies` | object[] | `{ "Id": "...", "Version": ">=1.0.0" }` — checked against the other plugins in the project. **Not auto-installed:** a dependency that isn't listed in `plugins.json` (or is disabled/unavailable/out of range) just makes *this* plugin `Unavailable`. |
| `Gameplay` | object | Required when `Kinds` contains `"gameplay"`. |
| `Editor` | object | Required when `Kinds` contains `"editor"`. |
| `ExternalSdks` | object[] | SDKs the user installs themselves (see §7). |

### `Gameplay`

| Field | Type | Notes |
| --- | --- | --- |
| `ManagedAssemblies` | string[] | Package-relative DLLs shipped into the game (runtime flavor, no `EDITOR` symbol). |
| `EditorManagedAssemblies` | string[] | Optional `EDITOR`-flavored twins of the above, same assembly names. The editor loads **these instead** when present. Only first-party engine modules with `#if EDITOR` sites need this. |
| `SourceRoots` | string[] | Folders whose `.cs` files compile together with the game's `Scripts/`. |
| `RootTypes` | string[] | One namespace-qualified public type per managed assembly. The generated bootstrap uses it to root the assembly for AOT and force its module initializers. **Auto-detected when omitted.** |
| `TrimmerRootAssemblies` | string[] | Assembly simple names preserved wholesale from trimming. Defaults to the names in `ManagedAssemblies`. |
| `Natives` | object[] | `{ "Rid": "win-x64", "Files": ["native/win-x64/*.dll"] }`. Layout convention is `native/<rid>/`. |
| `Content` | string[] | Copied into the game build's `Content/` folder. **Caveat:** only the *first path segment* is honored — `"content/**"` copies the whole `content/` tree recursively, preserving paths relative to it. The rest of the glob is ignored, so don't rely on it to filter. |

### `Editor`

| Field | Type | Notes |
| --- | --- | --- |
| `Assemblies` | string[] | Editor-plugin DLLs containing `IEditorPlugin` implementations (windows, menu items). |

### The real thing — Farseer

```json
{
	"SchemaVersion": 1,
	"Id": "voltage.farseer",
	"Name": "Farseer Physics",
	"Version": "1.0.0",
	"Description": "Full rigid-body physics engine (Box2D-lineage Farseer) with the FS* component family: FSRigidBody, FSCollisionBox/Circle/Polygon, joints, and FSWorld.",
	"Kinds": ["gameplay"],
	"EngineVersion": "*",
	"Gameplay": {
		"ManagedAssemblies": ["lib/Voltage.FarseerPhysics.dll"],
		"EditorManagedAssemblies": ["editor-lib/Voltage.FarseerPhysics.dll"],
		"RootTypes": ["Voltage.Farseer.FSRigidBody"],
		"TrimmerRootAssemblies": ["Voltage.FarseerPhysics"]
	}
}
```

The manifest is **validated on load** (`PluginManifest.Validate`): every file it references must actually
exist in the package, or the plugin goes `Unavailable` with a user-facing message. The one exception is
files that an external SDK pull is expected to produce.

---

## 4. The four project files

| File | Committed? | Written by | Purpose |
| --- | --- | --- | --- |
| `plugins.json` | **yes** | you / Plugin Manager | The wish list: which plugins this project wants, and from where. |
| `plugins.lock.json` | **yes** | the editor | The pins: exactly what was resolved (version, commit, content hash). |
| `plugins.local.json` | **no — gitignored** | the editor | Which plugins *this machine* resolves from a folder of its own. |
| `PluginLibs/` | **no — gitignored** | the editor | The materialized payloads + generated build glue. Regenerated on project open. |

### A folder on your machine is not a fact about the project

A path only means something on the machine that wrote it. Committing one makes every teammate's restore
fail on a folder only you have, so the two halves are kept apart:

* **`plugins.json`** carries the plugin's `Id` and a source a teammate can actually fetch — git, zip,
  bundled, or a path *inside the repository* (a vendored plugin travels with the checkout, so it is
  shareable).
* **`plugins.local.json`** carries your folder, and never leaves your machine.

The editor writes the split for you. Adding a plugin from a folder records the path locally and the **id**
in `plugins.json` with no source at all — so a teammate opening the project is told *"this project uses
`voltage.dialoguemaker`, and nobody has published it"* rather than opening to an empty plugin list and a
scene full of missing components. An older project with an absolute path in `plugins.json` migrates itself
on open, and the `.gitignore` rule is added at the same time.

This is the split npm draws between a dependency and `npm link`, and Cargo between a dependency and a
`paths` override. It accepts the same trade they do: two people can be running different code for one
plugin id with nothing in git recording it — which is why the Plugin Manager marks such a plugin **LOCAL**,
and **LOCAL ONLY** when nothing in `plugins.json` names it.

To make a local plugin shareable, either **publish** it (Plugin Manager → *Publish New Version*, then
*Declare for the team*, which rewrites the entry to the published tag) or **vendor** it (*Vendor*, which
copies it to `Plugins/<id>/` and declares it by relative path — what Unreal and Godot do with every plugin
by default).

`plugins.json` — the source of truth you edit:

```json
{
	"SchemaVersion": 1,
	"Plugins": [
		{
			"Id": "voltage.farseer",
			"Source": { "Bundled": true, "Git": null, "Ref": null, "Zip": null, "Path": null },
			"Dev": false,
			"Disabled": false
		}
	]
}
```

Exactly **one** of `Bundled` / `Git` / `Zip` / `Path` must be set.

> ### ⚠️ `PluginLibs/` must be gitignored
>
> It holds locally-built DLLs, generated MSBuild glue, and — for external-SDK plugins — **NDA-protected
> files that must never enter a repository**. It is 100% reproducible from `plugins.json` +
> `plugins.lock.json`, so there is nothing to gain by committing it and quite a lot to lose.
>
> New projects get the rule automatically (`ProjectStructureGenerator.CreateGitIgnoreFile`). If you have an
> older project where it's tracked, fix it once:
>
> ```sh
> git rm -r --cached PluginLibs
> printf '\nPluginLibs/\n' >> .gitignore
> ```
>
> The csproj's `Import` of `Plugins.g.props` is `Exists`-conditioned, so a fresh clone without `PluginLibs/`
> builds fine — but **open the project in the editor once** before building the game from an IDE, so the
> glue gets regenerated.

---

## 5. Lifecycle — what happens when a project opens

`PluginManager.RestoreForProject` runs this sequence. Every plugin is independent: one bad plugin never
blocks the project from opening.

```mermaid
flowchart TB

  A["Read plugins.json + plugins.lock.json"] --> B{"Disabled?"}
  B -->|yes| DIS(["Disabled"])
  B -->|no| C["PluginResolver.Resolve<br/>acquire from source, verify against the lock"]
  C -->|throws| UNAV(["Unavailable<br/>(missing source, no repo access,<br/>SDK not configured…)"])
  C --> D["CheckEngineVersion"]
  D --> E["PluginSync.SyncPlugin<br/>mirror payload → PluginLibs/&lt;id&gt;/<br/>+ pull external SDK files"]
  E --> RES(["Restored"])
  RES --> F["UpdateLockEntry → save plugins.lock.json (only if changed)"]
  F --> G["CheckDependencies · RemoveStalePayloads"]
  G --> H["LoadGameplayAssemblies<br/>Assembly.LoadFrom(editor-lib ?? lib)"]
  H -->|throws| FAIL(["Failed"])
  H --> LOAD(["Loaded"])
  LOAD --> I["EditorPluginHost.InitializePlugins<br/>IEditorPlugin windows / menu items"]
  I --> J["PluginSync.GenerateBuildFiles<br/>Plugins.g.props · PluginBootstrap.g.cs · trimmer roots"]

  classDef ok fill:#15211f,stroke:#4fd6c9,stroke-width:1.5px,color:#e8ebef;
  classDef bad fill:#2b1c1c,stroke:#d6584f,stroke-width:1.4px,color:#e8ebef;
  classDef step fill:#1c222b,stroke:#3a4453,stroke-width:1.2px,color:#e8ebef;
  class RES,LOAD ok;
  class UNAV,FAIL,DIS bad;
  class A,C,D,E,F,G,H,I,J step;
```

**States** (`PluginState`): `Disabled` · `Restored` (payload synced) · `Loaded` (assemblies in the editor) ·
`Unavailable` (couldn't acquire/verify) · `Failed` (acquired, but loading blew up).

A game build refuses to start if any plugin is `Unavailable` or `Failed` (`PluginSync.SyncForBuild`) —
silently shipping a game missing a plugin its scenes depend on is worse than a red build.

> **Plugin assemblies never unload.** They're `Assembly.LoadFrom`'d into the **default** `AssemblyLoadContext`,
> not a collectible one. So enabling, disabling, updating, or removing a plugin takes effect in the editor only
> after you **reopen the project** (or restart the editor). The Plugin Manager says as much when you act. Dev
> plugins are the exception for *source* changes — those hot-reload (see §9).

Removing a plugin is non-destructive to your scenes: entities keep their component data as
missing-component entries, and re-adding the plugin restores them. Nothing is silently dropped on save.

---

## 6. The four sources

| Source | Acquired from | Pinned by | Cached? |
| --- | --- | --- | --- |
| **Bundled** | `<editor>/BundledPlugins/<folder>/` | editor `Version` | n/a — read in place |
| **Path** | a folder on disk | *(nothing — see Dev mode)* | no |
| **Git** | `git` URL + `Ref` (tag/branch/SHA) | resolved `Commit` + `ContentHash` | yes |
| **Zip** | https URL | `ContentHash` | yes |

Git, Zip, and non-dev Path payloads are hashed and copied into an immutable per-user cache at
`<storage root>/PluginCache/<id>/<version>+<first-8-of-hash>/` — cross-project, written once. On the next
open, if the lock pins that exact payload and the cache already holds it, resolution short-circuits with
**no network access at all** — offline-friendly by design. A hash that doesn't match the lock is a **hard
error**: the payload changed underneath a pin, which is exactly what a lockfile exists to catch.

The hash is sha256 over each file's lowercased relative path plus its raw bytes, files sorted ordinally,
with `.git/` and `.DS_Store` excluded — so the *same* payload fetched via git and via zip hashes identically.

**Git uses your own `git` CLI** on `PATH`, with your ambient credentials (SSH agent, credential helper). The
editor never handles a token, which is precisely what lets a private/NDA plugin repo work with zero
credential plumbing. The `Ref` (tag, branch, or SHA) is resolved to a **full commit SHA** and that SHA is
what goes in the lock — so a force-pushed tag can never silently hand your teammates different code.

**Zip** accepts both common layouts: `plugin.json` at the archive root, or nested inside a single top-level
folder (GitHub's "Download ZIP" shape).

**Path also accepts a source checkout, not just a built package.** A published plugin is `plugin.json`
next to the assemblies it declares; a plugin *repository* is the same `plugin.json` next to the
**sources**, with `/lib/`, `/editor-lib/` and `/editor/` gitignored because CI builds them for a tagged
release. Clone one and add it as a local folder and every declared assembly is missing, so the manifest
cannot validate.

So when — and only when — a local folder is missing exactly the files its own packaging target produces,
the editor runs that target first (`PluginSourceBuild`): `dotnet restore` on each project at the plugin
root, then `dotnet msbuild <project> -t:PackagePlugin`, with `VoltageEnginePath` and `VoltageEditorPath`
pointed at the running editor's own folder — so a plugin can never be built against a different Voltage
than the one about to load it. The first build takes a few minutes; progress and failures go to the Plugin
Manager's **Messages**.

It triggers on files being *absent*, never on them looking stale. Rebuilding whenever a `.cs` looked newer
would mean an unpredictable multi-minute build on project open; picking up your own edits stays an
explicit rebuild plus a restart, exactly as it is for every other plugin (assemblies cannot unload).

If the folder has no project exposing `PackagePlugin`, the error says the folder is a source checkout and
what to do about it, instead of reporting a missing file as though the plugin were broken.

**Bundled plugins are deliberately not content-pinned.** Their payload is compiled by the editor build on
each machine, and .NET assemblies embed absolute source paths, so the bytes — and therefore the hash —
differ per machine. Recording that hash would make `plugins.lock.json` churn on every clone. They pin on the
editor-provided `Version` instead (`ResolvedPlugin.IsPinnable = false`).

---

## 7. External SDKs (FMOD, console SDKs, anything under NDA)

Some SDKs cannot be redistributed. A plugin declares them, and the files are pulled from the *user's own*
install at sync time — they never enter the package, the cache, or git.

```json
"ExternalSdks": [
	{
		"Id": "fmod",
		"DisplayName": "FMOD Engine",
		"EnvVar": "FMOD_SDK",
		"Required": true,
		"Pulls": [
			{ "From": "api/core/lib/x64/fmod.dll", "To": "native/win-x64/" }
		]
	}
]
```

The path resolves from the per-user setting (`PluginSdk_<Id>`, set in the Plugin Manager) or falls back to
`EnvVar`. A `Required` SDK that isn't configured makes the plugin **Unavailable** with a message telling the
user exactly what to set. Pulls re-run on *every* sync — the user may configure the path after the first
open — and afterwards every manifest-listed file is verified to actually exist, so a pull list that lies is
a loud failure rather than a mysterious one later.

This is the reason `PluginLibs/` being gitignored is a correctness requirement, not a tidiness preference.

---

## 8. Editor vs. runtime assemblies

Engine modules sometimes need editor-only code (gizmos, inspector hooks) behind `#if EDITOR`. That means two
builds of the same assembly:

- `lib/Foo.dll` — the runtime flavor, **no** `EDITOR` symbol. This is what ships in the game.
- `editor-lib/Foo.dll` — same assembly name, compiled **with** `EDITOR`. The editor loads this one when it
  exists, falling back to `lib/` otherwise.

Third-party plugins almost never need `editor-lib/` — it exists for first-party engine modules like Farseer,
whose `BuildBundledPlugins` target in `Voltage.Editor.csproj` compiles the project twice (`Release` and
`Editor-Release`) into the two folders.

For the **game**, `PluginSync.GenerateBuildFiles` writes `PluginLibs/Plugins.g.props` with a `<Reference>`
per managed assembly and a `TrimmerRootDescriptor` per plugin, and `PluginBootstrap.g.cs` with a
`[ModuleInitializer]` that calls `RunModuleConstructor` on each root type. That last part is subtle and worth
knowing: on CoreCLR a bare `typeof(X)` does **not** trigger a module initializer (ECMA-335 runs them on first
static-member access or method invocation), so without the explicit call, a plugin's component registrations
would silently never happen.

---

## 9. Authoring a new plugin

The fastest path is **Plugin Manager → Create New Plugin…**, which scaffolds the folder and manifest for you.
By hand, the minimum source-form plugin is two files:

```
my-plugin/
├── plugin.json
└── src/
    └── MyComponent.cs
```

```json
{
	"SchemaVersion": 1,
	"Id": "com.studio.myplugin",
	"Name": "My Plugin",
	"Version": "1.0.0",
	"Kinds": ["gameplay"],
	"Gameplay": { "SourceRoots": ["src"] }
}
```

Then add it to the project as a **Path** source with `Dev: true` and iterate — your `.cs` files compile
straight into the game alongside `Scripts/`, and components show up in the Inspector like any other.

Components in a plugin follow the same two rules as components in `Scripts/`: the class must be `partial`
(the source generator emits AOT-safe serialization into the other half), and it should carry a
`[ComponentId("…")]` — the stable on-disk identity that survives class and namespace renames. Get that wrong
and scenes will lose their data on the next rename.

To graduate to a prebuilt plugin, build your code to a `net8.0` DLL, drop it in `lib/`, and swap
`SourceRoots` for `ManagedAssemblies`. Add `RootTypes` only if auto-detection picks the wrong type. Your
assembly's simple name must not collide with an engine DLL or another plugin's — that's a hard failure, since
one would otherwise silently shadow the other.

### Editor plugins (`"Kinds": ["editor"]`)

An editor plugin ships DLLs in `editor/` that reference `Voltage.Editor.dll` and expose a public class with a
parameterless constructor implementing **`IEditorPlugin`**:

```csharp
void Initialize(IEditorPluginContext context);  // register windows / menu items; throwing disables the plugin
void Shutdown();                                // project close or editor exit
```

`IEditorPluginContext` hands you `RegisterWindow(EditorPluginWindow)`, `AddMenuItem(path, onClick)` (a `/`
nests submenus, e.g. `"FMOD/Event Browser"`), the `ImGuiManager`, the `CurrentProject`, and a `ProjectClosing`
event. `EditorPluginWindow` is abstract — you override `Title` and `Draw()`, and own your own `ImGui.Begin`/
`End` (pass `ref IsOpen` to `Begin`).

The editor plugin API is **explicitly unstable**. `EditorPluginApiVersion` in the manifest is hard-checked
against the editor's — a mismatch puts the plugin in `Failed` rather than crashing the editor. Note the Create
wizard can't auto-add an editor plugin to a project, because its DLL doesn't exist until you build it: build
first, then add the folder.

### Dev mode (`"Dev": true`)

You no longer ask for this. Adding a folder that holds a project exposing the `PackagePlugin` target — a
plugin *source checkout* rather than a built package — sets it automatically, because the alternative is
strictly worse for a checkout: a fresh cache copy on every rebuild, and a pin on an artifact whose hash no
other machine can reproduce. The flag still exists in `plugins.local.json`, and hand-editing it works.

For a Path source, dev mode is the live-edit workflow. It means:

- **Unpinned.** No `ContentHash` is recorded and any stale pin is dropped from the lockfile — no hash
  verification ever runs, so you're never fighting the lock while iterating.
- **No cache copy.** The payload folder *is* your working folder.
- **The editor compiles your sources straight from the working folder**, not the `PluginLibs` mirror — so
  edits take effect without waiting for a re-sync.
- **Hot reload.** `ScriptWatcher` watches dev plugin source roots (and only those); saving a `.cs` recompiles
  like any game script.
- **Writable.** Non-dev plugin sources are treated as immutable installs, so the ComponentIdStamper won't
  rewrite them; dev sources it will.
- Game builds re-mirror dev plugins right before building, so the build always sees your latest code.

### Rebuilding a checkout against the editor

A source checkout is built once, when its declared assemblies are found missing. After that, every edit you
make to it — and every rebuild of the editor — used to leave those assemblies exactly as they were, so the
plugin you were writing was the one plugin guaranteed not to be running your code.

A checkout is now rebuilt when its sources are newer than what was built from them, **or when the editor's
own assemblies are newer than the plugin's** — the second is what makes a plugin come back rebuilt against
the editor after you rebuild the editor. It happens at two moments:

* **When the editor is built.** List your checkouts in `Voltage.Editor/dev-plugins.txt` (one folder per
  line, `#` comments, gitignored) or pass `-p:VoltageDevPlugins="C:\src\A;C:\src\B"`. Skip it for one build
  with `-p:VoltageSkipDevPlugins=true`. A folder that has moved is logged and skipped, never a build failure.
* **At project open**, before anything is resolved or loaded — because plugin assemblies load through
  `Assembly.LoadFrom`, which is not collectible, so a rebuild after that point could not be swapped in.
  Only stale checkouts build, so the usual cost is a directory walk.

Editing a plugin while the editor is *running* still needs a restart. That is the same limitation, not an
oversight.

### When a teammate is missing a plugin

Opening a project that declares plugins the machine cannot use raises one dialog listing them, with
**Install All** for anything fetchable. It distinguishes what it cannot fix from what it can:

| What it says | What it means | What to do |
| --- | --- | --- |
| a git/zip source | a download | **Install All** |
| *not published yet* | somebody has it as a folder and never shared it | publish or vendor it, or **Browse** to a copy you have |
| *your own folder is missing* | your `plugins.local.json` points somewhere that has gone | **Browse** to it, or **Forget local folder** |
| missing from the repository | a vendored folder that is not in the checkout | pull; the checkout is short of files |

It is raised whether or not the Plugin Manager is open, dismissal is remembered until the set of missing
plugins actually changes, and `Plugins → Restore Plugins` brings it back.

---

## 10. The Plugin Manager window

`Plugins → Plugin Manager`. From here you can:

- **Browse Plugins** — search the registries and install with one click. Each result is a collapsed row;
  expand it for the description, tags and source registry.
- **Add Plugin** — pick a source: local folder, git URL + ref, or zip URL. There is no bundled option:
  nothing ships inside the editor. `PluginResolver` still resolves `Bundled` entries so an older
  `plugins.json` keeps working.
- **Create New Plugin…** — scaffold a fresh plugin package (`plugin.json` + starter code), optionally adding
  it to the project as a live-edit (dev) plugin.
- **Enable / Disable** — flips `Disabled` in `plugins.json` without removing the entry.
- **Update** — re-resolves the source (latest ref / zip content / folder) and re-pins `plugins.lock.json`.
  Offered for any non-bundled, non-dev plugin; bundled plugins version with the editor and dev plugins
  re-sync automatically, so neither has anything to update.
- **Remove** — drops the plugin. Scenes using its components show missing-component entries; **the data is
  preserved**, so re-adding the plugin restores them.
- **Configure SDK paths** — for plugins with `ExternalSdks`.

---

## 11. Publishing a release

Pushing source is not publishing. A plugin appears in **Browse Plugins** only once it is listed in a
registry, and it can only be installed once that listing points at a release asset. Three steps:

**1. Bump `plugin.json` and tag.** The tag must equal `Version` exactly — the release workflow compares
them and fails the build otherwise, because a tag that disagrees with the manifest ships a mislabelled
package.

```bash
# plugin.json says "Version": "0.1.0"
git push origin main
git tag -a v0.1.0 -m "MyPlugin 0.1.0"
git push origin v0.1.0
```

**2. CI builds and attaches the package.** `.github/workflows/release.yml` checks out the engine, runs
`PackagePlugin`, and attaches `<id>-<version>.zip` to the release. Watch it:

```bash
gh run watch --repo <owner>/<repo> --exit-status
gh release view v0.1.0 --repo <owner>/<repo> --json assets
```

**3. The registry entry.** `release.yml` calls `.github/workflows/registry.yml` as a final job, which
opens a pull request against the registry with an entry derived from `plugin.json` and the release asset.
Merge it and the plugin shows up in Browse Plugins.

That workflow needs a `REGISTRY_TOKEN` secret on the plugin repository — a fine-grained PAT with
`Contents: write` and `Pull requests: write` on the registry repo. Without it the job does not fail: it
warns and prints the exact JSON entry in the run summary so you can open the pull request by hand. An
update to an existing entry keeps hand-curated `Tags`, `Homepage` and `Description`; only the
release-derived fields are overwritten.

> **Why it is *called* rather than triggered.** `registry.yml` also declares `release: published`, and
> that trigger does not fire for a release the Release workflow created: GitHub raises no workflow events
> for anything done with the default `GITHUB_TOKEN`, so that a workflow cannot trigger itself. A plugin
> whose release workflow succeeded would therefore still never reach the catalogue, and the only visible
> symptom is Browse Plugins quietly continuing to offer the previous version. Chaining the job with
> `uses:` is what makes it run. The `release: published` trigger is still worth keeping for a release
> published by hand, which does fire.

### Publish Readiness

The Plugin Manager has a **Publish Readiness** panel for any plugin added from a local folder. Press
**Check** and it reports which step is missing and the command that fixes it: working tree, tag, tag
pushed, release asset, registry listing. It is read-only - it runs your own `git` and makes at most one
anonymous GitHub request per check, holds no credentials, and never writes anything.

Its first check is **ownership**, and that is the one worth understanding. Having a plugin as a local
folder proves nothing about authorship: it is equally true of someone else's plugin you cloned to modify.
**The `Id` is the identity.** If a registry already publishes that id from a different repository, this
checkout is a fork, and releasing under the same id would shadow the original everywhere the id is
resolved - in `plugins.json`, the lockfile, and every scene reference. In that case the panel blocks and
tells you to choose your own id first.

There is no "author" flag anywhere, because a flag would be self-asserted and worth nothing. Ownership is
whoever controls the repository the registry points at, and the registry is curated by pull request.

### Why a registry rather than scanning an organisation

Listing an org's repositories and reading their `plugin.json` sounds simpler, and it is worse:

- It only ever covers one organisation. A registry is a list of URLs, and the editor reads **several**, so
  a studio's internal catalogue sits beside the official one and a community plugin can live anywhere.
- The unauthenticated GitHub API allows 60 requests an hour per IP. Discovery needs the repo list plus a
  manifest and a release lookup per repo; an office behind one NAT is throttled almost immediately. A
  registry is one request for a static file.
- A repository having a `plugin.json` does not mean there is anything installable. Discovery would list
  plugins whose Install always fails.
- Nothing distinguishes a production plugin from an experiment or an abandoned fork.

---

## Rules of thumb

- **Commit `plugins.json` and `plugins.lock.json`. Never commit `PluginLibs/`.** It's regenerated, it's
  machine-specific, and it may contain NDA files.
- **`Id` is forever.** It keys the lockfile, the payload folder, and every scene reference. Renaming it
  orphans everything.
- **Prefer `src/` while developing, `lib/` when shipping.** Source-form needs no bootstrap and no trimmer
  roots; you can always switch later.
- **A fresh clone needs one editor open** before the game project will build with plugins from an IDE.
- **Don't hand-edit `plugins.lock.json`.** Use *Update* in the Plugin Manager, or delete the entry and
  reopen.

---

## 12. Engine versions

`VoltageVersion.Engine` is the single source of truth for every version check: whether a plugin's
`EngineVersion` range is satisfied, and whether a project was written by a newer editor than the one
opening it. All of it is inert if the constant is not bumped, so the release workflow refuses to build a
tag that disagrees with it.

Cutting an engine release:

```bash
# 1. Bump the constant in Voltage.Engine/VoltageVersion.cs, commit, push to main.
# 2. Tag it identically.
git tag -a v0.2.0 -m "Voltage 0.2.0"
git push origin v0.2.0
```

CI on `main` builds both engine configurations and the editor, and asserts the source generator is copied
next to `Voltage.dll` - a plugin shipping a compiled assembly references it from there, and if it stops
being copied those plugins silently build without their generated `ComponentData` rather than failing.

### Projects move forward, not back

A project records the engine version it targets. Opening it with an older editor warns; opening with a
newer one offers to move the project up, which rewrites the field and is committed like any other change.

**Downgrading is refused.** It is not the reverse of an upgrade: the project may already contain scenes,
assets and plugin pins the older build cannot read, and the version field is the only thing that would
have warned about it. Lowering it removes the warning while leaving the unreadable content in place. The
rule is enforced in `ProjectEngineVersion.StampCurrentVersion` rather than in whichever window draws the
button, so a launcher gets the same guarantee.
