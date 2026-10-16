using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;
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

        /// <summary>True while another mod drives the player's controls (see TbNav): AgentPlayer then leaves them alone.</summary>
        public virtual bool PassThroughControls => false;

        protected void Succeed(string message) { Done = true; Ok = true; Message = message; OnEnd(); }
        protected void Fail(string message) { Done = true; Ok = false; Message = message; OnEnd(); }
        /// <summary>Called once when the skill ends for any reason (success, failure, cancel, death, timeout).</summary>
        protected virtual void OnEnd() { }

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

        // Timeouts count game ticks, and a game that slows down stretches them: in the LLM track a mine_nearest with a
        // 180 s budget ran 6918 s of wall clock. A second budget in wall-clock time (twice the tick budget, plus the
        // guard's) ends a skill like that, and every result reports ticks, wall seconds and the tick rate it ran at.
        private static readonly System.Diagnostics.Stopwatch wall = new();
        private static readonly System.Diagnostics.Stopwatch busy = new();
        private static double busyMaxMs;
        private static double WallBudget(Skill s) => s.TimeoutTicks / 60.0 * 2 + MaxGuardTicks / 60.0 + 30;

        public static bool IsRunning => current != null;
        public static string CurrentName => current?.Name;
        /// <summary>The running skill lets another mod drive controls this tick (never while the guard is fighting).</summary>
        public static bool PassThrough => current != null && current.PassThroughControls && defense == null;

        /// <summary>Main thread. Returns a task that completes with the skill result.</summary>
        public static Task<object> Start(Skill skill)
        {
            Cancel($"interrupted by {skill.Name}");
            current = skill;
            wall.Restart();
            busy.Reset();
            busyMaxMs = 0;
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
            else if (Guard && hurt && !current.Survival && LowFor(p) && !TryHeal(p))
                current.ForceFail($"LOW HEALTH {p.statLife}/{p.statLifeMax2} and no healing potion. {Observation.ThreatSummary(p)} Get to safety (home, or away from enemies) and wait to regenerate before continuing.");
            else if (!Guard && current.InterruptOnDamage && hurt)
                current.ForceFail($"INTERRUPTED: took {lastLife - p.statLife} damage (hp {p.statLife}/{p.statLifeMax2}). {Observation.ThreatSummary(p)} Fight (attack), move away, or heal before continuing.");
            else if (current.Ticks > current.TimeoutTicks)
                current.ForceFail($"{current.Name} timed out after {current.TimeoutTicks / 60}s");
            else if (wall.Elapsed.TotalSeconds > WallBudget(current))
                current.ForceFail($"{current.Name} ran out of wall-clock time: {wall.Elapsed.TotalSeconds:0}s for {current.Ticks / 60}s of game time "
                                  + $"(the game slowed to {current.Ticks / wall.Elapsed.TotalSeconds:0.0} ticks/s; 60 is normal)");
            else
            {
                double before = busy.Elapsed.TotalMilliseconds;
                busy.Start();
                current.Tick(p);
                busy.Stop();
                busyMaxMs = Math.Max(busyMaxMs, busy.Elapsed.TotalMilliseconds - before);
            }

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
            // Too weak to fight: the low-health check hands control back. Not while escaping (dig_in): there the
            // enemy gets free hits for the whole dig unless the guard swings.
            if (!current.Survival && LowFor(p) && !TryHeal(p)) return false;
            defense = new Attack("nearest", 1, desperate: current.Survival);
            defenseTarget = threat.FullName;
            defenseTicks = 0;
            return true;
        }

        /// <summary>
        /// Too hurt to keep working: below 30%, or below 50% with an enemy within 12 tiles. Handing control back at 50%
        /// leaves time to dig in; at 30% (run 4) a zombie was already on top and every escape died mid-way.
        /// </summary>
        public static bool LowFor(Player p) =>
            p.statLife < p.statLifeMax2 * 0.3f || (p.statLife < p.statLifeMax2 * 0.5f && NearestThreat(p, 12) != null);

        /// <summary>
        /// A flier (Demon Eye) hovering out of a short melee weapon's reach. Chasing it is how the guard bled out in
        /// run 4: the shortsword can't touch it until it swoops, so wait for the swoop instead.
        /// </summary>
        public static bool OutOfReachFlier(Player p, NPC n)
        {
            if (!n.noGravity) return false;
            int slot = Game.BestSlot(p, i => i.damage > 0 && i.pick == 0 && i.axe == 0 && i.hammer == 0 && i.ammo == 0 && !i.consumable ? i.damage : 0);
            float reach = slot >= 0 ? Game.WeaponReach(p.inventory[slot]) : 2f;
            if (reach >= 7f) return false; // ranged, magic, yoyos reach it
            return (p.Center - n.Center).Length() / 16f > reach + 1.5f;
        }

        /// <summary>Nearest enemy within `range` tiles that has a clear line to the player (not behind blocks).</summary>
        public static NPC NearestThreat(Player p, float range) => Main.npc
            .Where(n => n.active && !n.friendly && !n.townNPC && n.damage > 0 && n.lifeMax > 5 && !n.dontTakeDamage
                        && (n.Center - p.Center).Length() / 16f <= range && !OutOfReachFlier(p, n)
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
                ["wall_s"] = Math.Round(wall.Elapsed.TotalSeconds, 1),
                ["skill_cpu_s"] = Math.Round(busy.Elapsed.TotalSeconds, 2),
                ["slowest_tick_ms"] = Math.Round(busyMaxMs, 1),
                ["tps"] = wall.Elapsed.TotalSeconds > 0.5 ? Math.Round(skill.Ticks / wall.Elapsed.TotalSeconds, 1) : 60.0,
                ["player"] = Observation.PlayerSummary(p),
                ["inventory"] = Observation.Inventory(p),
            });
        }
    }
}
