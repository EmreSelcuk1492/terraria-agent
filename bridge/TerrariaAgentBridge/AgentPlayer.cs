using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// The agent's hands. While a skill runs, the keyboard is ignored and the skill sets
    /// the player's controls every tick, the same flags a human's key presses would set.
    /// </summary>
    public class AgentPlayer : ModPlayer
    {
        /// <summary>World position the skill wants the cursor on (weapons aim at the mouse). Null = leave the mouse alone.</summary>
        public static Vector2? Aim;

        public override void SetControls()
        {
            if (Player.whoAmI != Main.myPlayer || !SkillRunner.IsRunning) return;

            // A pass-through skill (tb_nav) lets TerraBlind's SetControls, which runs before ours, keep its inputs.
            if (SkillRunner.PassThrough) { SkillRunner.Tick(Player); return; }

            Player.controlLeft = false;
            Player.controlRight = false;
            Player.controlUp = false;
            Player.controlDown = false;
            Player.controlJump = false;
            Player.controlUseItem = false;
            Player.controlUseTile = false;
            Player.controlHook = false;
            Player.controlThrow = false;
            Player.controlMount = false;
            Player.controlQuickHeal = false;
            Player.controlQuickMana = false;
            Aim = null;

            SkillRunner.Tick(Player);

            if (Aim is Vector2 world)
            {
                // TODO(verify): with game zoom != 1 the screen->world mapping is scaled; works at default zoom.
                Main.mouseX = (int)(world.X - Main.screenPosition.X);
                Main.mouseY = (int)(world.Y - Main.screenPosition.Y);
            }
        }
    }
}
