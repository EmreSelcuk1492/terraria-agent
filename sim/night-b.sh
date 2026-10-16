#!/usr/bin/env bash
# After sim/night.sh: the survival suite again on the flee-40 / besieged-wait build (2026-10-16).
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"; log="$HOME/Library/Caches/terraria-sim/night"
until grep -q 'queue done' "$log/queue.log" 2>/dev/null; do sleep 60; done
echo "$(date '+%H:%M:%S') survival-b start" >> "$log/queue.log"
SIM_MOD="$HOME/Library/Caches/terraria-sim/mods-night3/TerrariaAgentBridge.tmod" \
  "$here/sim/scenarios.sh" 2 --repeat 3 --tag survival > "$log/survival-b.log" 2>&1
echo "$(date '+%H:%M:%S') survival-b exit $?" >> "$log/queue.log"
