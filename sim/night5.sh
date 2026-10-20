#!/usr/bin/env bash
# Overnight 2026-10-18 on mods-night4 (bridge unchanged since cd6da7d), two worlds at a time (Supabase holds ~2.5 GB
# of Docker's 8 GB): track A = Director 19-step, a fresh 1 h world after another; track B = LLM A/B, then the
# survival and station suites, then more LLM A/Bs. Nothing new starts past the cutoffs, so all is done by ~07:30.
#   nohup sim/night5.sh > ~/Library/Caches/terraria-sim/night5/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/night5"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" SIM_MOD="$c/mods-night4/TerrariaAgentBridge.tmod" TA_SESSION=night5
stamp() { date '+%H:%M:%S'; }
q() { echo "$(stamp) $*" >> "$log/queue.log"; }
before() { [ "$(date +%H%M)" -lt "$1" ] && [ "$(date +%H)" -lt 12 ]; }   # e.g. before 0615

director_track() {
  local n=0
  while before 0615; do
    n=$((n + 1)); q "director run$n start"
    if "$here/sim/run.sh" dir5 18995 "Test 1" "The Burning Dam of Taxes" --fresh > "$log/director-start$n.log" 2>&1; then
      (cd "$here/agent" && .venv/bin/python -u -m terraria_agent.director --bridge http://127.0.0.1:18995 \
        --start-at dawn --hours 1 --runs-dir "runs/director-night5/run$n") > "$log/director$n.log" 2>&1
      q "director run$n exit $?"
    else
      q "director run$n world did not start"; sleep 60
    fi
    docker rm -f tsim-dir5 >/dev/null 2>&1; sleep 15
  done
  q "director track done"
}

bench_track() {
  local n=0
  sleep 60   # let the Director's world finish loading first
  n=$((n + 1)); q "llm night5-$n start"
  REC=1 TA_RENDER=1 REP=night5-$n "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-$n.log" 2>&1
  q "llm night5-$n exit $?"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10
  for suite in survival station; do
    before 0600 || break
    q "$suite start"
    "$here/sim/scenarios.sh" 1 --repeat 3 --tag $suite > "$log/$suite.log" 2>&1
    q "$suite exit $?"; docker rm -f tsim-s1 >/dev/null 2>&1; sleep 10
  done
  while before 0630; do
    n=$((n + 1)); q "llm night5-$n start"
    REC=1 TA_RENDER=1 REP=night5-$n "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-$n.log" 2>&1
    q "llm night5-$n exit $?"; docker rm -f tsim-llm >/dev/null 2>&1; sleep 10
  done
  q "bench track done"
}

q "night5 start (pid $$)"
director_track & a=$!
bench_track & b=$!
wait $a $b
q "all done"
