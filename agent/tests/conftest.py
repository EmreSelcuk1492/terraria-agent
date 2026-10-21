"""Tests never publish to the public lab (lab.emrecanselcuk.com).

A test run against the mock world once published a 19/19 Director run (fixed in fa2653f by gating on main()).
This makes it impossible rather than avoided: under pytest the Lab client can't be built, so any publish path a
test reaches fails closed (runners' publish_quietly() prints "lab publish skipped" and carries on).
"""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from terraria_agent import lab  # noqa: E402


@pytest.fixture(autouse=True)
def _no_lab_publish(monkeypatch):
    def refuse(self, *a, **k):
        raise RuntimeError("tests never publish to the public lab")

    monkeypatch.setattr(lab.Lab, "__init__", refuse)
