using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAgentBridge
{
    public static class Crafting
    {
        public static List<string> MissingStations(Player p, Recipe r) =>
            MissingStationTypes(p, r).Select(Game.TileName).ToList();

        public static List<int> MissingStationTypes(Player p, Recipe r) =>
            r.requiredTile.Where(t => t >= 0 && !p.adjTile[t]).ToList();

        /// <summary>
        /// adjTile is normally rebuilt in Player.Update. While the world is frozen (and on the tick a station
        /// was just placed) it is stale, so a skill that crafts must refresh it first.
        /// </summary>
        public static void RefreshStations(Player p) => p.AdjTiles();

        public static List<Recipe> RecipesFor(int type) => Enumerable.Range(0, Recipe.numRecipes).Select(i => Main.recipe[i])
            .Where(r => r.createItem.type == type && !r.Disabled).ToList();

        /// <summary>Crafts up to `times` batches with whatever recipe is usable right now. Returns batches made.</summary>
        public static int CraftNow(Player p, int type, int times)
        {
            RefreshStations(p);
            var recipes = RecipesFor(type);
            int made = 0;
            while (made < times)
            {
                Recipe usable = recipes.FirstOrDefault(r => MissingStationTypes(p, r).Count == 0 && MissingIngredients(p, r).Count == 0);
                if (usable == null) break;
                Consume(p, usable);
                p.QuickSpawnItem(p.GetSource_Misc("agent_craft"), type, usable.createItem.stack);
                made++;
            }
            return made;
        }

        /// <summary>Why each of the first few recipes for `type` can't be crafted here; `wanted` items still to make.</summary>
        public static string Problems(Player p, int type, int wanted = 1) => string.Join("; ", RecipesFor(type).Take(3).Select(r =>
        {
            var stations = MissingStations(p, r);
            var ingredients = MissingIngredients(p, r);
            return $"recipe [{Describe(r)}]" +
                (stations.Count > 0 ? $" missing station: {string.Join(", ", stations)}" : "") +
                (ingredients.Count > 0 ? $" missing: {string.Join(", ", ingredients)}" : "") + Totals(p, r, wanted);
        })) + VariantHint(p, type);

        /// <summary>
        /// The ingredients for all `wanted` items, not one batch. LLM track copper_bars: after 1/3 CopperBar the
        /// message said "CopperOre 0/3", the model mined 2 more, and failed again (3 bars need 9 ore).
        /// </summary>
        public static string Totals(Player p, Recipe r, int wanted)
        {
            int batches = (wanted + r.createItem.stack - 1) / r.createItem.stack;
            if (batches <= 1) return "";
            var parts = r.requiredItem.Where(i => !i.IsAir)
                .Select(i => $"{i.stack * batches} {Game.ItemName(i.type)} (have {Accepted(r, i).Sum(t => Game.CountItem(p, t))})");
            return $"; for {wanted} more {Game.ItemName(r.createItem.type)} you need {string.Join(", ", parts)} in total";
        }

        /// <summary>
        /// The same item in another material the player holds: "WorkBench" with 35 BorealWood -> BorealWoodWorkBench.
        /// In the LLM track the model asked for a WorkBench three times in a snow start; the message only said
        /// "Wood 0/10" (a fresh pass had found the Boreal one by luck).
        /// </summary>
        public static string VariantHint(Player p, int type)
        {
            string name = Game.ItemName(type);
            string core = name.StartsWith("Wooden") ? name.Substring(6) : name.StartsWith("Wood") ? name.Substring(4) : name;
            var mine = RecipesFor(type);
            if (mine.Count == 0 || core.Length < 3) return "";
            var hits = new List<string>();
            for (int i = 0; i < Recipe.numRecipes && hits.Count < 3; i++)
            {
                Recipe r = Main.recipe[i];
                string n = Game.ItemName(r.createItem.type);
                if (r.Disabled || r.createItem.type == type || n == core || !n.EndsWith(core) || hits.Contains(n)) continue;
                if (!mine.Any(m => Similar(m, r)) || MissingIngredients(p, r).Count > 0) continue;
                hits.Add(n);
            }
            return hits.Count == 0 ? "" : $" You hold the materials for {string.Join(", ", hits)} (the same thing in another material): craft that instead.";
        }

        private static bool Similar(Recipe a, Recipe b) =>
            a.createItem.stack == b.createItem.stack
            && a.requiredItem.Count(i => !i.IsAir) == b.requiredItem.Count(i => !i.IsAir)
            && a.requiredTile.Where(t => t >= 0).OrderBy(t => t).SequenceEqual(b.requiredTile.Where(t => t >= 0).OrderBy(t => t));

        /// <summary>Item types that satisfy an ingredient, honouring recipe groups ("any wood", "any iron bar").</summary>
        public static HashSet<int> Accepted(Recipe r, Item ingredient)
        {
            var types = new HashSet<int> { ingredient.type };
            foreach (int g in r.acceptedGroups)
                if (RecipeGroup.recipeGroups.TryGetValue(g, out var group) && group.ContainsItem(ingredient.type))
                    types.UnionWith(group.ValidItems);
            return types;
        }

        public static List<string> MissingIngredients(Player p, Recipe r)
        {
            var missing = new List<string>();
            foreach (Item ing in r.requiredItem)
            {
                if (ing.IsAir) continue;
                int have = Accepted(r, ing).Sum(t => Game.CountItem(p, t));
                if (have < ing.stack) missing.Add($"{Game.ItemName(ing.type)} {have}/{ing.stack}");
            }
            return missing;
        }

        public static void Consume(Player p, Recipe r)
        {
            foreach (Item ing in r.requiredItem)
            {
                if (ing.IsAir) continue;
                var ok = Accepted(r, ing);
                int need = ing.stack;
                for (int i = 0; i < 58 && need > 0; i++)
                {
                    Item it = p.inventory[i];
                    if (it.IsAir || !ok.Contains(it.type)) continue;
                    int take = System.Math.Min(need, it.stack);
                    it.stack -= take;
                    need -= take;
                    if (it.stack <= 0) it.TurnToAir();
                }
            }
        }

        /// <summary>"WoodWall x4 = 1 Wood @ WorkBenches", or why it can't be crafted. Used to make failure messages actionable.</summary>
        public static string HowToGet(int itemType)
        {
            for (int i = 0; i < Recipe.numRecipes; i++)
            {
                Recipe r = Main.recipe[i];
                if (r.createItem.type == itemType && !r.Disabled)
                    return $"craft: {Game.ItemName(itemType)}{(r.createItem.stack > 1 ? " x" + r.createItem.stack : "")} = {Describe(r)}";
            }
            return "not craftable; gather it";
        }

        /// <summary>
        /// The whole ingredient tree for `count` of `type` against what the player holds: every intermediate, every
        /// station (and what it takes to make one), and the raw totals to gather. LLM track copper_bars failed 28/28
        /// (2026-10-16/18) planning from one recipe at a time: 3 ore for 3 bars, then no Furnace, then no WorkBench.
        /// </summary>
        public static string Tree(Player p, int type, int count)
        {
            RefreshStations(p);
            var pool = new Dictionary<int, int>();  // what's left to use: one branch's wood isn't counted again by the next
            for (int i = 0; i < 58; i++)
            {
                Item it = p.inventory[i];
                if (!it.IsAir) pool[it.type] = pool.GetValueOrDefault(it.type) + it.stack;
            }
            var lines = new List<string>();
            var raw = new Dictionary<int, int>();
            Need(p, new HashSet<int> { type }, type, count, 0, pool, lines, raw, new HashSet<int>());
            lines.Add("Gather in total: " + (raw.Count == 0 ? "nothing, you hold everything"
                : string.Join(", ", raw.Select(kv => $"{kv.Value} {Game.ItemName(kv.Key)}{GatherHint(kv.Key)}"))));
            return string.Join("\n", lines);
        }

        private static void Need(Player p, HashSet<int> accepted, int type, int n, int depth, Dictionary<int, int> pool,
                                 List<string> lines, Dictionary<int, int> raw, HashSet<int> stations)
        {
            string pad = new string(' ', depth * 2), name = Game.ItemName(type);
            int have = 0;
            foreach (int t in accepted)
            {
                int take = System.Math.Min(pool.GetValueOrDefault(t), n - have);
                if (take > 0) { pool[t] -= take; have += take; }
            }
            int missing = n - have;
            if (missing <= 0) { lines.Add($"{pad}- {n} {name}: have them"); return; }
            Recipe r = depth < 6 ? RecipesFor(type).FirstOrDefault(x => !Reverses(x, type)) : null;
            if (r == null)
            {
                lines.Add($"{pad}- {n} {name}: have {have}, gather {missing}{GatherHint(type)}");
                raw[type] = raw.GetValueOrDefault(type) + missing;
                return;
            }
            int batches = (missing + r.createItem.stack - 1) / r.createItem.stack;
            lines.Add($"{pad}- {n} {name}: have {have}, craft {missing}: {batches} x [{Describe(r)}]");
            foreach (int t in r.requiredTile.Where(t => t >= 0))
            {
                if (!stations.Add(t)) continue;  // one station serves every recipe that needs it
                if (p.adjTile[t]) { lines.Add($"{pad}  - station {Game.TileName(t)}: you are next to one"); continue; }
                int item = StationItem(t, pool);
                if (item <= 0) { lines.Add($"{pad}  - station {Game.TileName(t)}: none nearby; find one"); continue; }
                lines.Add($"{pad}  - station {Game.TileName(t)}: none nearby; craft places one if you hold a {Game.ItemName(item)}:");
                Need(p, new HashSet<int> { item }, item, 1, depth + 2, pool, lines, raw, stations);
            }
            foreach (Item ing in r.requiredItem.Where(i => !i.IsAir))
                Need(p, Accepted(r, ing), ing.type, ing.stack * batches, depth + 1, pool, lines, raw, stations);
        }

        /// <summary>A recipe that undoes one of its own ingredients' recipes (StoneBlock from StoneWall, Wood from WoodWall).</summary>
        private static bool Reverses(Recipe r, int type) => r.requiredItem.Any(ing => !ing.IsAir &&
            RecipesFor(ing.type).Any(back => back.requiredItem.Any(x => !x.IsAir && x.type == type)));

        /// <summary>An item that places station tile `tile`: one the player holds, else the first craftable one.</summary>
        private static int StationItem(int tile, Dictionary<int, int> pool)
        {
            foreach (var kv in pool)
                if (kv.Value > 0 && ContentSamples.ItemsByType.TryGetValue(kv.Key, out var s) && s.createTile == tile) return kv.Key;
            for (int t = 1; t < ItemLoader.ItemCount; t++)
                if (ContentSamples.ItemsByType.TryGetValue(t, out var s) && s.createTile == tile && RecipesFor(t).Count > 0) return t;
            return -1;
        }

        private static string GatherHint(int type)
        {
            string name = Game.ItemName(type);
            if (name == "Wood") return " (mine_nearest Trees)";
            if (name == "Gel") return " (kill slimes)";
            if (ContentSamples.ItemsByType.TryGetValue(type, out var s) && s.createTile >= 0)
                return s.createTile == TileID.Stone ? " (mine_nearest StoneBlock; dig_down to Stone first if none is in reach)"
                    : $" (mine_nearest {name}{(TileID.Sets.Ore[s.createTile] ? "; ore is underground, dig_down to it" : "")})";
            return "";
        }

        public static string Describe(Recipe r) =>
            string.Join(", ", r.requiredItem.Where(i => !i.IsAir).Select(i => $"{i.stack} {Game.ItemName(i.type)}")) +
            (r.requiredTile.Any(t => t >= 0) ? " @ " + string.Join("+", r.requiredTile.Where(t => t >= 0).Select(Game.TileName)) : "");
    }
}
