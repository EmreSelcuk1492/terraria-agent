#!/usr/bin/env bash
# One-time setup for sim/: unpack the GOG Terraria Linux installer (never runs it), prepare a writable tModLoader copy,
# and build the image.   sim/setup.sh ~/Downloads/terraria_v1_4_5_8_93311.sh
set -euo pipefail
installer="${1:?usage: sim/setup.sh <GOG terraria_*.sh installer>}"
home="${SIM_HOME:-$HOME/Library/Caches/terraria-sim}"
steam_tml="$HOME/Library/Application Support/Steam/steamapps/common/tModLoader"
mkdir -p "$home/gog"
# A GOG .sh is a Makeself script with a zip appended; unzip skips the script part.
(cd "$home/gog" && unzip -qo "$installer" 'data/noarch/game/*' || true)
game="$home/gog/data/noarch/game"
[ -f "$game/Terraria.exe" ] || { echo "no Terraria.exe after unpacking $installer" >&2; exit 1; }
# The GOG build ships Images/TIles_650.xnb; tModLoader wants Tiles_650.xnb and can't rename it on a read-only mount.
[ -f "$game/Content/Images/TIles_650.xnb" ] && mv "$game/Content/Images/TIles_650.xnb" "$game/Content/Images/t650.tmp" \
  && mv "$game/Content/Images/t650.tmp" "$game/Content/Images/Tiles_650.xnb"
# tModLoader's GOG check copies Terraria.exe to Terraria_v<version>.exe on first run; do it here so the mount stays read-only.
ver="$(basename "$installer" | sed -nE 's/^terraria_v([0-9]+)_([0-9]+)_([0-9]+)_([0-9]+)_.*/\1.\2.\3.\4/p')"   # terraria_v1_4_5_8_93311.sh -> 1.4.5.8
[ -n "$ver" ] && cp -n "$game/Terraria.exe" "$game/Terraria_v$ver.exe"
# tModLoader writes steam_appid.txt into its own folder, so containers get a writable copy (without the macOS dotnet).
rsync -a --delete --exclude dotnet --exclude tModLoader-Logs "$steam_tml/" "$home/tModLoader/"
docker build --platform linux/amd64 -t terraria-sim "$(dirname "$0")"
echo "ready: $home"
