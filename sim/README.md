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

## Speed

Worlds start headless (`TA_RENDER=1000`: the game draws 1 frame in 1000 once a world is loaded; menus still draw).
Software rendering was what held the game at ~21 ticks/s; headless it runs at the normal 60 with ~0.5 GB instead of
~2 GB. Fast-forward on top: `/config {"speed": k}` runs k game ticks per frame (about 8-10x real time at k=8).

    sim/scenarios.sh 3 --repeat 3 --tag survival --speed 4
    sim/llm_ab.sh primitive --headless --speed 4

Time limits and timings are game time (the bridge's tick counter, `/health` `tick`), so a result means the same at any
speed. Fights are the exception: the world runs while the agent decides, so at 4x each second of thinking is 4
seconds of game (flier scenario 3/3 at 1x, 1/3 at 4x). Survival suites run at 1x; fast-forward the tracks that freeze
the world while the agent thinks. Check: the same 7-tree task took 1639 ticks and ended on the same tile at 1x and 4x (78 s vs 3.5 s of wall clock).
`TA_RENDER=1 sim/run.sh ...` for a world you want screenshots of.

A sim-only bridge build: `scripts/build-sim-mod.sh <out.tmod>` then `SIM_MOD=<out.tmod> sim/run.sh ...`, so new code can
run next to a benchmark that keeps its own build.

## Gotchas found on the way

- Linux arm64 can't use the GOG check (tModLoader skips its setup there), hence amd64 + Rosetta.
- A path containing `steamapps` makes tModLoader assume Steam, so the game is mounted at `/game/`.
- With no sound device tModLoader stops on a "No audio hardware found" popup that waits for a click. That is very
  likely the Mac autoplay "freeze" on 2026-10-15 too (same log line right before it).
- A GoG install also shows "Steam Workshop unavailable"; entrypoint.sh clicks Continue until the bridge is up.
- The bridge only answers Host `127.0.0.1:7979`; the agent's BridgeClient always sends that Host.
- `config.json` (language + last-run version) skips the first-run language screen and the 1.4.3 port check.

## Cost

Headless: about 1.5 cores per world at 1x, and ~1.8 GB once a world is loaded (rendering adds little memory; it
costs CPU). With Docker at 8 GB that is **3 worlds at once**: a fourth got OOM-killed twice on 2026-10-15, once taking
a benchmark world down with it. `sim/run.sh` caps each world at 3.5 GB (`SIM_MEM`) and `sim/scenarios.sh` staggers
starts by 25 s (`STAGGER`). Rendered: about 5 cores and 2 GB per world.
