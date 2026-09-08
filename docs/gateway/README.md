# Editor Gateway

The gateway lets external tools, scripts and AI agents drive a running Voltage Editor. It is a loopback TCP server inside the editor process; every request is executed on the game thread between frames, so handlers see the same state the editor UI does and their edits land in the undo history.

## Connecting

The editor starts the gateway on `127.0.0.1:47800` and writes `gateway.json` next to `Settings.json` in the editor data folder (`%APPDATA%\VoltageEngine\Editor` on Windows):

```json
{ "port": 47800, "token": "…", "pid": 12345, "started": "…", "exe": "…\\Voltage.Editor.exe", "args": [], "logs": "…\\Logs" }
```

The file is left behind when the editor exits: the pid tells clients it is gone, and the exe path is what `voltage start` relaunches.

Command-line flags: `--gateway-port N` picks another port (`0` = any free port) and `--no-gateway` disables it.

The protocol is newline-delimited UTF-8 JSON. The first line a client sends must be `{"auth":"<token>"}`; the server answers with a `hello` event. After that:

```
→ {"id": 1, "method": "entity.create", "params": {"name": "Player", "x": 100}}
← {"id": 1, "ok": true, "result": {…}}
← {"id": 2, "ok": false, "error": "entity not found: Foo"}
← {"event": "log", "data": {"type": "Warn", "message": "…", "time": "…", "caller": "Scene", "line": 42}}
```

`id` is echoed back verbatim and may be any JSON value. Requests without an id still run but get no reply. Events are only sent to clients that asked for them (`log.subscribe`).

While at least one client is connected the editor keeps updating and drawing when it loses focus, so an agent can work while the user is in another window. A minimized window still updates but skips drawing.

## CLI

`Voltage.Cli` builds to `voltage`, a thin client that reads `gateway.json` for you:

```
voltage status
voltage help                      # every method with its help text
voltage entity.list filter=Enemy
voltage entity.set entity=Player x=0 y=0 rotation=90
voltage scripts.compile reloadScene=true
voltage logs --follow --level Error
voltage pipe                      # one JSON request per stdin line; ideal for agents
voltage start [project.voltage]   # relaunch the last editor and wait for its gateway
voltage editor.exit force=true    # quit it again
```

Values are parsed as JSON when they look like it (`true`, `12.5`, `[1,2]`, `{"x":1}`) and as strings otherwise. `--timeout <sec>` applies to every request; `--exe <path>` tells `start` where the editor is the first time.

## MCP

`voltage mcp` is a Model Context Protocol server over stdio. Every gateway method becomes a tool with dots replaced by underscores (`entity_create`), with the `params:` part of its help turned into the input schema. `screenshot` returns the PNG as image content so a multimodal agent can look at the editor. The tool list is cached in `gateway-tools.json` so it exists before the editor is up; the connection is made lazily and re-made if the editor restarts.

```
claude mcp add voltage -- C:\path\to\voltage.exe mcp
```

## Methods

| Group | Methods |
|---|---|
| Editor | `ping`, `commands`, `status`, `editor.exit`, `undo`, `redo`, `undo.history`, `window.list`, `window.show`, `ui.info` |
| Console and log | `console.exec`, `console.commands`, `log.tail`, `log.subscribe`, `log.unsubscribe`, `log.clear` |
| Project and scene | `project.info`, `project.recent`, `project.load`, `scene.list`, `scene.info`, `scene.load`, `scene.save`, `scene.reload`, `scene.create` |
| Entities | `entity.list`, `entity.get`, `entity.create`, `entity.delete`, `entity.set`, `entity.select`, `entity.deselect`, `entity.selected` |
| Components | `component.types`, `component.add`, `component.remove`, `component.get`, `component.set` |
| Play and scripts | `play.state`, `play.start`, `play.stop`, `play.pause`, `play.reset`, `scripts.compile`, `scripts.types`, `screenshot` |
| Input and window | `input.state`, `input.move`, `input.click`, `input.down`, `input.up`, `input.drag`, `input.scroll`, `input.key`, `input.type`, `input.wait`, `input.script`, `input.release`, `hotkey.list`, `hotkey.press`, `ui.info`, `ui.resize`, `ui.maximize` |
| Scene components | `scenecomponent.types`, `scenecomponent.list`, `scenecomponent.add`, `scenecomponent.remove`, `scenecomponent.get`, `scenecomponent.set` |
| Data assets | `data.types`, `data.get`, `data.set`, `data.create` |
| View | `view.get`, `view.set`, `view.focus` |
| Assets and builds | `asset.list`, `asset.get`, `asset.refresh`, `asset.kinds`, `asset.drop`, `build.platforms`, `build.game`, `build.run` |
| Events and testing | `events.subscribe`, `events.unsubscribe`, `debug.crash` |

