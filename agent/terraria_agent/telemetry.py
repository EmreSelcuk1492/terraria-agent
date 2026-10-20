"""PostHog AI observability: every model call, skill call and run shows up in PostHog's LLM analytics.

- `$ai_generation`: one model call (any backend): messages in and out, tokens, latency, cost from the backend's
  own price table (local models cost 0).
- `$ai_span`: one skill call (or one planned program in the LLM track) under the generation that asked for it.
- `$ai_trace`: one run, goal or task in, outcome out; `$ai_is_error` = the run didn't reach its goal.

Sessions group a process's traces (one Director run, one LLM A/B pass). Every event also carries `context`, so
PostHog can break results down by mode, library, track, backend...

Config, from the environment or agent/.env: POSTHOG_PROJECT_TOKEN, POSTHOG_HOST (e.g. https://us.i.posthog.com),
optional TA_SESSION to name the session (the overnight scripts can set one per pass). TA_TELEMETRY=0 turns it
off. Without a token every call here is a no-op; tests never send.
"""

from __future__ import annotations

import atexit
import os
import sys
import time
import uuid
import warnings
from pathlib import Path
from typing import Any

ENV_FILE = Path(__file__).resolve().parent.parent / ".env"
DISTINCT_ID = "terraria-agent"

# Merged into every event; callers add run metadata (mode, library, track, backend...).
context: dict[str, Any] = {}
session_id: str | None = None

_client: Any = None
_ready = False


def _load_env_file() -> None:
    if not ENV_FILE.exists():
        return
    for line in ENV_FILE.read_text().splitlines():
        key, sep, value = line.strip().partition("=")
        if sep and not key.startswith("#"):
            os.environ.setdefault(key.strip(), value.strip().strip("'\""))


def _get() -> Any:
    """The PostHog client, or None when telemetry is off."""
    global _client, _ready, session_id
    if _ready:
        return _client
    _ready = True
    if "pytest" in sys.modules:
        return None
    _load_env_file()
    if os.environ.get("TA_TELEMETRY", "1") == "0":
        return None
    for var in ("POSTHOG_PROJECT_TOKEN", "POSTHOG_HOST"):
        if not os.environ.get(var):
            warnings.warn(f"{var} variable required by PostHog is missing or un-configured, this causes events to be "
                          f"silently missed. This error stops appearing once {var} is configured", stacklevel=3)
            return None
    try:
        from posthog import Posthog
    except ImportError:
        warnings.warn("posthog is not installed (uv pip install -p .venv posthog): LLM analytics are off", stacklevel=3)
        return None
    _client = Posthog(os.environ["POSTHOG_PROJECT_TOKEN"], host=os.environ["POSTHOG_HOST"])
    atexit.register(_client.shutdown)  # flush queued events, also after Ctrl-C
    entry = Path(sys.argv[0]).stem.replace("__main__", "agent")  # python -m terraria_agent -> "agent"
    session_id = session_id or os.environ.get("TA_SESSION") or f"{entry}-{time.strftime('%Y%m%d-%H%M%S')}"
    return _client


def enabled() -> bool:
    return _get() is not None


def new_id() -> str:
    return str(uuid.uuid4())


def jsonable(obj: Any) -> Any:
    """SDK content blocks (pydantic models), test stand-ins and plain data, as JSON-safe values."""
    if hasattr(obj, "model_dump"):
        return obj.model_dump(mode="json", exclude_none=True)
    if isinstance(obj, dict):
        return {k: jsonable(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [jsonable(v) for v in obj]
    if hasattr(obj, "__dict__"):
        return {k: jsonable(v) for k, v in vars(obj).items()}
    return obj if isinstance(obj, (str, int, float, bool, type(None))) else str(obj)


def _send(event: str, props: dict[str, Any]) -> None:
    client = _get()
    if client is None:
        return
    try:
        client.capture(event, distinct_id=DISTINCT_ID, properties={
            "$ai_session_id": session_id, "$process_person_profile": False, **context, **props})
    except Exception as e:  # analytics must never stop a run
        print(f"(posthog: {e})", file=sys.stderr)


def _error(error: Any) -> dict[str, Any]:
    if not error:
        return {}
    return {"$ai_is_error": True, "$ai_error": error if isinstance(error, str) else f"{type(error).__name__}: {error}"}


def generation(trace_id: str | None, *, provider: str, model: str, messages: list[Any], output: list[Any],
               latency: float, input_tokens: int = 0, output_tokens: int = 0, cache_read: int = 0, cache_write: int = 0,
               price: tuple[float, float, float, float] = (0, 0, 0, 0), stop: str | None = None,
               tools: list[Any] | None = None, base_url: str | None = None, span_id: str | None = None,
               span_name: str | None = None, error: Any = None, **extra: Any) -> None:
    if not trace_id or not enabled():
        return
    pi, po, pr, pw = price
    cost_in = (input_tokens * pi + cache_read * pr + cache_write * pw) / 1e6
    cost_out = output_tokens * po / 1e6
    _send("$ai_generation", {
        "$ai_trace_id": trace_id, "$ai_span_id": span_id or new_id(), "$ai_span_name": span_name,
        "$ai_parent_id": trace_id, "$ai_provider": provider, "$ai_model": model, "$ai_base_url": base_url,
        "$ai_input": jsonable(messages), "$ai_output_choices": jsonable(output), "$ai_tools": tools,
        "$ai_input_tokens": input_tokens, "$ai_output_tokens": output_tokens,
        "$ai_cache_read_input_tokens": cache_read, "$ai_cache_creation_input_tokens": cache_write,
        "$ai_input_cost_usd": cost_in, "$ai_output_cost_usd": cost_out, "$ai_total_cost_usd": cost_in + cost_out,
        "$ai_latency": round(latency, 3), "$ai_stop_reason": stop, **_error(error), **extra})


def span(trace_id: str | None, name: str, *, input: Any, output: Any, latency: float, parent_id: str | None = None,
         error: Any = None, **extra: Any) -> None:
    if not trace_id or not enabled():
        return
    _send("$ai_span", {
        "$ai_trace_id": trace_id, "$ai_span_id": new_id(), "$ai_span_name": name, "$ai_parent_id": parent_id or trace_id,
        "$ai_input_state": jsonable(input), "$ai_output_state": jsonable(output), "$ai_latency": round(latency, 3),
        **_error(error), **extra})


def trace(trace_id: str | None, name: str, *, input: Any, output: Any, latency: float, error: Any = None,
          **extra: Any) -> None:
    if not trace_id or not enabled():
        return
    _send("$ai_trace", {
        "$ai_trace_id": trace_id, "$ai_span_name": name, "$ai_input_state": jsonable(input),
        "$ai_output_state": jsonable(output), "$ai_latency": round(latency, 3), **_error(error), **extra})
