---
title: Source Generation, Serialization, and NativeAOT
sidebar_position: 1
---

# Source Generation, Serialization, and NativeAOT

## Why Components Must Be Declared `partial`

Voltage uses a Roslyn source generator (`ComponentDataGenerator`) to emit serialization code at compile time. For every `partial Component` or `partial SceneComponent` subclass that does not already override `Data`, the generator emits:

1. A nested `sealed class XxxGeneratedData : ComponentData` with one public field per serializable field on the component.
2. An override of the `Data` property — a getter that snapshots the component's current fields into a `XxxGeneratedData` instance, and a setter that applies a loaded `XxxGeneratedData` back to the component's fields.
3. A private static `Read_XxxGeneratedData(JsonTokenReader)` method — a hand-unrolled JSON parser with no reflection, safe under NativeAOT trimming.
4. A `[ModuleInitializer]` method `__RegisterDeserializer_Xxx` that registers the reader with `ComponentDataAotDeserializer` so the engine can deserialize the type by its fully-qualified name at runtime.

A second generator pipeline emits a shared `[ModuleInitializer]` in `ComponentDataAotBootstrap.AutoRegister` that registers every concrete Component and SceneComponent in `ComponentAotFactory` (so instances can be created by name without reflection).

**What this means in practice:**

- A component without `partial` has no `Data` override. Its fields serialize to nothing. In the editor this is immediately visible as "data not persisting on save/reload". In a published build the component type also cannot be instantiated by the factory, so it would not load from a scene file at all.
- The generated code uses only `new T()`, direct field assignment, and `JsonTokenReader` — no `Activator.CreateInstance`, no `GetType().GetFields()`, no LINQ. This is what makes the published build trim-safe.

## Serializable Fields

The generator picks up every `public` non-`static` field on the component (and its non-`Component` base classes, up to but not including `Component` itself). Fields of these types are supported out of the box:

| Category | Types |
|---|---|
| Primitives | `bool`, `int`, `uint`, `long`, `float`, `double`, `string` |
| Enums | Any `enum` |
| Engine value types | `Vector2`, `Color`, `RectangleF` |
| User structs | Plain structs with only public fields (no explicit constructor — see gotchas) |
| `IComponentGroup` | Classes implementing `IComponentGroup` with public fields — the generator emits both a `Read_` and a `Clone_` helper |
| Collections | `List<T>`, `Dictionary<string, TValue>`, `T[]` of any above |
| References | `ComponentReference`, `EntityReference` (resolved post-load) |

Fields marked `[JsonExclude]` are skipped entirely. Private fields are skipped unless marked `[SerializedField]` (`Voltage.SerializedFieldAttribute`).

## Serialization Attributes

| Attribute | Where | What it does |
|---|---|---|
| `[JsonExclude]` | Field or property | Excluded from serialization. Applied to `Entity.Scene`, `Component.Entity`, `Component.Transform`, and similar back-references to prevent cycles. |
| `[DecodeAlias("oldName")]` | Field in a `ComponentData` class | When loading JSON, a key matching `oldName` is mapped to this field. Use this when a field is renamed to keep old save files loading correctly. |
| `[ComponentId("alias")]` | Component / SceneComponent class | Stable, rename-proof identity — a human-readable alias (like a protobuf field number or an Orleans `[Alias]`). Scenes reference the component by this id instead of its type name, so renaming the class *or moving its namespace* no longer breaks the scenes that use it. **The editor stamps this automatically** on first compile (defaulting the id to the class's simple name), so you normally never write it by hand. The id is assigned once and **frozen** — renaming the class never changes it. The attribute is emitted by the source generator (not the engine DLL), so it always compiles even against a stale engine reference; the generator bakes the mapping into the NativeAOT build via `ComponentIdRegistry`. Don't change or reuse an id once a scene has referenced it. |
| `[FormerlyKnownAs("Old.Namespace.ClassName")]` | Component class | Legacy, name-based rename mechanism — the compatibility floor for scenes saved before a component had a `[ComponentId]`. The source generator registers the old name in `TypeRenameRegistry` so scenes referencing the old name still load. Once a component has a `[ComponentId]` and the scene stores it, renames are handled automatically and this is no longer required. |
| `[DynamicallyAccessedMembers(...)]` | Engine internals | Preserves reflection metadata for trim-sensitive code paths. You will rarely need this in game scripts. |

## FormerlyKnownAs Example

```csharp
// Class was "Jolt.Scripts.Enemies.BatEnemy", then moved to "Jolt.Scripts.BatEnemy".
[Voltage.Serialization.FormerlyKnownAs(
    "Jolt.Scripts.Enemies.BatEnemy",
    "Jolt.Scripts.BatEnemy")]
public partial class BatController : Component, IUpdatable
{
    public float Speed = 80f;
    public void Update() { /* ... */ }
}
```

Both old names are registered at module init. Any `.vscene` or `.vprefab` that references either old name will load and instantiate `BatController` correctly.

## The Build System

`GameBuilder.BuildGameAsync` in the editor orchestrates the publish:

1. Builds the engine DLLs without the `EDITOR` preprocessor symbol.
2. Runs `dotnet publish` on the game project with NativeAOT and trimming enabled.
3. Copies the `Content/` folder and the `Data/` folder (scenes, prefabs) into the build output.
4. Compiles custom effect shaders from `Effects/` using `EffectsCompiler`.

The output lands in `Build/<Configuration>/<platform>/` inside the project folder. Each `dotnet publish` target platform gets its own subfolder.

The `[ModuleInitializer]` pattern guarantees that AOT registrations run before any deserialization is attempted — no runtime reflection needed.

## Editor Hot-Reload vs Published Build

In the editor the `DynamicScripts` assembly is loaded via reflection. This is fine because the editor runs on full .NET with a JIT. When the editor recompiles scripts it sets `Core.LatestScriptAssembly` so type resolution finds the newest types.

In a published build there is no `DynamicScripts` assembly. All game code is statically compiled into the executable. The source generator's `[ModuleInitializer]` registrations replace the runtime reflection that the editor uses.
