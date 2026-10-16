#!/usr/bin/env bash
# After sim/night-b.sh: station placement scenarios, then an LLM A/B on every fix from the night (2026-10-16).
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"; log="$HOME/Library/Caches/terraria-sim/night"
export SIM_MOD="$HOME/Library/Caches/terraria-sim/mods-night3/TerrariaAgentBridge.tmod"
until grep -q 'survival-b exit' "$log/queue.log" 2>/dev/null; do sleep 60; done
sleep 10
echo "$(date '+%H:%M:%S') station start" >> "$log/queue.log"
"$here/sim/scenarios.sh" 1 --repeat 3 --tag station > "$log/station.log" 2>&1
echo "$(date '+%H:%M:%S') station exit $?" >> "$log/queue.log"; sleep 10
echo "$(date '+%H:%M:%S') llm all-fixes start" >> "$log/queue.log"
REC=1 TA_RENDER=1 REP=night3 "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-night3.log" 2>&1
echo "$(date '+%H:%M:%S') llm all-fixes exit $?" >> "$log/queue.log"
docker rm -f tsim-llm >/dev/null 2>&1
echo "$(date '+%H:%M:%S') all done" >> "$log/queue.log"
