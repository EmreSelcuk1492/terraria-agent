"""The director against the mock world: the whole starter plan, night rules, and the no-progress guard."""

from __future__ import annotations

import sys
import threading
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import mock_bridge  # noqa: E402
from terraria_agent.bridge import BridgeClient  # noqa: E402
from terraria_agent.director import Ctx, Director, Memo, Step  # noqa: E402


@pytest.fixture()
def world_and_bridge():
    world = mock_bridge.World()
    server = mock_bridge.make_server(0, world)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    bridge = BridgeClient(f"http://127.0.0.1:{server.server_address[1]}")
    yield world, bridge
    server.shutdown()
    bridge.close()


def test_starter_plan_completes(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    d.run(hours=0.002)  # ~7s of wall time; the mock answers instantly
    c = d.observe()
    assert all(s.done(c, d.memo) for s in d.plan), [s.key for s in d.plan if not s.done(c, d.memo)]
    assert len(d.memo.houses) >= 3 and all(h["npc_valid"] for h in d.memo.houses)
    assert {"CopperHelmet", "CopperChainmail", "CopperGreaves"} <= set(world.equipment)
    assert world.player["max_hp"] > 100
    assert any("Step 1/" in s for s in world.said), "narrates in the in-game chat"
    assert world.saves >= 1
    journal = d.journal_path.read_text()
    assert "## Summary" in journal and "NPC-valid" in journal


def test_night_prefers_underground_work(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.ticks = 54000  # dusk
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    d.memo.done.append("gear")
    c = d.observe()
    assert not c.is_day
    step = d.choose(c)
    assert step is not None and step.underground


def test_step_that_never_meets_its_goal_gets_parked(world_and_bridge, tmp_path):
    _, bridge = world_and_bridge
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    stuck = Step("stuck", "Impossible", "test", lambda c, m: False, lambda c, m: [[("wait", {"seconds": 0.1})]])
    d.plan = [stuck]
    for _ in range(2):
        assert not d.run_step(stuck, d.observe())
    assert d.memo.parked.get("stuck", 0) > 0
    assert d.choose(d.observe()) is None


def test_ctx_metal_detection():
    c = Ctx.of({"inventory": [{"item": "LeadOre", "stack": 9}, {"item": "CopperBar", "stack": 2}]})
    assert c.metal(("Copper", "Tin")) == "Copper"
    assert c.metal(("Iron", "Lead")) == "Lead"
    assert c.ore_and_bars(("Copper", "Tin")) == 6


def test_memo_round_trip(tmp_path):
    m = Memo(houses=[{"x": 1, "y": 2, "material": "Wood", "npc_valid": True}], done=["gear"])
    m.save(tmp_path / "m.json")
    assert Memo.load(tmp_path / "m.json").houses[0]["npc_valid"]
