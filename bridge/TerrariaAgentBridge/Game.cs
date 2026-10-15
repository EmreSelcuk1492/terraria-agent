using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAgentBridge
{
    /// <summary>Small helpers shared by skills and observations. Coordinates are tile coordinates unless named *Px.</summary>
    public static class Game
    {
        public const int MainInventorySlots = 50; // 0-9 hotbar, 10-49 backpack

        public static int TileX(Player p) => (int)(p.Center.X / 16f);
        public static int TileY(Player p) => (int)(p.Center.Y / 16f);

        // ---- names ---------------------------------------------------------------------

        public static string ItemName(int type) => ItemID.Search.ContainsId(type) ? ItemID.Search.GetName(type) : Lang.GetItemNameValue(type);
        public static string TileName(int type) => TileID.Search.ContainsId(type) ? TileID.Search.GetName(type) : $"Tile{type}";

        /// <summary>Accepts internal names ("CopperBar"), display names ("Copper Bar"), any case.</summary>
        public static int ResolveItem(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            if (ItemID.Search.TryGetId(name, out int id)) return id;
            string squashed = Squash(name);
            for (int t = 1; t < ItemLoader.ItemCount; t++)
            {
                if (Squash(ItemName(t)) == squashed || Squash(Lang.GetItemNameValue(t)) == squashed)
                    return t;
            }
            return -1;
        }

        public static int ResolveTile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            if (TileID.Search.TryGetId(name, out int id)) return id;
            string squashed = Squash(name);
            for (int t = 0; t < TileLoader.TileCount; t++)
                if (Squash(TileName(t)) == squashed) return t;
            // An item name for a placeable thing ("IronOre", "StoneBlock", "Wood") means the tile it places.
            int item = ResolveItem(name);
            if (item > 0 && ContentSamples.ItemsByType.TryGetValue(item, out var sample) && sample.createTile >= 0) return sample.createTile;
            return -1;
        }

        private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // ---- inventory -----------------------------------------------------------------

        public static int CountItem(Player p, int type)
        {
            int n = 0;
            for (int i = 0; i < 58; i++)
                if (p.inventory[i].type == type) n += p.inventory[i].stack;
            return n;
        }

        public static int FindSlot(Player p, Func<Item, bool> match)
        {
            for (int i = 0; i < MainInventorySlots; i++)
                if (!p.inventory[i].IsAir && match(p.inventory[i])) return i;
            return -1;
        }

        public static int BestSlot(Player p, Func<Item, int> score)
        {
            int best = -1, bestScore = 0;
            for (int i = 0; i < MainInventorySlots; i++)
            {
                if (p.inventory[i].IsAir) continue;
                int s = score(p.inventory[i]);
                if (s > bestScore) { best = i; bestScore = s; }
            }
            return best;
        }

        /// <summary>Puts an inventory slot in the player's hand, swapping it into hotbar slot 10 if needed.</summary>
        public static void Hold(Player p, int slot)
        {
            if (slot < 0) return;
            if (slot > 9)
            {
                (p.inventory[9], p.inventory[slot]) = (p.inventory[slot], p.inventory[9]);
                slot = 9;
            }
            p.selectedItem = slot;
        }

        /// <summary>
        /// How close (in tiles) the weapon has to be to its target. Shortswords "shoot" a 1-2 tile stab projectile, so
        /// item.shoot > 0 doesn't mean ranged: treating it as 14 tiles kept every fight at arm's length plus 12, with
        /// zero kills in runs 1-5 and the first scenario suite.
        /// </summary>
        public static float WeaponReach(Item w)
        {
            if (w.useStyle == ItemUseStyleID.Rapier) return 1.8f;                      // shortswords
            if (w.DamageType == DamageClass.Melee || w.DamageType == DamageClass.MeleeNoSpeed)
            {
                if (ItemID.Sets.Yoyo[w.type]) return 7f;
                if (w.shoot <= 0 || !w.noMelee && w.useStyle == ItemUseStyleID.Swing) return 2.5f; // swung blades (beam ones too)
                if (w.noMelee && w.useStyle == ItemUseStyleID.Shoot) return 3.5f;      // spears
                return 8f;                                                               // boomerangs, flails
            }
            return 14f;                                                                  // ranged, magic, thrown, summons
        }

        public static void ConsumeOne(Player p, int slot)
        {
            Item item = p.inventory[slot];
            item.stack--;
            if (item.stack <= 0) item.TurnToAir();
        }

        // ---- world ---------------------------------------------------------------------

        public static bool InWorld(int x, int y) => x > 1 && y > 1 && x < Main.maxTilesX - 2 && y < Main.maxTilesY - 2;

        public static bool Solid(int x, int y)
        {
            if (!InWorld(x, y)) return true;
            Tile t = Framing.GetTileSafely(x, y);
            return t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType] && !t.IsActuated;
        }

        public static bool Platform(int x, int y)
        {
            Tile t = Framing.GetTileSafely(x, y);
            return t.HasTile && TileID.Sets.Platforms[t.TileType];
        }

        public static bool Lava(int x, int y)
        {
            Tile t = Framing.GetTileSafely(x, y);
            return t.LiquidAmount > 0 && t.LiquidType == LiquidID.Lava;
        }

        /// <summary>Vanilla tile reach (5 x 4 tiles) plus the held item's tileBoost.</summary>
        public static bool InReach(Player p, int x, int y, int boost = 0)
        {
            int rx = Player.tileRangeX + boost + p.blockRange;
            int ry = Player.tileRangeY + boost + p.blockRange;
            float left = p.position.X / 16f - rx, right = (p.position.X + p.width) / 16f + rx - 1;
            float top = p.position.Y / 16f - ry, bottom = (p.position.Y + p.height) / 16f + ry - 2;
            return x >= left && x <= right && y >= top && y <= bottom;
        }

        // Minimum pickaxe power for vanilla tiles that need more than a starter pick.
        private static readonly Dictionary<int, int> MinPick = new()
        {
            [TileID.Meteorite] = 50,
            [TileID.Demonite] = 55, [TileID.Crimtane] = 55, [TileID.Obsidian] = 55,
            [TileID.Ebonstone] = 65, [TileID.Crimstone] = 65, [TileID.Pearlstone] = 65,
            [TileID.Hellstone] = 65,
            [TileID.BlueDungeonBrick] = 100, [TileID.GreenDungeonBrick] = 100, [TileID.PinkDungeonBrick] = 100,
            [TileID.Cobalt] = 100, [TileID.Palladium] = 100,
            [TileID.Mythril] = 110, [TileID.Orichalcum] = 110,
            [TileID.Adamantite] = 150, [TileID.Titanium] = 150,
            [TileID.Chlorophyte] = 200,
            [TileID.LihzahrdBrick] = 210,
        };

        public static int RequiredPick(int tileType) => MinPick.TryGetValue(tileType, out int v) ? v : 0;

        public static bool IsTree(int tileType) => tileType == TileID.Trees || tileType == TileID.PalmTree;

        /// <summary>Bottom trunk tile: tree, nothing tree-like below, more trunk above. Root tiles beside the trunk fail the last check.</summary>
        public static bool IsTreeBase(int x, int y)
        {
            Tile t = Framing.GetTileSafely(x, y), below = Framing.GetTileSafely(x, y + 1), above = Framing.GetTileSafely(x, y - 1);
            return t.HasTile && IsTree(t.TileType)
                && !(below.HasTile && IsTree(below.TileType))
                && above.HasTile && IsTree(above.TileType);
        }
    }
}
