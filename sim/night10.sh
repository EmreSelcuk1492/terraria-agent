#!/usr/bin/env bash
# Overnight, full capacity 00:00-08:00, after night9's A/A has given a noise floor.
#   1. LLM track A/B (fresh then learned) on mods-night9, one world, ~40 min: tests the bench side-step skip (90dbd27),
#      mine_nearest's partial count before its timeout (aaf17eb) and the second levelling pass (b00103e). No REC.
#   2. Bridge A/B on the Director baseline 6245dcd (rain as night, won night11), both arms that same frozen code:
#      old = mods-night8, new = mods-night9. Two worlds, staggered; last start 06:25, done ~07:30.
# Score: python3 .claude/skills/verify/verify.py ab ~/Library/Caches/terraria-sim/night10 --floor-steps 1.2 --floor-deaths 17.4
#   mkdir -p ~/Library/Caches/terraria-sim/night10
#   nohup caffeinate -i -s sim/night10.sh > ~/Library/Caches/terraria-sim/night10/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/night10"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" TA_SESSION=night10
stamp() { date '+%H:%M:%S'; }
q() { echo "$(stamp) $*" >> "$log/queue.log"; }
# Launched in the evening: wait for midnight, then take today's date for the cutoffs.
start="$(date +%Y%m%d)"; [ "$(date +%H)" -ge 12 ] && until [ "$(date +%Y%m%d)" != "$start" ]; do sleep 30; done
day="$(date +%Y%m%d)"
before() { [ "$(date +%s)" -lt "$(date -j -f %Y%m%d%H%M "$day$1" +%s)" ]; }   # e.g. before 0700

# arm <name> <port> <mod dir> <agent dir>
arm() {
  local name=$1 port=$2 mod=$3 agent=$4 n=0
  while before 0625; do
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

freeze() {  # freeze <dir> <commit>: that commit's agent package, so later edits to the checkout don't leak in
  rm -rf "$log/$1"; mkdir -p "$log/$1"; git -C "$here" archive "$2" agent/terraria_agent | tar -x -C "$log/$1" --strip-components 1
}
freeze agent-old 6245dcd
freeze agent-new 6245dcd
q "night10 start (pid $$)"

q "llm night10 start"
SIM_MOD="$c/mods-night9/TerrariaAgentBridge.tmod" REP=night10 "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm.log" 2>&1
q "llm night10 exit $?"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10

arm new 18995 mods-night9 "$log/agent-new" & a=$!
old_arm & b=$!
wait $a $b
q "all done"
