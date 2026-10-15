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
            else if (r.TryGetProperty("at", out var at))
            {
                int tx, ty;
                if (at.ValueKind == JsonValueKind.Array) { tx = at[0].GetInt32(); ty = at[1].GetInt32(); }
                else { tx = Main.spawnTileX; ty = Main.spawnTileY; }
                p.Teleport(new Vector2(tx * 16 + 8 - p.width / 2f, ty * 16 - p.height), TeleportationStyleID.DebugTeleport);
                p.velocity = Vector2.Zero;
                did["at"] = new[] { tx, ty };
            }

            if (r.TryGetProperty("time", out var time))
            {
                if (!SetTime(time.GetString())) return Error("time must be dawn, noon, dusk or midnight");
                did["time"] = time.GetString();
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

        private const int ArenaHalf = 20, ArenaHeight = 12;

        /// <summary>
        /// A flat, empty strip 80 tiles east of spawn, rebuilt on every call: everything above the ground row is
        /// removed (trees, houses, liquids), and the ground is three rows of dirt, so every scenario fights on the
        /// same terrain no matter what the world (or the last run) did to it. Returns centre column and ground row.
        /// </summary>
        public static (int x, int ground) BuildArena()
        {
            int x = Math.Clamp(Main.spawnTileX + 80, ArenaHalf + 50, Main.maxTilesX - ArenaHalf - 50);
            int g = 50;
            while (g < Main.worldSurface && !Game.Solid(x, g)) g++;
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
