"""Model backends. Each one keeps its own message history in its provider's format.

- ClaudeBackend:  Anthropic API (paid).
- OllamaBackend:  local open models through Ollama's native API (free; sets the context window explicitly).
- OpenAIBackend:  any OpenAI-compatible server: LM Studio, llama.cpp, vLLM, OpenRouter, Groq, Together...
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from typing import Any

import httpx

from .tools import TOOLS, tool_result


@dataclass
class Call:
    id: str
    name: str
    args: dict[str, Any] | None  # None = the model sent arguments that aren't valid JSON
    raw: str = ""


@dataclass
class Turn:
    texts: list[str] = field(default_factory=list)
    calls: list[Call] = field(default_factory=list)
    stop: str = "end"  # end | tool_use | max_tokens | refusal
    input_tokens: int = 0
    output_tokens: int = 0
    cache_read: int = 0
    cache_write: int = 0


class Backend:
    name = "base"
    model = ""
    # $ per million tokens: input, output, cache read, cache write
    price = (0.0, 0.0, 0.0, 0.0)

    def start(self, system: str, first_user: str) -> None:
        raise NotImplementedError

    def step(self) -> Turn:
        raise NotImplementedError

    def add_results(self, results: list[tuple[Call, str, bool]]) -> None:
        raise NotImplementedError

    def add_user(self, text: str) -> None:
        raise NotImplementedError


# ---------------------------------------------------------------------------------------------
# Claude
# ---------------------------------------------------------------------------------------------


class ClaudeBackend(Backend):
    name = "claude"
    BETAS = [
        "server-side-fallback-2026-07-01",  # fallbacks="default": a declined request is retried on another model
        "context-management-2025-06-27",  # clear old tool results (observations are big) as the run gets long
    ]
    PRICES = {
        "claude-opus-5-5": (4.00, 20.00, 0.20, 5.00),
        "claude-sonnet-5-5": (2.00, 10.00, 0.20, 2.50),
        "claude-haiku-5-5": (0.10, 0.50, 0.01, 0.125),
    }

    def __init__(self, model: str = "claude-opus-5-5", effort: str = "medium", client: Any | None = None):
        import anthropic

        self.client = client or anthropic.Anthropic()
        self.model = model
        self.effort = effort
        self.price = self.PRICES.get(model, self.PRICES["claude-opus-5-5"])
        self.system = ""
        # Append-only history: never edit earlier turns (keeps the cache and thinking blocks valid).
        self.messages: list[dict[str, Any]] = []

    def start(self, system: str, first_user: str) -> None:
        self.system = system
        self.messages = [{"role": "user", "content": first_user}]

    def step(self) -> Turn:
        kwargs: dict[str, Any] = dict(
            model=self.model,
            max_tokens=16000,
            system=self.system,
            tools=TOOLS,
            messages=self.messages,
            thinking={"type": "adaptive"},
            output_config={"effort": self.effort},
            cache_control={"type": "ephemeral"},
            betas=list(self.BETAS),
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
        if "haiku" not in self.model:  # Haiku has no server-side fallback
            kwargs["fallbacks"] = "default"
        r = self.client.beta.messages.create(**kwargs)

        u = r.usage
        turn = Turn(
            stop={"tool_use": "tool_use", "max_tokens": "max_tokens", "refusal": "refusal"}.get(r.stop_reason, "end"),
            input_tokens=u.input_tokens or 0,
            output_tokens=u.output_tokens or 0,
            cache_read=getattr(u, "cache_read_input_tokens", 0) or 0,
            cache_write=getattr(u, "cache_creation_input_tokens", 0) or 0,
        )
        if r.stop_reason == "refusal":
            turn.texts.append(f"(declined: {getattr(r, 'stop_details', None)})")
            return turn
        self.messages.append({"role": "assistant", "content": r.content})
        for b in r.content:
            if b.type == "text" and b.text.strip():
                turn.texts.append(b.text.strip())
            elif b.type == "tool_use":
                turn.calls.append(Call(b.id, b.name, dict(b.input)))
        return turn

    def add_results(self, results: list[tuple[Call, str, bool]]) -> None:
        self.messages.append({"role": "user", "content": [tool_result(c.id, text, err) for c, text, err in results]})

    def add_user(self, text: str) -> None:
        self.messages.append({"role": "user", "content": text})


# ---------------------------------------------------------------------------------------------
# Open models (OpenAI-style chat with tools)
# ---------------------------------------------------------------------------------------------

_THINK = re.compile(r"<think>.*?</think>", re.S)


def _openai_tools() -> list[dict[str, Any]]:
    return [
        {"type": "function", "function": {"name": t["name"], "description": t["description"], "parameters": t["input_schema"]}}
        for t in TOOLS
    ]


class _ChatBackend(Backend):
    """Shared history handling for OpenAI-style chat APIs.

    Small models have small context windows, so the history sent each step is trimmed: only the last
    `keep_results` tool results are sent in full; older ones shrink to their first line, and old
    observations are dropped (the model can always call observe again).
    """

    def __init__(self, model: str, keep_results: int = 4, no_think: bool = False):
        self.model = model
        self.keep_results = keep_results
        self.no_think = no_think
        self.messages: list[dict[str, Any]] = []
        self._ids = 0

    def start(self, system: str, first_user: str) -> None:
        if self.no_think:
            system += "\n/no_think"  # Qwen3 soft switch; harmless elsewhere
        self.messages = [{"role": "system", "content": system}, {"role": "user", "content": first_user}]

    def _view(self) -> list[dict[str, Any]]:
        tool_idx = [i for i, m in enumerate(self.messages) if m["role"] == "tool"]
        old = set(tool_idx[: max(0, len(tool_idx) - self.keep_results)])
        out = []
        for i, m in enumerate(self.messages):
            if i in old:
                content = m["content"]
                short = "[old observation removed; call observe for a fresh one]" if content.startswith("YOU (") else content.splitlines()[0]
                m = {**m, "content": short}
            elif i == 1 and len(tool_idx) > self.keep_results:
                goal = m["content"].split("\n\nWhat you see right now:")[0]
                m = {**m, "content": goal + "\n\n[initial observation removed; call observe for a fresh one]"}
            out.append(m)
        return out

    def _next_id(self) -> str:
        self._ids += 1
        return f"call_{self._ids}"

    def add_results(self, results: list[tuple[Call, str, bool]]) -> None:
        for c, text, err in results:
            self.messages.append(self._tool_message(c, ("ERROR: " if err else "") + text))

    def add_user(self, text: str) -> None:
        self.messages.append({"role": "user", "content": text})

    def _tool_message(self, c: Call, text: str) -> dict[str, Any]:
        raise NotImplementedError


class OllamaBackend(_ChatBackend):
    """Ollama's native /api/chat. Used instead of its OpenAI endpoint so num_ctx (context size) can be set;
    Ollama's default window is small enough to silently cut off the observation."""

    name = "ollama"

    def __init__(self, model: str = "qwen3:8b", base_url: str = "http://127.0.0.1:11434", num_ctx: int = 16384,
                 keep_results: int = 4, no_think: bool = False, timeout: float = 600):
        super().__init__(model, keep_results, no_think)
        self.num_ctx = num_ctx
        self.http = httpx.Client(base_url=base_url, timeout=timeout)

    def step(self) -> Turn:
        r = self.http.post("/api/chat", json={
            "model": self.model,
            "messages": self._view(),
            "tools": _openai_tools(),
            "stream": False,
            "think": not self.no_think,  # hard switch; the /no_think prompt hint alone is ignored when tools are on
            "options": {"num_ctx": self.num_ctx, "temperature": 0.6},
        })
        r.raise_for_status()
        data = r.json()
        msg = data["message"]
        turn = Turn(input_tokens=data.get("prompt_eval_count", 0), output_tokens=data.get("eval_count", 0))
        text = _THINK.sub("", msg.get("content") or "").strip()
        if text:
            turn.texts.append(text)
        stored = {"role": "assistant", "content": msg.get("content") or ""}
        if msg.get("tool_calls"):
            stored["tool_calls"] = msg["tool_calls"]
            for tc in msg["tool_calls"]:
                fn = tc["function"]
                args = fn.get("arguments")
                if isinstance(args, str):
                    try:
                        args = json.loads(args)
                    except json.JSONDecodeError:
                        args = None
                turn.calls.append(Call(self._next_id(), fn["name"], args, raw=str(fn.get("arguments"))))
            turn.stop = "tool_use"
        elif data.get("done_reason") == "length":
            turn.stop = "max_tokens"
        self.messages.append(stored)
        return turn

    def _tool_message(self, c: Call, text: str) -> dict[str, Any]:
        return {"role": "tool", "tool_name": c.name, "content": text}


