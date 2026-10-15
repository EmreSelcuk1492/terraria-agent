"""The agent loop: observe -> the model picks a skill -> the bridge runs it in game -> result goes back."""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .backends import Backend, Call, Turn
from .bridge import BridgeClient
from .memory import Memory
from .prompts import system_prompt
from .tools import ToolExecutor


@dataclass
class RunStats:
    price: tuple[float, float, float, float] = (0, 0, 0, 0)
    turns: int = 0
    tool_calls: int = 0
    failures: int = 0
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read: int = 0
    cache_write: int = 0
    started: float = field(default_factory=time.time)

    def add(self, t: Turn) -> None:
        self.input_tokens += t.input_tokens
        self.output_tokens += t.output_tokens
        self.cache_read += t.cache_read
        self.cache_write += t.cache_write

    @property
    def cost(self) -> float:
        pi, po, pr, pw = self.price
        return (self.input_tokens * pi + self.output_tokens * po + self.cache_read * pr + self.cache_write * pw) / 1e6

    def line(self) -> str:
        return (
            f"turns {self.turns}, tool calls {self.tool_calls} ({self.failures} failed), "
            f"tokens in {self.input_tokens} / cached {self.cache_read} / out {self.output_tokens}, "
            f"~${self.cost:.3f}, {time.time() - self.started:.0f}s"
        )


class Agent:
    def __init__(
        self,
        bridge: BridgeClient,
        memory: Memory,
        backend: Backend,
        log_dir: Path | None = None,
        verbose: bool = True,
        max_cost: float | None = None,
        until: str | None = None,
        freeze: bool = True,
    ):
        self.bridge = bridge
        self.memory = memory
        self.backend = backend
        self.tools = ToolExecutor(bridge, memory)
        self.verbose = verbose
        self.max_cost = max_cost
        # until="house": the run only ends when check_house passes and nothing was changed after it.
        self.until = until
        self.freeze = freeze
        self.verified = False
        self.last_check = "check_house has not been run yet."
        self.log_path = None
        if log_dir:
            log_dir.mkdir(parents=True, exist_ok=True)
            self.log_path = log_dir / time.strftime("run-%Y%m%d-%H%M%S.jsonl")

    def _log(self, event: str, **data: Any) -> None:
        if self.log_path:
            with self.log_path.open("a") as f:
                f.write(json.dumps({"t": round(time.time(), 2), "event": event, **data}, default=str) + "\n")

    def _say(self, msg: str) -> None:
        if self.verbose:
            print(msg, flush=True)

    def _wait_for_world(self, timeout: float = 900) -> bool:
        """The player left the world (menu, crash, reload). Pause the run until they're back."""
        self._say("\nworld closed: waiting for you to load back in (Ctrl-C to quit)...")
        self._log("world_lost")
        end = time.time() + timeout
        while time.time() < end:
            try:
                if self.bridge.health().get("in_world"):
                    if self.freeze:
                        self.bridge.set_freeze(True)
                    self._say("back in the world, continuing.")
                    return True
            except Exception:
                pass
            time.sleep(3)
        self._say("gave up waiting for the world.")
        return False

    def run(self, goal: str, max_turns: int = 150) -> RunStats:
        stats = RunStats(price=self.backend.price)
        first_look, _ = self.tools.run("observe", {})
        self.backend.start(system_prompt(self.memory.load()), f"Goal: {goal}\n\nWhat you see right now:\n{first_look}")
        self._log("start", goal=goal, backend=self.backend.name, model=self.backend.model)
        self._say(f"goal: {goal}  [{self.backend.name}: {self.backend.model}]")

        nudges = 0
        while stats.turns < max_turns:
            if self.max_cost is not None and stats.cost >= self.max_cost:
                self._say(f"stopping: spend cap ${self.max_cost:.2f} reached")
                break
            stats.turns += 1
            turn = self.backend.step()
            stats.add(turn)

            for text in turn.texts:
                self._say(f"\n{self.backend.name}: {text}")
                self._log("text", text=text)
            if turn.stop == "refusal":
                self._log("refusal", texts=turn.texts)
                break

            if not turn.calls:
                if turn.stop == "max_tokens":
                    self.backend.add_user("You hit the output limit. Continue, more briefly.")
                    continue
                if self.until and not self.verified:
                    self._say("  (not verified yet, pushing back)")
                    self.backend.add_user(
                        "Not done yet. The goal is only complete when check_house passes after your last change. "
                        f"Latest check: {self.last_check} Keep working; call tools."
                    )
                    continue
                # Small models sometimes narrate instead of calling a tool. Nudge twice before accepting "done".
                if not self.until and nudges < 2 and not _sounds_finished(turn.texts):
                    nudges += 1
                    self.backend.add_user("Keep going: call a tool to act. If the goal is fully done, say DONE and summarize.")
                    continue
                break

            nudges = 0
            results: list[tuple[Call, str, bool]] = []
            for call in turn.calls:
                stats.tool_calls += 1
                if call.args is None:
                    text, err = f"arguments were not valid JSON: {call.raw[:200]}", True
                else:
                    self._say(f"  -> {call.name} {json.dumps(call.args)}")
                    text, err = self.tools.run(call.name, call.args)
                stats.failures += int(err)
                if call.name == "check_house":
                    self.verified = not err
                    self.last_check = text.splitlines()[0]
                elif call.name not in ("observe", "remember"):
                    self.verified = False
                self._say("     " + (text.splitlines()[0] if call.name == "observe" else text.replace("\n", "\n     ")))
                self._log("tool", name=call.name, input=call.args, result=text, is_error=err)
                results.append((call, text, err))
            self.backend.add_results(results)
            self._log("usage", stats=stats.line())
            if any("not in a world" in text or "world unloaded" in text for _, text, _ in results):
                if not self._wait_for_world():
                    break
                self.backend.add_user("The world was closed and reloaded while you were acting. Call observe before doing anything else.")
            if self.until and self.verified:
                self._say("\ngoal verified in game, stopping.")
                break

        if self.until:
            self._say(f"\nverified: {'YES' if self.verified else 'NO'} ({self.last_check})")
            self._log("verified", ok=self.verified, check=self.last_check)
        self._say(f"\ndone: {stats.line()}")
        self._log("end", stats=stats.line())
        return stats




def _sounds_finished(texts: list[str]) -> bool:
    t = " ".join(texts).lower()
    return any(w in t for w in ("done", "complete", "finished", "impossible", "cannot", "can't"))
