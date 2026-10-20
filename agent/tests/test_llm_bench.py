"""The LLM track's loop against the mock world, with a scripted planner standing in for the model."""

from __future__ import annotations

import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import mock_bridge  # noqa: E402
from terraria_agent import llm_bench  # noqa: E402
from terraria_agent.bridge import BridgeClient  # noqa: E402
from terraria_agent.skill_library import SkillLibrary  # noqa: E402


class ScriptedPlanner:
    def __init__(self, plans):
        self.plans, self.calls, self.seconds, self.seen = list(plans), 0, 0.0, []

    def plan(self, messages):
        self.calls += 1
        self.seen.append(messages[-1]["content"])
        return self.plans.pop(0)


def _bridge():
    world = mock_bridge.World()
    server = mock_bridge.make_server(0, world)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return world, server, BridgeClient(f"http://127.0.0.1:{server.server_address[1]}")


def test_retry_verify_save_and_reuse(tmp_path):
    world, server, bridge = _bridge()
    lib = SkillLibrary(tmp_path / "lib.json")
    wood = {"thought": "", "name": "gather_wood", "description": "chop trees for wood",
            "program": [{"skill": "mine_nearest", "args": {"target": "Trees", "count": 12}}]}
    planner = ScriptedPlanner([
        {"thought": "", "name": "x", "description": "x", "program": [{"skill": "obtain", "args": {"item": "Wood", "count": 30}}]},
        wood,  # retry after the macro was refused in primitive mode
        {"thought": "", "name": "make_workbench", "description": "a work bench from wood",
         "program": [{"skill": "lib:gather_wood", "args": {}}, {"skill": "craft", "args": {"item": "WorkBench", "count": 1}}]},
    ])
    start = {"fresh_player": True, "clear": True}
    task = llm_bench.Task("wood", "Hold at least 10 Wood.", {"have": {"Wood": 10}})
    r1 = llm_bench.run_task(task, bridge, planner, lib, "primitive", 3, True, start)
    assert r1.passed and r1.attempts == 2 and r1.saved_as == "gather_wood"
    assert "not an available skill" in planner.seen[1]
    r2 = llm_bench.run_task(llm_bench.Task("workbench", "Hold a WorkBench.", {"have": {"WorkBench": 1}}),
                            bridge, planner, lib, "primitive", 3, True, start)
    assert r2.passed and r2.used_library == ["gather_wood"] and r2.saved_as == "make_workbench"
    assert "lib:gather_wood" in planner.seen[2]  # retrieval showed the saved skill to the planner
    assert SkillLibrary(tmp_path / "lib.json").get("gather_wood").successes == 2
    server.shutdown()


def test_verify_any_of():
    from collections import Counter
    check = {"any": [{"have": {"CopperBar": 3}}, {"have": {"TinBar": 3}}]}
    assert llm_bench.verify(check, Counter({"TinBar": 3}))
    assert not llm_bench.verify(check, Counter({"CopperBar": 2}))


def test_wood_check_takes_any_wood():
    from collections import Counter
    assert llm_bench.verify({"have": {"Wood": 30}}, Counter({"BorealWood": 31}))
    assert not llm_bench.verify({"have": {"Wood": 30}}, Counter({"BorealWood": 20}))


def test_wood_variants_count():
    from collections import Counter
    assert llm_bench.verify({"have": {"WorkBench": 1}}, Counter({"BorealWoodWorkBench": 1}))
    assert llm_bench.verify({"have": {"WoodenHammer": 1}}, Counter({"BorealWoodHammer": 1}))
    assert not llm_bench.verify({"have": {"WorkBench": 1}}, Counter({"Furnace": 1}))


def test_programs_with_coordinates_are_not_saved(tmp_path):
    world, server, bridge = _bridge()
    lib = SkillLibrary(tmp_path / "lib.json")
    planner = ScriptedPlanner([{"thought": "", "name": "go_chop", "description": "walk there and chop",
                                "program": [{"skill": "move_to", "args": {"x": 52, "y": 98}},
                                            {"skill": "mine_nearest", "args": {"target": "Trees", "count": 12}}]}])
    r = llm_bench.run_task(llm_bench.Task("wood", "w", {"have": {"Wood": 10}}), bridge, planner, lib, "primitive", 1, True,
                           {"fresh_player": True})
    assert r.passed and r.saved_as is None and not lib.skills
    server.shutdown()


def test_recipe_book_covers_every_goal_option():
    _, server, bridge = _bridge()
    check = {"any": [{"have": {"CopperBar": 3}}, {"have": {"TinBar": 3}}]}
    assert llm_bench.goal_items(check) == [("CopperBar", 3), ("TinBar", 3)]
    text = llm_bench.recipe_text(check, bridge)
    assert "Recipe book" in text and "3 CopperBar:" in text and "3 TinBar:" in text
    server.shutdown()


def test_prompt_carries_the_recipe_book_unless_turned_off(tmp_path):
    for recipes in (True, False):
        _, server, bridge = _bridge()
        planner = ScriptedPlanner([{"thought": "", "name": "wood", "description": "wood",
                                    "program": [{"skill": "mine_nearest", "args": {"target": "Trees", "count": 12}}]}])
        task = llm_bench.Task("wood", "Hold 30 Wood.", {"have": {"Wood": 30}})
        llm_bench.run_task(task, bridge, planner, None, "primitive", 1, False, {}, recipes=recipes)
        assert ("Recipe book" in planner.seen[0]) == recipes
        server.shutdown()
