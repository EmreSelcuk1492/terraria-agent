# sim: the real game in Linux containers

Runs the real tModLoader client with no visible window, one world per container, so the Director can play real
worlds in parallel without your screen, your lock state, or Steam.

How: Docker (linux/amd64 through Rosetta) + Xvfb as an in-memory screen + Mesa software OpenGL + PulseAudio with a
silent output. Ownership is proven the supported non-Steam way, with a GOG copy of Terraria.

## One-time setup

1. Buy Terraria on GOG and download the **Linux offline installer** (`terraria_v1_4_5_8_*.sh`).
2. `sim/setup.sh ~/Downloads/terraria_v1_4_5_8_93311.sh`
   Unpacks the installer (never runs it), fixes one file-name case, prepares a writable tModLoader copy from your
   Steam install, builds the `terraria-sim` image. Everything lands in `~/Library/Caches/terraria-sim`.

## Run worlds

    sim/run.sh w1 18979 "Test 1" "The Burning Dam of Taxes"            # ~30 s to in-world
    sim/run.sh w2 18980 "Test 1" "Jungle of the Wise" --fresh           # --fresh = reset from your Mac save
    cd agent && .venv/bin/python -m terraria_agent.director --bridge http://127.0.0.1:18979 --hours 2

Each world gets its own copy of the player + world + bridge mod under `instances/<name>/`, so runs never touch your
real saves. Logs: `instances/<name>/logs/client.log`. Screenshot of the virtual screen:

    docker exec tsim-w1 sh -c 'xwd -root -display :99 | convert xwd:- png:/tmp/s.png' && docker cp tsim-w1:/tmp/s.png .

Stop: `docker rm -f tsim-w1`.

## Gotchas found on the way

- Linux arm64 can't use the GOG check (tModLoader skips its setup there), hence amd64 + Rosetta.
- A path containing `steamapps` makes tModLoader assume Steam, so the game is mounted at `/game/`.
- With no sound device tModLoader stops on a "No audio hardware found" popup that waits for a click. That is very
  likely the Mac autoplay "freeze" on 2026-10-15 too (same log line right before it).
- A GoG install also shows "Steam Workshop unavailable"; entrypoint.sh clicks Continue until the bridge is up.
- The bridge only answers Host `127.0.0.1:7979`; the agent's BridgeClient always sends that Host.
- `config.json` (language + last-run version) skips the first-run language screen and the 1.4.3 port check.

## Cost

About 5 CPU cores and 1.5 GB RAM per world (software rendering under Rosetta): 2-3 worlds at once on an M4 Pro
with Docker at 8 GB.
