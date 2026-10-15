using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaAgentBridge.Skills
{
    // Macro skills: whole sub-goals that run inside the game with no model round trips.
    // Every model turn costs seconds (local models: tens of seconds); every tick in here costs 16ms.
    // So anything with a known recipe ("get 32 WoodWall") is planned here, not by the model.

    /// <summary>
    /// Get next to a crafting station of `tileType`: walk to a placed one nearby, place one from the inventory,
    /// or (gather=true) obtain the station item first. Ends with Player.adjTile set for it.
    /// </summary>
    public class EnsureStation : Skill
    {
        public override string Name => "ensure_station";
        public override int TimeoutTicks => 60 * 300;
        private readonly int tileType, depth;
        private readonly bool gather;
        private Skill child;
        private bool walked;
        private int steps;

        public EnsureStation(int tileType, bool gather, int depth = 0) { this.tileType = tileType; this.gather = gather; this.depth = depth; }

        public override void Tick(Player p)
        {
            if (child != null)
            {
                if (Step(child, p)) return;
                if (!child.Ok && child is not MoveTo) { Fail(child.Message); return; }
                child = null;
            }
            Crafting.RefreshStations(p);
            string name = Game.TileName(tileType);
            if (p.adjTile[tileType]) { Succeed($"next to {name}"); return; }
            if (++steps > 8) { Fail($"could not get next to a {name}"); return; }

            if (!walked)
            {
                walked = true;
                var placed = Nearest(p, tileType);
                if (placed != null) { child = new MoveTo(placed.Value.x, placed.Value.y, 1); return; }
            }

            int slot = Game.FindSlot(p, i => i.createTile == tileType);
            if (slot >= 0)
            {
                if (PlaceNear(p, slot)) return; // adjTile is checked next tick
                Fail($"have a {Game.ItemName(p.inventory[slot].type)} but found no flat 3-tile spot next to you to place it; move to flat ground");
                return;
            }

            int item = StationItem(tileType);
            if (item < 0) { Fail($"no {name} nearby and no known item that places one"); return; }
            if (gather) { child = new Obtain(item, Game.CountItem(p, item) + 1, depth + 1); return; }
            if (Crafting.CraftNow(p, item, 1) > 0) return; // e.g. a WorkBench from Wood on the spot
            Fail($"no {name} nearby or in inventory; {Crafting.HowToGet(item)}. `obtain` {Game.ItemName(item)} gathers and crafts it.");
        }

        public static (int x, int y)? Nearest(Player p, int tileType)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            (int, int)? best = null;
            int bestD = int.MaxValue;
            for (int x = px - Observation.ScanRadiusX; x <= px + Observation.ScanRadiusX; x++)
            for (int y = py - 20; y <= py + 20; y++)
            {
                if (!Game.InWorld(x, y)) continue;
                Tile t = Framing.GetTileSafely(x, y);
                if (!t.HasTile || t.TileType != tileType) continue;
                int d = Math.Abs(x - px) + Math.Abs(y - py) * 3;
                if (d < bestD) { bestD = d; best = (x, y); }
            }
            return best;
        }

        /// <summary>Places the station from `slot` on the floor beside the player.</summary>
        public static bool PlaceNear(Player p, int slot)
        {
            Item item = p.inventory[slot];
            int px = Game.TileX(p), feet = Game.TileY(p) + 1;
            foreach (int dx in new[] { 2, -2, 3, -3, 4, -4, 1, -1 })
            foreach (int dy in new[] { 0, -1, 1 })
            {
                int ax = px + dx, ay = feet + dy;
                if (Framing.GetTileSafely(ax, ay).HasTile) continue;
                bool before = Nearby(item.createTile, ax, ay);
                if (!WorldGen.PlaceTile(ax, ay, item.createTile, mute: false, forced: false, plr: p.whoAmI, style: item.placeStyle)) continue;
                if (before || !Nearby(item.createTile, ax, ay)) continue;
                Game.ConsumeOne(p, slot);
                return true;
            }
            return false;
        }

        private static bool Nearby(int tileType, int cx, int cy)
        {
            for (int j = cy - 2; j <= cy + 2; j++)
            for (int i = cx - 2; i <= cx + 2; i++)
            {
                Tile t = Framing.GetTileSafely(i, j);
                if (t.HasTile && t.TileType == tileType) return true;
            }
            return false;
        }

        private static readonly Dictionary<int, int> stationItems = new();

        /// <summary>The craftable item that places this tile (WorkBenches -> WorkBench, Anvils -> IronAnvil).</summary>
        public static int StationItem(int tileType)
        {
            if (stationItems.TryGetValue(tileType, out int cached)) return cached;
            int found = -1;
            for (int i = 1; i < ItemLoader.ItemCount && found < 0; i++)
                if (ContentSamples.ItemsByType.TryGetValue(i, out Item s) && s.createTile == tileType && Crafting.RecipesFor(i).Count > 0)
                    found = i;
            return stationItems[tileType] = found;
        }
    }

    /// <summary>
    /// Have at least `count` of an item: gathers raw materials (trees, ores, blocks, slime gel), crafts the
    /// intermediates, and gets to the stations each recipe needs, recursively.
    /// </summary>
    public class Obtain : Skill
    {
        public override string Name => "obtain";
        public override int TimeoutTicks => 60 * 600;
        private readonly int type = -1, count, depth;
        private readonly string itemName;
        private readonly HashSet<int> chain; // this item and every item above it in the recipe chain
        private Skill child;
        private int steps, stalls, lastHave = -1, digs;

        public Obtain(string item, int count) : this(Game.ResolveItem(item), count, 0) { itemName = item; }

        public Obtain(int type, int count, int depth, IEnumerable<int> parents = null)
        {
            this.type = type;
            this.count = Math.Clamp(count, 1, 999);
            this.depth = depth;
            chain = new HashSet<int>(parents ?? Enumerable.Empty<int>()) { type };
        }

        private string ItemLabel => type >= 0 ? Game.ItemName(type) : itemName;

        public override void Tick(Player p)
        {
            if (type < 0) { Fail($"unknown item '{itemName}'"); return; }
            if (child != null)
            {
                if (Step(child, p)) return;
                if (!child.Ok)
                {
                    // Nothing of it in view (Stone under a dirt surface): dig down for it, then look again.
                    int tile = ContentSamples.ItemsByType[type].createTile;
                    if (child is MineNearest && tile >= 0 && !Game.IsTree(tile) && digs < 3)
                    {
                        digs++;
                        child = new DigDown(tile);
                        return;
                    }
                    Fail($"getting {count} {ItemLabel}: {child.Message}");
                    return;
                }
                child = null;
            }

            int have = Game.CountItem(p, type);
            if (have >= count) { Succeed($"have {have} {ItemLabel}"); return; }
            if (depth > 5) { Fail($"recipe chain for {ItemLabel} is too deep"); return; }
            if (++steps > 80) { Fail($"gave up on {ItemLabel} after {steps} steps (have {have}/{count})"); return; }

            // Reverse crafts loop: StoneBlock <- 4 StoneWall <- 1 StoneBlock. Only keep recipes whose ingredients
            // can be had without going back through this chain; with none left, mine or chop the item itself.
            var recipes = Crafting.RecipesFor(type).Where(r => r.requiredItem.Where(i => !i.IsAir)
                .All(i => Crafting.Accepted(r, i).Any(t => Reachable(t, chain)))).ToList();
            if (recipes.Count == 0) { Gather(p, have); return; }

            Recipe r = recipes.FirstOrDefault(r => Crafting.MissingIngredients(p, r).Count == 0) ?? recipes[0];
            int batches = (count - have + r.createItem.stack - 1) / r.createItem.stack;

            foreach (Item ing in r.requiredItem)
            {
                if (ing.IsAir) continue;
                int haveIng = Crafting.Accepted(r, ing).Sum(t => Game.CountItem(p, t));
                int needIng = ing.stack * batches;
                if (haveIng < needIng)
                {
                    child = new Obtain(ing.type, Game.CountItem(p, ing.type) + needIng - haveIng, depth + 1, chain);
                    return;
                }
            }

            Crafting.RefreshStations(p);
            var missing = Crafting.MissingStationTypes(p, r);
            if (missing.Count > 0) { child = new EnsureStation(missing[0], gather: true, depth + 1); return; }

            if (Crafting.CraftNow(p, type, batches) == 0)
                Fail($"could not craft {ItemLabel}: {Crafting.Problems(p, type)}");
        }

        private static bool Gatherable(int t) =>
            t is ItemID.Wood or ItemID.BorealWood or ItemID.RichMahogany or ItemID.Ebonwood or ItemID.Shadewood or ItemID.Pearlwood
                or ItemID.PalmWood or ItemID.Gel || ContentSamples.ItemsByType[t].createTile >= 0;

        /// <summary>Can `t` be had without needing anything in `chain`: not in it, and gatherable or craftable from outside it.</summary>
        private static bool Reachable(int t, HashSet<int> chain) =>
            !chain.Contains(t) && (Gatherable(t) || Crafting.RecipesFor(t).Any(r =>
                r.requiredItem.Where(i => !i.IsAir).All(i => !chain.Contains(i.type) && i.type != t)));

        private void Gather(Player p, int have)
        {
            // Gathering that yields nothing three times in a row means the area is exhausted or unreachable.
            stalls = have > lastHave ? 0 : stalls + 1;
            lastHave = have;
            if (stalls >= 3) { Fail($"gathering {ItemLabel} stopped making progress (have {have}/{count}); move somewhere with more of it"); return; }

            if (type is ItemID.Wood or ItemID.BorealWood or ItemID.RichMahogany or ItemID.Ebonwood or ItemID.Shadewood or ItemID.Pearlwood)
                child = new MineNearest(TileID.Trees, 1);
            else if (type == ItemID.PalmWood)
                child = new MineNearest(TileID.PalmTree, 1);
            else if (type == ItemID.Gel)
                child = new Attack("Slime", 2);
            else
            {
                int tile = ContentSamples.ItemsByType[type].createTile;
                if (tile < 0) { Fail($"{ItemLabel} can't be crafted or mined; it comes from chests, enemies or shops"); return; }
                // Stone deep under a dirt surface is in view but can't be walked to: dig down to it first.
                if (!DigDown.Near(p, tile) && digs < 3) { digs++; child = new DigDown(tile); return; }
                child = new MineNearest(tile, Math.Min(count - have, 50));
            }
        }
    }

    /// <summary>
    /// The whole house goal in one call: pick a flat spot, gather and craft everything, build the room,
    /// furnish it (work bench, table, chair, a torch if there is one), and run check_house.
    /// Safe to call again after an interruption: it reuses the same site and skips what is already done.
    /// </summary>
    public class BuildHouse : Skill
    {
        public override string Name => "build_house";
        public override int TimeoutTicks => 60 * 900;
        public override bool InterruptOnDamage => true;

        private static (int x, int y)? lastSite;
        private const int W = 10, H = 6;
        private readonly int? askX, askY;
        private int x, y, stage, buildTries;
        private Skill child;
        private readonly Queue<Func<Player, Skill>> todo = new();
        private readonly List<string> notes = new();

        private static readonly (int item, int tileA, int tileB, int dx)[] Furniture =
        {
            (ItemID.WorkBench, TileID.WorkBenches, TileID.WorkBenches, 1),
            (ItemID.WoodenTable, TileID.Tables, TileID.Tables2, 4),
            (ItemID.WoodenChair, TileID.Chairs, TileID.Chairs, 7),
        };

        public BuildHouse(int? x, int? y) { askX = x; askY = y; }

        public override void Tick(Player p)
        {
            if (child != null)
            {
                if (Step(child, p)) return;
                Skill done = child;
                child = null;
                if (!done.Ok)
                {
                    if (done is BuildRoom && done.Message.StartsWith("not enough materials") && ++buildTries < 3) { stage = 1; return; }
                    if (done is Place) notes.Add(done.Message); // furniture: check_house will say what's still missing
                    else { Fail($"{done.Message} (house site ({x},{y}); call build_house again to resume)"); return; }
                }
            }
            if (todo.Count > 0) { child = todo.Dequeue()(p); return; }

            switch (stage++)
            {
                case 0:
                    if (askX is int ax && askY is int ay) (x, y) = (ax, ay);
                    else if (lastSite is { } s && Math.Abs(s.x - Game.TileX(p)) < 80) (x, y) = s;
                    else if (FindSite(p) is { } f) (x, y) = f;
                    else { Fail("no flat 10-tile-wide spot within 25 tiles; move to flatter ground or give x, y"); return; }
                    lastSite = (x, y);
                    return;

                case 1:
                {
                    // Furniture first, raw Wood for the frame last, so crafting doesn't eat the frame wood.
                    var room = new BuildRoom(x, y, W, H, "Wood", "WoodWall", "WoodenDoor");
                    var (blocks, walls, door) = room.Requirements();
                    if (walls > 0) todo.Enqueue(_ => new Obtain(ItemID.WoodWall, walls, 0));
                    if (door) todo.Enqueue(_ => new Obtain(ItemID.WoodenDoor, 1, 0));
                    foreach (var f in Furniture)
                        if (!InRoom(f.tileA, f.tileB)) todo.Enqueue(_ => new Obtain(f.item, 1, 0));
                    if (blocks > 0) todo.Enqueue(_ => new Obtain(ItemID.Wood, blocks, 0));
                    return;
                }

                case 2:
                    child = new BuildRoom(x, y, W, H, "Wood", "WoodWall", "WoodenDoor");
                    return;

                case 3:
                    foreach (var f in Furniture)
                        if (!InRoom(f.tileA, f.tileB))
                            todo.Enqueue(_ => new Place(Game.ItemName(f.item), x + f.dx, y - 1));
                    if (Game.CountItem(p, ItemID.Torch) > 0 && !InRoom(TileID.Torches, TileID.Torches))
                        todo.Enqueue(_ => new Place("Torch", x + 5, y - 4));
                    return;

                default:
                {
                    var check = new CheckHouse(x + 1, y - 4);
                    check.Tick(p); // finishes in one tick
                    string where = $"House at floor row y={y}, walls x {x}..{x + W - 1}.";
                    string extra = notes.Count > 0 ? " Placement notes: " + string.Join("; ", notes) : "";
                    if (check.Ok) Succeed($"{check.Message} {where}");
                    else Fail($"{check.Message} {where}{extra}");
                    return;
                }
            }
        }

        private bool InRoom(int tileA, int tileB)
        {
            for (int i = x + 1; i < x + W - 1; i++)
            for (int j = y - H + 2; j < y; j++)
            {
                Tile t = Framing.GetTileSafely(i, j);
                if (t.HasTile && (t.TileType == tileA || t.TileType == tileB)) return true;
            }
            return false;
        }

        /// <summary>A 10-wide stretch of ground near the player with at most 2 tiles of height difference.</summary>
        private static (int x, int y)? FindSite(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            (int, int)? best = null;
            int bestScore = int.MaxValue;
            for (int left = px - 25; left <= px + 15; left++)
            {
                var ground = new int[W];
                bool ok = true;
                for (int k = 0; k < W && ok; k++)
                {
                    ground[k] = Surface(left + k, py);
                    ok = ground[k] > 0;
                }
                if (!ok) continue;
                int floor = ground.GroupBy(g => g).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
                int spread = ground.Max() - ground.Min();
                if (spread > 2) continue;
                int blocked = 0;
                for (int i = left; i < left + W; i++)
                for (int j = floor - H + 1; j < floor; j++)
                {
                    if (Game.Solid(i, j)) blocked++;
                    if (Framing.GetTileSafely(i, j).LiquidAmount > 0) blocked += 50;
                }
                int score = spread * 6 + blocked * 3 + Math.Abs(left + W / 2 - px);
                if (score < bestScore) { bestScore = score; best = (left, floor); }
            }
            return best;
        }

        /// <summary>First solid tile with open space above it, scanning down from a bit above the player.</summary>
        private static int Surface(int x, int py)
        {
            for (int j = py - 10; j <= py + 12; j++)
                if (Game.Solid(x, j) && !Game.Solid(x, j - 1) && !Game.Solid(x, j - 2)) return j;
            return -1;
        }
    }
}
