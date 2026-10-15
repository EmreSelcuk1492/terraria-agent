using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>
    /// Break one tile with the right tool (axe for trees, pickaxe for everything else), walking
    /// into reach first and to the drop afterwards so items get picked up.
    /// Swing time is simulated from the tool's useTime and power; the tile is then broken with
    /// WorldGen.KillTile, which drops items exactly like a normal break.
    /// TODO: drive real item use (aim + controlUseItem) instead of KillTile, so hit-by-hit damage matches vanilla.
    /// </summary>
    public class Mine : Skill
    {
        public override string Name => "mine";
        public override int TimeoutTicks => 60 * 45;

        private readonly int x, y;
        private int tileType = -1;
        private int toolSlot = -1;
        private int swingTicks, swingTicksNeeded;
        private Skill approach, collect;
        private string broke;

        public Mine(int x, int y) { this.x = x; this.y = y; }

        public override void Tick(Player p)
        {
            if (broke != null)
            {
                // Walk onto the drop for a moment so the items fly into the inventory.
                if (Step(collect, p) && collect.Ticks < 90) return;
                Succeed(broke);
                return;
            }

            if (tileType < 0 && !Validate(p)) return;

            if (!Game.InReach(p, x, y, p.inventory[toolSlot].tileBoost))
            {
                approach ??= new MoveTo(x, y, 2);
                if (Step(approach, p)) return;
                if (!Game.InReach(p, x, y, p.inventory[toolSlot].tileBoost))
                {
                    Fail($"could not get within reach of ({x},{y}): {approach.Message}");
                    return;
                }
            }

            Game.Hold(p, toolSlot);
            toolSlot = p.selectedItem;
            p.direction = x * 16 + 8 < p.Center.X ? -1 : 1;
            AgentPlayer.Aim = new Microsoft.Xna.Framework.Vector2(x * 16 + 8, y * 16 + 8);

            if (++swingTicks < swingTicksNeeded) return;

            Tile t = Framing.GetTileSafely(x, y);
            if (!t.HasTile || t.TileType != tileType)
            {
                Succeed($"tile at ({x},{y}) is already gone");
                return;
            }
            WorldGen.KillTile(x, y);
            if (Framing.GetTileSafely(x, y).HasTile)
            {
                Fail($"{Game.TileName(tileType)} at ({x},{y}) did not break (something on top of it, or it is protected)");
                return;
            }
            broke = $"broke {Game.TileName(tileType)} at ({x},{y})";
            collect = new MoveTo(x, y, 0);
        }

        private bool Validate(Player p)
        {
            Tile t = Framing.GetTileSafely(x, y);
            if (!Game.InWorld(x, y) || !t.HasTile)
            {
                Fail($"nothing to mine at ({x},{y})");
                return false;
            }
            tileType = t.TileType;

            bool tree = Game.IsTree(tileType) || Main.tileAxe[tileType];
            bool hammerTile = Main.tileHammer[tileType];
            toolSlot = tree ? Game.BestSlot(p, i => i.axe)
                : hammerTile ? Game.BestSlot(p, i => i.hammer)
                : Game.BestSlot(p, i => i.pick);
            if (toolSlot < 0)
            {
                Fail($"no {(tree ? "axe" : hammerTile ? "hammer" : "pickaxe")} in inventory for {Game.TileName(tileType)}");
                return false;
            }

            Item tool = p.inventory[toolSlot];
            int power = tree ? tool.axe * 5 : hammerTile ? tool.hammer : tool.pick;
            int needed = Game.RequiredPick(tileType);
            if (!tree && !hammerTile && tool.pick < needed)
            {
                Fail($"{Game.TileName(tileType)} needs {needed}% pickaxe power; best pickaxe is {tool.pick}%");
                return false;
            }

            float hardness = TileID.Sets.Ore[tileType] ? 2f : 1f; // ores take roughly twice the hits
            int hits = Math.Max(1, (int)Math.Ceiling(100f * hardness / Math.Max(1, power)));
            if (tree) hits = Math.Max(hits, 3);
            swingTicksNeeded = hits * Math.Max(1, tool.useTime);
            return true;
        }
    }

    /// <summary>Find the nearest tiles of a type and mine `count` of them. Trees are cut at the trunk base.</summary>
    public class MineNearest : Skill
    {
        public override string Name => "mine_nearest";
        public override int TimeoutTicks => 60 * 180;

        private readonly int tileType;
        private readonly bool anyOre;
        private readonly int count;
        private int mined;
        private readonly HashSet<(int, int)> skip = new();
        private Mine current;
        private (int x, int y) currentTarget;
        private string lastFailure;

        public MineNearest(int tileType, int count)
        {
            this.tileType = tileType;
            this.count = Math.Clamp(count, 1, 50);
        }

        public MineNearest(string target, int count)
        {
            this.count = Math.Clamp(count, 1, 50);
            string t = target.Trim().ToLowerInvariant();
            if (t is "ore" or "any ore") anyOre = true;
            else if (t is "tree" or "trees" or "wood") tileType = TileID.Trees;
            else
            {
                tileType = Game.ResolveTile(target);
                if (tileType < 0) throw new ArgumentException($"unknown tile '{target}'. Use internal tile names like Trees, CopperOre, IronOre, Stone, Dirt, or 'ore'.");
            }
        }

        public override void Tick(Player p)
        {
            if (current != null)
            {
                if (Step(current, p)) return;
                if (current.Ok) mined++;
                else { skip.Add(currentTarget); lastFailure = current.Message; }
                current = null;
                if (mined >= count)
                {
                    Succeed($"mined {mined}/{count}");
                    return;
                }
            }

            var next = FindNearest(p);
            if (next == null)
            {
                string why = lastFailure != null ? $" Last failure: {lastFailure}" : "";
                if (mined > 0) Succeed($"mined {mined}/{count}; no more reachable targets within {Observation.ScanRadiusX} tiles.{why}");
                else Fail($"no reachable target within {Observation.ScanRadiusX} tiles.{why}");
                return;
            }
            currentTarget = next.Value;
            current = new Mine(currentTarget.x, currentTarget.y);
        }

        private (int x, int y)? FindNearest(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            (int, int)? best = null;
            float bestD = float.MaxValue;
            for (int x = px - Observation.ScanRadiusX; x <= px + Observation.ScanRadiusX; x++)
            for (int y = py - Observation.ScanRadiusY; y <= py + Observation.ScanRadiusY; y++)
            {
                if (!Game.InWorld(x, y) || skip.Contains((x, y))) continue;
                Tile t = Framing.GetTileSafely(x, y);
                if (!t.HasTile) continue;
                int type = t.TileType;
                if (anyOre ? !TileID.Sets.Ore[type] : type != tileType) continue;
                // Trees: only the trunk base, so the whole tree falls (not the 1-wood root tiles beside it).
                if (Game.IsTree(type) && !Game.IsTreeBase(x, y)) continue;
                // Prefer horizontal travel over vertical (vertical needs digging or climbing).
                float d = Math.Abs(x - px) + Math.Abs(y - py) * 2.5f;
                if (d < bestD) { bestD = d; best = (x, y); }
            }
            return best;
        }
    }

    /// <summary>
    /// Dig straight down under your feet until `tileType` shows up nearby (at least 3 tiles within reach of a
    /// mine_nearest), or `maxDepth` tiles down. Used when there is none of it in view, e.g. Stone under a dirt surface.
    /// </summary>
    public class DigDown : Skill
    {
        public override string Name => "dig_down";
        public override int TimeoutTicks => 60 * 240;
        public override bool InterruptOnDamage => true;

        private readonly int tileType, maxDepth;
        private int startY = -1;
        private Mine current;

        public DigDown(int tileType, int maxDepth = 80)
        {
            this.tileType = tileType;
            this.maxDepth = Math.Clamp(maxDepth, 1, 300);
        }

        public override void Tick(Player p)
        {
            if (current != null)
            {
                if (Step(current, p)) return;
                if (!current.Ok) { Fail($"digging down stopped: {current.Message}"); return; }
                current = null;
            }
            if (p.velocity.Y != 0) return; // still falling into the hole

            int by = (int)(p.Bottom.Y / 16f); // the row under your feet
            if (startY < 0) startY = by;
            int depth = by - startY;
            if (Near(p, tileType)) { Succeed($"dug down {depth} tiles; {Game.TileName(tileType)} is nearby now"); return; }
            if (depth >= maxDepth) { Fail($"dug {depth} tiles down and found no {Game.TileName(tileType)}"); return; }

            int left = (int)(p.position.X / 16f), right = (int)((p.position.X + p.width - 1) / 16f);
            for (int x = left; x <= right; x++)
                if (Game.InWorld(x, by) && Framing.GetTileSafely(x, by).HasTile && Main.tileSolid[Framing.GetTileSafely(x, by).TileType])
                {
                    current = new Mine(x, by);
                    return;
                }
        }

        /// <summary>At least 3 `tileType` tiles within pickaxe reach of where you stand (stone a few tiles under the dirt doesn't count).</summary>
        public static bool Near(Player p, int tileType)
        {
            int px = Game.TileX(p), py = Game.TileY(p), n = 0;
            for (int x = px - 4; x <= px + 4; x++)
            for (int y = py - 2; y <= py + 4; y++)
                if (Game.InWorld(x, y) && Framing.GetTileSafely(x, y).HasTile && Framing.GetTileSafely(x, y).TileType == tileType && ++n >= 3)
                    return true;
            return false;
        }
    }
}
