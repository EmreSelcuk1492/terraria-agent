using System.Collections.Generic;
using System.Linq;
using Terraria;

namespace TerrariaAgentBridge
{
    public static class Crafting
    {
        public static List<string> MissingStations(Player p, Recipe r)
        {
            // adjTile is refreshed every tick from the stations around the player.
            return r.requiredTile.Where(t => t >= 0 && !p.adjTile[t]).Select(Game.TileName).ToList();
        }

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
