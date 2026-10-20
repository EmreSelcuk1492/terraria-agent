#!/usr/bin/env bash
# Overnight 2026-10-18/19: night5's queue on mods-night6 = the recipe book (POST /recipe, in the LLM track's prompt)
# and the Director's respawn shelter. Full capacity only 00:00-08:00 (Emre, 2026-10-18): waits for midnight, nothing new past the cutoffs, done by 08:00.
#   nohup caffeinate -i -s sim/night6.sh > ~/Library/Caches/terraria-sim/night6/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/night6"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" SIM_MOD="$c/mods-night6/TerrariaAgentBridge.tmod" TA_SESSION=night6
stamp() { date '+%H:%M:%S'; }
q() { echo "$(stamp) $*" >> "$log/queue.log"; }
# Cutoffs are "HHMM tomorrow morning", taken from when the queue is launched (the evening before).
start_day="$(date -v+1d +%Y%m%d)"
before() { [ "$(date +%s)" -lt "$(date -j -f %Y%m%d%H%M "$start_day$1" +%s)" ]; }   # e.g. before 0615

director_track() {
  local n=0
  while before 0700; do
    n=$((n + 1)); q "director run$n start"
    if "$here/sim/run.sh" dir6 18995 "Test 1" "The Burning Dam of Taxes" --fresh > "$log/director-start$n.log" 2>&1; then
      (cd "$here/agent" && .venv/bin/python -u -m terraria_agent.director --bridge http://127.0.0.1:18995 \
        --start-at dawn --hours 1 --runs-dir "runs/director-night6/run$n") > "$log/director$n.log" 2>&1
      q "director run$n exit $?"
    else
      q "director run$n world did not start"; sleep 60
    fi
    docker rm -f tsim-dir6 >/dev/null 2>&1; sleep 15
  done
  q "director track done"
}

bench_track() {
  local n=0
  sleep 60   # let the Director's world finish loading first
  n=$((n + 1)); q "llm night6-$n start"
  REC=1 TA_RENDER=1 REP=night6-$n "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-$n.log" 2>&1
  q "llm night6-$n exit $?"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10
  for suite in survival station; do
    before 0700 || break
    q "$suite start"
    "$here/sim/scenarios.sh" 1 --repeat 3 --tag $suite > "$log/$suite.log" 2>&1
    q "$suite exit $?"; docker rm -f tsim-s1 >/dev/null 2>&1; sleep 10
  done
  while before 0725; do
    n=$((n + 1)); q "llm night6-$n start"
    REC=1 TA_RENDER=1 REP=night6-$n "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-$n.log" 2>&1
    q "llm night6-$n exit $?"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10
  done
  q "bench track done"
}

until [ "$(date +%Y%m%d)" = "$start_day" ]; do sleep 60; done   # midnight
q "night6 start (pid $$)"
director_track & a=$!
bench_track & b=$!
wait $a $b
q "all done"
