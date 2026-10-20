"""The director against the mock world: the whole starter plan, night rules, and the no-progress guard."""

from __future__ import annotations

import sys
import threading
import time
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


def test_low_health_cornered_fights_desperately(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.player["hp"] = 16  # run 3: "retreating: health 16/100" 277 times without a swing
    world.npcs = [{"name": "Zombie", "x": world.player["x"] + 1, "y": world.player["y"], "hp": 45, "max_hp": 45,
                   "hostile": True, "boss": False}]
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    assert d.safety(d.observe())
    assert not world.npcs, "the desperate attack swung and killed the zombie"


def test_low_health_with_room_digs_in_and_leaves(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.player["hp"] = 16
    world.npcs = []
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    assert d.safety(d.observe())
    assert world.player["hp"] >= 70 and len(d.roof) == 2
    d._leave_shelter()
    assert d.roof == []


def test_night_without_home_digs_in_until_day(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.ticks = 54000  # dusk
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    calls = []
    d.call = lambda name, args: (calls.append((name, args)), (True, "dug in at (1,2). Roof at (1, 1) (2, 1): mine it"))[1]
    d._night(d.observe())
    assert calls[0] == ("dig_in", {"wait_seconds": 600, "until_hp": 0.8, "until_day": True})


def test_scenario_suite_against_mock(world_and_bridge, tmp_path):
    from terraria_agent import scenarios
    _, bridge = world_and_bridge
    second = mock_bridge.make_server(0, mock_bridge.World())  # one world per worker, like one container each
    threading.Thread(target=second.serve_forever, daemon=True).start()
    urls = [str(bridge._http.base_url), f"http://127.0.0.1:{second.server_address[1]}"]
    quick = [scenarios.Scenario("zombie_mock", "mock smoke", {"hp": 100, "spawn": [{"npc": "Zombie", "dx": 2}]}, 1.5, min_cleared=1),
             scenarios.Scenario("low_hp_mock", "mock smoke", {"hp": 20}, 1.5, min_hp=45, work=None)]
    results = scenarios.run_suite(urls, quick, repeat=2, out_dir=tmp_path, verbose=False)
    assert len(results) == 4
    assert all(r.passed for r in results), [(r.scenario, r.failures, r.error) for r in results]
    assert "4/4 passed" in (tmp_path / "report.md").read_text()
    second.shutdown()


def test_low_health_flier_digs_in_instead_of_fighting(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.player["hp"] = 20
    world.npcs = [{"name": "Demon Eye", "x": world.player["x"] + 1, "y": world.player["y"] - 2, "hp": 60, "max_hp": 60,
                   "hostile": True, "boss": False, "flying": True}]
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    calls = []
    real = d.call
    d.call = lambda name, args: (calls.append(name), real(name, args))[1]
    assert d.safety(d.observe())
    assert calls[0] == "dig_in"


def _record(d):
    calls = []
    d.call = lambda name, args: (calls.append((name, args)), (True, "dug in at (1,2). Roof at (1, 1) (2, 1): mine it"))[1]
    return calls


def test_respawn_at_night_digs_in_until_morning(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.ticks = 54000  # dusk
    world.npcs = []
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    calls = _record(d)
    d.dead = True  # the previous poll saw the death; this one sees the respawned player
    assert d.safety(d.observe())
    assert calls == [("dig_in", {"wait_seconds": 600, "until_hp": 0.9, "until_day": True})]
    assert d.memo.stats.get("respawn_shelters") == 1


def test_second_death_in_minutes_shelters_longer(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.npcs = []
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    calls = _record(d)
    d.dead, d.deaths = True, [time.time() - 100, time.time() - 20]
    assert d.safety(d.observe())
    assert [n for n, _ in calls] == ["dig_in"]
    assert calls[0][1]["wait_seconds"] == 120 and not calls[0][1]["until_day"]


def test_respawn_beside_a_zombie_skips_the_bunker(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.npcs = [{"name": "Zombie", "x": world.player["x"] + 3, "y": world.player["y"], "hp": 45, "max_hp": 45,
                   "hostile": True, "boss": False}]
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    calls = _record(d)
    d.dead, d.deaths = True, [time.time() - 100, time.time() - 20]
    assert not d.safety(d.observe())  # digging in beside it died 31 of 59 times (night7 run 1)
    assert calls == []


def test_safe_respawn_goes_straight_back_to_work(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.npcs = []
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    calls = _record(d)
    d.dead = True
    assert not d.safety(d.observe())  # day, full health, nothing near: no reason to hide
    assert calls == [] and not d.dead


def test_cornered_at_low_health_fights_instead_of_digging(world_and_bridge, tmp_path):
    world, bridge = world_and_bridge
    world.player["hp"] = 30
    world.npcs = [{"name": "Zombie", "x": world.player["x"] + 5, "y": world.player["y"], "hp": 45, "max_hp": 45,
                   "hostile": True, "boss": False}]
    d = Director(bridge, advisor=None, memo_dir=tmp_path, verbose=False)
    calls = []
    replies = {"flee": (False, "cornered at (2101,255): can't run further east. Fight (attack desperate=true) or dig_in."),
               "attack": (True, "killed 1; no more targets nearby")}
    d.call = lambda name, args: (calls.append((name, args)), replies.get(name, (True, "")))[1]
    assert d.safety(d.observe())
    assert [n for n, _ in calls] == ["flee", "attack"], "no dig_in with a zombie on top (day6 run 4)"
    assert calls[1][1]["desperate"], "a plain attack at low health refuses without a swing (day6 runs 5-6)"


