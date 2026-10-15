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
        public override int TimeoutTicks => 60 * 150; // includes a tunnel when the tile is buried

        private readonly int x, y;
        private int tileType = -1;
        private int toolSlot = -1;
        private int swingTicks, swingTicksNeeded;
        private Skill approach, collect;
        private string broke;
        private readonly bool canTunnel;

        /// <param name="tunnel">Fall back to tunnel_to when walking can't get in reach. TunnelTo's own Mine children pass false, so the two can't nest without bound.</param>
        public Mine(int x, int y, bool tunnel = true) { this.x = x; this.y = y; canTunnel = tunnel; }

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
                    // Can't walk there (buried under dirt, up a cliff): dig a tunnel to it, once.
                    if (approach is MoveTo && canTunnel) { approach = new TunnelTo(x, y, 2); return; }
                    Fail($"could not get within reach of ({x},{y}): {approach.Message}");
                    return;
                }
            }
            // In reach: forget the finished approach, so being pushed out of reach later (the guard's fight, knockback)
            // walks again instead of stepping a Done child (which returns false at once) and failing.
            approach = null;

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

        private const int MaxSafeDrop = 20; // fall damage starts past 25 tiles

        private readonly int tileType, maxDepth;
        private int startY = -1, sidesteps, still;
        private Mine current;
        private (int x, int y) mining;
        private Skill walk;
        private string lastBlock;

        public DigDown(int tileType, int maxDepth = 80)
        {
            this.tileType = tileType;
            this.maxDepth = Math.Clamp(maxDepth, 1, 300);
        }

        private static bool Diggable(int x, int y) =>
            Game.InWorld(x, y) && Framing.GetTileSafely(x, y).HasTile && Main.tileSolid[Framing.GetTileSafely(x, y).TileType];

        public override void Tick(Player p)
        {
            if (walk != null)
            {
                if (Step(walk, p)) return;
                walk = null; // even a partial sidestep gives a new column to try
            }
            if (current != null)
            {
                bool running = Step(current, p);
                // Only the hole matters: drop the Mine once the tile is gone. Its walk onto the drop would step onto
                // the edge of the other column (the body is 20px wide) and creep sideways instead of falling.
                if (running && Diggable(mining.x, mining.y)) return;
                if (!running && !current.Ok) { string why = current.Message; current = null; Sidestep(p, why); return; }
                current = null;
            }
            if (!Explore.Settled(p, ref still)) return; // still falling into the hole

            int by = Explore.Feet(p); // the row under your feet
            if (startY < 0) startY = by;
            int depth = by - startY;
            if (Near(p, tileType)) { Succeed($"dug down {depth} tiles; {Game.TileName(tileType)} is nearby now"); return; }
            if (depth >= maxDepth) { Fail($"dug {depth} tiles down and found no {Game.TileName(tileType)}"); return; }

            int left = Explore.LeftCol(p), right = Explore.RightCol(p), first = -1;
            for (int x = left; x <= right; x++)
            {
                if (!Diggable(x, by)) continue;
                // Check every column before breaking any: once the floor is gone you fall whatever is below.
                int drop = Explore.DropDepth(x, by + 1, out bool lava);
                if (lava || Explore.LavaNear(x, by)) { Fail($"lava under ({x},{by}) after digging {depth} tiles; stopped. Tunnel sideways (tunnel_to) away from it."); return; }
                if (drop > MaxSafeDrop) { Fail($"digging ({x},{by}) opens a {drop}+ tile drop (fall damage) after {depth} tiles; stopped. Use tunnel_to to a point below and to the side (it digs stairs), or place platforms."); return; }
                if (first < 0) first = x;
            }
            if (first < 0) { Sidestep(p, $"nothing diggable under your feet at row {by} (furniture or a non-solid tile)"); return; }
            mining = (first, by);
            current = new Mine(first, by, tunnel: false);
        }

        /// <summary>Ground that won't break (a tree on it, too hard for the pickaxe): try a column a few tiles over.</summary>
        private void Sidestep(Player p, string why)
        {
            lastBlock = why;
            if (++sidesteps > 4) { Fail($"digging down stopped after {sidesteps - 1} sidesteps: {lastBlock}"); return; }
            // Offsets are from where you stand now: +3, -6, +9, -12 visits +3, -3, +6, -6 around the first blocked column
            // (the old +3, -3, +6, -6 from the current spot walked back onto the blocked column every second time).
            int off = (sidesteps % 2 == 1 ? 1 : -1) * 3 * sidesteps;
            // TunnelTo, not MoveTo: inside a shaft the walls are solid, so walking sideways can't move at all.
            walk = new TunnelTo(Game.TileX(p) + off, Game.TileY(p), 1);
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
