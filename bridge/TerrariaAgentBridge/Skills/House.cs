using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>
    /// Build an enclosed room: block frame, background walls inside, a door in the right wall.
    /// (x, y) is the bottom-left corner: x = left wall column, y = floor row. Existing solid ground is
    /// reused as floor/frame. Plants and loose objects inside the room are cleared first.
    /// Furniture is not placed; use `place` afterwards on the floor row y-1.
    /// </summary>
    public class BuildRoom : Skill
    {
        public override string Name => "build_room";
        public override int TimeoutTicks => 60 * 120;

        private readonly int x, y, w, h;
        private readonly string blockName, wallName, doorName;
        private int blockItem, wallItem, doorItem, blockTile, wallType;
        private bool planned;
        private readonly Queue<(string kind, int x, int y)> work = new();
        private Skill walk;
        private int cooldown, skipped;
        private readonly Dictionary<(int, int), int> retries = new();

        public BuildRoom(int x, int y, int width, int height, string block, string wall, string door)
        {
            this.x = x; this.y = y;
            w = Math.Clamp(width, 6, 16);
            h = Math.Clamp(height, 5, 9);
            blockName = string.IsNullOrWhiteSpace(block) ? "Wood" : block;
            wallName = string.IsNullOrWhiteSpace(wall) ? "WoodWall" : wall;
            doorName = string.IsNullOrWhiteSpace(door) ? "WoodenDoor" : door;
        }

        private int Top => y - h + 1;          // ceiling row
        private int Right => x + w - 1;        // right wall column (door side)
        private bool IsDoorGap(int i, int j) => i == Right && j >= y - 3 && j <= y - 1;
        private bool IsFrame(int i, int j) => (j == y || j == Top || i == x || i == Right) && !IsDoorGap(i, j);
        private bool IsInterior(int i, int j) => i > x && i < Right && j > Top && j < y;

        public override void Tick(Player p)
        {
            if (!planned && !Plan(p)) return;

            if (walk != null)
            {
                if (Step(walk, p)) return;
                walk = null;
            }
            if (cooldown-- > 0) return;
            if (work.Count == 0)
            {
                Succeed($"built room: walls x {x}..{Right}, rows {Top}..{y}; floor row y={y}, interior x {x + 1}..{Right - 1}, rows {Top + 1}..{y - 1}; door at x={Right}" +
                        (skipped > 0 ? $"; {skipped} tiles could not be placed (run check_house)" : "") +
                        $". Place furniture standing on the floor, e.g. place WoodenChair at ({x + 2},{y - 1}). Then check_house at ({x + 2},{y - 2}).");
                return;
            }

            var (kind, i, j) = work.Peek();
            if (!Game.InReach(p, i, j, 0))
            {
                walk = new MoveTo(Math.Clamp(i, x + 1, Right - 1), y - 2, 2);
                return;
            }
            AgentPlayer.Aim = new Vector2(i * 16 + 8, j * 16 + 8);
            p.direction = i * 16 + 8 < p.Center.X ? -1 : 1;
            cooldown = 5;

            if (Do(p, kind, i, j))
            {
                work.Dequeue();
                return;
            }
            // Couldn't do it right now (usually the player is standing in the way): move to the room's middle and retry later.
            work.Dequeue();
            int tries = retries.TryGetValue((i, j), out int n) ? n + 1 : 1;
            retries[(i, j)] = tries;
            if (tries < 4)
            {
                work.Enqueue((kind, i, j));
                walk = new MoveTo(x + w / 2, y - 2, 1);
            }
            else skipped++;
        }

        private bool Plan(Player p)
        {
            planned = true;
            blockItem = Game.ResolveItem(blockName);
            wallItem = Game.ResolveItem(wallName);
            doorItem = Game.ResolveItem(doorName);
            if (blockItem < 0 || wallItem < 0 || doorItem < 0)
            {
                Fail($"unknown item: {(blockItem < 0 ? blockName + " " : "")}{(wallItem < 0 ? wallName + " " : "")}{(doorItem < 0 ? doorName : "")}");
                return false;
            }
            var probe = new Item(); probe.SetDefaults(blockItem);
            blockTile = probe.createTile;
            probe.SetDefaults(wallItem);
            wallType = probe.createWall;
            if (blockTile < 0 || !Main.tileSolid[blockTile]) { Fail($"{blockName} doesn't place a solid block"); return false; }
            if (wallType <= 0) { Fail($"{wallName} isn't a wall item (craft WoodWall from Wood at a WorkBench: 1 Wood -> 4)"); return false; }

            int blocksNeeded = 0, wallsNeeded = 0;
            var clear = new List<(int, int)>();
            var frame = new List<(int, int)>();
            var walls = new List<(int, int)>();
            for (int i = x; i <= Right; i++)
            for (int j = Top; j <= y; j++)
            {
                if (!Game.InWorld(i, j)) { Fail($"({i},{j}) is outside the world"); return false; }
                Tile t = Framing.GetTileSafely(i, j);
                if (IsFrame(i, j))
                {
                    if (Game.Solid(i, j)) continue; // reuse existing ground/blocks
                    if (t.HasTile) clear.Add((i, j));
                    frame.Add((i, j));
                    blocksNeeded++;
                }
                else
                {
                    if (t.HasTile) clear.Add((i, j));
                    if (IsInterior(i, j) && t.WallType == 0) { walls.Add((i, j)); wallsNeeded++; }
                }
            }

            var missing = new List<string>();
            int haveBlocks = Game.CountItem(p, blockItem), haveWalls = Game.CountItem(p, wallItem), haveDoor = Game.CountItem(p, doorItem);
            if (haveBlocks < blocksNeeded) missing.Add($"{blocksNeeded} {Game.ItemName(blockItem)} (have {haveBlocks})");
            if (haveWalls < wallsNeeded) missing.Add($"{wallsNeeded} {Game.ItemName(wallItem)} (have {haveWalls})");
            if (haveDoor < 1) missing.Add($"1 {Game.ItemName(doorItem)} (have 0)");
            if (missing.Count > 0)
            {
                Fail($"not enough materials for a {w}x{h} room at ({x},{y}): need {string.Join(", ", missing)}");
                return false;
            }

            foreach (var c in clear) work.Enqueue(("clear", c.Item1, c.Item2));
            // Bottom-up so each block has support next to it.
            foreach (var c in frame.OrderByDescending(c => c.Item2).ThenBy(c => c.Item1)) work.Enqueue(("block", c.Item1, c.Item2));
            foreach (var c in walls.OrderByDescending(c => c.Item2)) work.Enqueue(("wall", c.Item1, c.Item2));
            work.Enqueue(("door", Right, y - 2));
            walk = new MoveTo(x + w / 2, y - 2, 1);
            return true;
        }

        private bool Do(Player p, string kind, int i, int j)
        {
            switch (kind)
            {
                case "clear":
                    if (!Framing.GetTileSafely(i, j).HasTile) return true;
                    WorldGen.KillTile(i, j);
                    return !Framing.GetTileSafely(i, j).HasTile;

                case "block":
                {
                    if (Game.Solid(i, j)) return true;
                    if (new Rectangle(i * 16, j * 16, 16, 16).Intersects(p.Hitbox)) return false;
                    int slot = Game.FindSlot(p, it => it.type == blockItem);
                    if (slot < 0) return false;
                    Game.Hold(p, slot);
                    if (!WorldGen.PlaceTile(i, j, blockTile, mute: false, forced: true, plr: p.whoAmI)) return false;
                    Game.ConsumeOne(p, p.selectedItem);
                    return true;
                }

                case "wall":
                {
                    if (Framing.GetTileSafely(i, j).WallType != 0) return true;
                    int slot = Game.FindSlot(p, it => it.type == wallItem);
                    if (slot < 0) return false;
                    Game.Hold(p, slot);
                    WorldGen.PlaceWall(i, j, wallType);
                    if (Framing.GetTileSafely(i, j).WallType == 0) return false;
                    Game.ConsumeOne(p, p.selectedItem);
                    return true;
                }

                case "door":
                {
                    for (int row = y - 3; row <= y - 1; row++)
                        if (Framing.GetTileSafely(i, row).HasTile && Framing.GetTileSafely(i, row).TileType == TileID.ClosedDoor) return true;
                    if (new Rectangle(i * 16, (y - 3) * 16, 16, 48).Intersects(p.Hitbox)) return false;
                    int slot = Game.FindSlot(p, it => it.type == doorItem);
                    if (slot < 0) return false;
                    Item door = p.inventory[slot];
                    Game.Hold(p, slot);
                    foreach (int row in new[] { y - 2, y - 3, y - 1 })
                    {
                        WorldGen.PlaceTile(i, row, door.createTile, mute: false, forced: false, plr: p.whoAmI, style: door.placeStyle);
                        for (int r = y - 3; r <= y - 1; r++)
                        {
                            Tile t = Framing.GetTileSafely(i, r);
                            if (t.HasTile && t.TileType == door.createTile)
                            {
                                Game.ConsumeOne(p, p.selectedItem);
                                return true;
                            }
                        }
                    }
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Flood-fills the room containing (x, y) and reports exactly what a house still needs:
    /// enclosed, background walls, a door, a chair, a table, a work bench (and whether there is light).
    /// </summary>
    public class CheckHouse : Skill
    {
        public override string Name => "check_house";
        private readonly int x, y;
        private const int MaxTiles = 750; // Terraria's own room size cap

        public CheckHouse(int x, int y) { this.x = x; this.y = y; }

        private static bool Barrier(int i, int j)
        {
            if (!Game.InWorld(i, j)) return true;
            Tile t = Framing.GetTileSafely(i, j);
            return Game.Solid(i, j) || Game.Platform(i, j) || (t.HasTile && (t.TileType == TileID.ClosedDoor || t.TileType == TileID.OpenDoor));
        }

        public override void Tick(Player p)
        {
            if (Barrier(x, y)) { Fail($"({x},{y}) is a block or door; give a tile inside the room, e.g. one above the floor"); return; }

            var seen = new HashSet<(int, int)> { (x, y) };
            var queue = new Queue<(int, int)>();
            queue.Enqueue((x, y));
            bool leaked = false, door = false, chair = false, table = false, bench = false, light = false;
            (int, int)? missingWall = null, leakAt = null;

            while (queue.Count > 0)
            {
                var (i, j) = queue.Dequeue();
                Tile t = Framing.GetTileSafely(i, j);
                if (t.WallType == 0 && missingWall == null) missingWall = (i, j);
                if (t.HasTile)
                {
                    int type = t.TileType;
                    chair |= type == TileID.Chairs || type == TileID.Benches || type == TileID.Thrones;
                    table |= type == TileID.Tables || type == TileID.Tables2;
                    bench |= type == TileID.WorkBenches;
                    light |= type == TileID.Torches || type == TileID.Candles || type == TileID.Chandeliers || type == TileID.Lamps || type == TileID.Candelabras || type == TileID.HangingLanterns;
                }
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int ni = i + di, nj = j + dj;
                    Tile n = Framing.GetTileSafely(ni, nj);
                    if (n.HasTile && (n.TileType == TileID.ClosedDoor || n.TileType == TileID.OpenDoor)) door = true;
                    if (Barrier(ni, nj) || !seen.Add((ni, nj))) continue;
                    if (seen.Count > MaxTiles) { leaked = true; leakAt ??= (ni, nj); break; }
                    queue.Enqueue((ni, nj));
                }
                if (leaked) break;
            }

            var problems = new List<string>();
            if (leaked) problems.Add($"NOT ENCLOSED: air escapes the room (around {leakAt}); close the gap with blocks");
            else
            {
                if (missingWall != null) problems.Add($"missing background wall at {missingWall} (place WoodWall)");
                if (!door) problems.Add("no door in the frame");
                if (!chair) problems.Add("no chair inside");
                if (!table) problems.Add("no table inside");
                if (!bench) problems.Add("no work bench inside");
            }
            string size = leaked ? "" : $" interior {seen.Count} tiles.";
            string lightNote = light ? " Has light." : " No light source (fine for this goal; a torch would make it NPC-valid).";
            if (problems.Count == 0) Succeed($"HOUSE OK: enclosed, walls, door, chair, table, work bench.{size}{lightNote}");
            else Fail("house not done: " + string.Join("; ", problems) + "." + size);
        }
    }
}
