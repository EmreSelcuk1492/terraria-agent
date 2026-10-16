using System.Collections.Generic;
using System.Linq;
using Terraria;

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

        public static string Describe(Recipe r) =>
            string.Join(", ", r.requiredItem.Where(i => !i.IsAir).Select(i => $"{i.stack} {Game.ItemName(i.type)}")) +
            (r.requiredTile.Any(t => t >= 0) ? " @ " + string.Join("+", r.requiredTile.Where(t => t >= 0).Select(Game.TileName)) : "");
    }
}
