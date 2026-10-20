# Next testing session

## Overnight 2026-10-21 (queued): A/A control, `sim/night9.sh`

Both arms run night7's Director (HEAD 50e919d, refuge reverted) on mods-night8. Goal: the noise floor between two
arms of the same code on one night, before trusting the next A/B. Score: `sim/ab_report.sh ~/Library/Caches/terraria-sim/night9`.

## Overnight 2026-10-20 (night8): house refuge lost

| Arm | avg steps | avg deaths | homes |
|---|---|---|---|
| old (night7 code) | 10.7 | 34.0 | 7/7 |
| new (house refuge, d821dde) | 7.7 | 65.5 | 6/7 |

Reverted in 50e919d. new3-5 died 85-144 times: worth reading why before retrying a shelter idea. LLM track 7/8 fresh
and learned; copper_bars still fails with the recipe book in the prompt.

## Overnight 2026-10-16 (01:00-06:55, unattended)

Queue: `sim/night.sh` -> `night-b.sh` -> `night-c.sh` -> `night-d.sh` (logs in `~/Library/Caches/terraria-sim/night/`).
Builds: `mods-night` (HEAD aa107e8^), `mods-night3` (craft totals, flee 40, besieged wait), `mods-night4` (+ station
levelling, mine lands before reach; latest, commit cd6da7d). Findings 24-27 in `docs/lab/findings.json`.

| Track | Tonight | Before | Notes |
|---|---|---|---|
| Survival (1x, x3) | 14/18 -> 15/18 -> **16/18** | 16/18 | two_zombies_low_night 0/3 -> 3/3 -> 2/3 after flee 40 + besieged wait |
| Nav | 4/4 (x1) | 12/12 | on mods-night4: the mine change didn't break it |
| Station (new, x3) | 9/12 -> **12/12** | - | shaft 0/3 -> 3/3 |
| LLM fresh / learned | 7/8 / 7/8 in all four A/Bs (night1-4) | 6/8 / 7/8 (rec1) | only copper_bars fails: 0/8 tonight |
| Director 19-step (sim, 1 h, survival on) | **10/19**, 26 deaths | 7/19 | journal `agent/runs/director-night/` |

copper_bars failed a different way every pass: ore count read as bar count (3 ore for 3 bars, even with the new
"you need 6 in total" hint), the Furnace not placeable in a shaft (fixed), giving up with `wait 0` (now rejected),
a retry re-running the wood skill in a cut forest (180 s timeout), and invented coordinates into a wall. The
harness blockers are fixed; what's left is the model's plan. Options: a recipe tool that returns the full ingredient
tree for N items, or let `craft` of a bar call `obtain` for missing ore.

### Open (not fixed, not in findings.json)

- Gathering: a retry calls the wood library skill again in a forest the first attempt cut ->
  `mine_nearest Trees x30 timed out after 180s` (3 passes). Stop early when nothing is reachable, or ask for the shortfall.
- Director: respawn death spiral at night (~10 deaths in the first 5 min, back at the house site at 25-35 hp into the
  same zombies). Shelter after a night respawn before resuming a step. build_room timed out repeatedly; the stone
  workshop found no flat 16-wide spot.
- Survival: single deaths outside the target scenario move the suite +-2 between runs (zombie_half_hp, demon_eye,
  low_hp_alone died once each in one suite; low_hp_alone died inside dig_in with no hostile logged). Record
  survival suites (REC=1 TA_RENDER=1) to see them.
- Harness: the arena keeps earlier builds; each repeat of the station suite starts ~20 rows higher.

## Earlier: state as of 2026-10-15 evening

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
