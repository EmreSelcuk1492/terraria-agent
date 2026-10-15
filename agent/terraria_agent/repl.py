"""Drive the bridge by hand, no model. The free way to test skills in game.

    > observe
    > mine_nearest Trees 2
    > move_to 120 230 1
    > craft WorkBench 1
    > place WorkBench 118 231
"""

from __future__ import annotations

import shlex

from .bridge import BridgeClient
from .memory import Memory
from .tools import TOOLS, ToolExecutor

_SCHEMAS = {t["name"]: t["input_schema"] for t in TOOLS}


def parse(line: str) -> tuple[str, dict]:
    parts = shlex.split(line)
    name, values = parts[0], parts[1:]
    if name not in _SCHEMAS:
        raise ValueError(f"unknown command '{name}'. Commands: {', '.join(_SCHEMAS)}")
    props = _SCHEMAS[name]["properties"]
    args = {}
    for key, value in zip(props, values):
        kind = props[key]["type"]
        args[key] = int(value) if kind == "integer" else float(value) if kind == "number" else value
    missing = [k for k in props if k not in args]
    if missing:
        raise ValueError(f"usage: {name} {' '.join(props)}")
    return name, args


def repl(bridge: BridgeClient, memory: Memory) -> int:
    tools = ToolExecutor(bridge, memory)
    print("manual mode. Commands: " + ", ".join(f"{n} {' '.join(s['properties'])}".strip() for n, s in _SCHEMAS.items()))
    print("Ctrl-D to quit.")
    while True:
        try:
            line = input("> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            bridge.cancel()
            return 0
        if not line:
            continue
        try:
            name, args = parse(line)
        except ValueError as e:
            print(e)
            continue
        text, _ = tools.run(name, args)
        print(text)