class OpenAIBackend(_ChatBackend):
    """Any OpenAI-compatible /chat/completions endpoint. Key from OPENAI_API_KEY (or --api-key-env)."""

    name = "openai"

    def __init__(self, model: str, base_url: str, api_key_env: str = "OPENAI_API_KEY", keep_results: int = 4,
                 no_think: bool = False, price: tuple[float, float, float, float] = (0, 0, 0, 0), timeout: float = 600):
        super().__init__(model, keep_results, no_think)
        self.price = price
        key = os.environ.get(api_key_env, "")
        headers = {"Authorization": f"Bearer {key}"} if key else {}
        self.http = httpx.Client(base_url=base_url.rstrip("/"), headers=headers, timeout=timeout)

    def step(self) -> Turn:
        r = self.http.post("/chat/completions", json={
            "model": self.model,
            "messages": self._view(),
            "tools": _openai_tools(),
            "tool_choice": "auto",
            "max_tokens": 4096,
        })
        r.raise_for_status()
        data = r.json()
        choice = data["choices"][0]
        msg = choice["message"]
        usage = data.get("usage") or {}
        turn = Turn(input_tokens=usage.get("prompt_tokens", 0), output_tokens=usage.get("completion_tokens", 0))
        text = _THINK.sub("", msg.get("content") or "").strip()
        if text:
            turn.texts.append(text)
        stored: dict[str, Any] = {"role": "assistant", "content": msg.get("content") or ""}
        if msg.get("tool_calls"):
            stored["tool_calls"] = msg["tool_calls"]
            for tc in msg["tool_calls"]:
                raw = tc["function"].get("arguments") or "{}"
                try:
                    args = json.loads(raw) if isinstance(raw, str) else raw
                except json.JSONDecodeError:
                    args = None
                turn.calls.append(Call(tc["id"], tc["function"]["name"], args, raw=str(raw)))
            turn.stop = "tool_use"
        elif choice.get("finish_reason") == "length":
            turn.stop = "max_tokens"
        self.messages.append(stored)
        return turn

    def _tool_message(self, c: Call, text: str) -> dict[str, Any]:
        return {"role": "tool", "tool_call_id": c.id, "content": text}
