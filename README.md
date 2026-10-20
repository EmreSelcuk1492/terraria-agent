# terraria-agent

Claude plays Terraria. This is the Minecraft agent setup (Mineflayer + Voyager/Mindcraft) rebuilt for Terraria, which had no bot API.

```
 agent/ (Python)                        bridge/ (tModLoader mod, C#)                  Terraria
 ┌────────────────────┐   HTTP JSON    ┌──────────────────────────────┐   per tick   ┌──────────┐
 │ Claude (tool use)  │ ─────────────▶ │ BridgeServer  127.0.0.1:7979 │ ───────────▶ │ player   │
 │ observe/move/mine… │ ◀───────────── │ Observation · SkillRunner    │ ◀─────────── │ world    │
 │ lessons.md memory  │  result+state  │ AgentPlayer.SetControls      │   state      │          │
 └────────────────────┘                └──────────────────────────────┘              └──────────┘
```

- **Layer 1, the bridge.** A client-side tModLoader mod. It serves game state as text (an ASCII map, inventory, NPCs, nearby resources, what you can craft) and runs skills by setting the player's controls each tick, the same flags a keyboard sets.
- **Layer 2, skills (C#).** `move_to`, `mine`, `mine_nearest`, `place`, `craft`, `attack`, `use_item`, `wait`. Each one runs over many ticks inside the game and fails with a message the LLM can act on ("Blocked by Stone at (412,233); wall is ~4 tiles high. Mine it or build up.").
- **Layer 3, the agent (Python).** A Claude tool-use loop. Lessons saved with `remember` go into `lessons.md` and are loaded into the next run's prompt.
- **Latency fix.** With freeze on (the default), the world stops while Claude thinks and only runs while a skill executes. Enemies can't hit you during a 10-second plan.

## Setup

1. **tModLoader**: install it from Steam (free, separate from Terraria) and launch it once.
2. **.NET 8 SDK + build the mod**
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/.dotnet
   ./scripts/link-mod.sh     # symlink into ModSources (also lets tModLoader's Develop Mods menu see it)
   ./scripts/build-mod.sh    # compiles + writes TerrariaAgentBridge.tmod into tModLoader's Mods folder
   ```
   In tModLoader: **Workshop → Manage Mods → Reload Mods** (the mod should show as enabled). `http://127.0.0.1:7979/health` answers once it's loaded.
3. **Agent**
   ```bash
   cd agent
   uv venv -p 3.12 .venv && uv pip install -p .venv anthropic httpx pytest
   export ANTHROPIC_API_KEY=...        # or: ant auth login
   ```
4. Load a **single-player** world (a fresh character on a small world works best), then:
   ```bash
   .venv/bin/python -m terraria_agent --goal "Gather wood, craft a work bench, and craft a wooden sword"
   ```
   The terminal streams Claude's reasoning and every skill call. Full transcripts go to `agent/runs/*.jsonl`. Ctrl-C cancels the running skill and unfreezes the world.

Flags: `--effort low|medium|high|xhigh|max` (default medium), `--max-turns 150`, `--no-freeze`, `--model`.

### Without the game

```bash
cd agent
.venv/bin/python mock_bridge.py &            # fake flat world, same HTTP protocol
.venv/bin/python -m terraria_agent --goal "Craft a wooden sword"
.venv/bin/python -m pytest -q                # offline tests, scripted Claude, no API calls
```

## LLM analytics (PostHog)

Every model call, skill call and run goes to PostHog's AI observability (`terraria_agent/telemetry.py`), for all
backends (Claude, Ollama, OpenAI-compatible), the Director's advisor and the LLM track:

- **Trace** = one agent run, one Director advisor consult, or one LLM-track task. `$ai_is_error` is set when it
  missed its goal, so failing traces filter out directly.
- **Generation** = one model call with the full prompt, reply, tool calls, tokens, latency and cost (local models: $0).
- **Span** = one skill call (or one planned program in the LLM track) with its result, under the generation that asked for it.
- **Session** = one process (a Director run, one LLM A/B pass). Name it with `TA_SESSION=night4-learned`.
- Breakdown properties on every event: `track` (agent / director / llm_bench), plus `mode`, `library`, `think` on the LLM track.

Setup: put `POSTHOG_PROJECT_TOKEN=phc_...` and `POSTHOG_HOST=https://us.i.posthog.com` in `agent/.env`
(gitignored) and `uv pip install -p .venv posthog`. Without a token it warns once and sends nothing;
`TA_TELEMETRY=0` turns it off; tests never send.

## The Director: hands-off early game

`python -m terraria_agent.director` plays the first in-game days of a fresh world on its own. A local model is too
slow and too erratic to drive every step, so the plan is code and the model only advises when something breaks:

1. **19 checked steps**, each tested against the live game state, not assumed: wood → torches → a home near spawn
   → stone → copper/tin → iron/lead → life crystals → bars + anvil → a stone workshop (furnace + anvil inside) →
   better pickaxe, broadsword, helmet, greaves, chainmail → raid nearby chests → a house for the Merchant →
   finish every house to Terraria's own NPC-housing standard → store the loot. Each step has fallbacks (the world has
   copper *or* tin, iron *or* lead; no crystals in range → tunnel deeper first).
2. **Rules that always win**: wait out death; drink a potion (one a minute) or retreat home at low health; at night
   do underground work or stay home; climb back to the surface before surface work.
3. **Recovery**: a step that fails asks the LLM advisor (`gpt-oss:20b` via Ollama, optional) for one corrective
   call, then retries; a step that keeps failing (or "succeeds" without meeting its goal) is parked and retried later.
