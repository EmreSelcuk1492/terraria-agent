#!/usr/bin/env bash
# Start the Director as soon as the Mac is usable: wait for the screen to be unlocked (a locked session can't open
# the game window), launch tModLoader straight into the world (one-shot autoload), then play for HOURS hours.
#   nohup ./scripts/autoplay.sh 2 "Test 1" "The Burning Dam of Taxes" > agent/runs/autoplay.log 2>&1 &
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
hours="${1:-2}"
player="${2:-}"
world="${3:-}"
bridge="http://127.0.0.1:7979"
save_dir="$HOME/Library/Application Support/Terraria/tModLoader"

locked() { ioreg -n Root -d1 | grep -q '"CGSSessionScreenIsLocked"=Yes'; }
in_world() { curl -s -m 2 "$bridge/health" | grep -q '"in_world":true'; }
stamp() { date "+%H:%M:%S"; }

echo "$(stamp) autoplay: ${hours}h once the screen is unlocked"
while locked; do sleep 15; done
echo "$(stamp) screen unlocked"
sleep 5

if ! in_world; then
  if [ -n "$player" ] && [ -n "$world" ]; then
    printf '{"player":"%s","world":"%s"}' "$player" "$world" > "$save_dir/agent-autoload.json"
  fi
  if pgrep -f "tModLoader.dll" >/dev/null && ! curl -s -m 2 "$bridge/health" >/dev/null; then
    echo "$(stamp) a stuck tModLoader is running (no bridge): restarting it"
    pkill -9 -f "tModLoader.dll"; sleep 3
  fi
  pgrep -f "tModLoader.dll" >/dev/null || open "steam://rungameid/1281930"
  for i in $(seq 1 300); do in_world && break; sleep 1; done
  if ! in_world; then echo "$(stamp) game did not reach a world in 300s; giving up"; exit 1; fi
fi
echo "$(stamp) in world: $(curl -s -m 2 "$bridge/health")"
cd "$here/agent" && exec .venv/bin/python -u -m terraria_agent.director --hours "$hours"
