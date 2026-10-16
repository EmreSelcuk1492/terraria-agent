#!/usr/bin/env bash
# Overnight queue, one world at a time after the survival suite (2026-10-16):
#   1. LLM A/B replication on HEAD (control)   2. LLM A/B with the craft totals hint   3. Director 19-step, fresh sim
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
log="$HOME/Library/Caches/terraria-sim/night"; c="$HOME/Library/Caches/terraria-sim"
stamp() { date '+%H:%M:%S'; }
while pgrep -f 'terraria_agent.scenarios' >/dev/null; do sleep 60; done
echo "$(stamp) survival done" >> "$log/queue.log"
docker rm -f tsim-s1 tsim-s2 >/dev/null 2>&1; sleep 10

echo "$(stamp) llm control start" >> "$log/queue.log"
REC=1 TA_RENDER=1 REP=night1 SIM_MOD="$c/mods-night/TerrariaAgentBridge.tmod" \
  "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-night1.log" 2>&1
echo "$(stamp) llm control exit $?" >> "$log/queue.log"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10

echo "$(stamp) llm totals start" >> "$log/queue.log"
REC=1 TA_RENDER=1 REP=night2 SIM_MOD="$c/mods-night2/TerrariaAgentBridge.tmod" \
  "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-night2.log" 2>&1
echo "$(stamp) llm totals exit $?" >> "$log/queue.log"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10

echo "$(stamp) director start" >> "$log/queue.log"
export SIM_SAVES="$c/bench-world" SIM_MOD="$c/mods-night2/TerrariaAgentBridge.tmod"
"$here/sim/run.sh" dirn 18995 "Test 1" "The Burning Dam of Taxes" --fresh > "$log/director-start.log" 2>&1
mkdir -p "$here/agent/runs/director-night"
(cd "$here/agent" && .venv/bin/python -u -m terraria_agent.director --bridge http://127.0.0.1:18995 --start-at dawn \
  --hours 1 --runs-dir runs/director-night) > "$log/director.log" 2>&1
echo "$(stamp) director exit $?" >> "$log/queue.log"
docker rm -f tsim-dirn >/dev/null 2>&1
echo "$(stamp) queue done" >> "$log/queue.log"
