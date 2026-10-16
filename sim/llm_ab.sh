#!/usr/bin/env bash
# LLM track A/B on identical worlds: pass 1 with an empty skill library, then a fresh copy of the same world and
# pass 2 starting from what pass 1 learned.   sim/llm_ab.sh [primitive|macro] [extra llm_bench args...]
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
mode="${1:-primitive}"; shift || true
export SIM_SAVES="${SIM_SAVES:-$HOME/Library/Caches/terraria-sim/bench-world}"   # the pinned benchmark world
port=18990; lib="$here/agent/skills/library-$mode-ab${REP:-}.json"
cd "$here/agent"
started="$(mktemp)"   # runs/llm dirs newer than this are this A/B's two passes
for pass in fresh learned; do
  "$here/sim/run.sh" llm "$port" "${PLAYER:-Test 1}" "${WORLD:-The Burning Dam of Taxes}" --fresh | tail -1
  # A fresh copy of the world before every task: no task inherits another's cut forest (2026-10-15).
  reset="$here/sim/run.sh llm $port '${PLAYER:-Test 1}' '${WORLD:-The Burning Dam of Taxes}' --fresh"
  .venv/bin/python -u -m terraria_agent.llm_bench --bridge "http://127.0.0.1:$port" --mode "$mode" --library "$pass" \
    --library-path "$lib" --reset-world "$reset" "$@"
done
docker exec tsim-llm pkill -INT ffmpeg >/dev/null 2>&1 && sleep 2
docker rm -f tsim-llm >/dev/null
# REC=1: put each task's recording on the lab next to its result (terraria_agent/clips.py)
if [ -n "${REC:-}" ]; then
  find runs/llm -maxdepth 1 -type d -newer "$started" -name '20*' | sort | xargs .venv/bin/python -m terraria_agent.clips llm || true
fi
rm -f "$started"
