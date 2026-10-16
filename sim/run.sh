#!/usr/bin/env bash
# Start one headless world:  sim/run.sh <name> <port> "<player>" "<world>" [--fresh]
# Copies the player, the world and the bridge mod from your Mac saves the first time (or with --fresh, which resets
# the world), then starts a container whose bridge is on localhost:<port>. Point the Director at it with
#   .venv/bin/python -m terraria_agent.director --bridge http://127.0.0.1:<port>
set -euo pipefail
name="${1:?name}"; port="${2:?port}"; player="${3:?player}"; world="${4:?world}"; fresh="${5:-}"
home="${SIM_HOME:-$HOME/Library/Caches/terraria-sim}"
# SIM_SAVES: a pinned Players/ + Worlds/ snapshot for benchmarks (your live Mac save changes every time you play).
saves="${SIM_SAVES:-$HOME/Library/Application Support/Terraria/tModLoader}"
inst="$home/instances/$name"
if [ "$fresh" = "--fresh" ] || [ ! -d "$inst/save" ]; then
  rm -rf "$inst"; mkdir -p "$inst/save/Mods" "$inst/save/Players" "$inst/save/Worlds" "$inst/logs"
  # A bridge built for the sim (e.g. from another branch) goes in $home/mods/ so your Mac game keeps its own.
  # SIM_MOD: one specific build (scripts/build-sim-mod.sh <out>), so a test world can run new code next to a benchmark.
  mod="${SIM_MOD:-$home/mods/TerrariaAgentBridge.tmod}"; [ -f "$mod" ] || mod="$saves/Mods/TerrariaAgentBridge.tmod"
  cp "$mod" "$inst/save/Mods/"
  printf '["TerrariaAgentBridge"]' > "$inst/save/Mods/enabled.json"   # only the bridge; stray .tmods pop error dialogs
  if [ -f "$home/mods/TerraBlind.tmod" ]; then                          # optional navigator for tb_nav (scripts/build-terrablind.sh)
    cp "$home/mods/TerraBlind.tmod" "$inst/save/Mods/"
    printf '["TerrariaAgentBridge","TerraBlind"]' > "$inst/save/Mods/enabled.json"
  fi
  cp "$(dirname "$0")/config.json" "$inst/save/config.json"   # language + last-run versions: skips first-run screens
  p="${player// /_}"; w="${world// /_}"
  cp "$saves/Players/$p.plr" "$inst/save/Players/"; cp "$saves/Players/$p.tplr" "$inst/save/Players/" 2>/dev/null || true
  cp "$saves/Worlds/$w.wld" "$inst/save/Worlds/"; cp "$saves/Worlds/$w.twld" "$inst/save/Worlds/" 2>/dev/null || true
fi
docker rm -f "tsim-$name" >/dev/null 2>&1 || true
# TA_RENDER=1000 (default): draw 1 frame in 1000 once in a world. Software rendering is what held the game at ~21
# ticks/s and ~2 GB; headless runs at 60 with ~0.5 GB. TA_RENDER=1 to watch screenshots. A memory cap per world means
# a runaway world dies alone (four worlds starting at once took down a benchmark's world with them, 2026-10-15).
docker run -d --platform linux/amd64 --name "tsim-$name" -p "127.0.0.1:$port:8979" --memory "${SIM_MEM:-2500m}" \
  -e PLAYER="$player" -e WORLD="$world" -e TA_RENDER="${TA_RENDER:-1000}" \
  -v "$home/tModLoader":/game/tModLoader -v "$inst/logs":/game/tModLoader/tModLoader-Logs \
  -v "$home/gog/data/noarch/game":/game/Terraria:ro -v "$inst/save":/root/.local/share/Terraria/tModLoader \
  terraria-sim >/dev/null
echo "tsim-$name starting; waiting for the world (a few minutes)..."
until curl -s -m 2 -H "Host: 127.0.0.1:7979" "http://127.0.0.1:$port/health" | grep -q '"in_world":true'; do
  docker ps -q --filter "name=^tsim-$name$" --filter status=running | grep -q . || { echo "container exited; see $inst/logs/client.log" >&2; exit 1; }
  sleep 5
done
echo "tsim-$name in world on http://127.0.0.1:$port"
