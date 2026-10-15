using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// One-shot "load straight back into this world" for unattended rebuild cycles.
    /// POST /quit writes agent-autoload.json next to the save folders; the next launch reads it on the title
    /// screen, deletes it, and enters that player + world the same way the world list's Play button does.
    /// Without the file, tModLoader starts at the menu as usual.
    /// </summary>
    public static class Autoload
    {
        private static string FilePath => Path.Combine(Main.SavePath, "agent-autoload.json");
        private static int titleFrames;
        private static bool done;

        public static void Remember(string player, string world)
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(new { player, world })); }
            catch (Exception e) { Log($"could not write {FilePath}: {e.Message}"); }
        }

        /// <summary>Called every frame while in the menus.</summary>
        public static void Tick()
        {
            if (done || !Main.gameMenu) return;
            if (Main.menuMode != 0) { titleFrames = 0; return; } // wait for the title screen (mod loading is over by then)
            if (++titleFrames < 90) return;
            done = true;
            if (!File.Exists(FilePath)) return;

            string player, world;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                player = doc.RootElement.GetProperty("player").GetString();
                world = doc.RootElement.GetProperty("world").GetString();
            }
            catch (Exception e) { Log($"bad {FilePath}: {e.Message}"); return; }
            finally { try { File.Delete(FilePath); } catch { } }

            Main.LoadPlayers();
            var pfd = Main.PlayerList.FirstOrDefault(p => p.Name == player);
            Main.LoadWorlds();
            var wfd = Main.WorldList.FirstOrDefault(w => w.Name == world);
            if (pfd == null || wfd == null || !wfd.IsValid) { Log($"autoload: player '{player}' or world '{world}' not found"); return; }

            Main.menuMultiplayer = false;
            Main.ServerSideCharacter = false;
            Main.myPlayer = 0; // as Main.SelectPlayer does for single player
            pfd.SetAsActive();
            wfd.SetAsActive();
            Main.menuMode = 10;
            WorldGen.playWorld();
            Log($"autoload: entering '{world}' as '{player}'");
        }

        private static void Log(string msg) => ModContent.GetInstance<TerrariaAgentBridge>().Logger.Info(msg);
    }
}
