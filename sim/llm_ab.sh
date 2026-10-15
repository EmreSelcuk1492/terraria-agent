#!/usr/bin/env bash
# LLM track A/B on identical worlds: pass 1 with an empty skill library, then a fresh copy of the same world and
# pass 2 starting from what pass 1 learned.   sim/llm_ab.sh [primitive|macro] [extra llm_bench args...]
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
mode="${1:-primitive}"; shift || true
export SIM_SAVES="${SIM_SAVES:-$HOME/Library/Caches/terraria-sim/bench-world}"   # the pinned benchmark world
port=18990; lib="$here/agent/skills/library-$mode-ab${REP:-}.json"
cd "$here/agent"
for pass in fresh learned; do
  "$here/sim/run.sh" llm "$port" "${PLAYER:-Test 1}" "${WORLD:-The Burning Dam of Taxes}" --fresh | tail -1
  .venv/bin/python -u -m terraria_agent.llm_bench --bridge "http://127.0.0.1:$port" --mode "$mode" --library "$pass" \
    --library-path "$lib" "$@"
done
docker rm -f tsim-llm >/dev/null
