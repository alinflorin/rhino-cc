#!/bin/sh
# Installs the RhinoCC relay as a systemd user service, so Rhino under Wine can start claude / codex with real pipes.
#   scripts/install-relay.sh              install and start
#   scripts/install-relay.sh --uninstall  stop and remove
# Optional: the plugin works without it (it falls back to starting the CLIs through `start /unix`).
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
BIN="$HOME/.local/share/rhinocc"
CONF="$HOME/.config/rhinocc"
UNIT="$HOME/.config/systemd/user/rhinocc-relay.service"
PORT=${RHINOCC_RELAY_PORT:-45601}

if [ "${1:-}" = "--uninstall" ]; then
    systemctl --user disable --now rhinocc-relay.service 2>/dev/null || true
    rm -f "$UNIT" "$BIN/rhinocc-relay.py" "$CONF/relay.json"
    systemctl --user daemon-reload
    echo "removed the RhinoCC relay"
    exit 0
fi

command -v python3 >/dev/null || { echo "python3 is needed" >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemd is needed (or run $BIN/rhinocc-relay.py yourself)" >&2; exit 1; }

mkdir -p "$BIN" "$CONF" "$(dirname "$UNIT")"
install -m 755 "$HERE/rhinocc-relay.py" "$BIN/rhinocc-relay.py"

# the token stays across reinstalls; only this user can read it
if [ ! -s "$CONF/relay.json" ]; then
    umask 077
    TOKEN=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
    printf '{"port": %s, "token": "%s"}\n' "$PORT" "$TOKEN" > "$CONF/relay.json"
fi
chmod 600 "$CONF/relay.json"

cat > "$UNIT" <<UNIT_EOF
[Unit]
Description=RhinoCC relay (starts claude / codex for Rhino under Wine)

[Service]
ExecStart=/usr/bin/env python3 $BIN/rhinocc-relay.py
Restart=on-failure

[Install]
WantedBy=default.target
UNIT_EOF

systemctl --user daemon-reload
systemctl --user enable rhinocc-relay.service
systemctl --user restart rhinocc-relay.service
sleep 1
systemctl --user --no-pager status rhinocc-relay.service | head -5 || true
echo
echo "RhinoCC relay installed. Restart Rhino's chat (or Rhino) and it is used automatically."
echo "Token and port: $CONF/relay.json"
