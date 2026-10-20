#!/usr/bin/env bash
# Light daytime queue 2026-10-18 while the Mac is in use: one headless world at a time, capped at 2 CPUs and 2.5 GB,
# no LLM (Director --advisor none, so Ollama stays unloaded), no recording, no telemetry. Tests the respawn shelter
# on mods-night6: survival suite once, then 1 h Director runs; nothing new starts after 19:00, all done by 20:00.
#   nohup sim/day6.sh > ~/Library/Caches/terraria-sim/day6/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/day6"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" SIM_MOD="$c/mods-night6/TerrariaAgentBridge.tmod" SIM_CPUS=2 SIM_MEM=2500m TA_TELEMETRY=0
q() { echo "$(date '+%H:%M:%S') $*" >> "$log/queue.log"; }

q "day6 start (pid $$)"
q "survival start"
"$here/sim/scenarios.sh" 1 --repeat 3 --tag survival > "$log/survival.log" 2>&1
q "survival exit $?"; docker rm -f tsim-s1 >/dev/null 2>&1; sleep 10
n=0
while [ "$(date +%H%M)" -lt 1900 ]; do
  n=$((n + 1)); q "director run$n start"
  if "$here/sim/run.sh" day6 18997 "Test 1" "The Burning Dam of Taxes" --fresh > "$log/director-start$n.log" 2>&1; then
    left=$(( ( $(date -j -f %H%M 2000 +%s) - $(date +%s) ) / 60 - 2 ))   # never run past 20:00
    hours=$(python3 -c "print(round(min(60, $left) / 60, 2))")
    (cd "$here/agent" && .venv/bin/python -u -m terraria_agent.director --bridge http://127.0.0.1:18997 --advisor none \
      --start-at dawn --hours "$hours" --runs-dir "runs/director-day6/run$n") > "$log/director$n.log" 2>&1
    q "director run$n exit $?"
  else
    q "director run$n world did not start"; sleep 60
  fi
  docker rm -f tsim-day6 >/dev/null 2>&1; sleep 15
done
q "all done"
