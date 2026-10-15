using System;
using System.Collections.Generic;
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
                Succeed($"arrived at ({px},{py}) {How}");
                return;
            }

            // Not ticked for a while (the guard paused us to fight): the player may have been knocked back or
            // chased an enemy, so judge progress from here on rather than against the old best distance.
            if (lastFrame != 0 && Main.GameUpdateCount - lastFrame > 2) { bestDist = float.MaxValue; ticksSinceProgress = 0; }
            lastFrame = Main.GameUpdateCount;

            if (FollowPath(p)) return;

            float dist = Math.Abs(dx) + Math.Abs(dy) * 0.5f;
            if (dist < bestDist - 0.5f) { bestDist = dist; ticksSinceProgress = 0; }
            else if (++ticksSinceProgress > 120)
            {
                Fail($"stuck at ({px},{py}) trying to reach ({tx},{ty}) {How}. {DescribeObstacle(p, Math.Sign(dx))}");
                return;
            }

            Drive(p, dx, dy, Math.Abs(dx) > tolerance);
        }

        // A* path (Pathfinder): planned on the first tick, re-planned when progress stalls, abandoned for the greedy
        // walk below after 3 re-plans or when no path exists. pathState: 0 not planned, 1 following, 2 greedy.
        private List<Pathfinder.Node> path;
        private int pathState, pathIdx, pathStall, replans;
        public int PathLength => path?.Count ?? 0;
        private int plannedNodes;
        private bool noPath;
        /// <summary>How the move went, for results and A/B runs: "via A* (23 nodes, 0 replans)" or "greedy (no A* path)".</summary>
        private string How => !Pathfinder.Enabled ? "(greedy: A* off)" : noPath ? "(greedy: no A* path)"
            : plannedNodes > 0 ? $"(A* {plannedNodes} nodes, {replans} replans{(pathState == 2 && replans > 3 ? ", then greedy" : "")})" : "(greedy)";

        private bool FollowPath(Player p)
        {
            if (pathState == 2 || !Pathfinder.Enabled) return false;
            if (pathState == 0 || pathStall > 90)
            {
                if (pathState == 1 && ++replans > 3) { pathState = 2; path = null; bestDist = float.MaxValue; return false; }
                path = Pathfinder.Find(p, tx, ty, tolerance);
                pathIdx = 0;
                pathStall = 0;
                pathState = path == null || path.Count < 2 ? 2 : 1;
                if (path == null) noPath = true; else plannedNodes = Math.Max(plannedNodes, path.Count);
                if (pathState == 2) { path = null; return false; }
            }
            int c = Explore.LeftCol(p), f = Explore.Feet(p);
            int at = -1;
            for (int k = path.Count - 1; k >= pathIdx; k--) // furthest node we are on (momentum skips some)
                if (Math.Abs(path[k].Col - c) <= 1 && path[k].Feet == f) { at = k; break; }
            if (at > pathIdx) { pathIdx = at; pathStall = 0; } else pathStall++;
            if (pathIdx >= path.Count - 1) { pathState = 2; path = null; bestDist = float.MaxValue; return false; } // last leg: greedy

            var next = path[Math.Min(pathIdx + 1, path.Count - 1)];
            bool onGround = Math.Abs(p.velocity.Y) < 0.01f;
            if (next.Jump && onGround && jumpCooldown == 0 && jumpHold == 0) jumpHold = 18; // a gap: jump at the edge
            Drive(p, next.Col - c + (next.Col > c ? 1 : next.Col < c ? -1 : 0), next.Feet - f, true);
            return true;
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
