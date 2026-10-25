# rhino-cc

An agent chat inside Rhino. Claude Code and Codex drive Rhino through one fake command line (`rhino`), exposed over a tiny local MCP server.
Works with Rhino 7 and Rhino 8 from one codebase. See [CLAUDE.md](CLAUDE.md) for the design, layout and open questions.

## Build

    ./build.sh        # Rhino 8 (.NET Core) build
    ./build.sh all    # also Rhino 7 (net48)

Needs the .NET SDK; no Windows tooling required. The agent CLIs (`claude`, `codex`) must be installed and signed in.

## Use

1. In Rhino: `PlugInManager` > Install > `src/RhinoCC/bin/Debug/net7.0-windows/RhinoCC.rhp` (Rhino 7: the `net48` folder).
2. Run `RhinoCCChat` to open the panel, `RhinoCCSettings` for agent paths, port and your own instructions.
3. Each conversation has its own agent / model / effort. Conversations are saved in the .3dm.

The agent gets the full command reference in its instructions; `help` lists it from inside the CLI.
Command set inspired by [Tectopus](https://github.com/Creative-Data-Critical-Design/tectopus) (Apache-2.0); no Tectopus code is included.
