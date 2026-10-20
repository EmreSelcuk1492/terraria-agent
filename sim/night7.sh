#!/usr/bin/env bash
# Overnight 2026-10-19 A/B: the Director, old code against new, on two worlds side by side (full capacity, 00:00-08:00).
#   old = mods-night6 + the day6 agent (a frozen copy in night7/agent-old)
#   new = mods-night7 (build_room tunnels to an unreachable site, fails fast) + cornered fights always desperate
# Same seed, dawn start, 1 h per run, advisor off on both (no Ollama noise). Nothing new starts after 07:00.
#   nohup caffeinate -i -s sim/night7.sh > ~/Library/Caches/terraria-sim/night7/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/night7"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" TA_SESSION=night7 TA_TELEMETRY=0
stamp() { date '+%H:%M:%S'; }
q() { echo "$(stamp) $*" >> "$log/queue.log"; }
# Launched in the evening: wait for midnight (full capacity is 00:00-08:00 only), then take today's date for the cutoffs.
start="$(date +%Y%m%d)"; [ "$(date +%H)" -ge 12 ] && until [ "$(date +%Y%m%d)" != "$start" ]; do sleep 30; done
day="$(date +%Y%m%d)"
before() { [ "$(date +%s)" -lt "$(date -j -f %Y%m%d%H%M "$day$1" +%s)" ]; }   # e.g. before 0700

# arm <name> <port> <mod dir> <agent dir>
arm() {
  local name=$1 port=$2 mod=$3 agent=$4 n=0
  while before 0700; do
    n=$((n + 1)); q "$name run$n start"
    if SIM_MOD="$c/$mod/TerrariaAgentBridge.tmod" "$here/sim/run.sh" "ab-$name" "$port" "Test 1" "The Burning Dam of Taxes" --fresh \
        > "$log/$name-start$n.log" 2>&1; then
      (cd "$agent" && PYTHONPATH=. "$here/agent/.venv/bin/python" -u -m terraria_agent.director --bridge "http://127.0.0.1:$port" \
        --advisor none --start-at dawn --hours 1 --runs-dir "$log/runs-$name/run$n") > "$log/$name$n.log" 2>&1
      q "$name run$n exit $?"
    else
      q "$name run$n world did not start"; sleep 60
    fi
    docker rm -f "tsim-ab-$name" >/dev/null 2>&1; sleep 15
  done
  q "$name done"
}

q "night7 start (pid $$)"
arm new 18995 mods-night7 "$here/agent" & a=$!
sleep 30   # stagger the world loads (each peaks above 2.5 GB)
arm old 18996 mods-night6 "$log/agent-old" & b=$!
wait $a $b
q "all done"
