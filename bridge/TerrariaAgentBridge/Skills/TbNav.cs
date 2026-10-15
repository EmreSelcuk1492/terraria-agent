using System;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>
    /// Walk to a tile with TerraBlind's navigator (Nav/RecedingNav.cs in github.com/Reisenbug/TerraBlind, MIT):
    /// frame-priced physics edges over a Dijkstra cost field, greedy with an A* escape. It jumps, digs and
    /// bridges where our greedy MoveTo gives up. Optional: fails with a clear message when TerraBlind isn't loaded.
    ///
    /// Driven in-process by reflection, so neither mod compiles against the other. TerraBlind's SetControls runs
    /// before ours (build.txt weakReferences), and PassThroughControls stops AgentPlayer clearing its inputs.
    /// The guard still fights: during a fight our controls win, and a nav that gave up meanwhile is restarted.
    /// </summary>
    public class TbNav : Skill
    {
        public override string Name => "tb_nav";
        public override int TimeoutTicks => 60 * 90;
        public override bool PassThroughControls => true;

        private const int MaxStarts = 3;
        private readonly int tx, ty;
        private readonly string mode;
        private int starts;
        private bool started;

        private static Type nav;
        private static MethodInfo start, stop;
        private static FieldInfo active, lastStop;

        /// <param name="x">Target tile column.</param>
        /// <param name="y">Target tile row in our coordinates (player center row). TerraBlind wants the feet row, y + 1.</param>
        /// <param name="mode">snap (land near it, 1.5 tile tolerance), stand (stand exactly on it), reach (get within use range), mine (until that tile is gone).</param>
        public TbNav(int x, int y, string mode = "snap")
        {
            tx = x; ty = y; this.mode = (mode ?? "snap").Trim().ToLowerInvariant();
        }

        private static bool Bind(out string why)
        {
            why = null;
            if (nav != null) return true;
            if (!ModLoader.TryGetMod("TerraBlind", out Mod tb)) { why = "TerraBlind isn't loaded (build it with scripts/build-terrablind.sh and enable it), so use move_to"; return false; }
            var t = tb.Code.GetType("TerraBlind.RecedingNav");
            var modeType = tb.Code.GetType("TerraBlind.RecedingNav+Mode");
            var s = t?.GetMethod("Start", new[] { typeof(int), typeof(int), modeType ?? typeof(void) });
            var st = t?.GetMethod("Stop", Type.EmptyTypes);
            var a = t?.GetField("Active");
            var ls = t?.GetField("LastStop");
            if (s == null || st == null || a == null || ls == null) { why = "this TerraBlind build doesn't match tb_nav (RecedingNav API changed)"; return false; }
            nav = t; start = s; stop = st; active = a; lastStop = ls;
            return true;
        }

        private bool Active => (bool)active.GetValue(null);
        private string LastStop => (string)lastStop.GetValue(null);

        private void Begin()
        {
            var modeType = start.GetParameters()[2].ParameterType;
            string name = mode switch { "stand" => "Stand", "reach" => "Reach", "mine" or "exact" => "Mine", _ => "Snap" };
            start.Invoke(null, new object[] { tx, ty + 1, Enum.Parse(modeType, name) });
            started = true;
            starts++;
        }

        public override void Tick(Player p)
        {
            if (!started)
            {
                if (!Bind(out string why)) { Fail(why); return; }
                Begin();
                return;
            }
            if (Active) return;

            string result = LastStop;
            if (result == "done") { Succeed($"arrived near ({tx},{ty}) at ({Game.TileX(p)},{Game.TileY(p)}) via TerraBlind nav"); return; }
            // A guard fight overrides TerraBlind's inputs, which can read to it as stuck: try again from where we are.
            if ((result == "stuck" || result == "stopped") && starts < MaxStarts) { Begin(); return; }
            Fail($"TerraBlind nav to ({tx},{ty}) failed: {result ?? "never_ran"} (at {Game.TileX(p)},{Game.TileY(p)}). {MoveTo.DescribeObstacle(p, Math.Sign(tx - Game.TileX(p)))}");
        }

        protected override void OnEnd()
        {
            if (nav != null && started && Active) stop.Invoke(null, null);
        }
    }
}
