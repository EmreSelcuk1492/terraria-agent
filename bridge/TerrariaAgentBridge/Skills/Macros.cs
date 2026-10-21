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
        private int steps, placeTries, still, settle;

        private int levels;  // levelling passes so far: the easier side first, then the other
        private readonly Queue<Skill> todo = new();
        private readonly HashSet<Skill> leveling = new();
        private readonly List<string> levelLog = new();

        /// <summary>
        /// Clear a 3x3 space beside the player (toward the side with less to dig) and fill the floor under it, using
        /// any solid block in the inventory (Wood counts). Steps that fail are skipped; the placement retry decides.
        /// `other`: the harder side, for a second pass when the first side's spot stayed unusable.
        /// </summary>
        private List<Skill> Level(Player p, bool other = false)
        {
            int px = Game.TileX(p), feet = Game.TileY(p) + 1;
            // From the floor, not the body: mid-jump in a 2-wide shaft (station_shaft, 2026-10-16) it cut the hole 6 rows
            // up the wall, and the Furnace never fit.
            for (int k = 0; k < 20 && !Game.Solid(px, feet + 1); k++) feet++;
            // The three columns right past the body's edge. Starting two columns out assumed the column beside you was
            // open: at the bottom of a 2-wide shaft it is the wall, every mine behind it was out of reach (station_shaft).
            int Edge(int d) => d > 0 ? Explore.RightCol(p) + 1 : Explore.LeftCol(p) - 1;
            int Dig(int d) { int n = 0; for (int k = 0; k < 3; k++) for (int y = feet - 2; y <= feet; y++) if (Game.Solid(Edge(d) + d * k, y)) n++; return n; }
            int dir = (Dig(1) <= Dig(-1) ? 1 : -1) * (other ? -1 : 1);
            int block = Game.FindSlot(p, i => i.createTile >= 0 && Main.tileSolid[i.createTile] && !Main.tileSolidTop[i.createTile]
                                                && !Main.tileFrameImportant[i.createTile] && i.stack > 0);
            var steps = new List<Skill>();
            for (int k = 0; k < 3; k++)
            {
                int x = Edge(dir) + dir * k;
                // Bottom up: a head-high pocket first and picking up its drop jumped the player up into it, and the
                // rest of the column was then out of reach (station_shaft).
                for (int y = feet; y >= feet - 2; y--)
                    if (Game.Solid(x, y)) steps.Add(new Mine(x, y, tunnel: false));
                if (!Game.Solid(x, feet + 1) && block >= 0) steps.Add(new Place(Game.ItemName(p.inventory[block].type), x, feet + 1));
            }
            foreach (var s in steps) leveling.Add(s);
            return steps;
        }

        private static bool FarFrom(Player p, (int x, int y) spot) =>
            Math.Abs(spot.y - Game.TileY(p)) > 2 || Math.Abs(spot.x - Game.TileX(p)) > 12;

        public EnsureStation(int tileType, bool gather, int depth = 0) { this.tileType = tileType; this.gather = gather; this.depth = depth; }

        public override void Tick(Player p)
        {
            if (child != null)
            {
                if (Step(child, p)) return;
                if (!child.Ok && child is not MoveTo && !leveling.Contains(child)) { Fail(child.Message); return; }
                if (leveling.Contains(child) && !child.Ok) levelLog.Add($"@({Game.TileX(p)},{Game.TileY(p)}) " + (child.Message.Length > 90 ? child.Message.Substring(0, 90) : child.Message));
                child = null;
                if (todo.Count > 0) { child = todo.Dequeue(); return; }
            }
            Crafting.RefreshStations(p);
            string name = Game.TileName(tileType);
            if (p.adjTile[tileType]) { Succeed($"next to {name}"); return; }
            if (++steps > 10) { Fail($"could not get next to a {name}"); return; }

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
                if (placeTries == 0 && levels == 0 && (FlatSpot(p) is not { } f0 || FarFrom(p, f0)))
                {
                    // Boxed in (a shaft, a tunnel end): the nearest flat stretch is up a wall, and walking there pillars you
                    // into the air (station_shaft levelled mid-jump 25 rows up). Level right here instead.
                    placeTries = 3;
                }
                if (placeTries < 3)
                {
                    // Uneven ground or furniture all around: walk to the nearest flat stretch (the LLM track's chair_table
                    // failed here and the model then guessed coordinates); without one, a few tiles either way.
                    var flat = placeTries == 0 ? FlatSpot(p) : null;
                    int off = (placeTries % 2 == 0 ? 1 : -1) * 6 * (placeTries + 1);
                    placeTries++;
                    child = flat is { } f ? new MoveTo(f.x, f.y, 1) : new MoveTo(Game.TileX(p) + off, Game.TileY(p), 2);
                    return;
                }
                if (levels < 2)
                {
                    if (!Explore.Settled(p, ref still) && settle++ < 120) { steps--; return; } // land first: dig from the floor
                    // No flat ground in reach (a steep hillside, LLM track door task): level a spot beside you the way a
                    // player would, then try again. Twice, the other side second: night8 copper_bars levelled one side
                    // after mining, got bumped 4 rows up by a drop, and that side was out of reach.
                    settle = 0;
                    foreach (var sk in Level(p, other: levels == 1)) todo.Enqueue(sk);
                    levelLog.Add($"levelled {(levels == 0 ? "" : "the other side ")}from ({Game.TileX(p)},{Game.TileY(p)}) vel {p.velocity.Y:0.0}: {todo.Count} steps");
                    levels++;
                    placeTries = 2;
                    if (todo.Count > 0) { child = todo.Dequeue(); return; }
                }
                Fail($"have a {Game.ItemName(p.inventory[slot].type)} but found no flat 3-tile spot next to you to place it, and could not level one ({string.Join("; ", levelLog)}); move to flat ground");
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

        /// <summary>
        /// Nearest stretch of floor `width` columns wide with 3 rows of air above it (plants don't count: placing cuts
        /// them), within 40 tiles sideways. Returns where to stand: its middle, one row above the floor.
        /// </summary>
        public static (int x, int y)? FlatSpot(Player p, int width = 6)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            (int, int)? best = null;
            int bestD = int.MaxValue;
            for (int x = px - 40; x <= px + 40; x++)
            for (int f = py - 12; f <= py + 14; f++)
            {
                int d = Math.Abs(x + width / 2 - px) + 2 * Math.Abs(f - py);
                if (d >= bestD || !Game.InWorld(x, f) || !Game.InWorld(x + width, f)) continue;
                bool ok = true;
                for (int i = 0; i < width && ok; i++)
                {
                    if (!Game.Solid(x + i, f) || Game.Platform(x + i, f)) { ok = false; break; }
                    for (int h = 1; h <= 3 && ok; h++)
                    {
                        Tile t = Framing.GetTileSafely(x + i, f - h);
                        if ((t.HasTile && !Main.tileCut[t.TileType]) || t.LiquidAmount > 0) ok = false;
                    }
                }
                if (ok) { bestD = d; best = (x + width / 2, f - 1); }
            }
            return best;
        }

        /// <summary>Grass, flowers, mushrooms: a swing of anything cuts them, and they block placing furniture.</summary>
        private static void CutPlants(int cx, int cy)
        {
            for (int x = cx - 5; x <= cx + 5; x++)
            for (int y = cy - 3; y <= cy; y++)
            {
                Tile t = Framing.GetTileSafely(x, y);
                if (Game.InWorld(x, y) && t.HasTile && Main.tileCut[t.TileType] && !Main.tileContainer[t.TileType]) WorldGen.KillTile(x, y);
            }
        }

        /// <summary>Places the station from `slot` on the floor beside the player.</summary>
        public static bool PlaceNear(Player p, int slot)
        {
            Item item = p.inventory[slot];
            int px = Game.TileX(p), feet = Game.TileY(p) + 1;
            CutPlants(px, feet);
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
                    int pick = PickVariant(p, Crafting.Accepted(r, ing), ing.type);
                    child = new Obtain(pick, Game.CountItem(p, pick) + needIng - haveIng, depth + 1, chain);
                    return;
                }
            }

            Crafting.RefreshStations(p);
            var missing = Crafting.MissingStationTypes(p, r);
            if (missing.Count > 0) { child = new EnsureStation(missing[0], gather: true, depth + 1); return; }

            if (Crafting.CraftNow(p, type, batches) == 0)
                Fail($"could not craft {ItemLabel}: {Crafting.Problems(p, type)}");
        }

        // Plain blocks (StoneBlock, ores, sand) or uncraftable placeables. A crafted object (WoodPlatform, Torch, bars:
        // frame-important tiles) is not: otherwise the reverse craft Wood <- 2 WoodPlatform passes the filter and
        // obtain Wood tries to mine platforms instead of chopping trees.
        /// <summary>
        /// For "any iron bar"-style ingredients, the variant this world can actually provide: the one we hold most of,
        /// counting its direct materials too (LeadOre for LeadBar). A world has only one of Iron/Lead, Copper/Tin...
        /// </summary>
        private int PickVariant(Player p, HashSet<int> accepted, int fallback)
        {
            if (accepted.Count <= 1) return fallback;
            int Score(int t) => Game.CountItem(p, t) * 4 + Crafting.RecipesFor(t)
                .SelectMany(rr => rr.requiredItem.Where(i => !i.IsAir).Select(i => Game.CountItem(p, i.type))).DefaultIfEmpty(0).Max();
            int best = fallback, bestScore = Score(fallback);
            foreach (int t in accepted)
                if (!chain.Contains(t) && Score(t) > bestScore) { best = t; bestScore = Score(t); }
            return best;
        }

        private static bool Gatherable(int t)
        {
            if (t is ItemID.Wood or ItemID.BorealWood or ItemID.RichMahogany or ItemID.Ebonwood or ItemID.Shadewood or ItemID.Pearlwood
                or ItemID.PalmWood or ItemID.Gel) return true;
            int tile = ContentSamples.ItemsByType[t].createTile;
            return tile >= 0 && (!Main.tileFrameImportant[tile] || Crafting.RecipesFor(t).Count == 0);
        }

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
                // Ores: tunnel to the nearest vein and clear it (and the next ones) in one go.
                if (TileID.Sets.Ore[tile]) { child = new MineVein(Game.TileName(tile), count - have, 60); return; }
                // Stone deep under a dirt surface is in view but can't be walked to: dig down to it first.
                if (!DigDown.Near(p, tile) && digs < 3) { digs++; child = new DigDown(tile); return; }
                child = new MineNearest(tile, Math.Min(count - have, 50));
            }
        }
    }

    /// <summary>
    /// The whole house goal in one call: pick a flat spot near you (or use x, y), gather and craft everything,
    /// build the room, furnish it (work bench, table, chair, plus extras like Furnace, Chest, Bed, IronAnvil),
    /// light it with a torch, and run check_house (which includes Terraria's own NPC-housing check).
    /// material "wood" or "stone". new_house=true builds another house next to the previous ones instead of
    /// resuming the last site. Safe to call again after an interruption: it reuses the site and skips what is done.
    /// </summary>
    public class BuildHouse : Skill
    {
        public override string Name => "build_house";
        public override int TimeoutTicks => 60 * 1200;
        public override bool InterruptOnDamage => true;

        private static (int x, int y, int w)? lastSite;
        private static readonly List<(int x, int y, int w)> sites = new();
        private readonly int W, H;
        private readonly int? askX, askY;
        private readonly bool newHouse;
        private readonly int blockItem, wallItem;
        private readonly string blockName, wallName;
        private int x, y, stage, buildTries;
        private Skill child;
        private readonly Queue<Func<Player, Skill>> todo = new();
        private readonly HashSet<Skill> optional = new();
        private readonly List<string> notes = new();
        private readonly List<(int item, int tileA, int tileB, int dx)> furniture = new();

        // Extra furniture, placed after the chair along the floor: (item, tile, alt tile, width).
        private static readonly Dictionary<string, (int item, int tile, int alt, int width)> Extras = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Furnace"] = (ItemID.Furnace, TileID.Furnaces, TileID.Furnaces, 3),
            ["Chest"] = (ItemID.Chest, TileID.Containers, TileID.Containers2, 2),
            ["Bed"] = (ItemID.Bed, TileID.Beds, TileID.Beds, 4),
            ["IronAnvil"] = (ItemID.IronAnvil, TileID.Anvils, TileID.Anvils, 2),
            ["LeadAnvil"] = (ItemID.LeadAnvil, TileID.Anvils, TileID.Anvils, 2),
            ["Sawmill"] = (ItemID.Sawmill, TileID.Sawmill, TileID.Sawmill, 3),
            ["Loom"] = (ItemID.Loom, TileID.Loom, TileID.Loom, 3),
            ["Bookcase"] = (ItemID.Bookcase, TileID.Bookcases, TileID.Bookcases, 3),
        };

        public BuildHouse(int? x, int? y, string material = "wood", int width = 10, int height = 6, IEnumerable<string> extras = null, bool newHouse = false)
        {
            askX = x; askY = y;
            this.newHouse = newHouse;
            bool stone = material != null && material.StartsWith("stone", StringComparison.OrdinalIgnoreCase);
            (blockItem, wallItem, blockName, wallName) = stone
                ? (ItemID.StoneBlock, ItemID.StoneWall, "StoneBlock", "StoneWall")
                : (ItemID.Wood, ItemID.WoodWall, "Wood", "WoodWall");
            H = Math.Clamp(height, 6, 9);

            furniture.Add((ItemID.WorkBench, TileID.WorkBenches, TileID.WorkBenches, 1));
            furniture.Add((ItemID.WoodenTable, TileID.Tables, TileID.Tables2, 4));
            furniture.Add((ItemID.WoodenChair, TileID.Chairs, TileID.Chairs, 7));
            int dx = 9;
            foreach (string e in extras ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(e)) continue;
                if (!Extras.TryGetValue(e.Trim(), out var ex)) { notes.Add($"unknown extra '{e}' (known: {string.Join(", ", Extras.Keys)})"); continue; }
                if (dx + ex.width > 15) { notes.Add($"no room for {e} (a house fits about two extras)"); continue; }
                furniture.Add((ex.item, ex.tile, ex.alt, dx + (ex.width - 1) / 2)); // anchor column: x origin of 2-wide (0) and 3/4-wide (1) objects
                dx += ex.width + 1;
            }
            W = Math.Clamp(Math.Max(width, dx + 1), 10, 16); // room for every extra (BuildRoom caps width at 16)
        }

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
                    if (done is Place || optional.Contains(done)) notes.Add(done.Message); // check_house says what's still missing
                    else { Fail($"{done.Message} (house site ({x},{y}); call build_house again to resume)"); return; }
                }
            }
            if (todo.Count > 0) { child = todo.Dequeue()(p); return; }

            switch (stage++)
            {
                case 0:
                    if (askX is int ax && askY is int ay) (x, y) = (ax, ay);
                    else if (!newHouse && lastSite is { } s && s.w == W && Math.Abs(s.x - Game.TileX(p)) < 80) (x, y) = (s.x, s.y);
                    else if (FindSite(p) is { } f) (x, y) = f;
                    else { Fail($"no flat {W}-tile-wide spot within 30 tiles; move to flatter ground or give x, y"); return; }
                    lastSite = (x, y, W);
                    if (!sites.Contains((x, y, W))) sites.Add((x, y, W));
                    return;

                case 1:
                {
                    // Furniture first, raw blocks for the frame last, so crafting doesn't eat the frame material.
                    var room = new BuildRoom(x, y, W, H, blockName, wallName, "WoodenDoor");
                    var (blocks, walls, door) = room.Requirements();
                    if (walls > 0) todo.Enqueue(_ => new Obtain(wallItem, walls, 0));
                    if (door) todo.Enqueue(_ => new Obtain(ItemID.WoodenDoor, 1, 0));
                    foreach (var f in furniture)
                        if (!InRoom(f.tileA, f.tileB)) todo.Enqueue(_ => new Obtain(f.item, 1, 0));
                    // Not gated on carrying a torch now: a Furnace crafted above can use up the ones in the inventory.
                    if (!InRoom(TileID.Torches, TileID.Torches))
                        todo.Enqueue(_ => Optional(new Obtain(ItemID.Torch, 1, 0)));
                    if (blocks > 0) todo.Enqueue(_ => new Obtain(blockItem, blocks, 0));
                    return;
                }

                case 2:
                    child = new BuildRoom(x, y, W, H, blockName, wallName, "WoodenDoor");
                    return;

                case 3:
                    foreach (var f in furniture)
                        if (!InRoom(f.tileA, f.tileB))
                            todo.Enqueue(_ => new Place(Game.ItemName(f.item), x + f.dx, y - 1));
                    if (Game.CountItem(p, ItemID.Torch) > 0 && !InRoom(TileID.Torches, TileID.Torches))
                        todo.Enqueue(_ => Optional(new Place("Torch", x + W / 2, y - H + 2)));
                    return;

                default:
                {
                    var check = new CheckHouse(x + 2, y - 2);
                    check.Tick(p); // finishes in one tick
                    string where = $"House at floor row y={y}, walls x {x}..{x + W - 1}, {blockName}.";
                    string extra = notes.Count > 0 ? " Notes: " + string.Join("; ", notes) : "";
                    if (check.Ok) Succeed($"{check.Message} {where}{extra}");
                    else Fail($"{check.Message} {where}{extra}");
                    return;
                }
            }
        }

        private Skill Optional(Skill s) { optional.Add(s); return s; }

        /// <summary>Sites are world coordinates; forget them when the world unloads.</summary>
        internal static void ForgetSites() { sites.Clear(); lastSite = null; }

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

        /// <summary>A W-wide stretch of ground near the player with at most 2 tiles of height difference, not overlapping
        /// a house already built (a new house prefers to sit right next to the last one).</summary>
        private (int x, int y)? FindSite(Player p)
        {
            int px = Game.TileX(p), py = Game.TileY(p);
            (int, int)? best = null;
            int bestScore = int.MaxValue;
            for (int left = px - 30; left <= px + 20; left++)
            {
                if (sites.Any(st => left < st.x + st.w + 1 && st.x < left + W + 1)) continue;
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
                if (newHouse && lastSite is { } ls) score += Math.Abs(left - (ls.x + ls.w + 2)) * 2; // neighbours
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