`component.set`, `scenecomponent.set` and `data.set` share one value language: numbers, strings, booleans, enums by name, `{x,y}` for vectors, `{r,g,b,a}` for colours, an asset path or GUID for asset and prefab references, an entity key for entity references and Entity/Transform fields, and `Entity/Type` for component references. Component and scene-component edits are undoable; data asset edits are written straight to disk through the shared asset cache, so an open Data Asset window for the same file sees them too. On `entity.set`, only the position change is undoable; the other fields mark the scene dirty without an undo entry. `component.get` lists every member the gateway can write under `values`.

`view.focus entity=Player zoom=2` points the game view at something before a screenshot; `screenshot x=0 y=0 width=400 height=300` crops a window region so small text stays legible at reduced scale.

`asset.drop` does what dragging from the Asset Browser does: a prefab instantiates, a texture or Aseprite file becomes a sprite entity, and the result is selected and undoable. `build.game` runs the same publish the Build window does and answers with the output folder and executable. `events.subscribe` streams `editor` events (`scene.loaded`, `scene.saved`, `play.started`, `play.stopped`, `scripts.compiled`, `project.loaded`, …); `voltage watch` prints them.

Entities are addressed by numeric id, persistent GUID or name (`entity=Player`). Numeric ids are runtime counters that change on every scene load, so prefer the GUID or name when a reference must outlive a reload. `scene.load` refuses to discard unsaved changes unless `force=true`. `project.load`, `scripts.compile`, `screenshot` and every `input.*` command answer once the work has actually finished.

## Navigating like a person

`ui.resize width=1600 height=900` fixes the window size first, so coordinates learned from one screenshot stay valid for the next. `screenshot` captures the whole editor window after the next frame (`window=false` gives the game view only). `scale=0.5` returns a quarter of the pixels, which is usually still readable and much cheaper for a model to look at; coordinates in the image times `1/scale` are window pixels. `ui.info` reports the window size and cursor position.

The `input.*` commands feed the engine's own input state, which ImGui and the game both read, so a synthetic click lands on the real widget, fires the real handler, and produces the real undo entry. Steps are spaced across frames the way ImGui needs (press, settle, release). Once any synthetic input has been sent the gateway owns the mouse and keyboard until `input.release`, so a stray hand on the physical mouse cannot fight the agent; the devices are handed back automatically when the last client disconnects.

`input.script` runs a list of steps in one request:

```json
{"steps": [
  {"action": "click", "x": 14, "y": 8},
  {"action": "wait", "frames": 5},
  {"action": "type", "text": "Hello\n"},
  {"action": "key", "key": "s", "modifiers": ["ctrl"]},
  {"action": "release"}
]}
```

`hotkey.list` shows the editor's rebindable commands and `hotkey.press id=Global.SaveScene` presses whatever keys are bound to one.

## Crashes

An unhandled exception writes `crash_<timestamp>.log` (with the tail of the editor log) to the logs folder and exits the process immediately, so no OS error dialog waits for a person. The CLI names the crash log when it finds the editor gone, and `voltage start` brings it back with the same arguments. `debug.crash confirm=true` exercises that whole path on purpose.

Claude Code picks the MCP server up from `.mcp.json` in the repo root, which runs the Editor-Debug build of the CLI through `dotnet`.

## Adding a method

Handlers live in `Voltage.Editor/Gateway/Commands`. Register one with `table.Add(name, help, (args, ctx) => …)`; return any serializable object, throw `GatewayException` for a user-facing error, or return a `Task<object>` when the answer depends on a later frame or event. Write the help as `What it does. params: a, b=default (note)` and the MCP schema follows automatically.

## Security

The listener binds to loopback only and requires the per-session token from `gateway.json`. Anything that can read that file can compile and run code through the editor, which is the same trust level as the user account itself.
