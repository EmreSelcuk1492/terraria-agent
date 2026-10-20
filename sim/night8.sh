#!/usr/bin/env bash
# Overnight 2026-10-20, full capacity 00:00-08:00. Two worlds at a time.
#   track A: the Director, new code (mods-night8 + this repo's agent), 1 h runs until 07:00
#     new since night7: the house as a refuge (in, door shut, the column inside it blocked; respawn/low health/night
#     go there), rain and blood moon count as night once there is a house, close_doors + world.raining in the bridge
#   track B: the LLM A/B once (recipe book in the prompt; copper_bars was 0/20 without it), then the Director
#     baseline = night7's final code (mods-night7 + a frozen copy in night8/agent-old) until 07:00
# Same seed, dawn start, advisor off on both Director arms. Score with: sim/ab_report.sh ~/Library/Caches/terraria-sim/night8
#   nohup caffeinate -i -s sim/night8.sh > ~/Library/Caches/terraria-sim/night8/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/night8"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" TA_SESSION=night8
stamp() { date '+%H:%M:%S'; }
q() { echo "$(stamp) $*" >> "$log/queue.log"; }
# Launched in the evening: wait for midnight, then take today's date for the cutoffs.
start="$(date +%Y%m%d)"; [ "$(date +%H)" -ge 12 ] && until [ "$(date +%Y%m%d)" != "$start" ]; do sleep 30; done
day="$(date +%Y%m%d)"
before() { [ "$(date +%s)" -lt "$(date -j -f %Y%m%d%H%M "$day$1" +%s)" ]; }   # e.g. before 0700

# arm <name> <port> <mod dir> <agent dir>
arm() {
  local name=$1 port=$2 mod=$3 agent=$4 n=0
  while before 0700; do
    n=$((n + 1)); q "$name run$n start"
    if SIM_MOD="$c/$mod/TerrariaAgentBridge.tmod" TA_TELEMETRY=0 "$here/sim/run.sh" "ab-$name" "$port" "Test 1" "The Burning Dam of Taxes" --fresh \
        > "$log/$name-start$n.log" 2>&1; then
      (cd "$agent" && PYTHONPATH=. TA_TELEMETRY=0 "$here/agent/.venv/bin/python" -u -m terraria_agent.director --bridge "http://127.0.0.1:$port" \
        --advisor none --start-at dawn --hours 1 --runs-dir "$log/runs-$name/run$n") > "$log/$name$n.log" 2>&1
      q "$name run$n exit $?"
    else
      q "$name run$n world did not start"; sleep 60
    fi
    docker rm -f "tsim-ab-$name" >/dev/null 2>&1; sleep 15
  done
  q "$name done"
}

bench_then_old() {
  sleep 60   # stagger the world loads (each peaks above 2.5 GB)
  q "llm night8-1 start"
  SIM_MOD="$c/mods-night8/TerrariaAgentBridge.tmod" REC=1 TA_RENDER=1 REP=night8-1 "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-1.log" 2>&1
  q "llm night8-1 exit $?"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10
  arm old 18996 mods-night7 "$log/agent-old"
}

q "night8 start (pid $$)"
arm new 18995 mods-night8 "$here/agent" & a=$!
bench_then_old & b=$!
wait $a $b
q "all done"
