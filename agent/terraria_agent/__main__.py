"""
python -m terraria_agent --backend ollama --goal "Craft a work bench and a wooden sword"   # free, local
python -m terraria_agent --backend manual                                               # you type the tool calls
python -m terraria_agent --backend claude --model claude-haiku-5-5 --max-cost 0.50 --goal "..."
python -m terraria_agent --backend openai --base-url https://openrouter.ai/api/v1 \\
    --api-key-env OPENROUTER_API_KEY --model qwen/qwen3-235b-a22b --goal "..."
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from .agent import Agent
from .bridge import DEFAULT_URL, BridgeClient, BridgeError
from .memory import Memory

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MODEL = {"claude": "claude-opus-5-5", "ollama": "qwen3:8b", "openai": "", "manual": ""}


def make_backend(a: argparse.Namespace):
    from . import backends

    model = a.model or DEFAULT_MODEL[a.backend]
    if a.backend == "claude":
        return backends.ClaudeBackend(model=model, effort=a.effort)
    if a.backend == "ollama":
        return backends.OllamaBackend(model=model, base_url=a.base_url or "http://127.0.0.1:11434", num_ctx=a.num_ctx,
                                      keep_results=a.keep_results, no_think=a.no_think)
    if a.backend == "openai":
        if not (a.base_url and model):
            sys.exit("--backend openai needs --base-url and --model")
        return backends.OpenAIBackend(model=model, base_url=a.base_url, api_key_env=a.api_key_env,
                                      keep_results=a.keep_results, no_think=a.no_think)
    raise ValueError(a.backend)


def main() -> int:
    ap = argparse.ArgumentParser(prog="terraria_agent")
    ap.add_argument("--goal", help="what the agent should do (not needed for --backend manual)")
    ap.add_argument("--backend", default="claude", choices=["claude", "ollama", "openai", "manual"])
    ap.add_argument("--model", help="model id; defaults: claude-opus-5-5 / qwen3:8b")
    ap.add_argument("--bridge", default=DEFAULT_URL)
    ap.add_argument("--max-turns", type=int, default=150)
    ap.add_argument("--max-cost", type=float, help="stop once estimated spend reaches this many dollars")
    ap.add_argument("--until", choices=["house"], help="don't stop until the goal is verified in game (house = check_house passes)")
    ap.add_argument("--no-freeze", action="store_true", help="let the world keep running while the model thinks")
    ap.add_argument("--memory", type=Path, default=ROOT / "lessons.md")
    ap.add_argument("--logs", type=Path, default=ROOT / "runs")
    g = ap.add_argument_group("claude")
    g.add_argument("--effort", default="medium", choices=["low", "medium", "high", "xhigh", "max"])
    g = ap.add_argument_group("open models (ollama / openai)")
    g.add_argument("--base-url", help="ollama default http://127.0.0.1:11434; openai-compatible e.g. http://127.0.0.1:1234/v1")
    g.add_argument("--api-key-env", default="OPENAI_API_KEY", help="env var holding the key for --backend openai")
    g.add_argument("--num-ctx", type=int, default=16384, help="ollama context window in tokens")
    g.add_argument("--keep-results", type=int, default=4, help="tool results kept in full; older ones are shortened")
    g.add_argument("--no-think", action="store_true", help="ask Qwen3-style models to skip thinking (faster, dumber)")
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

    if a.backend == "manual":
        from .repl import repl

        return repl(bridge, Memory(a.memory))

    if not a.goal:
        ap.error("--goal is required")
    agent = Agent(bridge, Memory(a.memory), make_backend(a), log_dir=a.logs, max_cost=a.max_cost, until=a.until)
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
