"""HTTP client for the tModLoader bridge mod (or mock_bridge.py)."""

from __future__ import annotations

from typing import Any

import httpx

DEFAULT_URL = "http://127.0.0.1:7979"


class BridgeError(RuntimeError):
    pass


class BridgeClient:
    def __init__(self, base_url: str = DEFAULT_URL, skill_timeout: float = 240.0):
        self._http = httpx.Client(base_url=base_url, timeout=httpx.Timeout(10.0, read=skill_timeout))

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

    def state(self) -> dict[str, Any]:
        return self._get("/state")

    def skill(self, name: str, args: dict[str, Any]) -> dict[str, Any]:
        """Starts a skill and blocks until it finishes in game."""
        return self._post("/skill", {"name": name, "args": args})

    def cancel(self) -> dict[str, Any]:
        return self._post("/cancel", {})

    def set_freeze(self, on: bool) -> dict[str, Any]:
        return self._post("/config", {"freeze": on})

    def set_background(self, on: bool) -> dict[str, Any]:
        """Keep the game running while its window is in the background (single player pauses otherwise)."""
        return self._post("/config", {"background": on})

    def close(self) -> None:
        self._http.close()
