using System;
using System.Text.Json;
using TerrariaAgentBridge.Skills;

namespace TerrariaAgentBridge
{
    /// <summary>Maps POST /skill {name, args} to a Skill. Keep in sync with agent/terraria_agent/tools.py.</summary>
    public static class SkillFactory
    {
        public static Skill Create(string name, JsonElement args) => name switch
        {
            "move_to" => new MoveTo(Int(args, "x"), Int(args, "y"), Int(args, "tolerance", 1)),
            "mine" => new Mine(Int(args, "x"), Int(args, "y")),
            "mine_nearest" => new MineNearest(Str(args, "target"), Int(args, "count", 1)),
            "dig_down" => Game.ResolveTile(Str(args, "target", "Stone")) is var t && t >= 0
                ? new DigDown(t, Int(args, "max_depth", 80))
                : throw new ArgumentException($"unknown tile '{Str(args, "target", "Stone")}'. Use internal tile names like Stone, CopperOre, IronOre."),
            "place" => new Place(Str(args, "item"), Int(args, "x"), Int(args, "y")),
            "craft" => new Craft(Str(args, "item"), Int(args, "count", 1)),
            "attack" => new Attack(Str(args, "target", "nearest"), Int(args, "max_kills", 1)),
            "use_item" => new UseItem(Str(args, "item")),
            "wait" => new Wait((float)Num(args, "seconds", 1)),
            "build_room" => new BuildRoom(Int(args, "x"), Int(args, "y"), Int(args, "width", 10), Int(args, "height", 6),
                Str(args, "block", "Wood"), Str(args, "wall", "WoodWall"), Str(args, "door", "WoodenDoor")),
            "check_house" => new CheckHouse(Int(args, "x"), Int(args, "y")),
            "obtain" => new Obtain(Str(args, "item"), Int(args, "count", 1)),
            "build_house" => new BuildHouse(OptInt(args, "x"), OptInt(args, "y")),
            _ => null,
        };

        private static bool Has(JsonElement a, string key, out JsonElement v)
        {
            v = default;
            return a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out v) && v.ValueKind != JsonValueKind.Null;
        }

        private static int Int(JsonElement a, string key, int? fallback = null) =>
            Has(a, key, out var v) ? (int)Math.Round(v.GetDouble()) : fallback ?? throw new ArgumentException($"missing argument '{key}'");

        private static int? OptInt(JsonElement a, string key) => Has(a, key, out var v) ? (int)Math.Round(v.GetDouble()) : null;

        private static double Num(JsonElement a, string key, double fallback) => Has(a, key, out var v) ? v.GetDouble() : fallback;

        private static string Str(JsonElement a, string key, string fallback = null) =>
            Has(a, key, out var v) ? v.GetString() : fallback ?? throw new ArgumentException($"missing argument '{key}'");
    }
}
