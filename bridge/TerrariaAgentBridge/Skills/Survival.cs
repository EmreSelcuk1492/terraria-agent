using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>
    /// The first-night bunker: dig a 2-wide hole 4 tiles straight down where you stand, seal the roof flush with the
    /// ground (and any open side) with blocks, then wait inside to regenerate. Surface zombies can't dig, so a sealed
    /// hole is safe without a house. Runs below the low-health threshold (it is the way out of a losing fight).
    /// Leave by mining the roof; return_to_surface does it on its own.
    /// </summary>
    public class DigIn : Skill
    {
        public override string Name => "dig_in";
        public override bool Survival => true;
        public override bool InterruptOnDamage => false;
        public override int TimeoutTicks => waitTicks + 60 * 45;

        private const int Depth = 4;
        private readonly int waitTicks;
        private readonly float untilHp;
        private readonly bool untilDay;

        private Skill walk;
        private int chops, moves, nudge, spot = int.MinValue;
        private int phase, still, c0, top, row, col, swing, swingNeeded, waited, startHp, sealedN, needN;
        private readonly List<(int x, int y)> roof = new();

        public DigIn(double waitSeconds, double untilHp, bool untilDay)
        {
            waitTicks = (int)(Math.Clamp(waitSeconds, 0, 600) * 60);
            this.untilHp = (float)Math.Clamp(untilHp, 0, 1);
            this.untilDay = untilDay;
        }

        public override void Tick(Player p)
        {
            switch (phase)
            {
                case 0: Start(p); break;
                case 1: Dig(p); break;
                case 2: Seal(p); break;
                default: Wait(p); break;
            }
        }

        private void Start(Player p)
        {
            if (walk != null) { if (Step(walk, p)) return; walk = null; still = 0; nudge = 0; }
            if (spot != int.MinValue)
            {
                // move_to lands on a tile, but the 20 px body can straddle the wrong pair: step until it sits in spot, spot+1.
                float want = (spot + 1) * 16f;
                if (Math.Abs(p.Center.X - want) > 4f && nudge++ < 90)
                {
                    if (Math.Abs(p.velocity.X) < 1.5f) { if (p.Center.X > want) p.controlLeft = true; else p.controlRight = true; }
                    return;
                }
                if (Math.Abs(p.velocity.X) > 0.1f) return;
                int a = spot;
                spot = int.MinValue;
                if (Explore.LeftCol(p) >= a && Explore.RightCol(p) <= a + 1 && Clean(a, Explore.Feet(p)))
                {
                    if (startHp == 0) startHp = p.statLife;
                    c0 = a; top = Explore.Feet(p); phase = 1; row = top; col = 0;
                    return;
                }
            }
            if (!Explore.Settled(p, ref still) && Ticks < 180) return; // land first (3 s at most: a fall can't wait)
            if (startHp == 0) startHp = p.statLife;
            c0 = Explore.LeftCol(p);
            if (Sealed(p)) { phase = 3; return; } // already in a closed pocket: just wait
            top = Explore.Feet(p);
            if (Game.BestSlot(p, i => i.pick) < 0) { Fail("no pickaxe to dig in with. Run away from the enemies, or attack with desperate=true."); return; }
            // The hole's two columns must hold the whole body: LeftCol..RightCol is one or two columns wide.
            int right = Explore.RightCol(p);
            if (Clean(c0, top) && right <= c0 + 1) { phase = 1; row = top; col = 0; return; }
            if (right == c0 && Clean(c0 - 1, top)) { c0--; phase = 1; row = top; col = 0; return; }
            // A tree on the spot: fell it (Terraria won't break grass a tree stands on) and dig right here.
            if (chops < 6)
                for (int x = c0 - 1; x <= c0 + 2; x++)
                    for (int y = top - 3; y < top; y++)
                    {
                        Tile t = Framing.GetTileSafely(x, y);
                        if (!t.HasTile || !Game.IsTree(t.TileType) || (y < top - 1 && !Game.IsTree(Framing.GetTileSafely(x, top - 1).TileType))) continue;
                        chops++;
                        walk = new Mine(x, top - 1 >= y && Framing.GetTileSafely(x, top - 1).HasTile ? top - 1 : y, tunnel: false);
                        return;
                    }
            // Furniture, chests, lava or a step in the ground right here (the first live try stood among tree trunks, whose
            // grass can't be broken): walk to the nearest clean spot. A few tries, then give up.
            if (++moves > 3) { Fail($"no clean spot to dig in near ({Game.TileX(p)},{top}) after {moves - 1} moves; walk away from trees and buildings first"); return; }
            for (int d = 1; d <= 10; d++)
                foreach (int a in new[] { c0 + d, c0 - d })
                {
                    int g = Ground(a, top);
                    if (g < 0 || Ground(a + 1, top) != g || !Clean(a, g)) continue;
                    walk = new MoveTo(a + 1, g - 2, 0);
                    spot = a;
                    return;
                }
            Fail($"no clean spot to dig in within 10 tiles of ({Game.TileX(p)},{top}) (trees, furniture, lava or uneven ground)");
        }

        /// <summary>First solid row in column x from 6 above `near` to 6 below, or -1.</summary>
        private static int Ground(int x, int near)
        {
            for (int y = near - 6; y <= near + 6; y++)
                if (Game.Solid(x, y) && !Game.Solid(x, y - 1)) return y;
            return -1;
        }

        /// <summary>Columns a, a+1 can become a bunker with ground row `top`: nothing unbreakable, no lava, even ground.</summary>
        private static bool Clean(int a, int top)
        {
            for (int x = a; x <= a + 1; x++)
            {
                if (!Game.Solid(x, top)) return false;
                for (int y = top - 3; y < top + Depth + 1; y++)
                {
                    Tile t = Framing.GetTileSafely(x, y);
                    if (Game.Lava(x, y) || Game.Lava(x - 1, y) || Game.Lava(x + 1, y)) return false;
                    if (!t.HasTile) continue;
                    if (Game.IsTree(t.TileType) || Main.tileContainer[t.TileType] || Game.RequiredPick(t.TileType) > 0) return false;
                    if (Main.tileFrameImportant[t.TileType] && !Main.tileCut[t.TileType]) return false; // furniture, saplings, pots
                }
                if (Game.IsTree(Framing.GetTileSafely(x, top - 1).TileType) && Framing.GetTileSafely(x, top - 1).HasTile) return false;
            }
            return true;
        }

        private void Dig(Player p)
        {
            if (row >= top + Depth || Explore.Feet(p) > top + Depth + 2) // dug out, or fell into a cave below
            {
                if (!Explore.Settled(p, ref still)) return;
                phase = 2;
                return;
            }
            int x = c0 + col, y = row;
            Tile t = Framing.GetTileSafely(x, y);
            if (!t.HasTile) { Next(); return; }
            if (Main.tileContainer[t.TileType] || Game.IsTree(t.TileType) || TileID.Sets.BasicChest[t.TileType])
            {
                Fail($"{Game.TileName(t.TileType)} at ({x},{y}) is in the way; step a few tiles sideways and dig in again");
                return;
            }
            int pick = Game.BestSlot(p, i => i.pick);
            Item tool = p.inventory[pick];
            if (tool.pick < Game.RequiredPick(t.TileType))
            {
                Fail($"{Game.TileName(t.TileType)} at ({x},{y}) needs {Game.RequiredPick(t.TileType)}% pick power; step sideways and dig in again");
                return;
            }
            if (swingNeeded == 0)
            {
                bool solid = Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
                swingNeeded = solid ? Math.Max(1, (int)Math.Ceiling(100f / Math.Max(1, tool.pick))) * Math.Max(1, tool.useTime) : 1;
            }
            Game.Hold(p, pick);
            AgentPlayer.Aim = new Microsoft.Xna.Framework.Vector2(x * 16 + 8, y * 16 + 8);
            if (++swing < swingNeeded) return;
            WorldGen.KillTile(x, y);
            if (Framing.GetTileSafely(x, y).HasTile) { Fail($"{Game.TileName(t.TileType)} at ({x},{y}) did not break"); return; }
            Next();
        }

        private void Next()
        {
            swing = swingNeeded = 0;
            if (++col > 1) { col = 0; row++; }
        }

        /// <summary>Roof over the head and walls beside the body, in the hole's two columns.</summary>
        private List<(int x, int y)> Shell(Player p)
        {
            int head = (int)(p.position.Y / 16f), feet = Explore.Feet(p);
            int left = Math.Min(c0, Explore.LeftCol(p)), right = Math.Max(c0 + 1, Explore.RightCol(p));
            var list = new List<(int, int)>();
            for (int x = left; x <= right; x++) list.Add((x, head - 1));
            for (int y = head; y < feet; y++) { list.Add((left - 1, y)); list.Add((right + 1, y)); }
            return list;
        }

        private bool Sealed(Player p)
        {
            foreach (var (x, y) in Shell(p)) if (!Game.Solid(x, y)) return false;
            return true;
        }

        private void Seal(Player p)
        {
            var shell = Shell(p);
            foreach (var (x, y) in shell)
            {
                if (Game.Solid(x, y)) continue;
                needN++;
                int block = Explore.BuildingBlock(p);
                if (block < 0) continue;
                Tile t = Framing.GetTileSafely(x, y);
                if (t.HasTile) WorldGen.KillTile(x, y); // grass, vines, a torch
                int slot = Game.FindSlot(p, i => i.type == block);
                if (WorldGen.PlaceTile(x, y, p.inventory[slot].createTile, mute: false, forced: false, plr: p.whoAmI)
                    && Game.Solid(x, y))
                {
                    Game.ConsumeOne(p, slot);
                    sealedN++;
                }
            }
            roof.Clear();
            int head = (int)(p.position.Y / 16f);
            foreach (var (x, y) in shell) if (y == head - 1) roof.Add((x, y));
            phase = 3;
        }

        private void Wait(Player p)
        {
            bool healthy = p.statLife >= p.statLifeMax2 * untilHp;
            bool dayOk = !untilDay || Main.dayTime;
            if ((healthy && dayOk) || ++waited >= waitTicks)
            {
                string seal = phase == 3 && needN > 0
                    ? $"sealed {sealedN}/{needN} open tiles{(sealedN < needN ? " (out of blocks: carry DirtBlock/StoneBlock)" : "")}"
                    : "already enclosed";
                string exit = roof.Count > 0 ? $" Roof at {string.Join(" ", roof)}: mine it to get out (return_to_surface does)." : "";
                Succeed($"dug in at ({Game.TileX(p)},{Game.TileY(p)}), {seal}; hp {startHp}->{p.statLife}/{p.statLifeMax2} after {waited / 60}s"
                        + (Main.dayTime ? ", day" : ", still night") + "." + exit);
            }
        }
    }
}