4. **When the plan is done** it patrols: deeper ore, more life crystals, unlooted chests, better gear.
5. **Narration** in the in-game chat (`[AI] Step 6/19: Mine copper or tin, ...`), a markdown journal with a final
   summary, a JSONL log of every call, and a per-world memo (houses, finished steps) in `agent/runs/`.

```bash
cd agent
.venv/bin/python -m terraria_agent.director --hours 2             # with the gpt-oss advisor
.venv/bin/python -m terraria_agent.director --advisor none        # rules only
```

## Unattended play

- **Keeps running in the background.** Single player normally pauses when its window loses focus (and only
  reads controls with focus). While an agent drives, the bridge patches that check (`IL_Main.DoUpdate`), so the
  game keeps playing behind other windows. Without an agent, alt-tab pauses as usual.
- **Bodyguard.** A hit or an enemy within 6 tiles (with line of sight) no longer ends the running skill: the skill
  pauses, the guard fights (and quick-heals when low), then the skill resumes. Results say `[guard: killed Zombie]`.
- **Rebuild without hands.** `POST /quit` saves and exits; the next launch loads straight back into the same world
  (one-shot `agent-autoload.json`). `scripts/cycle.sh` = quit → rebuild mod → relaunch → wait for the world (~20 s).
- **Start when the Mac is usable.** A locked screen can't open the game window, so `scripts/autoplay.sh HOURS PLAYER
  WORLD` waits for the unlock, launches into the world and starts the Director.

## Skills

| Skill | What it does in game |
|---|---|
| `move_to`, `mine`, `mine_nearest`, `place`, `craft`, `attack`, `use_item`, `wait` | basics (`mine` tunnels when it can't walk into reach; `attack` falls back to the nearest enemy) |
| `obtain` | any item end to end: gathers (chops, digs down for buried stone, mines ore veins, fights slimes for gel), crafts every intermediate, finds/places stations |
| `build_house` | a furnished, lit house: wood or stone, 10–16 × 6–9, extras (Furnace, Chest, Bed, anvils, Sawmill, Loom), Terraria's own NPC-housing check |
| `build_room`, `check_house` | the frame alone; the inspection alone |
| `dig_down`, `tunnel_to`, `return_to_surface` | straight down until a tile type is in reach (sidesteps trees/hard ground); a walkable stair tunnel to any spot (stops at lava, torches the way); climb out |
| `mine_vein` | nearest vein of an ore (or any ore), whole vein, then the next |
| `collect_life_crystals` | tunnel to Life Crystals, break and use them |
| `equip_best`, `loot_chests`, `store_items`, `set_spawn` | best armor/accessories/tools; empty nearby chests; stash loot at home; respawn at a bed |
| `tb_nav` | real pathfinding through [TerraBlind](https://github.com/Reisenbug/TerraBlind)'s navigator (jumps, drops, digs, bridges); needs TerraBlind built with `./scripts/build-terrablind.sh` and enabled. Not yet run live |

## Protocol

| Call | Body | Returns |
|---|---|---|
| `GET /health` | | `{ok, version, in_world, freeze}` |
| `GET /state` | | `{player, world, inventory, map{origin, legend, rows}, npcs, nearby, craftable_now}` |
| `POST /skill` | `{name, args}` | blocks until done → `{ok, skill, message, ticks, player, inventory}` |
| `POST /cancel` | | stops the running skill |
| `POST /config` | `{freeze, background, guard}` | freeze world while no skill runs; keep running unfocused; bodyguard on/off |
| `POST /say` | `{text}` | a line in the in-game chat |
| `POST /save`, `POST /quit` | | save now; save, exit, and autoload this world next launch |

Skill args are in `bridge/TerrariaAgentBridge/SkillFactory.cs` and mirrored in `agent/terraria_agent/tools.py`.

## Coordinates

Absolute tile coordinates. x grows right, **y grows down**. The player's reported tile is the centre of its 2×3 body. Reach is about 5×4 tiles.

## Verified vs. unverified in game

Checked in a live world (tModLoader 2026.08, small world): the bridge, observation, `move_to`, `mine`, `obtain` for
wood/workbench/door/table/chair, `build_house` (wood, 10x6), `dig_down` reaching buried stone, `attack nearest`,
autoload + `/quit` + `scripts/cycle.sh`, and the background (unfocused) patch keeping skills and game time running.

Written and reviewed but **not yet run in a live world** (the Mac's screen locked mid-session, and a locked
session can't open the game window): the bodyguard, the stone/extras/NPC-check version of `build_house`,
`tunnel_to`, `mine_vein`, `collect_life_crystals`, `return_to_surface`, `equip_best`, `loot_chests`,
`store_items`, `set_spawn`, the HUD, and the Director on real terrain (it completes its whole plan against
`mock_bridge.py`, and the tests cover the plan, night rules and the no-progress guard).

## Credits

Navigation, housing and survival work here learns from [TerraBlind](https://github.com/Reisenbug/TerraBlind) by Reisenbug (MIT), included as a pinned submodule in `third_party/TerraBlind` and built with `./scripts/build-terrablind.sh`. See [CREDITS.md](CREDITS.md) and [docs/learning/terrablind.md](docs/learning/terrablind.md).

## Honest limits of v0.1

- `move_to` is a greedy walker, not A*. It can't plan routes over cliffs or through caves.
- `mine` simulates swing time, then calls `WorldGen.KillTile` (it doesn't swing hit by hit). It checks the minimum pickaxe power for the vanilla tiles that need it.
- `craft` is instant and skips special recipe conditions (near water, etc.).
- `attack` walks straight at the target and swings. No kiting, no dodging projectiles.
- Single player only.
