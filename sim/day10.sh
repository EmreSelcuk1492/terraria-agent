#!/usr/bin/env bash
# Light queue 2026-10-21 (started 01:40, Emre at the Mac): one headless world, 2 CPUs / 2.5 GB, no LLM (advisor none),
# no telemetry. Baseline numbers for night7's Director (HEAD 2fd777f, refuge reverted) on mods-night8, run from a
# frozen copy (day10/agent-old) so agent/ can be edited while it runs. 1 h Director runs back to back; nothing new
# starts after 07:00. Light settings (2 CPUs) are not comparable with night runs: compare only with other day10 runs.
#   nohup sim/day10.sh > ~/Library/Caches/terraria-sim/day10/queue.out 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
c="$HOME/Library/Caches/terraria-sim"; log="$c/day10"; mkdir -p "$log"
export SIM_SAVES="$c/bench-world" SIM_MOD="$c/mods-night8/TerrariaAgentBridge.tmod" SIM_CPUS=2 SIM_MEM=2500m \
  TA_TELEMETRY=0 TA_SESSION=day10
q() { echo "$(date '+%H:%M:%S') $*" >> "$log/queue.log"; }
day="$(date +%Y%m%d)"
before() { [ "$(date +%s)" -lt "$(date -j -f %Y%m%d%H%M "$day$1" +%s)" ]; }

rm -rf "$log/agent-old"; mkdir -p "$log/agent-old"; cp -R "$here/agent/terraria_agent" "$log/agent-old/"
q "day10 start (pid $$)"
n=0
while before 0700; do
  n=$((n + 1)); q "base run$n start"
  if "$here/sim/run.sh" day10 18994 "Test 1" "The Burning Dam of Taxes" --fresh > "$log/base-start$n.log" 2>&1; then
    (cd "$log/agent-old" && PYTHONPATH=. "$here/agent/.venv/bin/python" -u -m terraria_agent.director \
      --bridge http://127.0.0.1:18994 --advisor none --start-at dawn --hours 1 --runs-dir "$log/runs-base/run$n") \
      > "$log/base$n.log" 2>&1
    q "base run$n exit $?"
  else
    q "base run$n world did not start"; sleep 60
  fi
  docker rm -f tsim-day10 >/dev/null 2>&1; sleep 15
done
q "all done"
