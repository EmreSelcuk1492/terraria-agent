"""Tool definitions Claude sees, and how each call is carried out against the bridge.

Every game action is one bridge skill (see bridge/TerrariaAgentBridge/SkillFactory.cs).
`observe` and `remember` are handled here in Python.
"""

from __future__ import annotations

import json
from collections import Counter
from typing import Any

from .bridge import BridgeClient, BridgeError
from .memory import Memory


def _tool(name: str, description: str, props: dict[str, Any]) -> dict[str, Any]:
    return {
        "name": name,
        "description": description,
        "strict": True,
        "input_schema": {
            "type": "object",
            "properties": props,
            "required": list(props),
            "additionalProperties": False,
        },
    }


_INT = {"type": "integer"}
_STR = {"type": "string"}

TOOLS: list[dict[str, Any]] = [
    _tool(
        "observe",
        "Look around: player stats, time, biome, inventory, a 61x35 ASCII map centred on you, nearby NPCs, "
        "the closest trees/ores/chests/stations within ~60 tiles, and what you can craft right now. "
        "Game time is frozen while you think, so observing is free.",
        {},
    ),
    _tool(
        "move_to",
        "Walk to a tile (absolute tile coords, y grows downward). Jumps small steps, drops through platforms. "
        "Not a pathfinder: if a wall, pit, or cliff is in the way it stops and says what blocks it.",
        {"x": _INT, "y": _INT, "tolerance": {"type": "integer", "description": "Tiles of slack; 1 is typical."}},
    ),
    _tool(
        "tb_nav",
        "Real pathfinder (TerraBlind's navigator, needs the TerraBlind mod): plans jumps, drops, digging and short "
        "bridges across caves and cliffs where move_to gets stuck. Slower to start (about 1-2s to plan). "
        "mode: snap (land within ~1.5 tiles, default), stand (stand exactly on the tile), reach (get within use "
        "range), mine (until that tile is gone).",
        {"x": _INT, "y": _INT, "mode": {"type": "string", "enum": ["snap", "stand", "reach", "mine"]}},
    ),
    _tool(
        "mine",
        "Break one tile at (x, y) with the right tool (axe for trees, pickaxe otherwise), walking into reach "
        "first and picking up the drop. Cutting a tree's trunk base fells the whole tree.",
        {"x": _INT, "y": _INT},
    ),
    _tool(
        "mine_nearest",
        "Find and mine the nearest `count` tiles of a type. target: an internal tile name like Trees, CopperOre, "
        "TinOre, IronOre, LeadOre, Stone, Dirt, ClayBlock, or 'ore' for any ore. Trees are cut at the base.",
        {"target": _STR, "count": _INT},
    ),
    _tool(
        "place",
        "Place a block, platform, wall, torch, or furniture item from your inventory at tile (x, y). "
        "Furniture is multi-tile: give a tile whose floor below is solid.",
        {"item": _STR, "x": _INT, "y": _INT},
    ),
    _tool(
        "obtain",
        "BEST FIRST CHOICE for getting any item. Ends with at least `count` of `item` in your inventory, all in one call: "
        "cuts trees, mines ore/stone, fights slimes for Gel, crafts every intermediate, and places or walks to the "
        "stations each recipe needs. E.g. obtain WoodenTable 1, obtain WoodWall 32, obtain CopperPickaxe 1. "
        "`count` is the total you want to hold, not how many more.",
        {"item": _STR, "count": _INT},
    ),
    _tool(
        "craft",
        "Craft an item `count` times from what you already carry. Finds or places the station by itself "
        "(walks to a nearby one, places one from your inventory, or makes a WorkBench from Wood). Doesn't gather: "
        "use obtain for that. On failure it lists missing ingredients.",
        {"item": _STR, "count": _INT},
    ),
    _tool(
        "attack",
        "Fight hostile NPCs with your best weapon until `max_kills` die or nothing is left within 40 tiles. "
        "target: 'nearest' or part of an NPC name (e.g. 'Slime'). Stops by itself below 30% health unless "
        "desperate=true (use that when an enemy is already on top of you and running isn't possible).",
        {"target": _STR, "max_kills": _INT, "desperate": {"type": "boolean"}},
    ),
    _tool(
        "dig_in",
        "Emergency shelter without a house: dig a 2-wide hole 4 tiles down where you stand, seal the roof and open "
        "sides with blocks, then wait inside until health reaches `until_hp` (fraction, default 0.8) and, with "
        "until_day=true, until morning (at most `wait_seconds`, default 60, max 600). Works at low health. "
        "Mine the roof tiles it reports (or return_to_surface) to get out.",
        {"wait_seconds": {"type": "number"}, "until_hp": {"type": "number"}, "until_day": {"type": "boolean"}},
    ),
    _tool(
        "use_item",
        "Use one item: drink a potion, eat food, use a summon item, etc.",
        {"item": _STR},
    ),
    _tool(
        "wait",
        "Let game time pass (0.1 to 20 seconds), e.g. to wait out a fall or let a potion cooldown end.",
        {"seconds": {"type": "number"}},
    ),
    _tool(
        "dig_down",
        "Dig straight down under your feet until `target` tiles (e.g. Stone, IronOre) are nearby, up to `max_depth` "
        "tiles. Use it when mine_nearest finds none in view. obtain already does this by itself.",
        {"target": _STR, "max_depth": _INT},
    ),
    _tool(
        "build_room",
        "Build an enclosed room in one go: solid block frame, background walls inside, a door in the right wall. "
        "(x, y) is the bottom-left corner: x = left wall column, y = floor row (usually the ground row under your feet, "
        "about your y+2). Width/height include the frame (10 x 6 is a good starter room: needs ~26 Wood, 32 WoodWall, "
        "1 WoodenDoor; existing ground is reused). On failure it lists missing materials.",
        {"x": _INT, "y": _INT, "width": _INT, "height": _INT,
         "block": {"type": "string", "description": "block item for the frame, e.g. Wood"},
         "wall": {"type": "string", "description": "wall item, e.g. WoodWall"},
         "door": {"type": "string", "description": "door item, e.g. WoodenDoor"}},
    ),
    _tool(
        "build_house",
        "A whole furnished house in one call: picks a flat spot near you (or x, y = left wall column, floor row), "
        "gathers and crafts everything (digs stone itself for material 'stone'), builds the room, places a work bench, "
        "table, chair, a torch, plus `extras` (Furnace, Chest, Bed, IronAnvil, LeadAnvil, Sawmill, Loom; about two fit), "
        "then runs check_house incl. Terraria's own NPC-housing check. width 10-16, height 6-9. new_house=true builds "
        "another house next to the previous ones; false resumes the last site after an interruption. "
        "Pass null for x and y to auto-pick.",
        {"x": {"type": ["integer", "null"]}, "y": {"type": ["integer", "null"]},
         "material": {"type": "string", "enum": ["wood", "stone"]}, "width": _INT, "height": _INT,
         "extras": {"type": "array", "items": _STR}, "new_house": {"type": "boolean"}},
    ),
    _tool(
        "check_house",
        "Inspect the room containing tile (x, y) (pick an air tile inside, e.g. one above the floor). Reports whether it is "
        "enclosed, fully walled, has a door, a chair, a table and a work bench, and exactly what is missing.",
        {"x": _INT, "y": _INT},
    ),
    _tool(
        "tunnel_to",
        "Dig a walkable tunnel (with stairs) to a tile that move_to can't reach, e.g. ore or a chest underground. "
        "Stops before lava. Places torches underground when you carry some.",
        {"x": _INT, "y": _INT, "tolerance": _INT},
    ),
    _tool(
        "mine_vein",
        "Find the nearest ore vein (target: Copper, Tin, Iron, Lead, Silver, Tungsten, Gold, Platinum, or 'ore'), "
        "tunnel to it and mine the whole vein, then the next, until `count` ore are gained or none are left in radius.",
        {"target": _STR, "count": _INT, "radius": _INT},
    ),
    _tool(
        "collect_life_crystals",
        "Tunnel to the nearest Life Crystals, break them and use them (+20 max life each, up to 400). They are deep "
        "underground; dig down first if none are in range.",
        {"max": _INT, "radius": _INT},
    ),
    _tool(
        "return_to_surface",
        "Get back up to the surface from underground (climbs out, builds pillars/stairs, or uses a Magic Mirror).",
        {},
    ),
    _tool(
        "equip_best",
        "Put on the best armor and accessories you carry and move your best weapon, pickaxe and axe into the hotbar.",
        {},
    ),
    _tool(
        "loot_chests",
        "Walk to unlooted chests within `radius` tiles (not your own near spawn) and take everything in them.",
        {"radius": _INT, "max": _INT},
    ),
    _tool(
        "store_items",
        "Put loot and spare materials into the chest(s) near you (places a Chest from your inventory if none). Never "
        "stores tools, weapons, armor, torches, potions, wood, stone, ore, bars, or anything listed in `keep`.",
        {"keep": {"type": "array", "items": _STR}},
    ),
    _tool(
        "set_spawn",
        "Set your respawn point at a Bed in a valid house nearby (places one from your inventory if needed).",
        {},
    ),
    _tool(
        "remember",
        "Save a short lesson for future runs (e.g. 'Furnace needs 20 Stone + 4 Wood + 3 Torch at a WorkBench'). "
        "Only for durable, reusable facts about the game or about how these tools behave.",
        {"lesson": _STR},
    ),
]

