#!/usr/bin/env python3
"""
rhinocc-relay: lets the RhinoCC plugin, running in Rhino under Wine, start `claude` and `codex` on this machine
with ordinary pipes. Wine cannot do that itself.

It listens on 127.0.0.1 only, wants the token from ~/.config/rhinocc/relay.json, and starts nothing but `claude` and `codex`.

Protocol (one connection = one run; every line is UTF-8):
  client -> one JSON line   {"token", "program", "args": [...], "stdin": <byte count>}
         -> exactly <byte count> bytes: the program's stdin
         -> closing the connection stops the program
  server -> "OK" (or "ERR <why>" and the end)
         -> "O <line>" for each line of stdout, "E <line>" for each line of stderr
         -> "X <exit code>" and the end
"""
import asyncio
import hmac
import json
import os
import pwd
import signal
import sys

CONFIG = os.path.join(os.path.expanduser("~"), ".config", "rhinocc", "relay.json")
ALLOWED = {"claude", "codex"}
LINE_LIMIT = 1 << 30  # a tool result with a picture is one very long line


async def handle(reader, writer, token):
    lock = asyncio.Lock()
    proc = None

    async def send(line):
        async with lock:
            writer.write(line.encode("utf-8", "replace") + b"\n")
            await writer.drain()

    def stop():
        if proc is not None and proc.returncode is None:
            try:
                os.killpg(proc.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass

    try:
        try:
            header = json.loads(await reader.readline())
            size = int(header.get("stdin", 0))
            data = await reader.readexactly(size) if size > 0 else b""
        except Exception:
            return  # a probe or garbage: nothing to answer
        if not hmac.compare_digest(str(header.get("token", "")), token):
            await send("ERR bad token")
            return
        program = str(header.get("program", ""))
        if os.path.basename(program) not in ALLOWED:
            await send("ERR only claude and codex can be started")
            return
        args = [str(a) for a in header.get("args", [])]

        home = os.path.expanduser("~")
        env = dict(os.environ)
        env["PATH"] = os.pathsep.join([os.path.join(home, ".local", "bin"), env.get("PATH", "")])
        # a login shell, so the CLIs are found the way they are from a terminal
        shell = pwd.getpwuid(os.getuid()).pw_shell or "/bin/sh"
        proc = await asyncio.create_subprocess_exec(
            shell, "-lc", 'exec "$0" "$@"', program, *args,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            cwd="/tmp", env=env, limit=LINE_LIMIT, start_new_session=True)
        await send("OK")

        async def feed():
            try:
                proc.stdin.write(data)
                await proc.stdin.drain()
                proc.stdin.close()
            except (BrokenPipeError, ConnectionResetError):
                pass

        async def pump(stream, tag):
            async for line in stream:
                await send(tag + " " + line.decode("utf-8", "replace").rstrip("\r\n"))

        async def watch():
            # anything more from the client, or its end, means it gave up
            await reader.read(1)
            stop()

        watcher = asyncio.ensure_future(watch())
        await feed()
        await asyncio.gather(pump(proc.stdout, "O"), pump(proc.stderr, "E"))
        code = await proc.wait()
        watcher.cancel()
        await send("X %d" % code)
    except (ConnectionResetError, BrokenPipeError):
        stop()
    finally:
        stop()
        try:
            writer.close()
        except Exception:
            pass


async def main():
    with open(CONFIG, encoding="utf-8") as f:
        config = json.load(f)
    token = config["token"]
    port = int(os.environ.get("RHINOCC_RELAY_PORT", config.get("port", 45601)))
    server = await asyncio.start_server(lambda r, w: handle(r, w, token), "127.0.0.1", port, limit=LINE_LIMIT)
    print("rhinocc-relay listening on 127.0.0.1:%d" % port, flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
    except FileNotFoundError:
        sys.exit("no %s; run scripts/install-relay.sh" % CONFIG)
