# rhino-cc

A Rhino plugin (`RhinoCC.rhp`) that puts an **agent chat inside Rhino** and lets the agent drive Rhino natively.
Claude Code and Codex are both supported. It is the Rhino counterpart of
[frederic-coultier](https://github.com/alinflorin/frederic-coultier) (same chat design, same "one fake CLI" idea) and a port of the
command set of [Tectopus / RhinoMCP](https://github.com/Creative-Data-Critical-Design/tectopus) (Apache-2.0): only the commands that work on both Rhino versions were kept, driven through the fake CLI instead of its TCP bridge and ~45 MCP tools.

Owner: Alin-Florin Ciu. Don't commit or push unless asked.

## The idea

- The plugin hosts a **loopback MCP server** (`127.0.0.1:45600`, port in settings). It exposes **two tools only**: `read_instructions` and `exec_rhino`.
  The built-in chat uses `/mcp/embedded`, which exposes **only `exec_rhino`**; the manual is already in the agent's system prompt.
  External agents can use `/mcp` (both tools; `exec_rhino` refuses until `read_instructions` was called, as in frederic-coultier).
- `exec_rhino` runs a **fake CLI** (`rhino`), parsed by us: `status`, `add box 20 10 5 --at 0,0,2.5`, `shot --view top`, and so on.
  Its reference (`RhinoCli.Reference()`) is **generated from the command specs** and sent as part of the agent's instructions, so docs cannot drift from code.
  **Add a command = register one `CommandSpec`; do not add MCP tools.**
- The chat spawns the user's own `claude -p` / `codex exec --json` as a subprocess per turn, points it at the MCP server, parses its JSON stream into
  events, and renders them. Per-conversation agent / model / effort; a settings dialog holds the defaults, executable paths, port, own-tools switch and custom instructions.
- Conversations are stored **inside the .3dm** (`doc.Strings`, keys `RhinoCC.index` and `RhinoCC.conversation.<id>`). Picture data is stripped on save so the model file doesn't bloat.
  Moving a conversation to another agent starts that agent without memory, so the first turn is prefixed with a short transcript (`Primer`).

## Chat features

- **Pictures:** `Attach…` button, Ctrl+V (clipboard image) and drag-and-drop of png/jpg/gif/webp (max 5 MB each). Claude gets them as stream-json input (`--input-format stream-json`), Codex as `-i FILE`.
  Persisted conversations keep only a `[picture: name]` placeholder.
- **Default instructions** live in `src/RhinoCC/Chat/DefaultInstructions.md` (embedded resource): a jewelry-designer workflow, intake questions, design loop, the Elegoo Mars 5 resin -> casting production flow
  and design rules, adapted from frederic-coultier's `fc-cli.md` with the OpenSCAD / knowledge-base / supports / FC-specific parts removed. The user can override them in the settings;
  a stored copy of the old stub default is migrated automatically. The generated command reference is appended after them.

## Layout

| Path | What |
|---|---|
| `src/RhinoCC.Core` | `netstandard2.0`, **no RhinoCommon**. Fake CLI (`Cli/`), MCP server (`Mcp/`), engines + process launching (`Agents/`), conversation model/store/renderer (`Conversations/`). |
| `src/RhinoCC` | The plugin: `net48` (Rhino 7) + `net7.0-windows` (Rhino 8). `Cli/RhinoCommands.cs` = the commands that work on both; `Chat/` is the Eto panel, controller, settings; `Commands/` the Rhino commands `RhinoCCChat`, `RhinoCCSettings`. |
| `src/RhinoCC.Tests` | xunit on `net10.0`; tests Core only (Rhino can't run in CI). |
| `build.sh` | Tests + build. `./build.sh all` also builds `net48`. |

Rule of thumb: logic that doesn't need Rhino goes in Core and gets a test. Only RhinoCommon-touching code lives in the plugin project.

## Build, test

    ./build.sh                    # tests + net7.0-windows build
    ./build.sh all                # + net48
    dotnet test src/RhinoCC.Tests # 33 tests, none need Rhino or network
    RHINOCC_LIVE=claude dotnet test src/RhinoCC.Tests --filter Live   # runs the real claude CLI against a fake Rhino (uses your account); RHINOCC_LIVE=codex likewise

Builds on Linux (`EnableWindowsTargeting`). RhinoCommon versions differ per target on purpose: `net48` -> 7.13, `net7.0-windows` -> 8.17
(RhinoCommon 7 only ships a net48 library). Both reference it with `ExcludeAssets=runtime`; Rhino provides RhinoCommon, Eto and Newtonsoft.Json.

## Environment: Rhino 8 in Wine on Linux (the owner's setup)

- Load via `PlugInManager` > Install: `Z:\home\alin\rhino-cc\src\RhinoCC\bin\Debug\net7.0-windows\RhinoCC.rhp` (`build.sh` prints it). Close Rhino before rebuilding (the `.rhp` is locked).
- `claude` and `codex` are installed on the **Linux host**. Under Wine (detected via `ntdll!wine_get_version`, `ProcessHost.IsWine`) the plugin starts
  `Z:\bin\sh -lc 'cd /tmp && exec "$0" "$@"' <program> <args>`, so Wine launches the Unix shell, which gives the CLI the host login PATH.
  If `claude` isn't on that PATH, set the full path in the settings. Prompts go over stdin.
  Things that can't ride on a command line go through an **exchange folder** (`Turn.ExchangeLocalDir` / `ExchangeHostDir`): `Z:\tmp\rhinocc` as the plugin sees it, `/tmp/rhinocc` as the CLI does
  (on native Windows both are `%TEMP%\rhinocc`). Only Codex's pictures (`-i`) go there; **instructions are never files** (owner's decision), they are command-line arguments for both agents.
  Instructions share the Windows 32K command line (Claude: `--append-system-prompt`, Codex: `-c developer_instructions=...`), so both engines cap them at 29,000 characters (`InstructionLimit`);
  the default prompt plus the generated reference is kept well under that, so keep `DefaultInstructions.md` short.
- Only the .NET Core side (Rhino 8) can be debugged here; see `.vscode/launch.json` (netcoredbg under Wine, attach on 4711, `sourceFileMap` Z:\home -> /home).
- The MCP server is a hand-rolled `TcpListener` HTTP server, deliberately not `HttpListener` (http.sys is shaky in Wine).

## Decisions made with the owner

- **UI is native Eto, not a WebView.** Rhino 7's Eto WebView is the old IE engine and McNeel won't add WebView2 to Rhino 7, so one shared codebase has to be native Eto.
  The transcript is a read-only text area (selectable/copyable); the latest picture from `shot` shows in an image view under it.
- **Storage inside the .3dm** (not sidecar/appdata).
- **Command scope: only what works on both Rhinos.** Document (`status save undo redo plugins`), layers, objects (`objects info add`), editing (`move rotate scale copy delete set select`), `shot views` (`Viewing`), `cmd script log`.
  The vendored Tectopus tools (scene/pose/contacts/mass/stability, Kangaroo) were tried and **removed** because they don't compile or run on Rhino 7 (see below). A backup was kept only outside the repo (they were never committed), so recover from the Tectopus repo if needed.
- **Plugin loads at startup** (`LoadTime => AtStartup`) and opens the chat panel on the first idle (setting, default on). Settings has a `Check agents` button (`ProcessHost.Probe`, runs `--version`).

## Not verified yet (nobody has run this inside Rhino)

1. The plugin has only been **compiled**, never loaded in Rhino 7 or 8. First run: `RhinoCCChat`, then send a message. Expect to fix Eto layout quirks.
2. Launching Unix programs through Wine with piped stdin/stdout via `Z:\bin\sh` is untested.
3. The real `claude` CLI works end to end against the MCP server (live test passed with haiku). **Codex has not been run** (not installed here); its flags (`-c` overrides, `developer_instructions`, `default_tools_approval_mode`) are copied from frederic-coultier's working code.
4. `Newtonsoft.Json` is pinned to 9.0.1 in Core so Rhino 7 can satisfy it (assumes Rhino 7 ships ~9.x). If Rhino 7 throws a `FileLoadException` for it, revisit.
5. `script` (`-_RunPythonScript`) and `save` are written from memory of the Rhino commands.
6. Command output for `cmd`/`script` is the diff of `RhinoApp.CommandHistoryWindowText`; it may miss output on some versions.
7. `RhinoDoc` events (`EndOpenDocument`, `CloseDocument`) drive switching conversations when the active document changes.

## Why the Tectopus tools were dropped

Compiling the vendored Tectopus code for net48 gave ~80 errors: .NET Framework gaps (`double.IsFinite`, `Math.Clamp`, `JObject.ContainsKey` in Newtonsoft 9) and real RhinoCommon 7 gaps (`PolylineCurve.CreateConvexHull2d`, `Curve.ControlPolygon`, `AddGeometry(occluding_sections)`); Kangaroo 2.5.3 needs RhinoCommon >= 7.24.
The owner chose parity over features: remove stability and everything else that isn't on both versions. Re-adding any of it means porting to Rhino 7 first.

## Conventions

- C# `LangVersion latest`, nullable off, 4-space indent, no regions. Comments say *why*, short.
- Commands throw `CliException` for the agent's mistakes (message goes straight back to the agent, plus the usage line); anything else becomes `error: <name> failed: ...`.
- All Rhino calls from commands run on the UI thread via `RhinoCli.Invoker` (`UiThread.Run`, 2-minute timeout).
- Events from the agent are applied on the UI thread (`Application.Instance.AsyncInvoke`) so the conversation model is single-threaded.