SKILL_TOOLS = {"move_to", "tb_nav", "mine", "mine_nearest", "dig_down", "place", "obtain", "craft", "attack", "use_item", "wait", "build_room",
               "build_house", "check_house", "tunnel_to", "dig_in", "mine_vein", "collect_life_crystals", "return_to_surface",
               "equip_best", "loot_chests", "store_items", "set_spawn"}


class ToolExecutor:
    def __init__(self, bridge: BridgeClient, memory: Memory):
        self.bridge = bridge
        self.memory = memory
        self._last_inventory: Counter[str] | None = None

    def run(self, name: str, args: dict[str, Any]) -> tuple[str, bool]:
        """Returns (text for the tool_result, is_error)."""
        try:
            if name == "observe":
                state = self.bridge.state()
                if not state.get("ok", False):
                    return f"observe failed: {state.get('error')}", True
                self._last_inventory = _inventory_counter(state.get("inventory", []))
                return format_observation(state), False
            if name == "remember":
                self.memory.add(str(args.get("lesson", "")))
                return "saved", False
            if name in SKILL_TOOLS:
                return self._skill(name, args)
            return f"unknown tool {name}", True
        except BridgeError as e:
            return str(e), True

    def _skill(self, name: str, args: dict[str, Any]) -> tuple[str, bool]:
        result = self.bridge.skill(name, args)
        if "error" in result and "message" not in result:
            return f"{name} rejected: {result['error']}", True

        lines = [f"{'OK' if result.get('ok') else 'FAILED'}: {result.get('message', '')} ({result.get('ticks', 0) / 60:.1f}s game time)"]
        player = result.get("player")
        if player:
            lines.append(
                f"you: ({player['x']},{player['y']}) hp {player['hp']}/{player['max_hp']}"
                + (" DEAD" if player.get("dead") else "")
                + (f", holding {player['holding']}" if player.get("holding") else "")
            )
        if "inventory" in result:
            now = _inventory_counter(result["inventory"])
            if self._last_inventory is not None:
                delta = now - self._last_inventory
                lost = self._last_inventory - now
                changes = [f"+{n} {k}" for k, n in delta.items()] + [f"-{n} {k}" for k, n in lost.items()]
                if changes:
                    lines.append("inventory: " + ", ".join(changes))
            self._last_inventory = now
        return "\n".join(lines), not result.get("ok", False)


