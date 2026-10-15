#!/usr/bin/env bash
# Start a fake screen, expose the bridge (it binds 127.0.0.1 only) on 0.0.0.0:8979, queue the world autoload, run the game.
set -euo pipefail
save="$HOME/.local/share/Terraria/tModLoader"
Xvfb :99 -screen 0 "${SCREEN:-1280x720x24}" -nolisten tcp &
# Silent sound card: without one tModLoader stops on a "No audio hardware found" popup that needs a click.
pulseaudio -D --exit-idle-time=-1 --load=module-null-sink 2>/dev/null || true
sleep 1
socat TCP-LISTEN:8979,fork,reuseaddr,bind=0.0.0.0 TCP:127.0.0.1:7979 &
if [ -n "${PLAYER:-}" ] && [ -n "${WORLD:-}" ]; then
  printf '{"player":"%s","world":"%s"}' "$PLAYER" "$WORLD" > "$save/agent-autoload.json"
fi
# Startup notices ("No audio hardware", "Steam Workshop unavailable on GoG") wait on a Continue click.
# Until the bridge answers, hold-click where Continue sits on a 1280x720 screen. The game runs at a few FPS here,
# so the button is held long enough to land on a frame.
(until curl -s -m 2 http://127.0.0.1:7979/health >/dev/null; do
   sleep 5; xdotool mousemove 475 627 mousedown 1; sleep 0.6; xdotool mouseup 1
 done) &
cd /game/tModLoader
exec dotnet tModLoader.dll ${TML_ARGS:-}
