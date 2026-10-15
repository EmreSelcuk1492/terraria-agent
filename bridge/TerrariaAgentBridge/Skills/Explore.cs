using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>
    /// Underground helpers shared by the exploration skills (and usable by others).
    /// Rows: "feet" always means the row directly UNDER the player's feet, (int)(p.Bottom.Y / 16f);
    /// the body occupies rows feet-3 .. feet-1.
    /// </summary>
    public static class Explore
    {
        /// <summary>Blocks used to pillar up / bridge gaps, best first. No sand/silt: they fall.</summary>
        private static readonly int[] BuildBlocks =
        {
            ItemID.DirtBlock, ItemID.StoneBlock, ItemID.MudBlock, ItemID.ClayBlock, ItemID.AshBlock,
            ItemID.GraniteBlock, ItemID.MarbleBlock, ItemID.Wood,
        };

        /// <summary>Item types that teleport you home when used.</summary>
        public static readonly int[] RecallItems =
        {
            ItemID.MagicMirror, ItemID.IceMirror, ItemID.CellPhone, ItemID.Shellphone, ItemID.ShellphoneSpawn, ItemID.RecallPotion,
        };

        public static int Feet(Player p) => (int)(p.Bottom.Y / 16f);

        /// <summary>
        /// Standing still vertically. velocity.Y is exactly 0 on flat ground, but on slopes vanilla's SlopeCollision
        /// leaves a tiny value (0.0101), so `== 0` alone can wait forever. Near-zero speed held for 5 ticks also counts
        /// (a jump apex passes in 1-2 ticks). `still` is the caller's counter.
        /// </summary>
        public static bool Settled(Player p, ref int still)
        {
            if (Math.Abs(p.velocity.Y) > 0.1f) { still = 0; return false; }
            return p.velocity.Y == 0f || ++still >= 5;
        }
        public static int LeftCol(Player p) => (int)(p.position.X / 16f);
        public static int RightCol(Player p) => (int)((p.position.X + p.width - 1) / 16f);

        /// <summary>A placeable building block in the main inventory, or -1.</summary>
        public static int BuildingBlock(Player p)
        {
            foreach (int type in BuildBlocks)
                if (Game.FindSlot(p, i => i.type == type) >= 0) return type;
            return -1;
        }

        public static string InternalName(int itemType) => ItemID.Search.ContainsId(itemType) ? ItemID.Search.GetName(itemType) : Game.ItemName(itemType);

        /// <summary>The held-best pickaxe's tileBoost (reach bonus), or null when there is no pickaxe.</summary>
        public static int? PickBoost(Player p)
        {
            int slot = Game.BestSlot(p, i => i.pick);
            return slot < 0 ? null : p.inventory[slot].tileBoost;
        }

        public static int BestPickPower(Player p)
        {
            int slot = Game.BestSlot(p, i => i.pick);
            return slot < 0 ? 0 : p.inventory[slot].pick;
        }

        /// <summary>Lava in the tile or any of its 4 neighbours (mining it would let the lava in).</summary>
        public static bool LavaNear(int x, int y) =>
            Game.Lava(x, y) || Game.Lava(x - 1, y) || Game.Lava(x + 1, y) || Game.Lava(x, y - 1) || Game.Lava(x, y + 1);

        /// <summary>
        /// How far something standing on row `fromY` in column x would fall: the number of non-solid,
        /// non-platform tiles from fromY downward (max 60). `lava` is set if the fall passes through lava.
        /// </summary>
        public static int DropDepth(int x, int fromY, out bool lava)
        {
            lava = false;
            int d = 0;
            for (int y = fromY; d < 60 && Game.InWorld(x, y); y++, d++)
            {
                if (Game.Lava(x, y)) lava = true;
                if (Game.Solid(x, y) || Game.Platform(x, y)) break;
            }
            return d;
        }

        /// <summary>A 2-wide, 3-tall player fits with its left column at `col` and feet on row `feet` (floor solid or platform).</summary>
        public static bool CanStand(int col, int feet)
        {
            for (int x = col; x <= col + 1; x++)
                for (int y = feet - 3; y <= feet - 1; y++)
                    if (Game.Solid(x, y) || Game.Lava(x, y)) return false;
            return Game.Solid(col, feet) || Game.Solid(col + 1, feet) || Game.Platform(col, feet) || Game.Platform(col + 1, feet);
        }

        /// <summary>
        /// Rough walk-reachability: breadth-first search over standing spots from where the player stands,
        /// walking 1 column, stepping/jumping up to 3 tiles, and falling up to 20. True if some reachable spot
        /// has tile (x, y) within vanilla tile reach. No digging, no lava. Approximate (jump arcs are not
        /// simulated) but good for "can I just move_to there, or do I need tunnel_to?".
        /// </summary>
        public static bool WalkReachable(Player p, int x, int y, int maxNodes = 4000)
        {
            int startCol = LeftCol(p), startFeet = Feet(p);
            var seen = new HashSet<(int, int)> { (startCol, startFeet) };
            var queue = new Queue<(int col, int feet)>();
            queue.Enqueue((startCol, startFeet));
            while (queue.Count > 0 && seen.Count < maxNodes)
            {
                var (c, f) = queue.Dequeue();
                // Reach from this spot: 5 tiles sideways from the body, 4 up/down.
                if (x >= c - Player.tileRangeX && x <= c + 1 + Player.tileRangeX && y >= f - 3 - Player.tileRangeY && y <= f - 1 + Player.tileRangeY)
                    return true;
                foreach (int d in new[] { -1, 1 })
                {
                    int nc = c + d;
                    // Same level, step up 1..3 (only if the head room above the current spot is clear), or fall.
                    for (int up = 0; up <= 3; up++)
                    {
                        if (up > 0 && (Game.Solid(c, f - 3 - up) || Game.Solid(c + 1, f - 3 - up))) break;
                        if (CanStand(nc, f - up)) { if (seen.Add((nc, f - up))) queue.Enqueue((nc, f - up)); goto nextDir; }
                    }
                    // Walk off a ledge: body fits in the next column at this level, then drop to the first floor.
                    bool bodyFits = true;
                    for (int xx = nc; xx <= nc + 1; xx++)
                        for (int yy = f - 3; yy <= f - 1; yy++)
                            if (Game.Solid(xx, yy)) bodyFits = false;
                    if (!bodyFits) continue;
                    for (int nf = f + 1; nf <= f + 20; nf++)
                    {
                        if (Game.Lava(nc, nf - 1) || Game.Lava(nc + 1, nf - 1)) break;
                        if (CanStand(nc, nf)) { if (seen.Add((nc, nf))) queue.Enqueue((nc, nf)); break; }
                        if (Game.Solid(nc, nf - 1) || Game.Solid(nc + 1, nf - 1)) break;
                    }
                    nextDir:;
                }
            }
            return false;
        }

        /// <summary>Standing on the surface: above Main.worldSurface, on the ground, nothing solid for 20 tiles above the head.</summary>
        public static bool AtSurface(Player p)
        {
            int feet = Feet(p);
            if (feet >= Main.worldSurface || Math.Abs(p.velocity.Y) > 0.1f) return false; // not == 0: slopes leave 0.0101
            for (int x = LeftCol(p); x <= RightCol(p); x++)
                for (int y = feet - 4; y >= Math.Max(1, feet - 24); y--)
                    if (Game.Solid(x, y)) return false;
            return true;
        }

        /// <summary>
        /// Looking up from the player in column x: the topmost solid row of the mass overhead, i.e. the ground you
        /// would stand on at the surface (first solid row below a run of 25 open tiles that starts above worldSurface).
        /// Returns -1 if the column above is already open.
        /// </summary>
        public static int GroundAbove(int x, int fromY)
        {
            int lastSolid = -1, open = 0;
            for (int y = fromY; y > 10; y--)
            {
                if (Game.Solid(x, y)) { lastSolid = y; open = 0; continue; }
                if (++open >= 25 && y + 24 < Main.worldSurface) return lastSolid;
            }
            return lastSolid;
        }

        /// <summary>Same as Mine, but used when only the hole matters: stop as soon as the tile is gone (drops float to you).</summary>
        internal static bool TileGone(int x, int y) => !Game.Solid(x, y);
    }

    /// <summary>
    /// Get within `tolerance` tiles (and pickaxe reach) of a tile, digging a walkable 2x3 tunnel if needed.
    /// Greedy, one column or one row per step: dig down when the target is below, stairs down/up diagonally,
    /// pillar up with blocks when it is straight above. Never digs into lava; places torches underground.
    /// </summary>
    public class TunnelTo : Skill
    {
        public override string Name => "tunnel_to";
        public override int TimeoutTicks => 60 * 240;
        public override bool InterruptOnDamage => true;

        private const int MaxStall = 10, TorchEvery = 12, MaxSafeDrop = 20;

        private enum Move { None, Walk, Fall, Pillar }

        private readonly int tx, ty, tolerance;
        private int climbDir;
        private readonly Queue<(int x, int y)> toMine = new();
        private Move move;
        private int walkX, walkY;

        private Skill child;
        private (int x, int y) mining = (-1, -1);
        private bool childIsTorch;

        private int settle, still, stall, bestDx = int.MaxValue, bestDy = int.MaxValue, steps;
        private int pillarTicks, pillarRow;
        private bool pillarPlaced;
        private int lastCol = int.MinValue, lastFeet, travel, torches, dug;
        private string note = "";

        /// <param name="climbDir">Sideways direction (-1/1) to stair-climb when the target is straight above and there are no blocks to pillar with; 0 = pick automatically.</param>
        public TunnelTo(int x, int y, int tolerance = 2, int climbDir = 0)
        {
            tx = x; ty = y;
            this.tolerance = Math.Clamp(tolerance, 0, 10);
            this.climbDir = Math.Sign(climbDir);
        }

        public override void Tick(Player p)
        {
            if (child != null && !RunChild(p)) return;
            if (Done) return;

            if (move == Move.Pillar && toMine.Count == 0) { PillarTick(p); return; }

            // Wait until standing still on the ground (falling down a shaft, landing from a jump).
            if (!Explore.Settled(p, ref still)) { settle = 0; return; }
            if (settle < 3) { settle++; return; }

            if (toMine.Count > 0) { StartNextMine(p); return; }

            switch (move)
            {
                case Move.Walk:
                    move = Move.None;
                    child = new MoveTo(walkX, walkY, 0);
                    return;
                case Move.Fall:
                    move = Move.None;
                    // Nothing solid was under us: probably a platform. Drop through it.
                    int feet = Explore.Feet(p);
                    if (Game.Platform(Explore.LeftCol(p), feet) || Game.Platform(Explore.RightCol(p), feet))
                    {
                        p.controlDown = true;
                        settle = 0;
                    }
                    return;
            }
            Plan(p);
        }

        /// <summary>Steps the child. Returns true when it finished and planning may continue.</summary>
        private bool RunChild(Player p)
        {
            bool running = Step(child, p);
            // Tunnel digging only needs the hole: drop the Mine child once the tile is gone (no walking onto the drop).
            if (running && child is Mine && mining.x >= 0 && Explore.TileGone(mining.x, mining.y))
            {
                dug++;
                child = null;
                mining = (-1, -1);
                return true;
            }
            if (running) return false;

            Skill done = child;
            child = null;
            if (done is Mine)
            {
                mining = (-1, -1);
                if (!done.Ok)
                {
                    Fail($"tunnel to ({tx},{ty}) blocked at {Here(p)}: {done.Message}. Go around it: tunnel_to a point a few tiles to the side, or get a stronger pickaxe.");
                    return false;
                }
                dug++;
            }
            else if (done is MoveTo && !done.Ok)
            {
                if (done.Message.Contains("lava")) { Fail($"stopped tunneling to ({tx},{ty}): {done.Message}. Pick a target away from the lava."); return false; }
                note = done.Message;
                toMine.Clear();
                move = Move.None;
            }
            else if (done is Place && !done.Ok && !childIsTorch)
            {
                note = done.Message; // a bridge/stair block didn't go down; the next plan sees the gap
            }
            if (childIsTorch && done.Ok) torches++;
            childIsTorch = false;
            return true;
        }

        private void StartNextMine(Player p)
        {
            while (toMine.Count > 0)
            {
                var (x, y) = toMine.Dequeue();
                if (!Game.Solid(x, y)) continue;
                if (Explore.LavaNear(x, y))
                {
                    Fail($"lava next to ({x},{y}) on the way to ({tx},{ty}); refusing to dig into it. Tunnel toward a target on another side, or pour/block the lava first.");
                    return;
                }
                mining = (x, y);
                child = new Mine(x, y, tunnel: false); // no nested tunnel_to if the tile is out of reach (Mine <-> TunnelTo recursion)
                return;
            }
        }

        private void Plan(Player p)
        {
            int? boost = Explore.PickBoost(p);
            if (boost == null) { Fail("no pickaxe in inventory; tunnel_to needs one (craft or obtain a pickaxe)"); return; }

            int px = Game.TileX(p), py = Game.TileY(p);
            int left = Explore.LeftCol(p), right = Explore.RightCol(p), feet = Explore.Feet(p);
            int dx = tx - px, dy = ty - py;

            if (Game.InReach(p, tx, ty, boost.Value) && Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance + 1)
            {
                string extra = dug > 0 ? $"; dug {dug} tiles" : "";
                if (torches > 0) extra += $", placed {torches} torches";
                Succeed($"reached {Here(p)}, within reach of ({tx},{ty}){extra}");
                return;
            }

            // Progress: either axis getting closer counts (stairs trade sideways distance for height).
            steps++;
            bool better = false;
            if (Math.Abs(dx) < bestDx) { bestDx = Math.Abs(dx); better = true; }
            if (Math.Abs(dy) < bestDy) { bestDy = Math.Abs(dy); better = true; }
            if (better) stall = 0;
            else if (++stall > MaxStall)
            {
                string why = note != "" ? $" Last problem: {note}" : "";
                Fail($"no progress toward ({tx},{ty}) for {MaxStall} steps; stuck at {Here(p)} (dx {dx}, dy {dy}).{why} " +
                     (dy < -1 && Explore.BuildingBlock(p) < 0 ? "Climbing straight up needs blocks (DirtBlock/StoneBlock) to pillar with. " : "") +
                     "Try a target to the side, or move_to/mine manually.");
                return;
            }

            MaybeTorch(p, left, right, feet, Math.Sign(dx));
            if (child != null) return;

            // 1) Something solid inside our own body space (sand fell in, spawned inside a block): clear it first.
            for (int x = left; x <= right; x++)
                for (int y = feet - 3; y <= feet - 1; y++)
                    if (Game.Solid(x, y)) toMine.Enqueue((x, y));
            if (toMine.Count > 0) { note = "clearing blocks inside the player's space"; return; }

            int block = Explore.BuildingBlock(p);

            // 2) Target below and roughly under us: dig the floor out and fall.
            if (dy > 1 && Math.Abs(dx) <= 1)
            {
                for (int x = left; x <= right; x++)
                {
                    int depth = Explore.DropDepth(x, feet + 1, out bool lava);
                    if (lava || Explore.LavaNear(x, feet)) { Fail($"lava below {Here(p)} (column {x}); not digging down here. Tunnel sideways first."); return; }
                    if (depth > MaxSafeDrop) { Fail($"digging down at column {x} opens a {depth}+ tile drop (fall damage). Place platforms/blocks below, or tunnel down beside it."); return; }
                    if (Game.Solid(x, feet)) toMine.Enqueue((x, feet));
                }
                move = Move.Fall;
                note = "digging down";
                return;
            }

            // 3) Target straight above: pillar up with blocks.
            if (dy < -1 && Math.Abs(dx) <= 1 && block >= 0)
            {
                for (int x = left; x <= right; x++)
                    for (int y = feet - 5; y <= feet - 4; y++)
                        if (Game.Solid(x, y)) toMine.Enqueue((x, y));
                move = Move.Pillar;
                pillarTicks = 0;
                pillarPlaced = false;
                pillarRow = feet - 1;
                note = "pillaring up";
                return;
            }

            // 4) Sideways step (level, stairs down, or stairs up).
            int dir;
            if (dy < -1 && Math.Abs(dx) <= 1)
            {
                // Straight above but no blocks: stair-climb sideways in one fixed direction.
                if (climbDir == 0) climbDir = dx != 0 ? Math.Sign(dx) : (Main.spawnTileX >= px ? 1 : -1);
                dir = climbDir;
            }
            else dir = dx != 0 ? Math.Sign(dx) : (climbDir != 0 ? climbDir : 1);

            int nx = dir > 0 ? right + 1 : left - 1;
            if (!Game.InWorld(nx, feet)) { Fail($"world edge at column {nx}"); return; }
            for (int y = feet - 4; y <= feet + 1; y++)
                if (Game.Lava(nx, y)) { Fail($"lava ahead at ({nx},{y}) on the way to ({tx},{ty}); refusing to tunnel into it. Pick a target on another side."); return; }

            bool up = dy < -1, down = dy > 1;
            int rise = 0;
            if (up)
            {
                // Stair up: stand on (nx, feet-1). Use the block that is there, or place one if we have any.
                bool stair = Game.Solid(nx, feet - 1);
                if (!stair && block >= 0 && !Framing.GetTileSafely(nx, feet - 1).HasTile
                    && (Game.Solid(nx, feet) || Game.Solid(nx + dir, feet - 1) || Game.Solid(nx, feet - 2)))
                {
                    child = new Place(Explore.InternalName(block), nx, feet - 1);
                    note = "placing a stair block";
                    // Re-plan after placing: next time `stair` is true.
                    return;
                }
                if (stair)
                {
                    rise = 1;
                    for (int y = feet - 4; y <= feet - 2; y++) if (Game.Solid(nx, y)) toMine.Enqueue((nx, y));
                    for (int x = left; x <= right; x++) if (Game.Solid(x, feet - 4)) toMine.Enqueue((x, feet - 4)); // head room for the step
                    note = "stairs up";
                }
            }
            if (rise == 0)
            {
                for (int y = feet - 3; y <= feet - 1; y++) if (Game.Solid(nx, y)) toMine.Enqueue((nx, y));
                if (down && Game.Solid(nx, feet)) toMine.Enqueue((nx, feet));
                note = down ? "stairs down" : up ? "walking level (no stair block)" : "tunneling sideways";

                // Floor ahead missing (or about to be dug): check what we'd fall into.
                int floorFrom = down ? feet + 1 : feet;
                if (!Game.Solid(nx, floorFrom) && !Game.Platform(nx, floorFrom))
                {
                    int depth = Explore.DropDepth(nx, floorFrom, out bool lava);
                    if (lava) { Fail($"lava down the gap at column {nx}; not stepping in. Pick a target on another side."); return; }
                    if (!down && depth > 2 && block >= 0 && !Framing.GetTileSafely(nx, feet).HasTile && toMine.Count == 0)
                    {
                        child = new Place(Explore.InternalName(block), nx, feet); // bridge the gap
                        note = "bridging a gap";
                        return;
                    }
                    if (depth > MaxSafeDrop)
                    {
                        Fail($"a {depth}-tile drop at column {nx} (fall damage) on the way to ({tx},{ty}). Carry DirtBlock to bridge it, or pick another route.");
                        return;
                    }
                }
            }
            move = Move.Walk;
            walkX = px + dir;
            walkY = py - rise;
        }

        /// <summary>Jump, place a block under the feet at the top of the jump, land on it.</summary>
        private void PillarTick(Player p)
        {
            pillarTicks++;
            if (!pillarPlaced)
            {
                p.controlJump = pillarTicks <= 15;
                if (p.Bottom.Y <= pillarRow * 16 - 2)
                {
                    int block = Explore.BuildingBlock(p);
                    int col = -1;
                    for (int x = Explore.LeftCol(p); x <= Explore.RightCol(p) && col < 0; x++)
                        if (!Framing.GetTileSafely(x, pillarRow).HasTile && Game.Solid(x, pillarRow + 1)) col = x;
                    if (block < 0 || col < 0) { EndPillar("no free spot or no block to pillar with"); return; }
                    var place = new Place(Explore.InternalName(block), col, pillarRow);
                    Step(place, p);
                    if (!place.Ok) { EndPillar(place.Message); return; }
                    pillarPlaced = true;
                }
                else if (pillarTicks > 45) EndPillar("jump did not rise a full tile (ceiling?)");
                return;
            }
            if (p.velocity.Y == 0f || pillarTicks > 120) EndPillar(null);
        }

        private void EndPillar(string problem)
        {
            move = Move.None;
            settle = 0;
            if (problem != null) note = $"pillar failed: {problem}";
        }

        /// <summary>Every ~12 tiles of travel below the surface, put a torch behind/next to the player. Failures are ignored.</summary>
        private void MaybeTorch(Player p, int left, int right, int feet, int dir)
        {
            if (lastCol != int.MinValue) travel += Math.Abs(left - lastCol) + Math.Abs(feet - lastFeet);
            lastCol = left; lastFeet = feet;
            if (travel < TorchEvery || feet <= Main.worldSurface) return;
            travel = 0;
            if (Game.FindSlot(p, i => i.type == ItemID.Torch) < 0) return;
            // Skip if a torch already lights this spot.
            for (int x = left - 5; x <= right + 5; x++)
                for (int y = feet - 7; y <= feet + 2; y++)
                    if (Framing.GetTileSafely(x, y).HasTile && Framing.GetTileSafely(x, y).TileType == TileID.Torches) return;
            int back = dir >= 0 ? left - 1 : right + 1;
            foreach (var (x, y) in new[] { (back, feet - 2), (back, feet - 1), (left, feet - 2), (right, feet - 2) })
            {
                if (!Game.InWorld(x, y) || Framing.GetTileSafely(x, y).HasTile || Game.Lava(x, y) || Framing.GetTileSafely(x, y).LiquidAmount > 0) continue;
                child = new Place("Torch", x, y);
                childIsTorch = true;
                return;
            }
        }

        private static string Here(Player p) => $"({Game.TileX(p)},{Game.TileY(p)})";
    }

    /// <summary>Find life crystals (2x2 Heart tiles) nearby, tunnel to each, break it, pick it up and use it.</summary>
    public class CollectLifeCrystals : Skill
    {
        public override string Name => "collect_life_crystals";
        public override int TimeoutTicks => 60 * 600;
        public override bool InterruptOnDamage => true;

        private readonly int max, radius;
        private readonly HashSet<(int, int)> skip = new();
        private (int x, int y) target = (-1, -1);
        private Skill child;
        private int phase; // 0 tunnel, 1 mine, 2 pick up
        private int before, got;
        private readonly List<string> log = new();
        private string lastFailure;

        public CollectLifeCrystals(int max = 3, int radius = 80)
        {
            this.max = Math.Clamp(max, 1, 15);
            this.radius = Math.Clamp(radius, 5, 200);
        }

        public override void Tick(Player p)
        {
            if (child != null)
            {
                if (Step(child, p)) return;
                Skill done = child;
                child = null;
                if (phase == 0)
                {
                    if (!done.Ok) { Abandon($"could not reach crystal at ({target.x},{target.y}): {done.Message}"); return; }
                    phase = 1;
                    before = Game.CountItem(p, ItemID.LifeCrystal);
                    child = new Mine(target.x, target.y + 1); // bottom-left tile; breaking any part breaks the whole crystal
                    return;
                }
                if (phase == 1 && !done.Ok && Framing.GetTileSafely(target.x, target.y + 1).HasTile)
                {
                    Abandon($"could not break crystal at ({target.x},{target.y}): {done.Message}");
                    return;
                }
                if (Game.CountItem(p, ItemID.LifeCrystal) <= before)
                {
                    if (phase == 1)
                    {
                        // The drop hasn't reached us yet: walk onto where it fell.
                        phase = 2;
                        child = new MoveTo(target.x, target.y + 1, 0);
                        return;
                    }
                    log.Add($"broke crystal at ({target.x},{target.y}) but did not pick up the LifeCrystal item; look for it on the ground there");
                    Next();
                    return;
                }
                got++;
                log.Add(Consume(p, target));
                Next();
                if (got >= max) { Finish(p); return; }
            }

            var next = FindNearest(p);
            if (next == null) { Finish(p); return; }
            target = next.Value;
            phase = 0;
            child = new TunnelTo(target.x, target.y + 1, 2);
        }

        private void Abandon(string why) { lastFailure = why; skip.Add(target); Next(); }
        private void Next() { target = (-1, -1); phase = 0; }

        /// <summary>Eat one LifeCrystal the way vanilla's ItemCheck_UseLifeCrystal does (+20 max life, heal 20, count it).</summary>
        private static string Consume(Player p, (int x, int y) at)
        {
            if (p.ConsumedLifeCrystals >= 15 || p.statLifeMax >= 400)
                return $"picked up a LifeCrystal at ({at.x},{at.y}); max life is already {p.statLifeMax}, kept it";
            int slot = Game.FindSlot(p, i => i.type == ItemID.LifeCrystal);
            if (slot < 0) return $"picked up a LifeCrystal at ({at.x},{at.y}) but could not find it in the main inventory to use";
            Game.ConsumeOne(p, slot);
            p.UseHealthMaxIncreasingItem(20);
            p.ConsumedLifeCrystals++;
            return $"+20 max life from crystal at ({at.x},{at.y}) (now {p.statLifeMax}/400)";
        }

        private void Finish(Player p)
        {
            string fail = lastFailure != null ? $" Last failure: {lastFailure}" : "";
            if (got > 0) { Succeed($"collected {got} life crystal(s): {string.Join("; ", log)}.{fail}"); return; }
            if (log.Count > 0) { Fail($"{string.Join("; ", log)}.{fail}"); return; }
            if (lastFailure != null) { Fail($"found life crystals but reached none.{fail}"); return; }
            int top = (int)((2 * Main.worldSurface + Main.rockLayer) / 3);
            int py = Game.TileY(p);
            string hint = py < top
                ? $"they generate below y={top} (you are at y={py}, rock layer starts at y={(int)Main.rockLayer}); dig down first (tunnel_to a point ~{Math.Max(40, top - py + 30)} tiles below you)"
                : $"you are already at crystal depth (y={py}); explore sideways (tunnel_to a point {radius} tiles left or right) or deeper and try again";
            Fail($"no life crystals within {radius} tiles; {hint}");
        }

        private (int x, int y)? FindNearest(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            (int, int)? best = null;
            float bestD = float.MaxValue;
            for (int x = px - radius; x <= px + radius; x++)
                for (int y = py - radius; y <= py + radius; y++)
                {
                    if (!Game.InWorld(x, y) || skip.Contains((x, y))) continue;
                    Tile t = Framing.GetTileSafely(x, y);
                    // Top-left tile of the 2x2 crystal only, so each crystal counts once.
                    if (!t.HasTile || t.TileType != TileID.Heart || t.TileFrameX % 36 != 0 || t.TileFrameY % 36 != 0) continue;
                    float d = (x - px) * (x - px) + (y - py) * (y - py);
                    if (d < bestD) { bestD = d; best = (x, y); }
                }
            return best;
        }
    }

    /// <summary>Find the nearest ore vein, tunnel to it, mine every connected tile, repeat until `count` ore items are gained.</summary>
    public class MineVein : Skill
    {
        public override string Name => "mine_vein";
        public override int TimeoutTicks => 60 * 600;
        public override bool InterruptOnDamage => true;

        private readonly int tileType = -1; // -1 = any ore
        private readonly string targetName;
        private readonly int count, radius;
        private Dictionary<int, int> start; // ore item type -> count at start
        private readonly HashSet<(int, int)> vein = new();
        private readonly HashSet<(int, int)> skip = new();
        private (int x, int y) current = (-1, -1);
        private Skill child;
        private int veins;
        private string lastFailure;

        public MineVein(string target, int count = 30, int radius = 50)
        {
            targetName = string.IsNullOrWhiteSpace(target) ? "ore" : target.Trim();
            this.count = Math.Clamp(count, 1, 999);
            this.radius = Math.Clamp(radius, 5, 150);
            tileType = ResolveOre(targetName);
        }

        /// <summary>-1 for "ore"/"any". Accepts tile names (Copper), item names (CopperOre, "Copper Ore").</summary>
        public static int ResolveOre(string name)
        {
            string s = name.Trim().ToLowerInvariant();
            if (s is "ore" or "any" or "any ore" or "ores") return -1;
            int tile = Game.ResolveTile(name);
            if (tile >= 0 && TileID.Sets.Ore[tile]) return tile;
            if (s.EndsWith("ore"))
            {
                tile = Game.ResolveTile(name.Trim()[..^3]);
                if (tile >= 0 && TileID.Sets.Ore[tile]) return tile;
            }
            int item = Game.ResolveItem(name);
            if (item > 0 && ContentSamples.ItemsByType.TryGetValue(item, out Item sample) && sample.createTile >= 0 && TileID.Sets.Ore[sample.createTile])
                return sample.createTile;
            throw new ArgumentException($"'{name}' is not an ore. Use Copper, Tin, Iron, Lead, Silver, Tungsten, Gold, Platinum (or CopperOre etc.), or 'ore' for any.");
        }

        private bool Matches(int type) => tileType < 0 ? TileID.Sets.Ore[type] : type == tileType;

        public override void Tick(Player p)
        {
            start ??= OreItemCounts(p);

            if (child != null)
            {
                if (Step(child, p)) return;
                Skill done = child;
                child = null;
                if (done is TunnelTo && done.Ok && TileMatches(current.x, current.y))
                {
                    child = new Mine(current.x, current.y); // now in reach
                    return;
                }
                if (TileMatches(current.x, current.y))
                {
                    // Still there: the tunnel or the mine failed (too hard, lava, unreachable). Don't retry it.
                    lastFailure = done.Ok ? $"{Game.TileName(Framing.GetTileSafely(current.x, current.y).TileType)} at {current} did not break" : done.Message;
                    skip.Add(current);
                }
                vein.Remove(current);
                current = (-1, -1);
            }

            if (Gained(p) >= count) { Succeed(Report(p, true)); return; }

            // Drop vein tiles that are gone (dug by the tunnel, or broken some other way).
            vein.RemoveWhere(t => !TileMatches(t.Item1, t.Item2));
            if (vein.Count == 0)
            {
                var seed = FindNearest(p);
                if (seed == null) { Finish(p); return; }
                Flood(seed.Value);
                veins++;
            }

            int? boost = Explore.PickBoost(p);
            if (boost == null) { Fail("no pickaxe in inventory; mine_vein needs one"); return; }
            int px = Game.TileX(p), py = Game.TileY(p);
            current = vein.OrderBy(t => (t.Item1 - px) * (t.Item1 - px) + (t.Item2 - py) * (t.Item2 - py)).First();
            child = Game.InReach(p, current.x, current.y, boost.Value)
                ? new Mine(current.x, current.y)
                : new TunnelTo(current.x, current.y, 2);
        }

        private bool TileMatches(int x, int y)
        {
            Tile t = Framing.GetTileSafely(x, y);
            return t.HasTile && Matches(t.TileType);
        }

        /// <summary>Collect the connected vein (8-neighbour, same tile type) starting at `seed`.</summary>
        private void Flood((int x, int y) seed)
        {
            int type = Framing.GetTileSafely(seed.x, seed.y).TileType;
            var queue = new Queue<(int x, int y)>();
            queue.Enqueue(seed);
            vein.Add(seed);
            while (queue.Count > 0 && vein.Count < 400)
            {
                var (x, y) = queue.Dequeue();
                for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int nx = x + ox, ny = y + oy;
                        if (!Game.InWorld(nx, ny) || vein.Contains((nx, ny)) || skip.Contains((nx, ny))) continue;
                        Tile t = Framing.GetTileSafely(nx, ny);
                        if (!t.HasTile || t.TileType != type) continue;
                        vein.Add((nx, ny));
                        queue.Enqueue((nx, ny));
                    }
            }
        }

        private (int x, int y)? FindNearest(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p), power = Explore.BestPickPower(p);
            (int, int)? best = null;
            float bestD = float.MaxValue;
            for (int x = px - radius; x <= px + radius; x++)
                for (int y = py - radius; y <= py + radius; y++)
                {
                    if (!Game.InWorld(x, y) || skip.Contains((x, y))) continue;
                    Tile t = Framing.GetTileSafely(x, y);
                    if (!t.HasTile || !Matches(t.TileType) || Game.RequiredPick(t.TileType) > power) continue;
                    float d = (x - px) * (x - px) + (y - py) * (y - py);
                    if (d < bestD) { bestD = d; best = (x, y); }
                }
            return best;
        }

        /// <summary>Counts of every ore item this skill is after (items whose placed tile is a matching ore).</summary>
        private Dictionary<int, int> OreItemCounts(Player p)
        {
            var counts = new Dictionary<int, int>();
            for (int type = 1; type < ItemLoader.ItemCount; type++)
            {
                if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item) || item.createTile < 0) continue;
                if (!TileID.Sets.Ore[item.createTile] || !Matches(item.createTile)) continue;
                counts[type] = Game.CountItem(p, type);
            }
            return counts;
        }

        private int Gained(Player p) => start.Sum(kv => Math.Max(0, Game.CountItem(p, kv.Key) - kv.Value));

        private string Report(Player p, bool reachedCount)
        {
            var parts = start.Select(kv => (kv.Key, n: Game.CountItem(p, kv.Key) - kv.Value)).Where(e => e.n > 0)
                .Select(e => $"+{e.n} {Game.ItemName(e.Key)}");
            string gained = string.Join(", ", parts);
            string head = reachedCount ? $"got {Gained(p)}/{count} ore" : $"got {Gained(p)}/{count} ore; no more {targetName} veins within {radius} tiles";
            string fail = lastFailure != null ? $" Last failure: {lastFailure}" : "";
            return $"{head} from {veins} vein(s): {gained}.{fail}";
        }

        private void Finish(Player p)
        {
            if (Gained(p) > 0) { Succeed(Report(p, false)); return; }
            string fail = lastFailure != null ? $" Last failure: {lastFailure}" : "";
            string what = tileType < 0 ? "ore" : Game.TileName(tileType);
            string depth = tileType == TileID.Gold || tileType == TileID.Platinum
                ? $" Gold/Platinum only generate in the cavern layer (below y={(int)Main.rockLayer})."
                : tileType == TileID.Silver || tileType == TileID.Tungsten
                    ? $" Silver/Tungsten are below the surface (y>{(int)Main.worldSurface})."
                    : "";
            string power = tileType >= 0 && Game.RequiredPick(tileType) > Explore.BestPickPower(p)
                ? $" {what} needs {Game.RequiredPick(tileType)}% pickaxe power; yours is {Explore.BestPickPower(p)}%." : "";
            Fail($"no reachable {what} within {radius} tiles of ({Game.TileX(p)},{Game.TileY(p)}).{depth}{power} Explore further (tunnel_to sideways/deeper) or raise radius.{fail}");
        }
    }

    /// <summary>
    /// Get back to open sky above Main.worldSurface: pillar straight up with blocks if you have them, otherwise
    /// dig diagonal stairs up toward spawn. Falls back to a MagicMirror/IceMirror/CellPhone/RecallPotion if stuck.
    /// </summary>
    public class ReturnToSurface : Skill
    {
        public override string Name => "return_to_surface";
        public override int TimeoutTicks => 60 * 360;
        public override bool InterruptOnDamage => true;

        private Skill child;
        private int attempts, startX, startY = -1, still;
        private bool recalled;
        private string lastFailure;

        public override void Tick(Player p)
        {
            if (startY < 0) { startX = Game.TileX(p); startY = Game.TileY(p); }

            if (child != null)
            {
                if (Step(child, p)) return;
                Skill done = child;
                child = null;
                if (done is UseItem)
                {
                    int moved = Math.Abs(Game.TileX(p) - startX) + Math.Abs(Game.TileY(p) - startY);
                    if (done.Ok && moved > 10) { Succeed($"{done.Message}: teleported home to ({Game.TileX(p)},{Game.TileY(p)}). {lastFailure}".Trim()); return; }
                    Fail($"climbing failed ({lastFailure}) and {done.Message} did not teleport you. Mine/place blocks manually to get out.");
                    return;
                }
                if (!done.Ok) { lastFailure = done.Message; attempts = 99; }
            }
            if (!Explore.Settled(p, ref still)) return;

            if (Explore.AtSurface(p))
            {
                Succeed($"on the surface at ({Game.TileX(p)},{Game.TileY(p)}), up {startY - Game.TileY(p)} tiles (surface level y<{(int)Main.worldSurface})");
                return;
            }

            if (attempts++ >= 6)
            {
                if (!recalled && TryRecall(p)) return;
                Fail($"still underground at ({Game.TileX(p)},{Game.TileY(p)}) after climbing. {lastFailure ?? ""} Get blocks (DirtBlock/StoneBlock) to pillar up, or a MagicMirror/RecallPotion.".Trim());
                return;
            }

            int px = Game.TileX(p), feet = Explore.Feet(p);
            int dir = Main.spawnTileX >= px ? 1 : -1;
            bool blocks = Explore.BuildingBlock(p) >= 0;
            int ground = Explore.GroundAbove(px, feet - 4);
            // Straight up if we can pillar; otherwise a 45-degree staircase that ends as far sideways as it climbs.
            int tx = px, rise = ground < 0 ? 4 : feet - ground;
            if (!blocks)
            {
                tx = px + dir * (rise + 2);
                int g2 = Explore.GroundAbove(tx, feet);
                if (g2 >= 0) ground = g2;
            }
            int ty = (ground < 0 ? Math.Min(feet - 6, (int)Main.worldSurface - 2) : ground) - 2;
            child = new TunnelTo(tx, ty, 2, dir);
        }

        private bool TryRecall(Player p)
        {
            recalled = true;
            foreach (int type in Explore.RecallItems)
            {
                if (Game.FindSlot(p, i => i.type == type) < 0) continue;
                startX = Game.TileX(p); startY = Game.TileY(p);
                child = new UseItem(Explore.InternalName(type));
                return true;
            }
            return false;
        }
    }
}
