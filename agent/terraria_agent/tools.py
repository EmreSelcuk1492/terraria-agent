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
        "craft",
        "Craft an item `count` times. You must be standing near the required crafting station "
        "(e.g. WorkBenches, Furnaces, Anvils). On failure it lists missing ingredients and stations.",
        {"item": _STR, "count": _INT},
    ),
    _tool(
        "attack",
        "Fight hostile NPCs with your best weapon until `max_kills` die or nothing is left within 40 tiles. "
        "target: 'nearest' or part of an NPC name (e.g. 'Slime'). Stops by itself below 30% health.",
        {"target": _STR, "max_kills": _INT},
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
        "check_house",
        "Inspect the room containing tile (x, y) (pick an air tile inside, e.g. one above the floor). Reports whether it is "
        "enclosed, fully walled, has a door, a chair, a table and a work bench, and exactly what is missing.",
        {"x": _INT, "y": _INT},
    ),
    _tool(
        "remember",
        "Save a short lesson for future runs (e.g. 'Furnace needs 20 Stone + 4 Wood + 3 Torch at a WorkBench'). "
        "Only for durable, reusable facts about the game or about how these tools behave.",
        {"lesson": _STR},
    ),
]

SKILL_TOOLS = {"move_to", "mine", "mine_nearest", "place", "craft", "attack", "use_item", "wait", "build_room", "check_house"}


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
