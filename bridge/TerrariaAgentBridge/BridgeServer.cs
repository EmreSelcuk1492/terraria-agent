using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAgentBridge
{
    /// <summary>
    /// Local HTTP server. Requests arrive on thread-pool threads; anything that touches
    /// game state is queued and run on the main thread by BridgeSystem.UpdateUI.
    ///
    ///   GET  /health           -> { ok, version, in_world }
    ///   GET  /state            -> observation (see Observation.cs)
    ///   POST /skill  {name, args}  -> blocks until the skill finishes -> { ok, message, ticks, player, inventory }
    ///   POST /cancel           -> stops the running skill
    ///   POST /config {freeze}  -> freeze the world while no skill is running
    ///   POST /save             -> save player + world now
    ///   POST /quit             -> save, exit tModLoader, and load back into this world on the next launch
    /// </summary>
    public static class BridgeServer
    {
        private static HttpListener listener;
        private static CancellationTokenSource cts;
        private static readonly ConcurrentQueue<(Func<object> work, TaskCompletionSource<object> done)> mainThreadQueue = new();

        public static volatile bool FreezeWhileThinking;
        /// <summary>An agent is driving: keep the game running while the window is in the background. Set by
        /// POST /config {background}; cleared when the world unloads.</summary>
        public static volatile bool Background;
        public static bool AgentActive => (Background || SkillRunner.IsRunning) && !Main.gameMenu;
        /// <summary>Set once Save &amp; Quit has finished; the menu loop then closes the game.</summary>
        public static volatile bool ExitRequested;

        public static void Start()
        {
            if (listener != null) return;
            cts = new CancellationTokenSource();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{TerrariaAgentBridge.Port}/");
            try
            {
                listener.Start();
            }
            catch (Exception e)
            {
                ModContent.GetInstance<TerrariaAgentBridge>().Logger.Error($"Bridge failed to listen on {TerrariaAgentBridge.Port}: {e.Message}");
                listener = null;
                return;
            }
            Task.Run(() => AcceptLoop(cts.Token));
            ModContent.GetInstance<TerrariaAgentBridge>().Logger.Info($"Bridge listening on http://127.0.0.1:{TerrariaAgentBridge.Port}/");
        }

        public static void Stop()
        {
            FreezeWhileThinking = false;
            cts?.Cancel();
            try { listener?.Stop(); listener?.Close(); } catch { }
            listener = null;
            while (mainThreadQueue.TryDequeue(out var item))
                item.done.TrySetResult(Error("bridge stopped"));
        }

        /// <summary>Called every frame on the main thread.</summary>
        public static void PumpMainThread()
        {
            int budget = 32;
            while (budget-- > 0 && mainThreadQueue.TryDequeue(out var item))
            {
                try { item.done.TrySetResult(item.work()); }
                catch (Exception e) { item.done.TrySetResult(Error(e.ToString())); }
            }
        }

        public static Task<object> OnMainThread(Func<object> work)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            mainThreadQueue.Enqueue((work, tcs));
            return tcs.Task;
        }

        public static Dictionary<string, object> Error(string message) => new() { ["ok"] = false, ["error"] = message };

        private static async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && listener != null)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private static async Task Handle(HttpListenerContext ctx)
        {
            object result;
            int status = 200;
            try
            {
                string body = "";
                if (ctx.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    body = await reader.ReadToEndAsync();
                }
                result = await Route(ctx.Request.HttpMethod, ctx.Request.Url.AbsolutePath, body);
            }
            catch (Exception e)
            {
                status = 500;
                result = Error(e.Message);
            }

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            try
            {
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch { }
        }

        private static bool InWorld => !Main.gameMenu && Main.LocalPlayer != null && Main.LocalPlayer.active;

        private static async Task<object> Route(string method, string path, string body)
        {
            switch (method + " " + path)
            {
                case "GET /health":
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["version"] = TerrariaAgentBridge.BridgeVersion, ["in_world"] = InWorld, ["freeze"] = FreezeWhileThinking,
                        ["player"] = InWorld ? Main.ActivePlayerFileData?.Name : null, ["world"] = InWorld ? Main.worldName : null,
                    };

                case "POST /status":
                {
                    // The on-screen HUD panel (HudSystem): what the AI is working on. Empty title hides it.
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    HudSystem.Title = doc.RootElement.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                    HudSystem.Detail = doc.RootElement.TryGetProperty("detail", out var de) ? de.GetString() ?? "" : "";
                    return new Dictionary<string, object> { ["ok"] = true };
                }

                case "POST /say":
                {
                    // Narration: show a line in the in-game chat (the player sees what the AI is doing and why).
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    string text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : "";
                    if (!InWorld || string.IsNullOrWhiteSpace(text)) return Error("nothing to say or not in a world");
                    return await OnMainThread(() =>
                    {
                        Main.NewText("[AI] " + text, 120, 220, 255);
                        return new Dictionary<string, object> { ["ok"] = true };
                    });
                }

                case "POST /time":
                {
                    // Testing aid for headless runs (sim/): start a run at a known time of day so one bad first night
                    // doesn't decide it. {"at": "dawn" | "noon" | "dusk" | "midnight", "clear": bool}
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    string at = doc.RootElement.TryGetProperty("at", out var a) ? a.GetString() ?? "" : "";
                    if (!InWorld) return Error("not in a world");
                    (bool day, double t) = at switch
                    {
                        "dawn" => (true, 0.0), "noon" => (true, 27000.0), "dusk" => (false, 0.0), "midnight" => (false, 16200.0),
                        _ => (true, -1.0),
                    };
                    if (t < 0) return Error("at must be dawn, noon, dusk or midnight");
                    // Jumping to daytime also removes the hostiles left over from the night: they don't despawn on
                    // their own while the player stands among them, so every respawn landed back in the same fight.
                    bool clear = !doc.RootElement.TryGetProperty("clear", out var c) ? day : c.ValueKind == JsonValueKind.True;
                    return await OnMainThread(() =>
                    {
                        Main.dayTime = day;
                        Main.time = t;
                        int cleared = 0;
                        if (clear)
                            foreach (NPC n in Main.npc)
                                if (n.active && !n.friendly && !n.townNPC && n.damage > 0 && !n.boss)
                                {
                                    n.active = false;
                                    cleared++;
                                }
                        return new Dictionary<string, object> { ["ok"] = true, ["at"] = at, ["cleared"] = cleared };
                    });
                }

                case "POST /scenario":
                {
                    if (!InWorld) return Error("not in a world");
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    JsonElement req = doc.RootElement.Clone();
                    return await OnMainThread(() => Scenario.Apply(req));
                }

                case "POST /save":
                    if (!InWorld) return Error("not in a world");
                    return await OnMainThread(() =>
                    {
                        Player.SavePlayer(Main.ActivePlayerFileData);
                        WorldGen.saveAndPlay(); // world save on a worker thread, like the vanilla autosave
                        return new Dictionary<string, object> { ["ok"] = true };
                    });

                case "POST /quit":
                    if (!InWorld) { ExitRequested = true; return new Dictionary<string, object> { ["ok"] = true, ["saved"] = false }; }
                    return await OnMainThread(() =>
                    {
                        SkillRunner.Cancel("game is quitting");
                        FreezeWhileThinking = false;
                        Autoload.Remember(Main.ActivePlayerFileData.Name, Main.worldName);
                        WorldGen.SaveAndQuit(() => ExitRequested = true);
                        return new Dictionary<string, object> { ["ok"] = true, ["saved"] = true, ["autoload"] = Main.worldName };
                    });

                case "GET /state":
                    if (!InWorld) return Error("not in a world");
                    return await OnMainThread(() => Observation.Build(Main.LocalPlayer));

                case "POST /config":
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    if (doc.RootElement.TryGetProperty("freeze", out var freeze))
                        FreezeWhileThinking = freeze.GetBoolean();
                    if (doc.RootElement.TryGetProperty("background", out var bg))
                        Background = bg.GetBoolean();
                    if (doc.RootElement.TryGetProperty("guard", out var guard))
                        SkillRunner.Guard = guard.GetBoolean();
                    if (doc.RootElement.TryGetProperty("astar", out var astar))
                        Skills.Pathfinder.Enabled = astar.GetBoolean();
                    if (doc.RootElement.TryGetProperty("god", out var god))
                        Sandbox.God = god.GetBoolean();
                    int cleared = 0;
                    if (doc.RootElement.TryGetProperty("peaceful", out var peaceful))
                    {
                        Sandbox.Peaceful = peaceful.GetBoolean();
                        if (Sandbox.Peaceful && InWorld) cleared = (int)await OnMainThread(() => Sandbox.ClearHostiles());
                    }
                    return new Dictionary<string, object> { ["ok"] = true, ["freeze"] = FreezeWhileThinking, ["background"] = Background,
                        ["guard"] = SkillRunner.Guard, ["peaceful"] = Sandbox.Peaceful, ["god"] = Sandbox.God, ["astar"] = Skills.Pathfinder.Enabled, ["cleared"] = cleared };
                }

                case "POST /cancel":
                    return await OnMainThread(() =>
                    {
                        SkillRunner.Cancel("cancelled by agent");
                        return new Dictionary<string, object> { ["ok"] = true };
                    });

                case "POST /skill":
                {
                    if (!InWorld) return Error("not in a world");
                    using var doc = JsonDocument.Parse(body);
                    string name = doc.RootElement.GetProperty("name").GetString();
                    JsonElement args = doc.RootElement.TryGetProperty("args", out var a) ? a.Clone() : default;

                    // Build + start the skill on the main thread, then wait (off the main thread) for it to finish.
                    var started = await OnMainThread(() =>
                    {
                        if (Main.LocalPlayer.dead)
                            return Error($"you are dead; respawning in about {Main.LocalPlayer.respawnTimer / 60 + 1}s. Call wait, then observe.");
                        Skill skill;
                        try { skill = SkillFactory.Create(name, args); }
                        catch (Exception e) { return Error(e.Message); }
                        if (skill == null) return Error($"unknown skill '{name}'");
                        return SkillRunner.Start(skill);
                    });
                    if (started is Task<object> running)
                        return await running;
                    return started;
                }

                default:
                    return Error($"no route {method} {path}");
            }
        }
    }
}
