using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerrariaAgentBridge.Skills
{
    /// <summary>Place a block, wall, platform, or furniture item at a tile (walks into reach first).</summary>
    public class Place : Skill
    {
        public override string Name => "place";
        private readonly string itemName;
        private readonly int x, y;
        private int itemType = -1;
        private Skill approach;

        public Place(string item, int x, int y) { itemName = item; this.x = x; this.y = y; }

        private static (int, int)? FindPlaced(int tileType, int cx, int cy)
        {
            for (int j = cy - 2; j <= cy + 2; j++)
            for (int i = cx - 2; i <= cx + 2; i++)
            {
                Tile t = Framing.GetTileSafely(i, j);
                if (t.HasTile && t.TileType == tileType) return (i, j);
            }
            return null;
        }

        public override void Tick(Player p)
        {
            if (itemType < 0)
            {
                itemType = Game.ResolveItem(itemName);
                if (itemType < 0) { Fail($"unknown item '{itemName}'"); return; }
            }
            int slot = Game.FindSlot(p, i => i.type == itemType);
            if (slot < 0) { Fail($"no {Game.ItemName(itemType)} in inventory ({Crafting.HowToGet(itemType)})"); return; }
            Item item = p.inventory[slot];
            if (item.createTile < 0 && item.createWall <= 0) { Fail($"{Game.ItemName(itemType)} is not placeable"); return; }

            if (!Game.InReach(p, x, y, item.tileBoost))
            {
                approach ??= new MoveTo(x, y, 3);
                if (Step(approach, p)) return;
                if (!Game.InReach(p, x, y, item.tileBoost)) { Fail($"could not get within reach of ({x},{y}): {approach.Message}"); return; }
            }

            Game.Hold(p, slot);
            slot = p.selectedItem;
            Tile t = Framing.GetTileSafely(x, y);

            if (item.createTile >= 0)
            {
                if (t.HasTile) { Fail($"({x},{y}) is occupied by {Game.TileName(t.TileType)}; mine it first"); return; }
                if (new Rectangle(x * 16, y * 16, 16, 16).Intersects(p.Hitbox) && Main.tileSolid[item.createTile])
                {
                    Fail($"({x},{y}) overlaps the player; step aside first");
                    return;
                }
                // Multi-tile furniture anchors differently per object; try the given tile, then nearby anchors.
                (int, int)? at = null;
                foreach (var (ax, ay) in new[] { (x, y), (x, y - 1), (x - 1, y), (x + 1, y), (x - 1, y - 1), (x + 1, y - 1), (x, y + 1) })
                {
                    if (Framing.GetTileSafely(ax, ay).HasTile) continue;
                    if (!WorldGen.PlaceTile(ax, ay, item.createTile, mute: false, forced: false, plr: p.whoAmI, style: item.placeStyle)) continue;
                    at = FindPlaced(item.createTile, x, y);
                    if (at != null) break;
                }
                if (at == null)
                {
                    Fail($"could not place {Game.ItemName(itemType)} at ({x},{y}): needs solid floor right below, and enough free space (furniture is 2-3 tiles wide/tall)");
                    return;
                }
                Game.ConsumeOne(p, slot);
                Succeed($"placed {Game.ItemName(itemType)} at {at}");
                return;
            }
            else
            {
                if (t.WallType != 0) { Fail($"({x},{y}) already has a wall"); return; }
                WorldGen.PlaceWall(x, y, item.createWall);
                if (Framing.GetTileSafely(x, y).WallType == 0) { Fail($"could not place wall at ({x},{y}): walls need an adjacent wall or block"); return; }
            }
            Game.ConsumeOne(p, slot);
            Succeed($"placed {Game.ItemName(itemType)} at ({x},{y})");
        }
    }

    /// <summary>
    /// Craft an item `count` times. Missing stations are handled: it walks to a placed one nearby, or places one
    /// from the inventory, or crafts a WorkBench from Wood. It doesn't gather ingredients (that's `obtain`).
    /// </summary>
    public class Craft : Skill
    {
        public override string Name => "craft";
        public override int TimeoutTicks => 60 * 60;
        private readonly string itemName;
        private readonly int count;
        private int type = -1, made;
        private Skill station;
        private readonly HashSet<int> tried = new();

        public Craft(string item, int count) { itemName = item; this.count = Math.Clamp(count, 1, 999); }

        public override void Tick(Player p)
        {
            if (station != null)
            {
                if (Step(station, p)) return;
                if (!station.Ok) { Fail($"crafted {made}/{count} {Game.ItemName(type)}: {station.Message}"); return; }
                station = null;
            }
            if (type < 0)
            {
                type = Game.ResolveItem(itemName);
                if (type < 0) { Fail($"unknown item '{itemName}'"); return; }
                if (Crafting.RecipesFor(type).Count == 0) { Fail($"{Game.ItemName(type)} has no crafting recipe"); return; }
            }

            made += Crafting.CraftNow(p, type, count - made);
            if (made >= count) { Succeed($"crafted {Game.ItemName(type)} x{made}"); return; }

            // Only ingredients missing -> report. A station missing -> go get it, once per station type.
            Recipe r = Crafting.RecipesFor(type).FirstOrDefault(r => Crafting.MissingIngredients(p, r).Count == 0);
            int need = r == null ? -1 : Crafting.MissingStationTypes(p, r).FirstOrDefault(t => !tried.Contains(t), -1);
            if (need >= 0)
            {
                tried.Add(need);
                station = new EnsureStation(need, gather: false);
                return;
            }
            string msg = $"crafted {made}/{count} {Game.ItemName(type)}. {Crafting.Problems(p, type)}";
            if (made > 0) Succeed(msg); else Fail(msg);
        }
    }

    /// <summary>
    /// Fight the nearest hostile NPC (or a named one) with the best weapon. Bails out at low health unless desperate:
    /// with an enemy already on top of you, refusing to swing just means taking the hits anyway.
    /// </summary>
    public class Attack : Skill
    {
        public override string Name => "attack";
        public override int TimeoutTicks => 60 * 30;
        public override bool InterruptOnDamage => false; // taking hits is expected; it retreats at 30% on its own
        private readonly string target;
        private int npcIndex = -1;
        private int kills;
        private readonly int maxKills;
        private string fellBack;
        private readonly MoveTo mover = new(0, 0);
        private readonly bool desperate;
        public override bool Survival => desperate;

        public Attack(string target, int maxKills, bool desperate = false)
        {
            this.desperate = desperate;
            this.target = string.IsNullOrWhiteSpace(target) ? "nearest" : target.Trim();
            this.maxKills = Math.Clamp(maxKills, 1, 20);
        }

        public override void Tick(Player p)
        {
            if (p.dead) { Fail("you died"); return; }
            if (!desperate && p.statLife < p.statLifeMax2 * 0.3f) { Fail($"retreating: health {p.statLife}/{p.statLifeMax2}. Heal (use_item a potion) or run."); return; }

            if (npcIndex < 0 || !Main.npc[npcIndex].active || Main.npc[npcIndex].life <= 0)
            {
                if (npcIndex >= 0) kills++;
                if (kills >= maxKills) { Succeed($"killed {kills}{fellBack}"); return; }
                npcIndex = FindTarget(p, target);
                if (npcIndex < 0 && target != "nearest" && (npcIndex = FindTarget(p, "nearest")) >= 0)
                    fellBack = $" (no '{target}' here; fought the nearest hostile, {Main.npc[npcIndex].FullName})";
                if (npcIndex < 0)
                {
                    if (kills > 0) Succeed($"killed {kills}{fellBack}; no more targets nearby");
                    else Fail($"no hostile '{target}' within 40 tiles");
                    return;
                }
            }

            int slot = Game.BestSlot(p, i => i.damage > 0 && i.pick == 0 && i.axe == 0 && i.hammer == 0 && i.ammo == 0 && !i.consumable ? i.damage : 0);
            if (slot < 0) slot = Game.BestSlot(p, i => i.damage); // fall back to a pickaxe/axe swing
            if (slot < 0) { Fail("no weapon in inventory"); return; }
            Game.Hold(p, slot);
            Item weapon = p.inventory[p.selectedItem];

            NPC npc = Main.npc[npcIndex];
            float range = weapon.DamageType == DamageClass.Melee && weapon.shoot <= 0 ? 2.5f : 14f;
            int dx = (int)((npc.Center.X - p.Center.X) / 16f);
            int dy = (int)((npc.Center.Y - p.Center.Y) / 16f);

            bool inRange = Math.Abs(dx) <= range && Math.Abs(dy) <= Math.Max(3f, range * 0.6f);
            mover.Drive(p, dx, dy, walk: !inRange);
            if (mover.Done) { Fail(mover.Message); return; }

            p.direction = npc.Center.X < p.Center.X ? -1 : 1;
            AgentPlayer.Aim = npc.Center;
            if (inRange) p.controlUseItem = true;
        }

        private static int FindTarget(Player p, string target)
        {
            int best = -1;
            float bestD = 40f;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC n = Main.npc[i];
                if (!n.active || n.friendly || n.townNPC || n.damage <= 0 || n.dontTakeDamage) continue;
                if (target != "nearest" && !n.FullName.Contains(target, StringComparison.OrdinalIgnoreCase)) continue;
                float d = (n.Center - p.Center).Length() / 16f;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }

    /// <summary>Use (consume/activate) an item once: potions, food, summons, torches in hand, etc.</summary>
    public class UseItem : Skill
    {
        public override string Name => "use_item";
        public override int TimeoutTicks => 60 * 5;
        private readonly string itemName;
        private bool pressed;

        public UseItem(string item) { itemName = item; }

        public override void Tick(Player p)
        {
            if (!pressed)
            {
                int type = Game.ResolveItem(itemName);
                int slot = type < 0 ? -1 : Game.FindSlot(p, i => i.type == type);
                if (slot < 0) { Fail($"no '{itemName}' in inventory"); return; }
                Game.Hold(p, slot);
                AgentPlayer.Aim = p.Center + new Vector2(p.direction * 48, 0);
                p.controlUseItem = true;
                if (Ticks >= 2) pressed = true;
                return;
            }
            if (p.itemAnimation == 0) Succeed($"used {itemName}");
        }
    }

    public class Wait : Skill
    {
        public override string Name => "wait";
        private readonly int ticks;
        public Wait(float seconds) { ticks = (int)(Math.Clamp(seconds, 0.1f, 20f) * 60); }
        public override int TimeoutTicks => ticks + 60;
        public override void Tick(Player p) { if (Ticks >= ticks) Succeed($"waited {ticks / 60f:0.#}s"); }
    }
}
