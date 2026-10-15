using System;
using Terraria;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>
    /// Greedy walker: run toward the target, jump over 1-3 block steps, drop through platforms.
    /// It is NOT a pathfinder. When it gets stuck it fails with a description of what blocks it,
    /// so the agent can mine through, place blocks to climb, or pick another route.
    /// TODO: replace with A* over the tile grid using real jump arcs.
    /// </summary>
    public class MoveTo : Skill
    {
        public override string Name => "move_to";
        public override int TimeoutTicks => 60 * 40;

        private readonly int tx, ty, tolerance;
        private int jumpHold, jumpCooldown;
        private float bestDist = float.MaxValue;
        private int ticksSinceProgress;
        private ulong lastFrame;

        public MoveTo(int x, int y, int tolerance = 1)
        {
            tx = x; ty = y; this.tolerance = Math.Max(0, tolerance);
        }

        public override void Tick(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            int dx = tx - px, dy = ty - py;

            if (Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance + 2)
            {
                Succeed($"arrived at ({px},{py})");
                return;
            }

            // Not ticked for a while (the guard paused us to fight): the player may have been knocked back or
            // chased an enemy, so judge progress from here on rather than against the old best distance.
            if (lastFrame != 0 && Main.GameUpdateCount - lastFrame > 2) { bestDist = float.MaxValue; ticksSinceProgress = 0; }
            lastFrame = Main.GameUpdateCount;

            float dist = Math.Abs(dx) + Math.Abs(dy) * 0.5f;
            if (dist < bestDist - 0.5f) { bestDist = dist; ticksSinceProgress = 0; }
            else if (++ticksSinceProgress > 120)
            {
                Fail($"stuck at ({px},{py}) trying to reach ({tx},{ty}). {DescribeObstacle(p, Math.Sign(dx))}");
                return;
            }

            Drive(p, dx, dy, Math.Abs(dx) > tolerance);
        }

        /// <summary>One tick of movement toward a tile offset. Shared with Attack.</summary>
        public void Drive(Player p, int dx, int dy, bool walk)
        {
            int dir = Math.Sign(dx);
            int px = Game.TileX(p), py = Game.TileY(p);
            int feetY = (int)((p.position.Y + p.height - 1) / 16f);
            bool onGround = p.velocity.Y == 0f;

            if (walk && dir != 0)
            {
                if (Game.Lava(px + dir, feetY) || Game.Lava(px + dir * 2, feetY) || Game.Lava(px + dir, feetY + 1))
                {
                    Fail($"lava ahead at ({px + dir},{feetY}); refusing to walk into it");
                    return;
                }
                if (dir < 0) p.controlLeft = true; else p.controlRight = true;
            }

            // Want to drop: target is well below and we are standing on a platform.
            if (dy > 2 && Game.Platform(px, feetY + 1)) p.controlDown = true;

            bool wallAhead = dir != 0 && (Game.Solid(px + dir, feetY) || Game.Solid(px + dir, feetY - 1));
            bool wantUp = dy < -2;

            if (jumpCooldown > 0) jumpCooldown--;
            if (jumpHold > 0)
            {
                p.controlJump = true;
                jumpHold--;
                if (jumpHold == 0) jumpCooldown = 4; // must release jump before the next one registers
            }
            else if (onGround && jumpCooldown == 0 && (wallAhead || wantUp))
            {
                p.controlJump = true;
                jumpHold = 18;
            }
        }

        public static string DescribeObstacle(Player p, int dir)
        {
            int px = Game.TileX(p);
            int feetY = (int)((p.position.Y + p.height - 1) / 16f);
            if (dir == 0) return "Target is straight above or below: mine down, or place blocks/platforms to climb.";
            for (int h = 0; h <= 4; h++)
            {
                if (!Game.Solid(px + dir, feetY - h)) continue;
                Tile t = Framing.GetTileSafely(px + dir, feetY - h);
                int wallHeight = 0;
                while (wallHeight < 12 && Game.Solid(px + dir, feetY - wallHeight)) wallHeight++;
                return $"Blocked by {Game.TileName(t.TileType)} at ({px + dir},{feetY - h}); wall is ~{wallHeight} tiles high. Mine it or build up.";
            }
            if (!Game.Solid(px + dir, feetY + 1) && !Game.Solid(px + dir, feetY + 2) && !Game.Solid(px + dir, feetY + 3))
                return "Gap or drop ahead.";
            return "No obvious block ahead (maybe a slope, liquid, or the target is unreachable from here).";
        }
    }
}
