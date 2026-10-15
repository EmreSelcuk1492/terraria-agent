using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.Enums;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAgentBridge.Skills
{
    // Gear and base upkeep: wear the best armor, loot world chests, stash loot at base, set spawn at a bed.

    /// <summary>
    /// Wear the best armor per slot, fill empty accessory slots, and make sure the best weapon, pickaxe and axe
    /// sit in the hotbar. Finishes in one tick.
    /// </summary>
    public class EquipBest : Skill
    {
        public override string Name => "equip_best";
        public override int TimeoutTicks => 60;
        public override bool InterruptOnDamage => false;

        private static readonly string[] ArmorSlotNames = { "head", "body", "legs" };

        public override void Tick(Player p)
        {
            var changes = new List<string>();

            // Armor: p.armor[0..2] = head, body, legs.
            for (int k = 0; k < 3; k++)
            {
                Item worn = p.armor[k];
                int best = -1, bestDef = worn.IsAir ? -1 : worn.defense;
                for (int i = 0; i < Game.MainInventorySlots; i++)
                {
                    Item it = p.inventory[i];
                    if (it.IsAir || it.vanity || !FitsArmorSlot(it, k)) continue;
                    if (it.defense > bestDef) { best = i; bestDef = it.defense; }
                }
                if (best < 0) continue;
                string old = worn.IsAir ? "" : $" (was {Game.ItemName(worn.type)})";
                (p.armor[k], p.inventory[best]) = (p.inventory[best], p.armor[k]);
                changes.Add($"equipped {Game.ItemName(p.armor[k].type)} on {ArmorSlotNames[k]}{old}");
            }

            // Accessories: only into empty slots, never a duplicate type or a second pair of wings.
            for (int a = 3; a < 10; a++)
            {
                if (a > 7 && !p.IsItemSlotUnlockedAndUsable(a)) continue;
                if (!p.armor[a].IsAir) continue;
                int pick = Game.FindSlot(p, it => it.accessory && !it.vanity && !AccessoryClash(p, it));
                if (pick < 0) break;
                p.armor[a] = p.inventory[pick];
                p.inventory[pick] = new Item();
                changes.Add($"equipped accessory {Game.ItemName(p.armor[a].type)}");
            }

            // Hotbar: best weapon, pickaxe and axe within slots 0-9.
            int weapon = Game.BestSlot(p, it => WeaponScore(p, it));
            int pickaxe = Game.BestSlot(p, it => it.pick);
            int axe = Game.BestSlot(p, it => it.axe);
            var wanted = new List<(int slot, string role)>();
            if (weapon >= 0) wanted.Add((weapon, "weapon"));
            if (pickaxe >= 0 && pickaxe != weapon) wanted.Add((pickaxe, "pickaxe"));
            if (axe >= 0 && axe != weapon && axe != pickaxe) wanted.Add((axe, "axe"));
            var keepTypes = new HashSet<int>(wanted.Select(w => p.inventory[w.slot].type));

            foreach (var (slot, role) in wanted)
            {
                if (slot <= 9) continue;
                int target = HotbarVictim(p, keepTypes);
                if (target < 0) continue; // hotbar holds only the wanted tools (cannot happen with 10 slots and 3 wants)
                string bumped = p.inventory[target].IsAir ? "" : $", moved {Game.ItemName(p.inventory[target].type)} to slot {slot}";
                (p.inventory[target], p.inventory[slot]) = (p.inventory[slot], p.inventory[target]);
                changes.Add($"put {role} {Game.ItemName(p.inventory[target].type)} in hotbar slot {target}{bumped}");
            }

            string gear = Summary(p);
            if (changes.Count == 0) Succeed($"already wearing the best gear: {gear}");
            else Succeed($"{string.Join("; ", changes)}. Now: {gear}");
        }

        private static bool FitsArmorSlot(Item it, int slot) => slot switch
        {
            0 => it.headSlot >= 0,
            1 => it.bodySlot >= 0,
            _ => it.legSlot >= 0,
        };

        private static bool AccessoryClash(Player p, Item it)
        {
            for (int a = 3; a < 10; a++)
            {
                Item e = p.armor[a];
                if (e.IsAir) continue;
                if (e.type == it.type) return true;
                if (e.wingSlot > 0 && it.wingSlot > 0) return true;
            }
            return false;
        }

        /// <summary>Melee or ranged weapon damage; 0 for tools, ammo, consumables, and ranged weapons with no ammo.</summary>
        public static int WeaponScore(Player p, Item it)
        {
            if (it.damage <= 0 || it.pick > 0 || it.axe > 0 || it.hammer > 0 || it.ammo > 0 || it.consumable) return 0;
            if (!it.CountsAsClass(DamageClass.Melee) && !it.CountsAsClass(DamageClass.Ranged)) return 0;
            if (it.useAmmo > 0 && !p.HasAmmo(it)) return 0;
            return it.damage;
        }

        /// <summary>The hotbar slot to give up: empty first, then plain blocks/materials, never a wanted tool. Prefers the right end.</summary>
        private static int HotbarVictim(Player p, HashSet<int> keepTypes)
        {
            int best = -1, bestCost = int.MaxValue;
            for (int i = 9; i >= 0; i--)
            {
                Item it = p.inventory[i];
                int cost;
                if (it.IsAir) cost = 0;
                else if (keepTypes.Contains(it.type)) continue;
                else if (it.damage > 0 || it.pick > 0 || it.axe > 0 || it.hammer > 0 || it.createTile == TileID.Torches || it.healLife > 0) cost = 3;
                else if (it.createTile >= 0 || it.createWall > 0 || it.useStyle == 0) cost = 1;
                else cost = 2;
                if (cost < bestCost) { best = i; bestCost = cost; }
            }
            return best;
        }

        private static string Summary(Player p)
        {
            string Worn(int k) => p.armor[k].IsAir ? "none" : Game.ItemName(p.armor[k].type);
            var acc = Enumerable.Range(3, 7).Where(a => !p.armor[a].IsAir).Select(a => Game.ItemName(p.armor[a].type)).ToList();
            // statDefense is only recomputed next tick, so sum the equipped pieces (set bonuses not included).
            int defense = Enumerable.Range(0, 10).Sum(a => p.armor[a].IsAir ? 0 : p.armor[a].defense);
            var hot = new List<string>();
            int w = BestHotbar(p, it => WeaponScore(p, it)), pk = BestHotbar(p, it => it.pick), ax = BestHotbar(p, it => it.axe);
            if (w >= 0) hot.Add($"{Game.ItemName(p.inventory[w].type)} (damage {p.inventory[w].damage}, slot {w})");
            if (pk >= 0) hot.Add($"{Game.ItemName(p.inventory[pk].type)} (pick {p.inventory[pk].pick}, slot {pk})");
            if (ax >= 0 && ax != pk) hot.Add($"{Game.ItemName(p.inventory[ax].type)} (axe {p.inventory[ax].axe * 5}, slot {ax})");
            string accText = acc.Count > 0 ? string.Join(", ", acc) : "none";
            string hotText = hot.Count > 0 ? string.Join(", ", hot) : "no weapon or tools";
            return $"head {Worn(0)}, body {Worn(1)}, legs {Worn(2)}; accessories {accText}; defense {defense}; hotbar {hotText}";
        }

        private static int BestHotbar(Player p, Func<Item, int> score)
        {
            int best = -1, bestScore = 0;
            for (int i = 0; i < 10; i++)
            {
                if (p.inventory[i].IsAir) continue;
                int s = score(p.inventory[i]);
                if (s > bestScore) { best = i; bestScore = s; }
            }
            return best;
        }
    }

    /// <summary>
    /// Walk to unlooted world chests nearby (nearest first) and take everything that fits.
    /// Chests near the world spawn are treated as the player's own storage and left alone.
    /// </summary>
    public class LootChests : Skill
    {
        public override string Name => "loot_chests";
        public override int TimeoutTicks => 60 * 400;

        /// <summary>Chests emptied by this skill, by top-left tile; persists across calls.</summary>
        private static readonly HashSet<(int x, int y)> Looted = new();
        private const int BaseRadius = 25;

        private readonly int radius, max;
        private Queue<int> todo;
        private int current = -1, cx, cy;
        private MoveTo walk;
        private readonly List<string> report = new(), skipped = new();
        private readonly Dictionary<int, int> totals = new();
        private int visited;

        public LootChests(int radius = 60, int max = 10)
        {
            this.radius = Math.Clamp(radius, 1, 400);
            this.max = Math.Clamp(max, 1, 50);
        }

        public static bool IsBaseChest(int x, int y) =>
            Math.Abs(x - Main.spawnTileX) <= BaseRadius && Math.Abs(y - Main.spawnTileY) <= BaseRadius;

        private static float Dist(Player p, Chest c) =>
            (float)Math.Sqrt(Math.Pow(c.x + 1 - Game.TileX(p), 2) + Math.Pow(c.y + 1 - Game.TileY(p), 2));

        public override void Tick(Player p)
        {
            if (todo == null && !Plan(p)) return;

            if (current < 0)
            {
                if (todo.Count == 0 || visited >= max) { Finish(); return; }
                current = todo.Dequeue();
                Chest c = Main.chest[current];
                if (c == null) { current = -1; return; }
                (cx, cy) = (c.x, c.y);
                walk = null;
            }

            Chest chest = Main.chest[current];
            if (chest == null || chest.x != cx || chest.y != cy) { skipped.Add($"({cx},{cy}) is gone"); current = -1; return; }

            if (!InReach(p))
            {
                walk ??= new MoveTo(cx, cy, 2);
                if (Step(walk, p)) return;
                if (!InReach(p))
                {
                    string why = walk.Ok ? "arrived but still out of reach" : walk.Message;
                    skipped.Add($"({cx},{cy}) {(int)Dist(p, chest)} tiles away: {why}");
                    current = -1;
                    return;
                }
            }

            visited++;
            bool full = Take(p, chest);
            current = -1;
            if (full)
            {
                todo.Clear();
                skipped.Add("stopped: inventory is full (store_items at base, or drop junk)");
            }
        }

        private bool InReach(Player p) => Game.InReach(p, cx, cy) || Game.InReach(p, cx + 1, cy + 1);

        /// <summary>Queue chests in radius, nearest first. Fails with the nearest chest outside the radius when none qualify.</summary>
        private bool Plan(Player p)
        {
            var inRange = new List<(int idx, float d)>();
            float nearestOut = float.MaxValue;
            Chest outChest = null;
            int baseCount = 0, lootedCount = 0, lockedCount = 0;
            for (int i = 0; i < Main.maxChests; i++)
            {
                Chest c = Main.chest[i];
                if (c == null || !Game.InWorld(c.x, c.y)) continue;
                if (IsBaseChest(c.x, c.y)) { baseCount++; continue; }
                if (Looted.Contains((c.x, c.y)) || c.item.All(it => it == null || it.IsAir))
                {
                    Looted.Add((c.x, c.y));
                    lootedCount++;
                    continue;
                }
                if (Chest.IsLocked(c.x, c.y)) { lockedCount++; continue; }
                float d = Dist(p, c);
                if (d <= radius) inRange.Add((i, d));
                else if (d < nearestOut) { nearestOut = d; outChest = c; }
            }
            if (inRange.Count == 0)
            {
                string near = outChest != null
                    ? $" Nearest unlooted chest is at ({outChest.x},{outChest.y}), {(int)nearestOut} tiles away; walk toward it or raise radius."
                    : " No unlooted chests are known anywhere; explore caves (chests are mostly underground).";
                Fail($"no unlooted chests within {radius} tiles (skipped {baseCount} base chests near spawn, {lootedCount} empty/looted, {lockedCount} locked).{near}");
                return false;
            }
            todo = new Queue<int>(inRange.OrderBy(c => c.d).Select(c => c.idx));
            return true;
        }

        /// <summary>Moves chest items into the inventory. Returns true when something did not fit.</summary>
        private bool Take(Player p, Chest chest)
        {
            var got = new Dictionary<int, int>();
            bool full = false;
            for (int s = 0; s < chest.item.Length; s++)
            {
                Item it = chest.item[s];
                if (it == null || it.IsAir) continue;
                Item give = it.Clone();
                give.noGrabDelay = 0;
                int before = give.stack;
                Item left = p.GetItem(p.whoAmI, give, GetItemSettings.InventoryEntityToPlayerInventorySettings);
                int remaining = left == null || left.IsAir ? 0 : left.stack;
                int taken = before - remaining;
                if (taken > 0)
                {
                    got[it.type] = got.GetValueOrDefault(it.type) + taken;
                    totals[it.type] = totals.GetValueOrDefault(it.type) + taken;
                }
                if (remaining <= 0) chest.item[s] = new Item();
                else { it.stack = remaining; full = true; }
            }
            if (!full) Looted.Add((chest.x, chest.y));
            string what = got.Count > 0 ? string.Join(", ", got.Select(g => $"{Game.ItemName(g.Key)} x{g.Value}")) : "nothing (inventory full)";
            report.Add($"chest ({chest.x},{chest.y}): {what}{(full ? " [some left inside]" : "")}");
            return full;
        }

        private void Finish()
        {
            string skip = skipped.Count > 0 ? " Skipped: " + string.Join("; ", skipped) + "." : "";
            if (totals.Count == 0)
            {
                Fail($"looted nothing.{skip} Move closer to a chest (or dig to it) and try again.");
                return;
            }
            string total = string.Join(", ", totals.OrderByDescending(t => t.Value).Select(t => $"{Game.ItemName(t.Key)} x{t.Value}"));
            Succeed($"looted {report.Count} chest(s). {string.Join("; ", report)}. Totals: {total}.{skip}");
        }
    }

    /// <summary>
    /// Store loot and spare materials in chests within 10 tiles (the base chest), placing a Chest from the
    /// inventory if there is none. Keeps the hotbar, coins, tools, weapons, armor, accessories, torches,
    /// potions, Gel, Wood, StoneBlock, ores, bars, favorited items and anything in `keep`.
    /// </summary>
    public class StoreItems : Skill
    {
        public override string Name => "store_items";
        public override int TimeoutTicks => 60 * 60;

        private const int Range = 10;
        private readonly List<string> keepNames;
        private HashSet<int> keepTypes;
        private readonly List<string> unknownKeep = new();
        private List<int> chests;
        private MoveTo walk;
        private string placedNote = "";

        /// <summary>`keep` entries may themselves be comma-separated lists.</summary>
        public StoreItems(IEnumerable<string> keep = null)
        {
            keepNames = (keep ?? Enumerable.Empty<string>())
                .SelectMany(k => (k ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
        }

        public StoreItems(string keepCsv) : this(new[] { keepCsv }) { }

        public override void Tick(Player p)
        {
            if (keepTypes == null)
            {
                keepTypes = new HashSet<int>();
                foreach (string n in keepNames)
                {
                    int t = Game.ResolveItem(n);
                    if (t > 0) keepTypes.Add(t); else unknownKeep.Add(n);
                }
            }

            if (chests == null)
            {
                chests = NearbyChests(p);
                if (chests.Count == 0)
                {
                    int idx = PlaceChest(p);
                    if (idx < 0) return; // Fail already called
                    chests.Add(idx);
                }
            }

            // Stand within reach of the nearest chest, like opening it.
            Chest first = Main.chest[chests[0]];
            if (first == null) { Fail("the base chest disappeared; call store_items again"); return; }
            if (!Game.InReach(p, first.x, first.y) && !Game.InReach(p, first.x + 1, first.y + 1))
            {
                walk ??= new MoveTo(first.x, first.y, 2);
                if (Step(walk, p)) return;
                if (!Game.InReach(p, first.x, first.y) && !Game.InReach(p, first.x + 1, first.y + 1))
                {
                    Fail($"could not reach the chest at ({first.x},{first.y}): {walk.Message}");
                    return;
                }
            }

            Deposit(p);
        }

        /// <summary>Unlocked chests within 10 tiles, nearest first.</summary>
        private static List<int> NearbyChests(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            var found = new List<(int idx, int d)>();
            for (int i = 0; i < Main.maxChests; i++)
            {
                Chest c = Main.chest[i];
                if (c == null || Chest.IsLocked(c.x, c.y)) continue;
                int dx = c.x + 1 - px, dy = c.y + 1 - py;
                if (dx * dx + dy * dy <= Range * Range) found.Add((i, dx * dx + dy * dy));
            }
            return found.OrderBy(f => f.d).Select(f => f.idx).ToList();
        }

        /// <summary>Places a chest item on the floor beside the player. Returns the chest index, or -1 after Fail.</summary>
        private int PlaceChest(Player p)
        {
            int slot = Game.FindSlot(p, it => it.createTile == TileID.Containers || it.createTile == TileID.Containers2);
            if (slot < 0)
            {
                Fail($"no chest within {Range} tiles and no Chest in inventory. Craft one (`craft` Chest: 8 Wood + 2 IronBar or LeadBar at a WorkBench), or walk to the base chest first.");
                return -1;
            }
            Item item = p.inventory[slot];
            int px = Game.TileX(p), feet = Game.TileY(p) + 1; // bottom row of the player's body
            foreach (int dx in new[] { 2, -3, 3, -4, 4, -5 })
            foreach (int dy in new[] { 0, -1, 1 })
            {
                int ax = px + dx, ay = feet + dy; // PlaceChest takes the bottom-left tile
                if (!Game.InWorld(ax, ay)) continue;
                int idx = WorldGen.PlaceChest(ax, ay, (ushort)item.createTile, false, item.placeStyle);
                if (idx < 0 || Main.chest[idx] == null) continue;
                placedNote = $"placed a {Game.ItemName(item.type)} at ({Main.chest[idx].x},{Main.chest[idx].y}). ";
                Game.ConsumeOne(p, slot);
                return idx;
            }
            Fail($"no chest within {Range} tiles, and found no flat 2-tile spot beside you to place your {Game.ItemName(item.type)}; move to flat ground");
            return -1;
        }

        private bool NeverStore(Item it) =>
            it.favorited || it.IsACoin || keepTypes.Contains(it.type)
            || it.pick > 0 || it.axe > 0 || it.hammer > 0 || it.damage > 0
            || it.headSlot >= 0 || it.bodySlot >= 0 || it.legSlot >= 0 || it.accessory
            || it.createTile == TileID.Torches || it.healLife > 0
            || it.type is ItemID.Gel or ItemID.Wood or ItemID.StoneBlock
            || it.createTile == TileID.MetalBars
            || (it.createTile >= 0 && TileID.Sets.Ore[it.createTile]);

        private void Deposit(Player p)
        {
            var stored = new Dictionary<int, int>();
            var noRoom = new List<string>();
            int kept = 0;
            for (int i = 10; i < Game.MainInventorySlots; i++)
            {
                Item it = p.inventory[i];
                if (it.IsAir) continue;
                if (NeverStore(it)) { kept++; continue; }
                int type = it.type, before = it.stack;

                // Top up existing stacks first (quick-stack), then use empty slots.
                foreach (int ci in chests)
                {
                    Chest c = Main.chest[ci];
                    if (c == null) continue;
                    for (int s = 0; s < c.item.Length && it.stack > 0; s++)
                    {
                        Item dst = c.item[s];
                        if (dst == null || dst.IsAir || dst.type != type || dst.stack >= dst.maxStack) continue;
                        int move = Math.Min(it.stack, dst.maxStack - dst.stack);
                        dst.stack += move;
                        it.stack -= move;
                    }
                }
                if (it.stack > 0)
                {
                    foreach (int ci in chests)
                    {
                        Chest c = Main.chest[ci];
                        if (c == null) continue;
                        int s = Array.FindIndex(c.item, x => x == null || x.IsAir);
                        if (s < 0) continue;
                        c.item[s] = it;
                        p.inventory[i] = new Item();
                        break;
                    }
                }
                if (p.inventory[i].stack <= 0) p.inventory[i] = new Item(); // fully merged into existing stacks
                int left = p.inventory[i].IsAir ? 0 : p.inventory[i].stack;
                if (before - left > 0) stored[type] = stored.GetValueOrDefault(type) + before - left;
                if (left > 0) noRoom.Add($"{Game.ItemName(type)} x{left}");
            }

            string where = string.Join(", ", chests.Where(ci => Main.chest[ci] != null).Select(ci => $"({Main.chest[ci].x},{Main.chest[ci].y})"));
            string unknown = unknownKeep.Count > 0 ? $" Unknown keep names ignored: {string.Join(", ", unknownKeep)}." : "";
            string full = noRoom.Count > 0 ? $" Chests full, still carrying: {string.Join(", ", noRoom)}. Place another Chest nearby." : "";
            if (stored.Count == 0)
            {
                if (noRoom.Count > 0) Fail($"{placedNote}stored nothing: chest(s) at {where} are full.{full}{unknown}");
                else Succeed($"{placedNote}nothing to store; {kept} backpack stacks are kept items (tools, gear, torches, potions, Gel, Wood, StoneBlock, ores, bars, keep list) and the hotbar is never stored.{unknown}");
                return;
            }
            string list = string.Join(", ", stored.OrderByDescending(s => s.Value).Select(s => $"{Game.ItemName(s.Key)} x{s.Value}"));
            Succeed($"{placedNote}stored into chest(s) at {where}: {list}. Kept {kept} backpack stacks plus the hotbar.{full}{unknown}");
        }
    }

    /// <summary>
    /// Set the spawn point at a Bed within 30 tiles, the way right-clicking it does; places a Bed from the
    /// inventory first if none is placed. Vanilla only accepts a bed inside a valid room.
    /// </summary>
    public class SetSpawn : Skill
    {
        public override string Name => "set_spawn";
        public override int TimeoutTicks => 60 * 45;

        private const int Radius = 30;
        private (int x, int y)? bed;
        private MoveTo walk;
        private bool placed;

        public override void Tick(Player p)
        {
            if (bed == null)
            {
                bed = FindBed(p);
                if (bed == null)
                {
                    if (placed) { Fail("placed a Bed but can't find it again; call set_spawn once more"); return; }
                    int slot = Game.FindSlot(p, it => it.createTile == TileID.Beds);
                    if (slot < 0)
                    {
                        Fail($"no Bed within {Radius} tiles or in inventory: craft one at a Sawmill: 15 Wood + 5 Silk (`obtain` Bed), then place it inside your house");
                        return;
                    }
                    if (!PlaceBed(p, slot))
                    {
                        Fail($"have a {Game.ItemName(p.inventory[slot].type)} but found no flat 4-tile floor beside you to place it; stand inside the house on its floor and retry");
                        return;
                    }
                    placed = true;
                    return; // find it next tick
                }
            }

            var (bx, by) = bed.Value;
            Tile t = Framing.GetTileSafely(bx, by);
            if (!t.HasTile || t.TileType != TileID.Beds) { bed = null; return; }

            // Vanilla Player.TileInteractionsUse for beds: spawn = (left + 2 | +1 when facing right, row below the bed).
            int col = t.TileFrameX / 18;
            int sx = bx - col + (t.TileFrameX >= 72 ? 5 : 2);
            int sy = by - (t.TileFrameY / 18) % 2 + 2;

            if (!Game.InReach(p, bx, by))
            {
                walk ??= new MoveTo(sx, sy - 1, 2);
                if (Step(walk, p)) return;
                if (!Game.InReach(p, bx, by)) { Fail($"could not get within reach of the Bed at ({bx},{by}): {walk.Message}"); return; }
            }

            string where = placed ? "placed Bed, " : "";
            p.FindSpawn();
            if (p.SpawnX == sx && p.SpawnY == sy) { Succeed($"{where}spawn point is already this Bed at ({sx},{sy})"); return; }
            if (Player.CheckSpawn(sx, sy))
            {
                p.ChangeSpawn(sx, sy);
                Main.NewText(Terraria.Localization.Language.GetTextValue("Game.SpawnPointSet"), 255, 240, 20);
                Succeed($"{where}spawn point set at the Bed ({sx},{sy}); you respawn here instead of world spawn ({Main.spawnTileX},{Main.spawnTileY})");
                return;
            }
            Fail($"{where}Bed at ({sx},{sy}) refused to set spawn: {WhyNot(sx, sy)}");
        }

        /// <summary>Nearest bed tile within 30 tiles.</summary>
        private static (int x, int y)? FindBed(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            (int, int)? best = null;
            int bestD = int.MaxValue;
            for (int x = px - Radius; x <= px + Radius; x++)
            for (int y = py - Radius; y <= py + Radius; y++)
            {
                if (!Game.InWorld(x, y)) continue;
                Tile t = Framing.GetTileSafely(x, y);
                if (!t.HasTile || t.TileType != TileID.Beds) continue;
                int d = (x - px) * (x - px) + (y - py) * (y - py);
                if (d < bestD) { bestD = d; best = (x, y); }
            }
            return best;
        }

        private static bool PlaceBed(Player p, int slot)
        {
            Item item = p.inventory[slot];
            int px = Game.TileX(p), feet = Game.TileY(p) + 1;
            foreach (int dx in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4 })
            foreach (int dy in new[] { 0, -1, 1 })
            {
                int ax = px + dx, ay = feet + dy;
                if (!Game.InWorld(ax, ay) || Framing.GetTileSafely(ax, ay).HasTile) continue;
                if (BedNear(ax, ay)) continue;
                if (!WorldGen.PlaceTile(ax, ay, item.createTile, mute: false, forced: false, plr: p.whoAmI, style: item.placeStyle)) continue;
                if (!BedNear(ax, ay)) continue;
                Game.ConsumeOne(p, slot);
                return true;
            }
            return false;
        }

        private static bool BedNear(int cx, int cy)
        {
            for (int j = cy - 2; j <= cy + 2; j++)
            for (int i = cx - 3; i <= cx + 3; i++)
            {
                Tile t = Framing.GetTileSafely(i, j);
                if (t.HasTile && t.TileType == TileID.Beds) return true;
            }
            return false;
        }

        /// <summary>Explains a failed Player.CheckSpawn using the same checks vanilla ran.</summary>
        private static string WhyNot(int x, int y)
        {
            for (int i = x - 1; i <= x + 1; i++)
            for (int j = y - 3; j < y; j++)
                if (Game.Solid(i, j))
                    return $"bed is obstructed by a solid block at ({i},{j}); mine it";
            return WorldGen.roomCheckFailureReason switch
            {
                TownNPCRoomCheckFailureReason.RoomIsTooSmall => "the room is too small (needs at least 60 tiles inside, e.g. 10x6 outer walls); enlarge it",
                TownNPCRoomCheckFailureReason.RoomIsTooBig => "the room is too big or not closed (over 750 tiles); close off the room",
                TownNPCRoomCheckFailureReason.HoleInWallIsTooBig => "the room has a hole in its walls; close it with blocks or a door",
                TownNPCRoomCheckFailureReason.TooCloseToWorldEdge => "too close to the world edge",
                _ => "the bed is not inside a valid room (closed by blocks/doors/platforms with background walls everywhere inside); run check_house on a tile in the room to see what is missing, or move the bed into your house",
            };
        }
    }
}
