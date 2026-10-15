#!/usr/bin/env bash
# Build TerraBlind (third_party/TerraBlind, by Reisenbug, MIT) next to our bridge.
# Copies the pinned submodule into tModLoader's ModSources, applies our patches
# (third_party/patches/*.patch: concessions off so it never edits our inventory),
# then compiles TerraBlind.tmod into the Mods folder. The submodule itself stays pristine.
# After building, reload mods in tModLoader and enable TerraBlind (Workshop > Manage Mods).
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
here="$(cd "$(dirname "$0")/.." && pwd)"
case "$(uname)" in
  Darwin) src_dir="$HOME/Library/Application Support/Terraria/tModLoader/ModSources" ;;
  Linux)  src_dir="$HOME/.local/share/Terraria/tModLoader/ModSources" ;;
  *)      echo "Unsupported OS"; exit 1 ;;
esac
git -C "$here" submodule update --init third_party/TerraBlind
dest="$src_dir/TerraBlind"
rsync -a --delete --exclude .git --exclude bin --exclude obj "$here/third_party/TerraBlind/" "$dest/"
for p in "$here"/third_party/patches/terrablind-*.patch; do
  patch -d "$dest" -p1 --forward --quiet < "$p"
  echo "patched: $(basename "$p")"
done
cd "$dest"
dotnet build "$@"
