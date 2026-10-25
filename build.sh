#!/usr/bin/env bash
# ./build.sh        builds the Rhino 8 (.NET Core) side and runs the tests
# ./build.sh all    also builds Rhino 7 (net48)
# CONFIG=Release ./build.sh
set -euo pipefail
cd "$(dirname "$0")"
cfg="${CONFIG:-Debug}"
dotnet test src/RhinoCC.Tests -c "$cfg"
if [[ "${1:-}" == "all" ]]; then
  dotnet build src/RhinoCC -c "$cfg"
else
  dotnet build src/RhinoCC -c "$cfg" -f net7.0-windows
fi
echo
echo "Load in Rhino (PlugInManager > Install) from:"
echo "  Z:$(pwd | tr '/' '\\')\\src\\RhinoCC\\bin\\$cfg\\net7.0-windows\\RhinoCC.rhp"
