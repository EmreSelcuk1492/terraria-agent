"""A tiny fake Terraria that speaks the bridge protocol, for testing the agent without the game.

    python mock_bridge.py            # serves on 127.0.0.1:7979
    python -m terraria_agent --goal "Craft a work bench and a wooden sword"

Flat grass at y=100 over stone, a few trees, some copper ore, one slime. Recipes are a small real subset.
"""

from __future__ import annotations

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any

GROUND_Y = 100  # top solid row
RECIPES = {
    "WorkBench": ({"Wood": 10}, None, 1),
    "WoodenSword": ({"Wood": 7}, "WorkBenches", 1),
    "WoodPlatform": ({"Wood": 1}, None, 2),
    "Torch": ({"Wood": 1, "Gel": 1}, None, 3),
    "Furnace": ({"StoneBlock": 20, "Wood": 4, "Torch": 3}, "WorkBenches", 1),
    "CopperBar": ({"CopperOre": 3}, "Furnaces", 1),
    "WoodWall": ({"Wood": 1}, "WorkBenches", 4),
    "WoodenDoor": ({"Wood": 6}, "WorkBenches", 1),
    "WoodenChair": ({"Wood": 4}, "WorkBenches", 1),
    "WoodenTable": ({"Wood": 8}, "WorkBenches", 1),
    "StoneWall": ({"StoneBlock": 1}, "WorkBenches", 4),
    "LeadBar": ({"LeadOre": 3}, "Furnaces", 1),
    "LeadAnvil": ({"LeadBar": 5}, "WorkBenches", 1),
    "LeadPickaxe": ({"LeadBar": 10, "Wood": 3}, "Anvils", 1),
    "LeadBroadsword": ({"LeadBar": 8}, "Anvils", 1),
    "CopperHelmet": ({"CopperBar": 12}, "Anvils", 1),
    "CopperChainmail": ({"CopperBar": 20}, "Anvils", 1),
    "CopperGreaves": ({"CopperBar": 16}, "Anvils", 1),
    "Chest": ({"Wood": 8, "LeadBar": 2}, "WorkBenches", 1),
}
WORLD_ORES = ("Copper", "Lead")  # this mock world has copper and lead, not tin or iron
ARMOR = ("Helmet", "Chainmail", "Greaves")
PLACES = {"WorkBench": "WorkBenches", "Furnace": "Furnaces", "LeadAnvil": "Anvils", "Chest": "Containers", "WoodenChair": "Chairs", "WoodenTable": "Tables", "Wood": "WoodBlock", "DirtBlock": "Dirt", "StoneBlock": "Stone", "WoodPlatform": "Platforms"}
DROPS = {"Trees": ("Wood", 8), "Dirt": ("DirtBlock", 1), "Stone": ("StoneBlock", 1), "CopperOre": ("CopperOre", 1)}


