using System.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAgentBridge
{
    public class BridgeSystem : ModSystem
    {
        public override void OnModLoad()
        {
            if (Main.dedServ) return;
            BridgeServer.Start();
            On_Main.DoUpdateInWorld += FreezeHook;
        }

        public override void Unload()
        {
            BridgeServer.Stop();
        }

        public override void OnWorldUnload()
        {
            SkillRunner.Cancel("world unloaded");
            BridgeServer.FreezeWhileThinking = false;
        }

        // UpdateUI runs every frame in a world, even while FreezeHook skips the world update,
        // so queued /state and /skill requests keep getting served.
        public override void UpdateUI(GameTime gameTime)
        {
            BridgeServer.PumpMainThread();
        }

        // Latency fix: while the agent is thinking (no skill running), the world doesn't advance.
        // Enemies wait, the clock stops, the player can't be hit. Only single player.
        private static void FreezeHook(On_Main.orig_DoUpdateInWorld orig, Main self, Stopwatch sw)
        {
            if (BridgeServer.FreezeWhileThinking && Main.netMode == Terraria.ID.NetmodeID.SinglePlayer && !SkillRunner.IsRunning)
                return;
            orig(self, sw);
        }
    }
}
