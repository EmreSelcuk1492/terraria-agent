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
            // Sim containers start headless (TA_RENDER=1000, see sim/run.sh): menus still draw, worlds don't.
            if (int.TryParse(Environment.GetEnvironmentVariable("TA_RENDER"), out int render)) RenderEvery = Math.Max(1, render);
            BridgeServer.Start();
            On_Main.DoUpdateInWorld += FreezeHook;
            On_Main.DoDraw += DrawHook;
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
            Skills.BuildHouse.ForgetSites();
        }

        /// <summary>
        /// Draw 1 frame in RenderEvery (POST /config {"render": N}). The sim draws with software OpenGL under Rosetta,
        /// and drawing, not the world update, is what held it at ~21 ticks/s: nobody watches a container's screen.
        /// A skipped frame still moves the camera, because aiming maps world positions through Main.screenPosition.
        /// </summary>
        public static volatile int RenderEvery = 1;
        public static volatile int Speed = 1;
        private static int drawCount;

        private static void DrawHook(On_Main.orig_DoDraw orig, Main self, GameTime gameTime)
        {
            int every = RenderEvery;
            if (every <= 1 || Main.gameMenu || ++drawCount % every == 0) { orig(self, gameTime); return; }
            Player p = Main.LocalPlayer;
            if (p != null && p.active)
                Main.screenPosition = p.Center - new Vector2(Main.screenWidth, Main.screenHeight) / 2f;
        }

        // Ticks per second over the last few seconds of wall clock (60 = full speed), for /health and run logs.
        private static readonly Stopwatch tpsClock = Stopwatch.StartNew();
        private static uint tpsStartTick;
        public static double Tps { get; private set; } = 60;

        public override void PostUpdateEverything()
        {
            double s = tpsClock.Elapsed.TotalSeconds;
            if (s < 3) return;
            Tps = Math.Round((Main.GameUpdateCount - tpsStartTick) / s, 1);
            tpsStartTick = Main.GameUpdateCount;
            tpsClock.Restart();
        }

        // Skills tick from SetControls, which a dead player never reaches; end the running skill here instead.
        public override void PostUpdatePlayers()
        {
            if (!Main.dedServ) SkillRunner.CheckDead(Main.LocalPlayer);
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
            // Fast-forward (POST /config {"speed": k}): k game ticks per frame. Only in a world, only while an agent
            // drives; with drawing off the sim has the CPU for it. Benchmarks measure game ticks, so results match.
            for (int i = 1; i < Speed && !Main.gameMenu && BridgeServer.AgentActive; i++) orig(self, ref gameTime);
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
        // Enemies wait, the clock stops, the player can't be hit. Only single player. Never while dead: the
        // respawn timer only counts down in the world update, and /skill refuses to start while dead.
        private static void FreezeHook(On_Main.orig_DoUpdateInWorld orig, Main self, Stopwatch sw)
        {
            if (BridgeServer.FreezeWhileThinking && Main.netMode == Terraria.ID.NetmodeID.SinglePlayer && !SkillRunner.IsRunning && !Main.LocalPlayer.dead)
                return;
            orig(self, sw);
        }
    }
}
