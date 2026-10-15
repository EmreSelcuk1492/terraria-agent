using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Terraria;
using TerrariaAgentBridge.Skills;

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
        /// <summary>
        /// A way out of danger (dig_in, a desperate attack). The runner's low-health kill skips it: ending the escape
        /// because health is low is what kept the character standing in the fight that was taking it.
        /// </summary>
        public virtual bool Survival => false;

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

        internal static bool RunChild(Skill child, Player p) => Step(child, p);
    }

    public static class SkillRunner
    {
        private static Skill current;
        private static TaskCompletionSource<object> completion;
        private static int lastLife;

        // Bodyguard: with Guard on (the default), an enemy in reach or a hit doesn't end the running skill. It is
        // paused, the guard fights (and drinks a potion when low), then the skill resumes. Guard time doesn't count
        // toward the skill's timeout. With Guard off, the old behaviour: a hit ends skills that InterruptOnDamage.
        public static volatile bool Guard = true;
        private static Attack defense;
        private static int defenseTicks, guardCooldown, guardTicksUsed;
        private static string defenseTarget;
        private static readonly List<string> guardNotes = new();
        // A fight that ends without a kill (target out of reach, no weapon) backs off for a few seconds so the skill
        // gets to run; the per-skill budget stops an unkillable/unreachable enemy from pausing a skill forever.
        private const int GuardRange = 6, MaxDefenseTicks = 60 * 20, MaxGuardTicks = 60 * 90;

        public static bool IsRunning => current != null;
        public static string CurrentName => current?.Name;

        /// <summary>Main thread. Returns a task that completes with the skill result.</summary>
        public static Task<object> Start(Skill skill)
        {
            Cancel($"interrupted by {skill.Name}");
            current = skill;
            lastLife = Main.LocalPlayer.statLife;
            defense = null;
            guardCooldown = 0;
            guardTicksUsed = 0;
            guardNotes.Clear();
            completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            return completion.Task;
        }

        public static void Cancel(string reason)
        {
            if (current == null) return;
            current.ForceFail(reason);
            Finish(Main.LocalPlayer);
        }

        /// <summary>
        /// Player.Update returns before SetControls while the player is dead, so Tick never sees p.dead.
        /// Called every world tick (BridgeSystem.PostUpdatePlayers) to end the skill on death.
        /// </summary>
        public static void CheckDead(Player p)
        {
            if (current == null || p == null || !p.dead) return;
            current.ForceFail($"you are dead; respawning in about {p.respawnTimer / 60 + 1}s at spawn. Call wait, then observe.");
            Finish(p);
        }

        public static void Tick(Player p)
        {
            if (current == null) return;
            bool hurt = p.statLife < lastLife;
            if (!p.dead && Guard && GuardTick(p, hurt))
            {
                lastLife = p.statLife;
                return;
            }

            current.Ticks++;
            if (p.dead)
                current.ForceFail($"you are dead; respawning in about {p.respawnTimer / 60 + 1}s at spawn. Call wait, then observe.");
            else if (Guard && hurt && !current.Survival && p.statLife < p.statLifeMax2 * 0.3f && !TryHeal(p))
                current.ForceFail($"LOW HEALTH {p.statLife}/{p.statLifeMax2} and no healing potion. {Observation.ThreatSummary(p)} Get to safety (home, or away from enemies) and wait to regenerate before continuing.");
            else if (!Guard && current.InterruptOnDamage && hurt)
                current.ForceFail($"INTERRUPTED: took {lastLife - p.statLife} damage (hp {p.statLife}/{p.statLifeMax2}). {Observation.ThreatSummary(p)} Fight (attack), move away, or heal before continuing.");
            else if (current.Ticks > current.TimeoutTicks)
                current.ForceFail($"{current.Name} timed out after {current.TimeoutTicks / 60}s");
            else
                current.Tick(p);

            lastLife = p.statLife;
            if (current.Done) Finish(p);
        }

        /// <summary>Runs one guard tick if a fight is on or should start. True = the skill is paused this tick.</summary>
        private static bool GuardTick(Player p, bool hurt)
        {
            if (defense != null)
            {
                defenseTicks++;
                guardTicksUsed++;
                if (p.statLife < p.statLifeMax2 * 0.4f) TryHeal(p);
                if (defenseTicks < MaxDefenseTicks && Skill.RunChild(defense, p)) return true;
                if (defense.Ok) { guardNotes.Add($"killed {defenseTarget}"); guardCooldown = 30; }
                else { guardNotes.Add($"broke off fight with {defenseTarget}"); guardCooldown = 60 * 4; }
                defense = null;
                return false;
            }
            if (current is Attack || --guardCooldown > 0 || guardTicksUsed >= MaxGuardTicks) return false;
            NPC threat = NearestThreat(p, hurt ? 12 : GuardRange);
            if (threat == null) return false;
            if (p.statLife < p.statLifeMax2 * 0.3f && !TryHeal(p)) return false; // too weak to fight: the low-health check decides
            defense = new Attack("nearest", 1);
            defenseTarget = threat.FullName;
            defenseTicks = 0;
            return true;
        }

        /// <summary>Nearest enemy within `range` tiles that has a clear line to the player (not behind blocks).</summary>
        public static NPC NearestThreat(Player p, float range) => Main.npc
            .Where(n => n.active && !n.friendly && !n.townNPC && n.damage > 0 && n.lifeMax > 5 && !n.dontTakeDamage
                        && (n.Center - p.Center).Length() / 16f <= range
                        && Collision.CanHit(p.position, p.width, p.height, n.position, n.width, n.height))
            .OrderBy(n => (n.Center - p.Center).LengthSquared())
            .FirstOrDefault();

        /// <summary>Drink the best healing potion, like the quick-heal key. False if none or on potion cooldown.</summary>
        public static bool TryHeal(Player p)
        {
            if (p.potionDelay > 0 || p.QuickHeal_GetItemToUse() == null) return false;
            int before = p.statLife;
            p.QuickHeal();
            if (p.statLife > before) guardNotes.Add($"healed {before}->{p.statLife}");
            return p.statLife > before;
        }

        private static void Finish(Player p)
        {
            var skill = current;
            string guard = guardNotes.Count > 0 ? $" [guard: {string.Join(", ", guardNotes.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key))}]" : "";
            defense = null;
            var tcs = completion;
            current = null;
            completion = null;
            tcs?.TrySetResult(new Dictionary<string, object>
            {
                ["ok"] = skill.Ok,
                ["skill"] = skill.Name,
                ["message"] = skill.Message + guard,
                ["ticks"] = skill.Ticks,
                ["player"] = Observation.PlayerSummary(p),
                ["inventory"] = Observation.Inventory(p),
            });
        }
    }
}