class World:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.player = {"x": 50, "y": GROUND_Y - 2, "hp": 100, "max_hp": 100}
        self.inventory: dict[str, int] = {"CopperShortsword": 1, "CopperPickaxe": 1, "CopperAxe": 1}
        self.tiles: dict[tuple[int, int], str] = {}
        for x in range(0, 200):
            self.tiles[(x, GROUND_Y)] = "Dirt"
            for y in range(GROUND_Y + 1, GROUND_Y + 30):
                self.tiles[(x, y)] = "Stone"
        for x in (58, 63, 71, 40, 33, 80, 86, 92, 25, 18):
            for y in range(GROUND_Y - 8, GROUND_Y):
                self.tiles[(x, y)] = "Trees"
        for x in range(44, 48):
            self.tiles[(x, GROUND_Y + 4)] = "CopperOre"
        self.npcs = [{"name": "Green Slime", "x": 60, "y": GROUND_Y - 1, "hp": 14, "max_hp": 14, "hostile": True, "boss": False}]
        self.rooms: list[tuple[int, int, int, int]] = []  # x1, y1, x2, y2 interior bounds
        self.freeze = False
        self.ticks = 0
        self.equipment: list[str] = []
        self.houses: list[tuple[int, int, int]] = []
        self.looted = False
        self.said: list[str] = []
        self.saves = 0

    # ---- observation -----------------------------------------------------------------

    def inv_list(self) -> list[dict[str, Any]]:
        return [{"slot": i, "item": k, "stack": v} for i, (k, v) in enumerate(self.inventory.items()) if v > 0]

    def summary(self) -> dict[str, Any]:
        p = self.player
        return {**p, "mana": 20, "max_mana": 20, "defense": 0, "on_ground": True, "in_water": False, "dead": False, "holding": None}

    def station_near(self, station: str | None) -> bool:
        if station is None:
            return True
        px, py = self.player["x"], self.player["y"]
        return any(t == station and abs(x - px) <= 4 and abs(y - py) <= 3 for (x, y), t in self.tiles.items())

    def craftable(self) -> list[str]:
        return [n for n, (ing, st, _) in RECIPES.items() if self.station_near(st) and all(self.inventory.get(k, 0) >= v for k, v in ing.items())]

    def state(self) -> dict[str, Any]:
        px, py = self.player["x"], self.player["y"]
        ox, oy = px - 30, py - 17
        chars = {"Dirt": "#", "Stone": "#", "Trees": "T", "CopperOre": "$", "WorkBenches": "+", "Furnaces": "+", "Platforms": "="}
        rows = []
        for y in range(oy, oy + 35):
            row = "".join(chars.get(self.tiles.get((x, y), ""), ".") for x in range(ox, ox + 61))
            rows.append(row)
        for n in self.npcs:
            if 0 <= n["x"] - ox < 61 and 0 <= n["y"] - oy < 35:
                r = rows[n["y"] - oy]
                rows[n["y"] - oy] = r[: n["x"] - ox] + "E" + r[n["x"] - ox + 1 :]
        r = rows[py - oy]
        rows[py - oy] = r[: px - ox] + "@" + r[px - ox + 1 :]

        nearby: dict[str, Any] = {}
        for (x, y), t in self.tiles.items():
            if t == "Trees" and self.tiles.get((x, y + 1)) == "Trees":
                continue
            if t in ("Trees", "CopperOre", "WorkBenches", "Furnaces"):
                nearby.setdefault(t, []).append((x, y))
        nearby_out = {
            k: {"count": len(v), "closest": sorted(v, key=lambda c: abs(c[0] - px) + abs(c[1] - py))[:3]} for k, v in nearby.items()
        }
        return {
            "ok": True,
            "player": self.summary(),
            "world": {**self.clock(), "blood_moon": False, "biomes": ["forest", "surface"], "spawn": [50, GROUND_Y - 2],
                      "surface_y": GROUND_Y + 10, "rock_layer_y": GROUND_Y + 40, "size": [200, 300], "bosses_defeated": [],
                      "town_npcs": ["Guide" + (" (home)" if self.houses else " (homeless)")], "max_life": self.player["max_hp"],
                      "defense": 2 * len(self.equipment)},
            "equipment": list(self.equipment),
            "inventory": self.inv_list(),
            "map": {"origin": [ox, oy], "legend": "@ you, # solid, T tree, $ ore, + furniture, E hostile, . air", "rows": rows},
            "npcs": [{**n, "distance": abs(n["x"] - px)} for n in self.npcs],
            "nearby": nearby_out,
            "craftable_now": self.craftable(),
        }

    def clock(self) -> dict[str, Any]:
        """Day 8:30 at tick 0; a day is 54000 ticks from 4:30, a night 32400."""
        t = (self.ticks + 4 * 3600) % (54000 + 32400)  # ticks since 4:30 am
        hours = 4.5 + t / 3600
        return {"time": f"{int(hours) % 24:02d}:{int(hours % 1 * 60):02d}", "is_day": t < 54000}

    # ---- skills ------------------------------------------------------------------------

    def skill(self, name: str, a: dict[str, Any]) -> dict[str, Any]:
        fn = getattr(self, f"s_{name}", None)
        if fn is None:
            return {"ok": False, "error": f"unknown skill '{name}'"}
        # Bodyguard, like the mod's: enemies within 6 tiles are fought off first (a few hits taken).
        fought = ""
        near = [n for n in self.npcs if n.get("hostile") and abs(n["x"] - self.player["x"]) <= 6 and name != "attack"]
        if near and self.player["hp"] >= self.player["max_hp"] * 0.5:
            for n in near:
                self.npcs.remove(n)
                self.player["hp"] -= 8
            fought = f" [guard: {', '.join('killed ' + n['name'] for n in near)}]"
        try:
            ok, msg, ticks = fn(**a)
        except TypeError as e:
            return {"ok": False, "error": str(e)}
        self.ticks += ticks
        msg += fought
        return {"ok": ok, "skill": name, "message": msg, "ticks": ticks, "player": self.summary(), "inventory": self.inv_list()}

    def _walk(self, x: int) -> int:
        dist = abs(x - self.player["x"])
        self.player["x"] = x
        return dist * 8

    def s_move_to(self, x: int, y: int, tolerance: int = 1):
        if not 0 <= x < 200:
            return False, f"stuck at ({self.player['x']},{self.player['y']}): world edge", 60
        return True, f"arrived at ({x},{self.player['y']})", self._walk(x)

    def s_mine(self, x: int, y: int):
        t = self.tiles.get((x, y))
        if not t:
            return False, f"nothing to mine at ({x},{y})", 1
        ticks = self._walk(x - 1)
        if t == "Trees":
            yy = y
            while self.tiles.get((x, yy)) == "Trees":
                del self.tiles[(x, yy)]
                yy -= 1
            # forests are big: a new tree grows further out
            nx = 100 + (len(self.tiles) * 7) % 90
            for k in range(GROUND_Y - 8, GROUND_Y):
                self.tiles[(nx, k)] = "Trees"
            self.inventory["Wood"] = self.inventory.get("Wood", 0) + 8
            self.inventory["Acorn"] = self.inventory.get("Acorn", 0) + 1
        else:
            del self.tiles[(x, y)]
            item, n = DROPS.get(t, (t, 1))
            self.inventory[item] = self.inventory.get(item, 0) + n
        return True, f"broke {t} at ({x},{y})", ticks + 40

    def s_mine_nearest(self, target: str, count: int = 1):
        key = {"tree": "Trees", "trees": "Trees", "wood": "Trees", "ore": "CopperOre"}.get(target.lower(), target)
        mined, ticks = 0, 0
        for _ in range(count):
            px = self.player["x"]
            cands = [(x, y) for (x, y), t in self.tiles.items() if t == key and not (key == "Trees" and self.tiles.get((x, y + 1)) == "Trees")]
            if not cands:
                break
            x, y = min(cands, key=lambda c: abs(c[0] - px) + abs(c[1] - self.player["y"]) * 2.5)
            ok, _, t = self.s_mine(x, y)
            ticks += t
            mined += ok
        if mined == 0:
            return False, f"no reachable {target} within 60 tiles", 10
        return True, f"mined {mined}/{count}", ticks

    def s_dig_down(self, target: str = "Stone", max_depth: int = 80):
        if any(t == target for t in self.tiles.values()):
            return True, f"dug down 0 tiles; {target} is nearby now", 10
        return False, f"dug {max_depth} tiles down and found no {target}", 60 * max_depth

    def s_place(self, item: str, x: int, y: int):
        if self.inventory.get(item, 0) <= 0:
            return False, f"no {item} in inventory", 1
        if item not in PLACES:
            return False, f"{item} is not placeable", 1
        if (x, y) in self.tiles:
            return False, f"({x},{y}) is occupied by {self.tiles[(x, y)]}; mine it first", 1
        if (x, y + 1) not in self.tiles:
            return False, f"could not place {item} at ({x},{y}): needs support below", 1
        self.tiles[(x, y)] = PLACES[item]
        self.inventory[item] -= 1
        return True, f"placed {item} at ({x},{y})", self._walk(x + 2) + 15

    def s_craft(self, item: str, count: int = 1):
        if item not in RECIPES:
            return False, f"unknown item '{item}' (mock knows {', '.join(RECIPES)})", 1
        ing, station, out = RECIPES[item]
        made = 0
        while made < count and self.station_near(station) and all(self.inventory.get(k, 0) >= v for k, v in ing.items()):
            for k, v in ing.items():
                self.inventory[k] -= v
            self.inventory[item] = self.inventory.get(item, 0) + out
            made += 1
        if made == count:
            return True, f"crafted {item} x{made}", 1
        missing = [f"{k} {self.inventory.get(k, 0)}/{v}" for k, v in ing.items() if self.inventory.get(k, 0) < v]
        why = (f" missing station: {station} (stand next to it)" if not self.station_near(station) else "") + (
            f" missing: {', '.join(missing)}" if missing else "")
        return made > 0, f"crafted {made}/{count} {item}.{why}", 1

    def s_attack(self, target: str = "nearest", max_kills: int = 1, desperate: bool = False):
        if not desperate and self.player["hp"] < self.player["max_hp"] * 0.3:
            return False, f"retreating: health {self.player['hp']}/{self.player['max_hp']}", 1
        if not self.npcs:
            return False, "no hostile within 40 tiles", 1
        n = self.npcs.pop(0)
        self.inventory["Gel"] = self.inventory.get("Gel", 0) + 2
        self.player["hp"] -= 12
        return True, f"killed 1 ({n['name']})", self._walk(n["x"] - 1) + 90

    def s_use_item(self, item: str):
        if self.inventory.get(item, 0) <= 0:
            return False, f"no '{item}' in inventory", 1
        if item in ("LesserHealingPotion", "HealingPotion", "Mushroom"):
            self.inventory[item] -= 1
            self.player["hp"] = min(self.player["max_hp"], self.player["hp"] + 50)
        return True, f"used {item}", 20

    def s_build_room(self, x: int, y: int, width: int = 10, height: int = 6, block: str = "Wood", wall: str = "WoodWall", door: str = "WoodenDoor"):
        blocks = 2 * width + 2 * (height - 2) - 3 - width  # floor row is the existing ground
        walls = (width - 2) * (height - 2)
        need = {block: blocks, wall: walls, door: 1}
        missing = [f"{n} {k} (have {self.inventory.get(k, 0)})" for k, n in need.items() if self.inventory.get(k, 0) < n]
        if missing:
            return False, f"not enough materials for a {width}x{height} room at ({x},{y}): need {', '.join(missing)}", 5
        for k, n in need.items():
            self.inventory[k] -= n
        for i in range(x, x + width):
            self.tiles.pop((i, y - 1), None)  # clear grass/trees inside (mock simplification)
        self.rooms.append((x + 1, y - height + 2, x + width - 2, y - 1))
        return True, f"built room: walls x {x}..{x + width - 1}, floor row y={y}; place furniture on row {y - 1}", 600

    def s_check_house(self, x: int, y: int):
        for (x1, y1, x2, y2) in self.rooms:
            if x1 <= x <= x2 and y1 <= y <= y2:
                inside = {t for (i, j), t in self.tiles.items() if x1 <= i <= x2 and y1 <= j <= y2}
                missing = [n for n, t in (("chair", "Chairs"), ("table", "Tables"), ("work bench", "WorkBenches")) if t not in inside]
                if missing:
                    return False, "house not done: " + "; ".join(f"no {m} inside" for m in missing), 1
                return True, "HOUSE OK: enclosed, walls, door, chair, table, work bench.", 1
        return False, "house not done: NOT ENCLOSED: air escapes the room; close the gap with blocks", 1

    def _ensure_station(self, station: str | None) -> tuple[bool, str, int]:
        if self.station_near(station):
            return True, "", 0
        placed = [x for (x, _), t in self.tiles.items() if t == station]
        if placed:  # walk back to one already placed, like the real EnsureStation
            return True, "", self._walk(min(placed, key=lambda x: abs(x - self.player["x"])) - 1)
        item = next((k for k, v in PLACES.items() if v == station), None)
        if item is None:
            return False, f"no {station} nearby", 1
        ok, msg, t = self.s_obtain(item, self.inventory.get(item, 0) + 1) if self.inventory.get(item, 0) == 0 else (True, "", 0)
        if not ok:
            return ok, msg, t
        px, py = self.player["x"], self.player["y"]
        self.tiles[(px + 2, py + 1)] = station
        self.inventory[item] -= 1
        return True, "", t + 10

    def s_obtain(self, item: str, count: int = 1, depth: int = 0):
        ticks = 0
        for _ in range(60):
            have = self.inventory.get(item, 0)
            if have >= count:
                return True, f"have {have} {item}", ticks
            if depth > 5:
                return False, f"recipe chain for {item} is too deep", ticks
            if item not in RECIPES:
                if item.endswith("Ore"):
                    ok, msg, t = self.s_mine_vein(item[:-3], count - have)
                    ticks += t
                    if not ok:
                        return False, f"getting {count} {item}: {msg}", ticks
                    continue
                if item == "Gel":
                    self.inventory["Gel"] = have + 2
                    ticks += 300
                    continue
                tile = {"Wood": "Trees", "StoneBlock": "Stone", "DirtBlock": "Dirt"}.get(item)
                if tile is None:
                    return False, f"{item} can't be crafted or mined; it comes from chests, enemies or shops", ticks
                ok, msg, t = self.s_mine_nearest(tile, min(20, count - have) if tile != "Trees" else 1)
                ticks += t
                if not ok:
                    return False, f"getting {count} {item}: {msg}", ticks
                continue
            ing, station, out = RECIPES[item]
            batches = -(-(count - have) // out)
            for k, v in ing.items():
                if self.inventory.get(k, 0) < v * batches:
                    ok, msg, t = self.s_obtain(k, v * batches, depth + 1)
                    ticks += t
                    if not ok:
                        return False, msg, ticks
            ok, msg, t = self._ensure_station(station)
            ticks += t
            if not ok:
                return False, msg, ticks
            if t:
                continue  # making the station may have used ingredients up; re-check
            ok, msg, t = self.s_craft(item, batches)
            ticks += t
            if not ok:
                return False, f"could not craft {item}: {msg}", ticks
        return False, f"gave up on {item}", ticks

    def s_build_house(self, x: int | None = None, y: int | None = None, material: str = "wood", width: int = 10, height: int = 6,
                      extras: list[str] | None = None, new_house: bool = False):
        stone = material == "stone"
        w = max(10, min(16, width))
        if x is None:
            x = (self.houses[-1][0] + self.houses[-1][2] + 2) if (new_house and self.houses) else (self.houses[-1][0] if self.houses else self.player["x"] - 5)
        y = GROUND_Y if y is None else y
        ticks = 0
        needs = [("StoneWall" if stone else "WoodWall", 40), ("WoodenDoor", 1), ("WoodenChair", 1), ("WoodenTable", 1), ("WorkBench", 1)]
        needs += [(e, 1) for e in (extras or [])]
        needs += [("StoneBlock" if stone else "Wood", 20)]
        for item, n in needs:
            ok, msg, t = self.s_obtain(item, n)
            ticks += t
            if not ok:
                return False, f"{msg} (house site ({x},{y}); call build_house again to resume)", ticks
        for item, n in needs:
            self.inventory[item] -= n
        lit = self.inventory.get("Torch", 0) > 0
        if lit:
            self.inventory["Torch"] -= 1
        if not any(h[0] == x for h in self.houses):
            self.houses.append((x, y, w))
        self.rooms.append((x + 1, y - height + 2, x + w - 2, y - 1))
        valid = " NPC-VALID: valid NPC house (84 tiles)." if lit else " Not NPC-valid yet: missing light source (Torch)."
        mat = "StoneBlock" if stone else "Wood"
        return True, f"HOUSE OK: enclosed, walls, door, chair, table, work bench. interior 50 tiles. Has light.{valid} House at floor row y={y}, walls x {x}..{x + w - 1}, {mat}.", ticks + 1800

    def s_tunnel_to(self, x: int, y: int, tolerance: int = 2):
        if y > GROUND_Y + 200:
            return False, "lava ahead at that depth; stopped", 300
        dist = abs(x - self.player["x"]) + abs(y - self.player["y"])
        self.player["x"], self.player["y"] = x, y
        return True, f"tunneled to ({x},{y})", dist * 20

    def s_mine_vein(self, target: str = "ore", count: int = 30, radius: int = 50):
        metal = WORLD_ORES[0] if target == "ore" else target
        if metal not in WORLD_ORES:
            return False, f"no {metal} ore within {radius} tiles", 120
        self.player["y"] = max(self.player["y"], GROUND_Y + 15)
        self.inventory[f"{metal}Ore"] = self.inventory.get(f"{metal}Ore", 0) + count
        return True, f"got {count}/{count} ore from 2 vein(s): +{count} {metal}Ore", count * 40

    def s_collect_life_crystals(self, max: int = 3, radius: int = 80):
        if self.player["y"] < GROUND_Y + 30:
            return False, f"no life crystals within {radius} tiles; they generate below y={GROUND_Y + 30}; dig down first", 60
        got = min(max, (400 - self.player["max_hp"]) // 20, 2)
        self.player["max_hp"] += 20 * got
        self.player["hp"] += 20 * got
        return (got > 0), f"+{20 * got} max life (now {self.player['max_hp']}/400)", 1200

    def s_return_to_surface(self):
        self.player["y"] = GROUND_Y - 2
        return True, "back on the surface", 600

    def s_equip_best(self):
        worn = [k for k in list(self.inventory) if k.endswith(ARMOR) and self.inventory[k] > 0]
        for k in worn:
            self.inventory[k] -= 1
            self.equipment.append(k)
        return True, ("equipped " + ", ".join(worn)) if worn else "already wearing the best gear", 1

    def s_loot_chests(self, radius: int = 60, max: int = 10):
        if self.looted:
            return False, f"no unlooted chests within {radius} tiles", 30
        self.looted = True
        for k, v in (("LesserHealingPotion", 4), ("SilverCoin", 75), ("Shuriken", 50)):
            self.inventory[k] = self.inventory.get(k, 0) + v
        return True, "looted 2 chests: 4 LesserHealingPotion, 75 SilverCoin, 50 Shuriken", 900

    def s_store_items(self, keep: list[str] | None = None):
        moved = self.inventory.pop("Shuriken", 0)
        return True, f"stored {moved} Shuriken" if moved else "nothing to store", 60

    def s_set_spawn(self):
        return False, "no Bed within 30 tiles or in inventory: craft one at a Sawmill: 15 Wood + 5 Silk", 1

    def s_dig_in(self, wait_seconds: float = 60, until_hp: float = 0.8, until_day: bool = False):
        x, y = self.player["x"], self.player["y"]
        self.player["hp"] = max(self.player["hp"], int(self.player["max_hp"] * until_hp))
        return True, f"dug in at ({x},{y + 4}), sealed 2/2 open tiles; hp regenerated. Roof at ({x}, {y + 1}) ({x + 1}, {y + 1}): mine it to get out.", int(wait_seconds * 60)

    def scenario(self, b: dict[str, Any]) -> dict[str, Any]:
        """Mock of the mod's POST /scenario: health, time, enemies at offsets (no physics)."""
        if b.get("clear", True):
            self.npcs = []
        if b.get("fresh_player"):
            self.inventory = {"CopperShortsword": 1, "CopperPickaxe": 1, "CopperAxe": 1}
            self.player["max_hp"] = self.player["hp"] = 100
        for k, v in (b.get("give") or {}).items():
            self.inventory[k] = self.inventory.get(k, 0) + int(v)
        if "hp" in b:
            self.player["hp"] = max(1, min(self.player["max_hp"], int(b["hp"])))
        if "time" in b:
            self.ticks = {"dawn": 0, "noon": 27000, "dusk": 54000, "midnight": 70200}[b["time"]]
        spawned = []
        for i, s in enumerate(b.get("spawn", [])):
            n = {"name": s["npc"], "index": 100 + i, "x": self.player["x"] + s.get("dx", 3), "y": self.player["y"] + s.get("dy", 0),
                 "hp": 45, "max_hp": 45, "hostile": True, "boss": False}
            self.npcs.append(n)
            spawned.append({"npc": n["name"], "index": n["index"], "at": [n["x"], n["y"]]})
        return {"ok": True, "spawned": spawned}

    def s_wait(self, seconds: float = 1):
        return True, f"waited {seconds}s", int(seconds * 60)


def make_server(port: int = 7979, world: World | None = None) -> ThreadingHTTPServer:
    world = world or World()

    class Handler(BaseHTTPRequestHandler):
        def _send(self, obj: Any) -> None:
            body = json.dumps(obj).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:
            with world.lock:
                if self.path == "/health":
                    self._send({"ok": True, "version": "mock", "in_world": True, "freeze": world.freeze, "world": "Mock World", "player": "Bot"})
                elif self.path == "/state":
                    self._send(world.state())
                else:
                    self._send({"ok": False, "error": f"no route GET {self.path}"})

        def do_POST(self) -> None:
            n = int(self.headers.get("Content-Length") or 0)
            body = json.loads(self.rfile.read(n) or b"{}")
            with world.lock:
                if self.path == "/skill":
                    self._send(world.skill(body["name"], body.get("args") or {}))
                elif self.path == "/config":
                    world.freeze = bool(body.get("freeze", world.freeze))
                    self._send({"ok": True, "freeze": world.freeze})
                elif self.path == "/scenario":
                    self._send(world.scenario(body))
                elif self.path == "/say":
                    world.said.append(body.get("text", ""))
                    self._send({"ok": True})
                elif self.path == "/status":
                    world.status = body.get("title", "")
                    self._send({"ok": True})
                elif self.path == "/save":
                    world.saves += 1
                    self._send({"ok": True})
                elif self.path == "/cancel":
                    self._send({"ok": True})
                else:
                    self._send({"ok": False, "error": f"no route POST {self.path}"})

        def log_message(self, *args: Any) -> None:
            pass

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    server.world = world  # type: ignore[attr-defined]
    return server


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 7979
    print(f"mock bridge on http://127.0.0.1:{port}")
    make_server(port).serve_forever()
