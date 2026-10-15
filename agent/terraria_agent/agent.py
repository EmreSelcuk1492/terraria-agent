"""The agent loop: observe -> Claude picks a skill -> the bridge runs it in game -> result goes back to Claude."""

from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import anthropic

from .bridge import BridgeClient
from .memory import Memory
from .prompts import system_prompt
from .tools import TOOLS, ToolExecutor, tool_result

MODEL = "claude-opus-5-5"
BETAS = [
    "server-side-fallback-2026-07-01",  # fallbacks="default": a declined request is retried on another model
    "context-management-2025-06-27",  # clear old tool results (observations are big) as the run gets long
]
# Opus 5.5 list prices, $ per million tokens.
PRICE = {"input": 4.00, "output": 20.00, "cache_read": 0.20, "cache_write": 5.00}


@dataclass
class RunStats:
    turns: int = 0
    tool_calls: int = 0
    failures: int = 0
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read: int = 0
    cache_write: int = 0
    started: float = field(default_factory=time.time)

    def add_usage(self, usage: Any) -> None:
        self.input_tokens += usage.input_tokens or 0
        self.output_tokens += usage.output_tokens or 0
        self.cache_read += getattr(usage, "cache_read_input_tokens", 0) or 0
        self.cache_write += getattr(usage, "cache_creation_input_tokens", 0) or 0

    @property
    def cost(self) -> float:
        return (
            self.input_tokens * PRICE["input"]
            + self.output_tokens * PRICE["output"]
            + self.cache_read * PRICE["cache_read"]
            + self.cache_write * PRICE["cache_write"]
        ) / 1e6

    def line(self) -> str:
        return (
            f"turns {self.turns}, tool calls {self.tool_calls} ({self.failures} failed), "
            f"tokens in {self.input_tokens} / cached {self.cache_read} / out {self.output_tokens}, "
            f"~${self.cost:.2f}, {time.time() - self.started:.0f}s"
        )


class Agent:
    def __init__(
        self,
        bridge: BridgeClient,
        memory: Memory,
        client: Any | None = None,
        model: str = MODEL,
        effort: str = "medium",
        log_dir: Path | None = None,
        verbose: bool = True,
    ):
        self.bridge = bridge
        self.memory = memory
        self.client = client or anthropic.Anthropic()
        self.model = model
        self.effort = effort
        self.tools = ToolExecutor(bridge, memory)
        self.verbose = verbose
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

    def _call(self, system: str, messages: list[dict[str, Any]]) -> Any:
        return self.client.beta.messages.create(
            model=self.model,
            max_tokens=16000,
            system=system,
            tools=TOOLS,
            messages=messages,
            thinking={"type": "adaptive"},
            output_config={"effort": self.effort},
            cache_control={"type": "ephemeral"},
            betas=BETAS,
            fallbacks="default",
            context_management={
                "edits": [
                    {
                        "type": "clear_tool_uses_20250919",
                        "trigger": {"type": "input_tokens", "value": 80_000},
                        "keep": {"type": "tool_uses", "value": 8},
                    }
                ]
            },
        )

    def run(self, goal: str, max_turns: int = 150) -> RunStats:
        stats = RunStats()
        system = system_prompt(self.memory.load())
        first_look, _ = self.tools.run("observe", {})
        # Append-only history: never edit earlier turns (keeps the cache and thinking blocks valid).
        messages: list[dict[str, Any]] = [
            {"role": "user", "content": f"Goal: {goal}\n\nWhat you see right now:\n{first_look}"}
        ]
        self._log("start", goal=goal, model=self.model, effort=self.effort)
        self._say(f"goal: {goal}")

        while stats.turns < max_turns:
            stats.turns += 1
            response = self._call(system, messages)
            stats.add_usage(response.usage)

            if response.stop_reason == "refusal":
                self._say(f"model declined: {getattr(response, 'stop_details', None)}")
                self._log("refusal", details=getattr(response, "stop_details", None))
                break

            messages.append({"role": "assistant", "content": response.content})
            for block in response.content:
                if block.type == "text" and block.text.strip():
                    self._say(f"\nclaude: {block.text.strip()}")
                    self._log("text", text=block.text)

            tool_uses = [b for b in response.content if b.type == "tool_use"]
            if not tool_uses:
                if response.stop_reason == "max_tokens":
                    messages.append({"role": "user", "content": "You hit the output limit. Continue, more briefly."})
                    continue
                break  # end_turn without a tool call = Claude says the goal is done (or impossible)

            results = []
            for use in tool_uses:
                if response.stop_reason == "max_tokens" and use is tool_uses[-1]:
                    results.append(tool_result(use.id, "call was cut off by the output limit; resend it", True))
                    continue
                stats.tool_calls += 1
                self._say(f"  -> {use.name} {json.dumps(use.input)}")
                text, is_error = self.tools.run(use.name, dict(use.input))
                stats.failures += int(is_error)
                self._say("     " + text.splitlines()[0] if use.name == "observe" else "     " + text.replace("\n", "\n     "))
                self._log("tool", name=use.name, input=use.input, result=text, is_error=is_error)
                results.append(tool_result(use.id, text, is_error))
            messages.append({"role": "user", "content": results})
            self._log("usage", stats=stats.line())

        self._say(f"\ndone: {stats.line()}")
        self._log("end", stats=stats.line())
        return stats
