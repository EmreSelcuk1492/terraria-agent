"""python -m terraria_agent --goal "Build a work bench and a wooden sword" """

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from .agent import MODEL, Agent
from .bridge import DEFAULT_URL, BridgeClient, BridgeError
from .memory import Memory

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    ap = argparse.ArgumentParser(prog="terraria_agent")
    ap.add_argument("--goal", required=True)
    ap.add_argument("--bridge", default=DEFAULT_URL)
    ap.add_argument("--model", default=MODEL)
    ap.add_argument("--effort", default="medium", choices=["low", "medium", "high", "xhigh", "max"])
    ap.add_argument("--max-turns", type=int, default=150)
    ap.add_argument("--no-freeze", action="store_true", help="let the world keep running while Claude thinks")
    ap.add_argument("--memory", type=Path, default=ROOT / "lessons.md")
    ap.add_argument("--logs", type=Path, default=ROOT / "runs")
    a = ap.parse_args()

    bridge = BridgeClient(a.bridge)
    try:
        health = bridge.health()
    except BridgeError as e:
        print(f"{e}\nIs Terraria running with the Terraria Agent Bridge mod enabled?", file=sys.stderr)
        return 1
    if not health.get("in_world"):
        print("Bridge is up but you're not in a world yet. Load a single-player world, then rerun.", file=sys.stderr)
        return 1

    agent = Agent(bridge, Memory(a.memory), model=a.model, effort=a.effort, log_dir=a.logs)
    if not a.no_freeze:
        bridge.set_freeze(True)
    try:
        agent.run(a.goal, max_turns=a.max_turns)
    except KeyboardInterrupt:
        print("\ninterrupted")
        bridge.cancel()
    finally:
        bridge.set_freeze(False)
        bridge.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
