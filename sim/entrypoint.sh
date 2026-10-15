#!/usr/bin/env bash
# Start a fake screen, expose the bridge (it binds 127.0.0.1 only) on 0.0.0.0:8979, queue the world autoload, run the game.
set -euo pipefail
save="$HOME/.local/share/Terraria/tModLoader"
Xvfb :99 -screen 0 "${SCREEN:-1280x720x24}" -nolisten tcp &
sleep 1
socat TCP-LISTEN:8979,fork,reuseaddr,bind=0.0.0.0 TCP:127.0.0.1:7979 &
if [ -n "${PLAYER:-}" ] && [ -n "${WORLD:-}" ]; then
  printf '{"player":"%s","world":"%s"}' "$PLAYER" "$WORLD" > "$save/agent-autoload.json"
fi
cd /steam/steamapps/common/tModLoader
exec dotnet tModLoader.dll ${TML_ARGS:-}
