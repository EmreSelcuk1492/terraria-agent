using System;
using System.Collections.Generic;
using Terraria;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>
    /// A* over standing spots. A node is (left column, feet row) where the 2x3 player fits with a floor under it.
    /// Edges: walk one column, step/jump up 1-4 rows, walk off a ledge and fall up to 20, and jump a 2-4 column gap.
    /// Cost is roughly ticks of movement; the heuristic is the column distance, which never overestimates (every
    /// edge costs at least one per column crossed), so the path is optimal for this move set. No digging: when the
    /// target can't be reached on foot the caller tunnels (TunnelTo) instead.
    /// </summary>
    public static class Pathfinder
    {
        /// <summary>POST /config {"astar": false} switches MoveTo back to the greedy walker, for A/B scenario runs.</summary>
        public static volatile bool Enabled = true;
        private const int MaxStep = 4; // a held jump clears ~5 rows; plan for 4
        public readonly record struct Node(int Col, int Feet, bool Jump);

        public static List<Node> Find(Player p, int tx, int ty, int tolerance, int maxNodes = 8000)
        {
            var start = (Explore.LeftCol(p), Explore.Feet(p));
            if (!Explore.CanStand(start.Item1, start.Item2) && Explore.CanStand(start.Item1, start.Item2 + 1)) start.Item2++;
            var open = new PriorityQueue<(int c, int f), float>();
            var g = new Dictionary<(int, int), float> { [start] = 0 };
            var from = new Dictionary<(int, int), ((int, int) prev, bool jump)>();
            open.Enqueue(start, H(start.Item1, tx));
            int expanded = 0;
            while (open.Count > 0 && expanded++ < maxNodes)
            {
                var cur = open.Dequeue();
                var (c, f) = cur;
                if (IsGoal(c, f, tx, ty, tolerance)) return Build(from, cur);
                float gc = g[cur];
                foreach (var (next, cost, jump) in Neighbours(c, f))
                {
                    float ng = gc + cost;
                    if (g.TryGetValue(next, out float old) && old <= ng) continue;
                    g[next] = ng;
                    from[next] = (cur, jump);
                    open.Enqueue(next, ng + H(next.Item1, tx));
                }
            }
            return null;
        }

        private static float H(int col, int tx) => Math.Max(0, Math.Abs(col - tx) - 1);

        /// <summary>The player's centre tile is the left column or the one right of it; its centre row is feet - 2.</summary>
        private static bool IsGoal(int c, int f, int tx, int ty, int tol) =>
            (Math.Abs(c - tx) <= tol || Math.Abs(c + 1 - tx) <= tol) && Math.Abs(f - 2 - ty) <= tol + 2;

        private static List<Node> Build(Dictionary<(int, int), ((int, int) prev, bool jump)> from, (int, int) end)
        {
            var path = new List<Node>();
            var cur = end;
            bool jump = false;
            while (true)
            {
                bool has = from.TryGetValue(cur, out var link);
                path.Add(new Node(cur.Item1, cur.Item2, has && link.jump));
                if (!has) break;
                cur = link.prev;
            }
            path.Reverse();
            _ = jump;
            return path;
        }

        private static bool BodyClear(int col, int feet, int extraHead = 0)
        {
            for (int x = col; x <= col + 1; x++)
                for (int y = feet - 3 - extraHead; y <= feet - 1; y++)
                    if (Game.Solid(x, y) || Game.Lava(x, y)) return false;
            return true;
        }

        private static IEnumerable<((int, int) next, float cost, bool jump)> Neighbours(int c, int f)
        {
            foreach (int d in new[] { -1, 1 })
            {
                int nc = c + d;
                bool stepped = false;
                for (int up = 0; up <= MaxStep; up++)
                {
                    if (up > 0 && !BodyClear(c, f, up)) break; // head room to jump from here
                    if (Explore.CanStand(nc, f - up))
                    {
                        stepped = true;
                        yield return ((nc, f - up), 1f + up * 1.5f, false);
                        break;
                    }
                }
                if (stepped) continue;
                if (!BodyClear(nc, f)) continue;
                // Walk off the ledge: fall to the first floor below (no lava on the way).
                for (int nf = f + 1; nf <= f + 20; nf++)
                {
                    if (Game.Lava(nc, nf - 1) || Game.Lava(nc + 1, nf - 1)) break;
                    if (Explore.CanStand(nc, nf)) { yield return ((nc, nf), 1f + (nf - f) * 0.15f, false); break; }
                    if (!BodyClear(nc, nf)) break;
                }
                // Jump a gap 2-4 columns wide, landing up to 1 row higher or 2 lower. Needs 2 rows of extra head room
                // over the whole arc.
                if (!BodyClear(c, f, 2)) continue;
                for (int w = 2; w <= 4; w++)
                {
                    int lc = c + d * w;
                    bool arc = true;
                    for (int k = 1; k < w && arc; k++) arc = BodyClear(c + d * k, f, 2);
                    if (!arc) break;
                    for (int lf = f - 1; lf <= f + 2; lf++)
                        if (Explore.CanStand(lc, lf) && BodyClear(lc, lf, 1))
                        {
                            yield return ((lc, lf), w + 2f, true);
                            goto nextDir;
                        }
                }
                nextDir:;
            }
        }
    }
}
