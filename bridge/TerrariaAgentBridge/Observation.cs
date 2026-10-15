using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Terraria;
using Terraria.ID;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// What the agent sees. Text, not pixels: an ASCII map around the player plus structured lists.
    /// Tile coordinates are absolute; x grows right, y grows DOWN (surface ~ worldSurface, hell at the bottom).
    /// </summary>
    public static class Observation
    {
        public const int MapHalfWidth = 30;  // 61 columns
        public const int MapHalfHeight = 17; // 35 rows
        public const int ScanRadiusX = 60, ScanRadiusY = 40;

        public const string Legend =
            "@ you, # solid block, = platform, T tree, $ ore, C chest, + other object/furniture, " +
            "~ water, % lava, h honey, E hostile npc, N friendly npc, . air";

        public static Dictionary<string, object> Build(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["player"] = PlayerSummary(p),
                ["world"] = World(p),
                ["inventory"] = Inventory(p),
                ["map"] = new Dictionary<string, object>
                {
                    ["origin"] = new[] { px - MapHalfWidth, py - MapHalfHeight },
                    ["legend"] = Legend,
                    ["rows"] = AsciiMap(p, px, py),
                },
                ["npcs"] = Npcs(p),
                ["nearby"] = NearbyTiles(px, py),
                ["craftable_now"] = Craftable(p),
            };
        }

        /// <summary>One line on nearby hostiles, closest first.</summary>
        public static string ThreatSummary(Player p)
        {
            var near = Main.npc.Where(n => n.active && !n.friendly && !n.townNPC && n.damage > 0)
                .Select(n => (n, d: (n.Center - p.Center).Length() / 16f))
                .Where(e => e.d < 30).OrderBy(e => e.d).Take(3)
                .Select(e => $"{e.n.FullName} at ({(int)(e.n.Center.X / 16)},{(int)(e.n.Center.Y / 16)}) {(int)e.d} tiles away, hp {e.n.life}/{e.n.lifeMax}")
                .ToList();
            return near.Count > 0 ? "Hostiles: " + string.Join("; ", near) + "." : "No hostile in sight (fall damage, a projectile, or something off-screen).";
        }

        public static Dictionary<string, object> PlayerSummary(Player p)
        {
            Item held = p.inventory[p.selectedItem];
            return new Dictionary<string, object>
            {
                ["x"] = Game.TileX(p),
                ["y"] = Game.TileY(p),
                ["hp"] = p.statLife,
                ["max_hp"] = p.statLifeMax2,
                ["mana"] = p.statMana,
                ["max_mana"] = p.statManaMax2,
                ["defense"] = (int)p.statDefense,
                ["on_ground"] = p.velocity.Y == 0f,
                ["in_water"] = p.wet,
                ["dead"] = p.dead,
                ["holding"] = held.IsAir ? null : Game.ItemName(held.type),
            };
        }

        private static Dictionary<string, object> World(Player p)
        {
            double hours = Main.dayTime ? 4.5 + Main.time / 3600.0 : 19.5 + Main.time / 3600.0;
            if (hours >= 24) hours -= 24;
            var biomes = new List<string>();
            if (p.ZoneForest) biomes.Add("forest");
            if (p.ZoneSnow) biomes.Add("snow");
            if (p.ZoneDesert) biomes.Add("desert");
            if (p.ZoneJungle) biomes.Add("jungle");
            if (p.ZoneCorrupt) biomes.Add("corruption");
            if (p.ZoneCrimson) biomes.Add("crimson");
            if (p.ZoneHallow) biomes.Add("hallow");
            if (p.ZoneBeach) biomes.Add("ocean");
            if (p.ZoneDungeon) biomes.Add("dungeon");
            if (p.ZoneUnderworldHeight) biomes.Add("underworld");
            else if (p.ZoneRockLayerHeight) biomes.Add("caverns");
            else if (p.ZoneDirtLayerHeight) biomes.Add("underground");
            else if (p.ZoneSkyHeight) biomes.Add("space");
            else biomes.Add("surface");

            return new Dictionary<string, object>
            {
                ["time"] = $"{(int)hours:00}:{(int)((hours % 1) * 60):00}",
                ["is_day"] = Main.dayTime,
                ["blood_moon"] = Main.bloodMoon,
                ["biomes"] = biomes,
                ["spawn"] = new[] { Main.spawnTileX, Main.spawnTileY },
                ["surface_y"] = (int)Main.worldSurface,
                ["size"] = new[] { Main.maxTilesX, Main.maxTilesY },
                ["bosses_defeated"] = Bosses(),
            };
        }

        private static List<string> Bosses()
        {
            var b = new List<string>();
            if (NPC.downedSlimeKing) b.Add("King Slime");
            if (NPC.downedBoss1) b.Add("Eye of Cthulhu");
            if (NPC.downedBoss2) b.Add("Eater of Worlds/Brain of Cthulhu");
            if (NPC.downedQueenBee) b.Add("Queen Bee");
            if (NPC.downedBoss3) b.Add("Skeletron");
            if (NPC.downedDeerclops) b.Add("Deerclops");
            if (Main.hardMode) b.Add("Wall of Flesh");
            return b;
        }

        public static List<Dictionary<string, object>> Inventory(Player p)
        {
            var list = new List<Dictionary<string, object>>();
            for (int i = 0; i < 58; i++)
            {
                Item it = p.inventory[i];
                if (it.IsAir) continue;
                var e = new Dictionary<string, object> { ["slot"] = i, ["item"] = Game.ItemName(it.type), ["stack"] = it.stack };
                if (it.pick > 0) e["pick"] = it.pick;
                if (it.axe > 0) e["axe"] = it.axe * 5;
                if (it.hammer > 0) e["hammer"] = it.hammer;
                if (it.damage > 0 && it.pick == 0 && it.axe == 0 && it.hammer == 0) e["damage"] = it.damage;
                if (it.createTile >= 0) e["places"] = Game.TileName(it.createTile);
                if (it.healLife > 0) e["heals"] = it.healLife;
                list.Add(e);
            }
            return list;
        }

        private static List<string> AsciiMap(Player p, int px, int py)
        {
            var grid = new char[MapHalfHeight * 2 + 1, MapHalfWidth * 2 + 1];
            for (int dy = -MapHalfHeight; dy <= MapHalfHeight; dy++)
            for (int dx = -MapHalfWidth; dx <= MapHalfWidth; dx++)
                grid[dy + MapHalfHeight, dx + MapHalfWidth] = TileChar(px + dx, py + dy);

            void Mark(int x, int y, char c)
            {
                int gx = x - px + MapHalfWidth, gy = y - py + MapHalfHeight;
                if (gx >= 0 && gy >= 0 && gx < grid.GetLength(1) && gy < grid.GetLength(0)) grid[gy, gx] = c;
            }

            foreach (NPC n in Main.npc)
                if (n.active) Mark((int)(n.Center.X / 16), (int)(n.Center.Y / 16), n.friendly || n.townNPC ? 'N' : 'E');
            Mark(px, py, '@');

            var rows = new List<string>();
            var sb = new StringBuilder();
            for (int r = 0; r < grid.GetLength(0); r++)
            {
                sb.Clear();
                for (int c = 0; c < grid.GetLength(1); c++) sb.Append(grid[r, c]);
                rows.Add(sb.ToString());
            }
            return rows;
        }

        private static char TileChar(int x, int y)
        {
            if (!Game.InWorld(x, y)) return '#';
            Tile t = Framing.GetTileSafely(x, y);
            if (t.HasTile && !t.IsActuated)
            {
                int type = t.TileType;
                if (Game.IsTree(type)) return 'T';
                if (TileID.Sets.Ore[type]) return '$';
                if (TileID.Sets.BasicChest[type]) return 'C';
                if (TileID.Sets.Platforms[type]) return '=';
                if (Main.tileSolid[type] && !Main.tileSolidTop[type]) return '#';
                if (!Main.tileCut[type]) return '+'; // grass/vines are cut-on-touch, show them as air
            }
            if (t.LiquidAmount > 64)
                return t.LiquidType == LiquidID.Lava ? '%' : t.LiquidType == LiquidID.Honey ? 'h' : '~';
            return '.';
        }

        private static List<Dictionary<string, object>> Npcs(Player p)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (NPC n in Main.npc)
            {
                if (!n.active) continue;
                float dist = (n.Center - p.Center).Length() / 16f;
                if (dist > ScanRadiusX) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = n.FullName,
                    ["x"] = (int)(n.Center.X / 16),
                    ["y"] = (int)(n.Center.Y / 16),
                    ["hp"] = n.life,
                    ["max_hp"] = n.lifeMax,
                    ["hostile"] = !n.friendly && !n.townNPC && n.damage > 0,
                    ["boss"] = n.boss,
                    ["distance"] = (int)dist,
                });
            }
            return list.OrderBy(d => (int)d["distance"]).Take(15).ToList();
        }

        /// <summary>Closest few of each interesting tile type outside the visible map too.</summary>
        private static Dictionary<string, object> NearbyTiles(int px, int py)
        {
            var found = new Dictionary<string, List<(int x, int y, int d)>>();
            for (int x = px - ScanRadiusX; x <= px + ScanRadiusX; x++)
            for (int y = py - ScanRadiusY; y <= py + ScanRadiusY; y++)
            {
                if (!Game.InWorld(x, y)) continue;
                Tile t = Framing.GetTileSafely(x, y);
                if (!t.HasTile) continue;
                int type = t.TileType;
                bool interesting = TileID.Sets.Ore[type] || TileID.Sets.BasicChest[type] || Main.tileTable[type]
                    || type == TileID.WorkBenches || type == TileID.Furnaces || type == TileID.Anvils || type == TileID.MythrilAnvil
                    || type == TileID.Hellforge || type == TileID.Sawmill || type == TileID.Loom || type == TileID.Bottles
                    || type == TileID.DemonAltar || type == TileID.Heart || type == TileID.LifeFruit || type == TileID.Pots
                    || Game.IsTreeBase(x, y);
                if (!interesting) continue;

                string name = Game.TileName(type);
                if (!found.TryGetValue(name, out var l)) found[name] = l = new List<(int, int, int)>();
                l.Add((x, y, Math.Abs(x - px) + Math.Abs(y - py)));
            }

            var result = new Dictionary<string, object>();
            foreach (var (name, l) in found)
            {
                result[name] = new Dictionary<string, object>
                {
                    ["count"] = l.Count,
                    ["closest"] = l.OrderBy(e => e.d).Take(3).Select(e => new[] { e.x, e.y }).ToList(),
                };
            }
            return result;
        }

        /// <summary>Recipes whose ingredients and stations the player has right now.</summary>
        private static List<string> Craftable(Player p)
        {
            var names = new SortedSet<string>();
            for (int i = 0; i < Recipe.numRecipes; i++)
            {
                Recipe r = Main.recipe[i];
                if (r.createItem.IsAir || r.Disabled) continue;
                if (Crafting.MissingStations(p, r).Count == 0 && Crafting.MissingIngredients(p, r).Count == 0)
                    names.Add(Game.ItemName(r.createItem.type));
            }
            return names.Take(40).ToList();
        }
    }
}
