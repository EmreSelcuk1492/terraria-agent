#!/usr/bin/env bash
# Scenario suite on N fresh headless worlds at once:  sim/scenarios.sh [N] [scenarios.py args...]
#   sim/scenarios.sh 2 --repeat 3                 # every scenario 3x, split over 2 containers
#   sim/scenarios.sh 1 --only zombie_half_hp
# Worlds are reset from your Mac save (--fresh) so every suite starts the same. About 5 cores per world.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
n="${1:-2}"; shift || true
export SIM_SAVES="${SIM_SAVES:-$HOME/Library/Caches/terraria-sim/bench-world}"   # the pinned benchmark world
player="${PLAYER:-Test 1}"; world="${WORLD:-The Burning Dam of Taxes}"
bridges=(); pids=()
for i in $(seq 1 "$n"); do
  port=$((18978 + i))
  "$here/sim/run.sh" "s$i" "$port" "$player" "$world" --fresh > "/tmp/tsim-s$i.start.log" 2>&1 &
  pids+=($!); bridges+=(--bridge "http://127.0.0.1:$port")
  [ "$i" -lt "$n" ] && sleep "${STAGGER:-25}"   # world loads peak in memory; don't stack them
done
for i in "${!pids[@]}"; do
  wait "${pids[$i]}" || { echo "world s$((i + 1)) did not start: $(tail -2 /tmp/tsim-s$((i + 1)).start.log)" >&2; exit 1; }
done
echo "$n world(s) up; running scenarios"
cd "$here/agent"
status=0
.venv/bin/python -u -m terraria_agent.scenarios "${bridges[@]}" "$@" || status=$?
[ "${KEEP:-}" = 1 ] || for i in $(seq 1 "$n"); do docker rm -f "tsim-s$i" >/dev/null 2>&1 || true; done
exit $status
