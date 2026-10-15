using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// POST /scenario: put the world in a known state for a short, repeatable test (agent/terraria_agent/scenarios.py).
    /// All fields optional:
    ///   {"arena": true | "at": "spawn" | [x, y], "clear": true, "natural_spawns": false, "time": "dawn|noon|dusk|midnight",
    ///    "hp": 40, "give": {"LesserHealingPotion": 3}, "take": ["LesserHealingPotion"],
    ///    "spawn": [{"npc": "Zombie", "dx": 3, "dy": 0}]}
    /// dx/dy are tiles from the player after the move. Runs on the main thread; returns what it did.
    /// </summary>
    public static class Scenario
    {
        public static Dictionary<string, object> Apply(JsonElement r)
        {
            Player p = Main.LocalPlayer;
            var did = new Dictionary<string, object> { ["ok"] = true };
            if (p.dead) return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"player is dead; respawning in {p.respawnTimer / 60 + 1}s" };

            SkillRunner.Cancel("scenario reset");

            if (r.TryGetProperty("natural_spawns", out var ns))
                Sandbox.NoNaturalSpawns = ns.ValueKind == JsonValueKind.False;
            if (!r.TryGetProperty("clear", out var clear) || clear.ValueKind != JsonValueKind.False)
                did["cleared"] = Sandbox.ClearHostiles();

            if (r.TryGetProperty("arena", out var arena) && arena.ValueKind == JsonValueKind.True)
            {
                var (ax, ag) = BuildArena();
                p.Teleport(new Vector2(ax * 16 + 8 - p.width / 2f, ag * 16 - p.height), TeleportationStyleID.DebugTeleport);
                p.velocity = Vector2.Zero;
                p.fallStart = (int)(p.position.Y / 16f);
                did["arena"] = new[] { ax, ag };
            }
            else if (r.TryGetProperty("surface_at", out var sa))
            {
                // Natural terrain `dx` tiles east of spawn (away from whatever earlier runs built or dug at spawn).
                int tx = Math.Clamp(Main.spawnTileX + sa.GetInt32(), 60, Main.maxTilesX - 60);
                int ty = Ground(tx, 50);
                p.Teleport(new Vector2(tx * 16 + 8 - p.width / 2f, ty * 16 - p.height), TeleportationStyleID.DebugTeleport);
                p.velocity = Vector2.Zero;
                p.fallStart = (int)(p.position.Y / 16f);
                did["at"] = new[] { tx, ty };
            }
            else if (r.TryGetProperty("at", out var at))
            {
                int tx, ty;
                if (at.ValueKind == JsonValueKind.Array) { tx = at[0].GetInt32(); ty = at[1].GetInt32(); }
                else { tx = Main.spawnTileX; ty = Main.spawnTileY; }
                p.Teleport(new Vector2(tx * 16 + 8 - p.width / 2f, ty * 16 - p.height), TeleportationStyleID.DebugTeleport);
                p.velocity = Vector2.Zero;
                did["at"] = new[] { tx, ty };
            }

            // Terrain on top of the arena, relative to its centre column and ground row (negative dy is up):
            // {"build": [{"tile": "Dirt" | "air", "dx0": 4, "dx1": 6, "dy0": -4, "dy1": -1}]}
            if (r.TryGetProperty("build", out var build) && build.ValueKind == JsonValueKind.Array && did.TryGetValue("arena", out var ao))
            {
                var a = (int[])ao;
                foreach (var b in build.EnumerateArray())
                {
                    string tile = b.TryGetProperty("tile", out var tt) ? tt.GetString() : "Dirt";
                    int type = tile == "air" ? -1 : Game.ResolveTile(tile);
                    if (tile != "air" && type < 0) return Error($"unknown tile '{tile}'");
                    for (int i = a[0] + b.GetProperty("dx0").GetInt32(); i <= a[0] + b.GetProperty("dx1").GetInt32(); i++)
                        for (int j = a[1] + b.GetProperty("dy0").GetInt32(); j <= a[1] + b.GetProperty("dy1").GetInt32(); j++)
                        {
                            if (Framing.GetTileSafely(i, j).HasTile) WorldGen.KillTile(i, j, fail: false, effectOnly: false, noItem: true);
                            if (type >= 0) WorldGen.PlaceTile(i, j, type, mute: true, forced: true);
                        }
                }
                // Teleport again: the build may have changed what is under the player.
                p.Teleport(new Vector2(a[0] * 16 + 8 - p.width / 2f, a[1] * 16 - p.height), TeleportationStyleID.DebugTeleport);
                p.velocity = Vector2.Zero;
            }

            if (r.TryGetProperty("time", out var time))
            {
                if (!SetTime(time.GetString())) return Error("time must be dawn, noon, dusk or midnight");
                did["time"] = time.GetString();
            }

            // A new character's kit and stats, so benchmark results don't depend on what earlier runs left in the
            // save (the Mac run left 423 stone, iron ore and a life crystal on the player the sim copies).
            if (r.TryGetProperty("fresh_player", out var fresh) && fresh.ValueKind == JsonValueKind.True)
            {
                for (int i = 0; i < p.inventory.Length; i++) p.inventory[i].TurnToAir();
                for (int i = 0; i < p.armor.Length; i++) p.armor[i].TurnToAir();
                for (int i = 0; i < p.miscEquips.Length; i++) p.miscEquips[i].TurnToAir();
                p.ConsumedLifeCrystals = 0;
                p.ConsumedLifeFruit = 0;
                p.ConsumedManaCrystals = 0;
                p.statLifeMax = 100;
                p.statManaMax = 20;
                int slot = 0;
                foreach (int type in new[] { ItemID.CopperShortsword, ItemID.CopperPickaxe, ItemID.CopperAxe })
                    p.inventory[slot++].SetDefaults(type);
                p.selectedItem = 0;
                p.statLife = 100;
                did["fresh_player"] = true;
            }

            if (r.TryGetProperty("take", out var take) && take.ValueKind == JsonValueKind.Array)
                foreach (var name in take.EnumerateArray())
                {
                    int type = Game.ResolveItem(name.GetString());
                    for (int i = 0; i < 58; i++)
                        if (type > 0 && p.inventory[i].type == type) p.inventory[i].TurnToAir();
                }

            if (r.TryGetProperty("give", out var give) && give.ValueKind == JsonValueKind.Object)
                foreach (var kv in give.EnumerateObject())
                {
                    int type = Game.ResolveItem(kv.Name);
                    if (type <= 0) return Error($"unknown item '{kv.Name}'");
                    p.QuickSpawnItem(p.GetSource_Misc("agent-scenario"), type, Math.Max(1, kv.Value.GetInt32()));
                }

            if (r.TryGetProperty("hp", out var hp))
            {
                p.statLife = Math.Clamp(hp.GetInt32(), 1, p.statLifeMax2);
                did["hp"] = p.statLife;
            }

            var spawned = new List<object>();
            if (r.TryGetProperty("spawn", out var spawn) && spawn.ValueKind == JsonValueKind.Array)
                foreach (var s in spawn.EnumerateArray())
                {
                    string name = s.GetProperty("npc").GetString();
                    if (!NPCID.Search.TryGetId(name, out int type)) return Error($"unknown npc '{name}' (use internal names: Zombie, DemonEye, BlueSlime, Skeleton)");
                    int dx = s.TryGetProperty("dx", out var vx) ? vx.GetInt32() : 3;
                    int dy = s.TryGetProperty("dy", out var vy) ? vy.GetInt32() : 0;
                    // NewNPC takes the bottom-centre in world pixels.
                    int x = (int)p.Center.X + dx * 16, y = (int)p.Bottom.Y + dy * 16;
                    int idx = NPC.NewNPC(p.GetSource_Misc("agent-scenario"), x, y, type);
                    if (idx >= Main.maxNPCs) return Error($"could not spawn {name} (NPC limit)");
                    spawned.Add(new Dictionary<string, object> { ["npc"] = Main.npc[idx].FullName, ["index"] = idx, ["at"] = new[] { x / 16, y / 16 } });
                }
            did["spawned"] = spawned;
            return did;
        }

        /// <summary>
        /// First standable ground in column x from `fromY` down that isn't a floating island: a 10+ row air gap within
        /// 60 rows under it means an island (an LLM-track start landed on one at y=66 with a 29-tile drop below).
        /// </summary>
        public static int Ground(int x, int fromY)
        {
            int y = fromY;
            while (y < Main.worldSurface + 100)
            {
                while (y < Main.worldSurface + 100 && !(Game.Solid(x, y) && !Game.Solid(x, y - 1))) y++;
                int gap = 0, run = 0;
                for (int j = y + 1; j < y + 60 && gap < 10; j++) { run = Game.Solid(x, j) ? 0 : run + 1; gap = Math.Max(gap, run); }
                if (gap < 10) return y;
                y += 2; // an island: keep looking below it
            }
            return y;
        }

        private const int ArenaHalf = 20, ArenaHeight = 12;

        /// <summary>
        /// A flat, empty strip 80 tiles east of spawn, rebuilt on every call: everything above the ground row is
        /// removed (trees, houses, liquids), and the ground is three rows of dirt, so every scenario fights on the
        /// same terrain no matter what the world (or the last run) did to it. Returns centre column and ground row.
        /// </summary>
        public static (int x, int ground) BuildArena()
        {
            int x = Math.Clamp(Main.spawnTileX + 80, ArenaHalf + 50, Main.maxTilesX - ArenaHalf - 50);
            int g = Ground(x, 50);
            for (int i = x - ArenaHalf - 1; i <= x + ArenaHalf + 1; i++)
            {
                for (int j = g - ArenaHeight; j < g; j++)
                {
                    Tile t = Framing.GetTileSafely(i, j);
                    if (t.HasTile) WorldGen.KillTile(i, j, fail: false, effectOnly: false, noItem: true);
                    t = Framing.GetTileSafely(i, j);
                    t.LiquidAmount = 0;
                    t.WallType = 0;
                }
                for (int j = g; j < g + 3; j++)
                {
                    Tile t = Framing.GetTileSafely(i, j);
                    if (t.HasTile && t.TileType != TileID.Dirt) WorldGen.KillTile(i, j, fail: false, effectOnly: false, noItem: true);
                    if (!Framing.GetTileSafely(i, j).HasTile) WorldGen.PlaceTile(i, j, TileID.Dirt, mute: true, forced: true);
                    t = Framing.GetTileSafely(i, j);
                    t.LiquidAmount = 0;
                }
            }
            foreach (Item it in Main.item) // drops from earlier fights, so pickups don't vary between attempts
                if (it.active && Math.Abs(it.Center.X / 16f - x) < ArenaHalf + 4 && Math.Abs(it.Center.Y / 16f - g) < ArenaHeight) it.active = false;
            return (x, g);
        }

        public static bool SetTime(string at)
        {
            (bool day, double t) = at switch
            {
                "dawn" => (true, 0.0), "noon" => (true, 27000.0), "dusk" => (false, 0.0), "midnight" => (false, 16200.0),
                _ => (true, -1.0),
            };
            if (t < 0) return false;
            Main.dayTime = day;
            Main.time = t;
            return true;
        }

        private static Dictionary<string, object> Error(string msg) => new() { ["ok"] = false, ["error"] = msg };
    }
}
