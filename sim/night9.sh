#!/usr/bin/env bash
# Overnight 2026-10-21, full capacity 00:00-08:00. Two worlds at a time.
#   A/A control: both arms run night7's Director (HEAD after the refuge revert; the bridge is mods-night8, which only
#   adds close_doors, world.raining and /recipe). night8 lost its A/B by 3 steps and 31 deaths with a 0-step outlier
#   (new1); this measures how far two arms of the same code drift apart on one night, so the next A/B has a noise floor.
#   Both arms run frozen copies of baseline commit 2fd777f (night9/agent-old, agent-new): the checkout has moved on
#   (rain change 3a0d830, A/B'd in night10). No LLM pass: nothing changed there.
# Same seed, dawn start, advisor off on both arms. Score with: sim/ab_report.sh ~/Library/Caches/terraria-sim/night9
#   nohup caffeinate -i -s sim/night9.sh > ~/Library/Caches/terraria-sim/night9/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/night9"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" TA_SESSION=night9
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

old_arm() {
  sleep 60   # stagger the world loads (each peaks above 2.5 GB)
  arm old 18996 mods-night8 "$log/agent-old"
}

base=2fd777f
for d in agent-old agent-new; do
  rm -rf "$log/$d"; mkdir -p "$log/$d"; git -C "$here" archive "$base" agent/terraria_agent | tar -x -C "$log/$d" --strip-components 1
done
q "night9 start (pid $$), both arms on $base"
arm new 18995 mods-night8 "$log/agent-new" & a=$!
old_arm & b=$!
wait $a $b
q "all done"