def _inventory_counter(items: list[dict[str, Any]]) -> Counter[str]:
    c: Counter[str] = Counter()
    for it in items:
        c[it["item"]] += it["stack"]
    return c


def format_observation(s: dict[str, Any]) -> str:
    """Compact text rendering. The map gets y labels and an x ruler so coordinates are easy to read off."""
    p, w, m = s["player"], s["world"], s["map"]
    ox, oy = m["origin"]
    out = [
        f"YOU ({p['x']},{p['y']}) hp {p['hp']}/{p['max_hp']} mana {p['mana']}/{p['max_mana']} def {p['defense']}"
        f"{' on ground' if p.get('on_ground') else ' airborne'}{', in water' if p.get('in_water') else ''}"
        f"{', holding ' + p['holding'] if p.get('holding') else ''}{' DEAD' if p.get('dead') else ''}",
        f"WORLD {w['time']} {'day' if w['is_day'] else 'NIGHT'}{' BLOOD MOON' if w.get('blood_moon') else ''}, "
        f"biome {'/'.join(w['biomes'])}, spawn {tuple(w['spawn'])}, surface y={w['surface_y']}, "
        f"bosses down: {', '.join(w['bosses_defeated']) or 'none'}",
        "INVENTORY " + "; ".join(_item_str(i) for i in s["inventory"]),
        "CRAFTABLE NOW " + (", ".join(s["craftable_now"]) or "nothing"),
    ]
    if s.get("npcs"):
        out.append("NPCS " + "; ".join(
            f"{n['name']} ({n['x']},{n['y']}) {n['hp']}/{n['max_hp']}hp d{n['distance']}"
            + (" HOSTILE" if n["hostile"] else "") + (" BOSS" if n.get("boss") else "")
            for n in s["npcs"]
        ))
    if s.get("nearby"):
        out.append("NEARBY " + "; ".join(
            f"{name} x{v['count']} closest {', '.join(f'({x},{y})' for x, y in v['closest'])}"
            for name, v in sorted(s["nearby"].items())
        ))

    rows = m["rows"]
    width = len(rows[0]) if rows else 0
    ruler = "".join("|" if (ox + i) % 10 == 0 else " " for i in range(width))
    labels = [" "] * (width + 6)
    for i in range(width):
        if (ox + i) % 10 == 0:
            for j, ch in enumerate(f"x={ox + i}"):
                if i + j < len(labels):
                    labels[i + j] = ch
    out.append(f"MAP origin=({ox},{oy}), legend: {m['legend']}")
    out.append("       " + "".join(labels).rstrip())
    out.append("       " + ruler)
    for i, row in enumerate(rows):
        out.append(f"y={oy + i:<5}{row}")
    return "\n".join(out)


def _item_str(i: dict[str, Any]) -> str:
    extras = [f"{k} {i[k]}" for k in ("pick", "axe", "hammer", "damage", "heals") if k in i]
    return f"{i['item']} x{i['stack']}" + (f" ({', '.join(extras)})" if extras else "")


def tool_result(tool_use_id: str, text: str, is_error: bool) -> dict[str, Any]:
    block: dict[str, Any] = {"type": "tool_result", "tool_use_id": tool_use_id, "content": text}
    if is_error:
        block["is_error"] = True
    return block


def dumps(obj: Any) -> str:
    return json.dumps(obj, default=str)
