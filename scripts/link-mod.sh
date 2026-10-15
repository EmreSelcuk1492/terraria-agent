#!/usr/bin/env bash
# Symlink the bridge mod into tModLoader's ModSources so tModLoader can build it.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname)" in
  Darwin) src_dir="$HOME/Library/Application Support/Terraria/tModLoader/ModSources" ;;
  Linux)  src_dir="$HOME/.local/share/Terraria/tModLoader/ModSources" ;;
  *)      echo "On Windows: mklink /D \"%USERPROFILE%\\Documents\\My Games\\Terraria\\tModLoader\\ModSources\\TerrariaAgentBridge\" bridge\\TerrariaAgentBridge"; exit 1 ;;
esac
if [ ! -d "$src_dir" ]; then
  echo "ModSources not found at: $src_dir"
  echo "Install tModLoader (free on Steam), launch it once, open Workshop > Develop Mods, then rerun."
  exit 1
fi
ln -sfn "$here/bridge/TerrariaAgentBridge" "$src_dir/TerrariaAgentBridge"
echo "linked -> $src_dir/TerrariaAgentBridge"
echo "Now in tModLoader: Workshop > Develop Mods > Terraria Agent Bridge > Build + Reload"
