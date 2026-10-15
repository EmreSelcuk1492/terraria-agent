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

## Protocol

| Call | Body | Returns |
|---|---|---|
| `GET /health` | | `{ok, version, in_world, freeze}` |
| `GET /state` | | `{player, world, inventory, map{origin, legend, rows}, npcs, nearby, craftable_now}` |
| `POST /skill` | `{name, args}` | blocks until done → `{ok, skill, message, ticks, player, inventory}` |
| `POST /cancel` | | stops the running skill |
| `POST /config` | `{freeze: bool}` | freeze world while no skill runs |

Skill args are in `bridge/TerrariaAgentBridge/SkillFactory.cs` and mirrored in `agent/terraria_agent/tools.py`.

## Coordinates

Absolute tile coordinates. x grows right, **y grows down**. The player's reported tile is the centre of its 2×3 body. Reach is about 5×4 tiles.

## Unverified in game

The mod compiles against tModLoader 1.4.4.9 (2026.08 stable). Not yet checked in a live world:

- World freeze (`On_Main.DoUpdateInWorld` hook): that the screen and the bridge keep responding while frozen.
- Skill feel: jump timing in `move_to`, swing-time estimates in `mine`.
- Mouse aiming (`AgentPlayer.Aim`) writes `Main.mouseX/Y`. It's right at zoom 100%, but at other zoom levels ranged weapons may aim off.

## Honest limits of v0.1

- `move_to` is a greedy walker, not A*. It can't plan routes over cliffs or through caves.
- `mine` simulates swing time, then calls `WorldGen.KillTile` (it doesn't swing hit by hit). It checks the minimum pickaxe power for the vanilla tiles that need it.
- `craft` is instant and skips special recipe conditions (near water, etc.).
- `attack` walks straight at the target and swings. No kiting, no dodging projectiles.
- Single player only.
