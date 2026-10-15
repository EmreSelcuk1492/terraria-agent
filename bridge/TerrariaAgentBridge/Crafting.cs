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

        /// <summary>Why each of the first few recipes for `type` can't be crafted here.</summary>
        public static string Problems(Player p, int type) => string.Join("; ", RecipesFor(type).Take(3).Select(r =>
        {
            var stations = MissingStations(p, r);
            var ingredients = MissingIngredients(p, r);
            return $"recipe [{Describe(r)}]" +
                (stations.Count > 0 ? $" missing station: {string.Join(", ", stations)}" : "") +
                (ingredients.Count > 0 ? $" missing: {string.Join(", ", ingredients)}" : "");
        }));

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
