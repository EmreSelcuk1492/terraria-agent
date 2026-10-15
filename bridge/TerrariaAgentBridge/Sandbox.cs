using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// Benchmark switches (POST /config {"peaceful": bool, "god": bool}). Peaceful: no hostile spawns, and any that
    /// appear anyway (events, bosses) vanish on spawn. God: every hit is dodged. Together they score the build/gather
    /// plan on its own; with both off, the same plan is the survival track.
    /// </summary>
    public static class Sandbox
    {
        public static volatile bool Peaceful, God;
        /// <summary>Scenario tests: only the enemies the scenario spawns, none of the world's own.</summary>
        public static volatile bool NoNaturalSpawns;

        public static bool Hostile(NPC n) => n.active && !n.friendly && !n.townNPC && n.damage > 0;

        /// <summary>Main thread. Removes the hostiles already in the world; returns how many.</summary>
        public static int ClearHostiles()
        {
            int n = 0;
            foreach (NPC npc in Main.npc)
                if (Hostile(npc) && !npc.boss) { npc.active = false; n++; }
            return n;
        }
    }

    public class SandboxNPC : GlobalNPC
    {
        public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
        {
            if (Sandbox.Peaceful || Sandbox.NoNaturalSpawns) maxSpawns = 0;
        }

        public override void OnSpawn(NPC npc, IEntitySource source)
        {
            if (Sandbox.Peaceful && Sandbox.Hostile(npc)) npc.active = false;
        }
    }

    public class SandboxPlayer : ModPlayer
    {
        public override bool FreeDodge(Player.HurtInfo info) => Sandbox.God && Player.whoAmI == Main.myPlayer;
    }
}
