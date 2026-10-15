#!/usr/bin/env bash
# Compile the bridge mod and package it into tModLoader's Mods folder (TerrariaAgentBridge.tmod).
# Needs a .NET 8 SDK: https://dot.net/v1/dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet
# After building, reload mods in tModLoader (Workshop > Manage Mods > Reload Mods).
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")/../bridge/TerrariaAgentBridge"
dotnet build "$@"
