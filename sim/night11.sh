#!/usr/bin/env bash
# Overnight 2026-10-21 from ~01:55 (night9 never launched; replaces the day10 light run), three worlds, all done by ~07:30.
#   A/A/B in one night, identical conditions on every arm (mods-night8, pinned world, dawn, advisor off, 2.7 GB cap):
#     old  = baseline 2fd777f        old2 = baseline 2fd777f (old vs old2 = the noise floor)
#     new  = rain counts as night (6245dcd; 3a0d830 looped, see night11-aborted)
#   All three are frozen copies (night11/agent-*). Staggered 90 s apart: a world load peaks above 2.5 GB. Last start 06:25.
# Score: ARMS="old old2 new" sim/ab_report.sh ~/Library/Caches/terraria-sim/night11
#   mkdir -p ~/Library/Caches/terraria-sim/night11
#   nohup caffeinate -i -s sim/night11.sh > ~/Library/Caches/terraria-sim/night11/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/night11"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" TA_SESSION=night11 SIM_MEM=2700m
stamp() { date '+%H:%M:%S'; }
q() { echo "$(stamp) $*" >> "$log/queue.log"; }
day="$(date +%Y%m%d)"
before() { [ "$(date +%s)" -lt "$(date -j -f %Y%m%d%H%M "$day$1" +%s)" ]; }   # e.g. before 0625

# arm <name> <port> <agent dir> <stagger seconds>
arm() {
  local name=$1 port=$2 agent=$3 n=0
  sleep "$4"
  while before 0625; do
    n=$((n + 1)); q "$name run$n start"
    if SIM_MOD="$c/mods-night8/TerrariaAgentBridge.tmod" TA_TELEMETRY=0 "$here/sim/run.sh" "ab-$name" "$port" "Test 1" "The Burning Dam of Taxes" --fresh \
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

freeze() {  # freeze <dir> <commit>: that commit's agent package, so later edits to the checkout don't leak in
  rm -rf "$log/$1"; mkdir -p "$log/$1"; git -C "$here" archive "$2" agent/terraria_agent | tar -x -C "$log/$1" --strip-components 1
}
freeze agent-old 2fd777f
freeze agent-old2 2fd777f
freeze agent-new 6245dcd
q "night11 start (pid $$)"
arm new 18995 "$log/agent-new" 0 & a=$!
arm old 18996 "$log/agent-old" 90 & b=$!
arm old2 18997 "$log/agent-old2" 180 & d=$!
wait $a $b $d
q "all done"
