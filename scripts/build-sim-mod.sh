#!/usr/bin/env bash
# Build the bridge for sim containers only:  scripts/build-sim-mod.sh [out.tmod]
# tModLoader packages into your Mac Mods/ folder; this keeps the Mac copy as it was and puts the new build at
# out.tmod (default ~/Library/Caches/terraria-sim/mods/TerrariaAgentBridge.tmod). Start a world with it via
#   SIM_MOD=out.tmod sim/run.sh ...
set -euo pipefail
out="${1:-$HOME/Library/Caches/terraria-sim/mods/TerrariaAgentBridge.tmod}"
mac="$HOME/Library/Application Support/Terraria/tModLoader/Mods/TerrariaAgentBridge.tmod"
keep="$(mktemp -d)"; [ -f "$mac" ] && cp -p "$mac" "$keep/"
trap '[ -f "$keep/TerrariaAgentBridge.tmod" ] && cp -p "$keep/TerrariaAgentBridge.tmod" "$mac"; rm -rf "$keep"' EXIT
"$(dirname "$0")/build-mod.sh" -v q -clp:ErrorsOnly
mkdir -p "$(dirname "$out")"; cp "$mac" "$out"
echo "built $out"
