"""HTTP client for the tModLoader bridge mod (or mock_bridge.py)."""

from __future__ import annotations

from typing import Any

import httpx

DEFAULT_URL = "http://127.0.0.1:7979"


class BridgeError(RuntimeError):
    pass


def exclusive(base_url: str):
    """Hold a per-instance lock for a whole run: two drivers on one game cancel each other's skills ("interrupted by
    move_to", "scenario reset"), which silently turned an LLM-track run into 0/8. Exits if another run holds it."""
    import fcntl
    import os
    import re
    import sys
    from pathlib import Path

    d = Path.home() / ".cache" / "terraria-agent"
    d.mkdir(parents=True, exist_ok=True)
    f = open(d / f"{re.sub(r'[^0-9A-Za-z]+', '_', base_url)}.lock", "w")
    try:
        fcntl.flock(f, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        sys.exit(f"another run is driving {base_url}; stop it first (lock {f.name})")
    f.write(str(os.getpid()))
    f.flush()
    return f  # keep a reference: the lock lives as long as the file object


class BridgeClient:
    def __init__(self, base_url: str = DEFAULT_URL, skill_timeout: float = 1800.0):
        # Macro skills (obtain, build_house, mine_vein) run for minutes, longer with guard fights; a shorter read
        # timeout would make the caller think a still-running skill failed, and its next call would cancel it.
        # The mod's HttpListener only answers Host 127.0.0.1:7979; a containerised game is reached on another
        # host port (sim/), so always present the Host the listener expects.
        self._http = httpx.Client(base_url=base_url, headers={"Host": "127.0.0.1:7979"},
                                  timeout=httpx.Timeout(10.0, read=skill_timeout))

    def _get(self, path: str) -> dict[str, Any]:
        try:
            return self._http.get(path).json()
        except httpx.HTTPError as e:
            raise BridgeError(f"bridge unreachable at {self._http.base_url}: {e}") from e

    def _post(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        try:
            return self._http.post(path, json=payload).json()
        except httpx.HTTPError as e:
            raise BridgeError(f"bridge unreachable at {self._http.base_url}: {e}") from e

    def health(self) -> dict[str, Any]:
        return self._get("/health")

    def game_seconds(self) -> float | None:
        """The game's own clock (update ticks / 60). With fast-forward or a slow sim, wall time says nothing about
        how much game time passed; time limits and timings use this. None on bridges older than the tick counter."""
        t = self.health().get("tick")
        return None if t is None else t / 60.0

    def sim_speed(self, speed: int = 1, render: int = 1) -> dict[str, Any]:
        """speed k = k game ticks per frame (fast-forward); render N = draw 1 frame in N (headless sims: 1000)."""
        return self._post("/config", {"speed": speed, "render": render})

    def state(self) -> dict[str, Any]:
        return self._get("/state")

    def skill(self, name: str, args: dict[str, Any]) -> dict[str, Any]:
        """Starts a skill and blocks until it finishes in game."""
        return self._post("/skill", {"name": name, "args": args})

    def recipe(self, item: str, count: int = 1) -> dict[str, Any]:
        """The full ingredient tree for `count` of `item` against the inventory: intermediates, stations, raw totals."""
        return self._post("/recipe", {"item": item, "count": count})

    def cancel(self) -> dict[str, Any]:
        return self._post("/cancel", {})

    def set_freeze(self, on: bool) -> dict[str, Any]:
        return self._post("/config", {"freeze": on})

    def set_background(self, on: bool) -> dict[str, Any]:
        """Keep the game running while its window is in the background (single player pauses otherwise)."""
        return self._post("/config", {"background": on})

    def say(self, text: str) -> dict[str, Any]:
        """Show a line in the in-game chat."""
        return self._post("/say", {"text": text})

    def status(self, title: str, detail: str = "") -> dict[str, Any]:
        """The on-screen HUD panel (empty title hides it). Older bridges without /status are ignored."""
        try:
            return self._post("/status", {"title": title, "detail": detail})
        except BridgeError:
            return {"ok": False}

    def set_time(self, at: str) -> dict[str, Any]:
        """Testing aid: jump to "dawn", "noon", "dusk" or "midnight"."""
        return self._post("/time", {"at": at})

    def save(self) -> dict[str, Any]:
        return self._post("/save", {})

    def set_sandbox(self, peaceful: bool, god: bool) -> dict[str, Any]:
        """Benchmark switches: peaceful = no hostile spawns (and clears the ones there), god = every hit dodged."""
        return self._post("/config", {"peaceful": peaceful, "god": god})

    def set_guard(self, on: bool) -> dict[str, Any]:
        """Bodyguard: pause a skill to fight off enemies (and drink potions) instead of ending it on a hit."""
        return self._post("/config", {"guard": on})

    def close(self) -> None:
        self._http.close()
