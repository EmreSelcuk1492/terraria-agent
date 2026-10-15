using System.Collections.Generic;
using System.Threading.Tasks;
using Terraria;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// A multi-tick action. Tick() is called once per game tick from AgentPlayer.SetControls,
    /// after all controls were cleared. Call Succeed/Fail to finish. Failure messages are read
    /// by the LLM, so make them say what went wrong and what would fix it.
    /// </summary>
    public abstract class Skill
    {
        public abstract string Name { get; }
        public virtual int TimeoutTicks => 60 * 30;
        /// <summary>Stop the moment the player takes damage, so the agent can react while the world is frozen.</summary>
        public virtual bool InterruptOnDamage => true;

        public int Ticks;
        public bool Done { get; private set; }
        public bool Ok { get; private set; }
        public string Message { get; private set; } = "";

        public abstract void Tick(Player p);

        protected void Succeed(string message) { Done = true; Ok = true; Message = message; }
        protected void Fail(string message) { Done = true; Ok = false; Message = message; }

        /// <summary>Runs a child skill for one tick. Returns true while the child is still running.</summary>
        protected static bool Step(Skill child, Player p)
        {
            if (child.Done) return false;
            child.Ticks++;
            if (child.Ticks > child.TimeoutTicks)
            {
                child.Fail($"{child.Name} timed out after {child.TimeoutTicks / 60}s");
                return false;
            }
            child.Tick(p);
            return !child.Done;
        }

        internal void ForceFail(string message) => Fail(message);
    }

    public static class SkillRunner
    {
        private static Skill current;
        private static TaskCompletionSource<object> completion;
        private static int lastLife;

        public static bool IsRunning => current != null;

        /// <summary>Main thread. Returns a task that completes with the skill result.</summary>
        public static Task<object> Start(Skill skill)
        {
            Cancel($"interrupted by {skill.Name}");
            current = skill;
            lastLife = Main.LocalPlayer.statLife;
            completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            return completion.Task;
        }

        public static void Cancel(string reason)
        {
            if (current == null) return;
            current.ForceFail(reason);
            Finish(Main.LocalPlayer);
        }

        public static void Tick(Player p)
        {
            if (current == null) return;
            current.Ticks++;
            if (p.dead)
                current.ForceFail($"you are dead; respawning in about {p.respawnTimer / 60 + 1}s at spawn. Call wait, then observe.");
            else if (current.InterruptOnDamage && p.statLife < lastLife)
                current.ForceFail($"INTERRUPTED: took {lastLife - p.statLife} damage (hp {p.statLife}/{p.statLifeMax2}). {Observation.ThreatSummary(p)} Fight (attack), move away, or heal before continuing.");
            else if (current.Ticks > current.TimeoutTicks)
                current.ForceFail($"{current.Name} timed out after {current.TimeoutTicks / 60}s");
            else
                current.Tick(p);

            lastLife = p.statLife;
            if (current.Done) Finish(p);
        }

        private static void Finish(Player p)
        {
            var skill = current;
            var tcs = completion;
            current = null;
            completion = null;
            tcs?.TrySetResult(new Dictionary<string, object>
            {
                ["ok"] = skill.Ok,
                ["skill"] = skill.Name,
                ["message"] = skill.Message,
                ["ticks"] = skill.Ticks,
                ["player"] = Observation.PlayerSummary(p),
                ["inventory"] = Observation.Inventory(p),
            });
        }
    }
}
