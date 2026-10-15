using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// A small panel at the top of the screen saying what the AI is doing ("AI Director 6/19: Mine copper or tin")
    /// and, below it, the skill running right now. Set through POST /status; hidden when empty.
    /// </summary>
    public class HudSystem : ModSystem
    {
        public static volatile string Title = "";
        public static volatile string Detail = "";

        public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
        {
            int at = layers.FindIndex(l => l.Name == "Vanilla: Mouse Text");
            if (at < 0) at = layers.Count;
            layers.Insert(at, new LegacyGameInterfaceLayer("TerrariaAgentBridge: AI status", Draw, InterfaceScaleType.UI));
        }

        private static bool Draw()
        {
            string title = Title, detail = SkillRunner.CurrentName is string s ? $"> {s}" : Detail;
            if (string.IsNullOrEmpty(title)) return true;
            var font = FontAssets.MouseText.Value;
            Vector2 size = font.MeasureString(title);
            if (!string.IsNullOrEmpty(detail)) size.Y += 26;
            float w = System.Math.Max(size.X, string.IsNullOrEmpty(detail) ? 0 : font.MeasureString(detail).X * 0.85f);
            var pos = new Vector2(Main.screenWidth / 2f - w / 2f, 78);
            var box = new Rectangle((int)pos.X - 12, (int)pos.Y - 8, (int)w + 24, (int)size.Y + 14);
            Main.spriteBatch.Draw(TextureAssets.MagicPixel.Value, box, new Color(10, 20, 40) * 0.72f);
            Utils.DrawBorderString(Main.spriteBatch, title, pos, new Color(140, 220, 255));
            if (!string.IsNullOrEmpty(detail))
                Utils.DrawBorderString(Main.spriteBatch, detail, pos + new Vector2(0, 26), new Color(225, 225, 225), 0.85f);
            return true;
        }
    }
}
