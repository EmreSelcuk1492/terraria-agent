using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
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
            On_Main.DoUpdate += MenuHook;
            IL_Main.DoUpdate += KeepRunningUnfocused;
        }

        public override void Unload()
        {
            BridgeServer.Stop();
        }

        public override void OnWorldUnload()
        {
            SkillRunner.Cancel("world unloaded");
            BridgeServer.FreezeWhileThinking = false;
            BridgeServer.Background = false;
        }

        // UpdateUI runs every frame in a world, even while FreezeHook skips the world update,
        // so queued /state and /skill requests keep getting served.
        public override void UpdateUI(GameTime gameTime)
        {
            BridgeServer.PumpMainThread();
        }

        // In the menus UpdateUI doesn't run: serve requests here, run the one-shot autoload, and close the game
        // once a /quit has saved.
        private static void MenuHook(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
        {
            orig(self, ref gameTime);
            if (!Main.gameMenu) return;
            BridgeServer.PumpMainThread();
            if (BridgeServer.ExitRequested) { BridgeServer.ExitRequested = false; self.Exit(); return; }
            Autoload.Tick();
        }

        // Single player pauses the whole game while the window is in the background (Main.DoUpdate: hasFocus =
        // IsActive; if !hasFocus -> gamePaused, return), and Player.Update only reads controls (where skills tick)
        // with focus. While an agent is driving, the game must keep running unattended, so the focus check reads
        // "IsActive || agent active". Without an agent, alt-tab still pauses as usual.
        private static void KeepRunningUnfocused(ILContext il)
        {
            var c = new ILCursor(il);
            if (!c.TryGotoNext(MoveType.After,
                    i => i.MatchCallOrCallvirt<Microsoft.Xna.Framework.Game>("get_IsActive"),
                    i => i.MatchStsfld<Main>(nameof(Main.hasFocus))))
            {
                ModContent.GetInstance<TerrariaAgentBridge>().Logger.Warn("focus patch: hasFocus = IsActive not found; the game will pause in the background");
                return;
            }
            c.Index--; // before the store: transform the IsActive value on the stack
            c.EmitDelegate<Func<bool, bool>>(active => active || BridgeServer.AgentActive);
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
