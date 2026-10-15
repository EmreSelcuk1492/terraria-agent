#!/usr/bin/env bash
# Unattended rebuild: save + quit tModLoader, rebuild the bridge mod, relaunch, and wait until the game is back
# in the same world (the bridge's one-shot autoload). Needs Steam running and the mod already enabled.
#   ./scripts/cycle.sh                         # cycle using the world the game is in now
#   ./scripts/cycle.sh "Test 1" "World Name"   # when the game isn't running: launch straight into this world
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
bridge="http://127.0.0.1:7979"
save_dir="$HOME/Library/Application Support/Terraria/tModLoader"

running() { pgrep -f "tModLoader.dll" >/dev/null; }

if running; then
  echo "saving and quitting..."
  curl -s -m 10 -X POST "$bridge/quit" -H 'Content-Type: application/json' -d '{}' || true; echo
  for _ in $(seq 1 90); do running || break; sleep 1; done
  if running; then echo "tModLoader did not exit after 90s" >&2; exit 1; fi
fi

if [ $# -ge 2 ]; then
  printf '{"player":"%s","world":"%s"}' "$1" "$2" > "$save_dir/agent-autoload.json"
fi
[ -f "$save_dir/agent-autoload.json" ] || echo "warning: no autoload file; tModLoader will stop at the title screen" >&2

echo "building..."
if ! out="$("$here/scripts/build-mod.sh" -nologo -v q 2>&1)"; then
  echo "$out" | grep -E "error" | sort -u >&2
  echo "build failed; relaunching the previous mod" >&2
  build_failed=1
fi

echo "launching..."
open "steam://rungameid/1281930"
for i in $(seq 1 240); do
  if curl -s -m 2 "$bridge/health" | grep -q '"in_world":true'; then
    echo "back in world after ${i}s: $(curl -s -m 2 "$bridge/health")"
    exit "${build_failed:-0}"
  fi
  sleep 1
done
echo "game did not reach a world within 240s" >&2
exit 1
