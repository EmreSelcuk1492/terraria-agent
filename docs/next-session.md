# Next testing session

State as of 2026-10-15 evening. Branch `bg-explore` (from `docker-sim`), clone at `~/Documents/GitHub/terraria-agent-bg`
(the career repo's hook blocks git worktrees, so this is a separate clone of the same GitHub repo). Not merged.

## Where things stand

| Track | Latest | Before | Notes |
|---|---|---|---|
| Survival suite (1x, headless) | 16/18 | 15/18 rendered | `two_zombies_low_night` is the weak spot (1/3) |
| Nav suite | 12/12 | 12/12 | |
| LLM track, fresh library | 5/8 | 5/8 (old harness) | fresh world per task; misses were door/furnace/copper, since fixed in the bridge |
| LLM track, learned library | 6/7, incomplete | 1/8 (crashed) | wood to door all first-attempt; stopped by host memory before copper_bars |
| Director 19-step | not rerun | 7/19 sim, 4/19 Mac | the 7/19 started from a world that already had a house |

## Untested or half-tested

- `flee` (Survival.cs): first live run in the Mac showcase escaped 25 tiles, then the zombies caught up during the
  ~6 s `dig_in` and it died. Next: run further (distance 30+) or keep running until zombies lose track, then dig in.
  The bodyguard no longer interrupts flee.
- Hillside levelling for stations, clearing a tree/plant before mining the block under it, `craft` succeeding when
  you already hold enough: targeted live checks passed; no full LLM run on them yet.

## Run order next time (one or two worlds at a time; Docker 8 GB fits 3, the Mac gets tight with Ollama)

1. Build: `scripts/build-sim-mod.sh ~/Library/Caches/terraria-sim/mods/TerrariaAgentBridge.tmod`
2. Survival with flee: `sim/scenarios.sh 2 --repeat 3 --tag survival` (1x: fast-forward costs fights reaction time)
3. LLM A/B: `REP=next sim/llm_ab.sh primitive --headless --speed 4` (fresh world per task is built in)
4. Director 19-step, to watch it on the Mac: back up `~/Library/Application Support/Terraria/tModLoader/{Worlds,Players}`,
   copy `~/Library/Caches/terraria-sim/bench-world/{Worlds,Players}` over them, `scripts/cycle.sh "Test 1" "The Burning Dam of Taxes"`,
   then `agent/.venv/bin/python -m terraria_agent.director --peaceful --start-at dawn --hours 0.75`. Restore the backup after.
   In a sim instead: `sim/run.sh dir1 18995 "Test 1" "The Burning Dam of Taxes" --fresh` (`TA_RENDER=1` to take screenshots).
5. Showcase on the Mac: `python -m terraria_agent.showcase` (acts: flee, wall, fight, nav, llm). The arena flattens a
   strip 80 tiles east of spawn: back the world up first.

## Mac saves

Backups of the real world and character are in `~/Library/Application Support/Terraria/tModLoader/agent-backups/`
(before the showcase, and after it). The live save is the after-showcase one. The Mac Mods folder has the
`bg-explore` bridge (render/speed/flee); the earlier build is in the before-showcase backup.
