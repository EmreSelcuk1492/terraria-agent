"""Offline tests: the agent loop + tools against mock_bridge, with a scripted stand-in for the Claude API."""

from __future__ import annotations

import sys
import threading
from pathlib import Path
from types import SimpleNamespace as NS

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from mock_bridge import make_server  # noqa: E402
from terraria_agent.agent import Agent  # noqa: E402
from terraria_agent.backends import Call, ClaudeBackend, OllamaBackend  # noqa: E402
from terraria_agent.repl import parse  # noqa: E402
from terraria_agent.bridge import BridgeClient  # noqa: E402
from terraria_agent.memory import Memory  # noqa: E402
from terraria_agent.tools import TOOLS, ToolExecutor  # noqa: E402


@pytest.fixture
def bridge():
    server = make_server(0)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    client = BridgeClient(f"http://127.0.0.1:{server.server_address[1]}")
    yield client
    client.close()
    server.shutdown()


def usage():
    return NS(input_tokens=1000, output_tokens=100, cache_read_input_tokens=0, cache_creation_input_tokens=0)


def call(i, name, args):
    return NS(type="tool_use", id=f"t{i}", name=name, input=args)


class ScriptedClaude:
    """Replays a fixed list of turns; records each request."""

    def __init__(self, turns):
        self.turns = list(turns)
        self.requests = []
        self.beta = NS(messages=NS(create=self.create))

    def create(self, **kw):
        self.requests.append({**kw, "messages": list(kw["messages"])})
        blocks = self.turns.pop(0)
        stop = "tool_use" if any(b.type == "tool_use" for b in blocks) else "end_turn"
        return NS(content=blocks, stop_reason=stop, usage=usage())


def test_tools_are_strict_and_closed():
    for t in TOOLS:
        s = t["input_schema"]
        assert t["strict"] is True
        assert s["additionalProperties"] is False
        assert set(s["required"]) == set(s["properties"])


def test_observe_renders_map(bridge, tmp_path):
    text, err = ToolExecutor(bridge, Memory(tmp_path / "l.md")).run("observe", {})
    assert not err
    assert "YOU (50,98)" in text and "MAP origin=(20,81)" in text
    assert "x=30" in text and "y=98   " in text and "@" in text
    assert "Trees x10" in text


def test_skill_failure_is_error_with_reason(bridge, tmp_path):
    ex = ToolExecutor(bridge, Memory(tmp_path / "l.md"))
    ex.run("observe", {})
    text, err = ex.run("craft", {"item": "WoodenSword", "count": 1})
    assert err and "missing station: WorkBenches" in text and "Wood 0/7" in text


def test_inventory_delta(bridge, tmp_path):
    ex = ToolExecutor(bridge, Memory(tmp_path / "l.md"))
    ex.run("observe", {})
    text, err = ex.run("mine_nearest", {"target": "Trees", "count": 2})
    assert not err and "+16 Wood" in text


def test_full_loop_crafts_sword(bridge, tmp_path):
    turns = [
        [NS(type="text", text="Need wood."), call(1, "mine_nearest", {"target": "Trees", "count": 3})],
        [call(2, "craft", {"item": "WorkBench", "count": 1})],
        [call(3, "place", {"item": "WorkBench", "x": 52, "y": 99})],
        [call(4, "craft", {"item": "WoodenSword", "count": 1}), call(5, "remember", {"lesson": "WoodenSword = 7 Wood at a WorkBench"})],
        [NS(type="text", text="Done: crafted a wooden sword.")],
    ]
    claude = ScriptedClaude(turns)
    mem = Memory(tmp_path / "lessons.md")
    stats = Agent(bridge, mem, ClaudeBackend(client=claude), log_dir=tmp_path / "runs", verbose=False).run("Craft a wooden sword", max_turns=10)

    assert stats.turns == 5 and stats.tool_calls == 5 and stats.failures == 0
    assert any(i["item"] == "WoodenSword" for i in bridge.state()["inventory"])
    assert mem.load() == ["WoodenSword = 7 Wood at a WorkBench"]

    req = claude.requests[-1]
    assert req["model"] == "claude-opus-5-5"
    assert req["fallbacks"] == "default" and "server-side-fallback-2026-07-01" in req["betas"]
    assert req["thinking"] == {"type": "adaptive"}
    # history is append-only and alternates user/assistant with tool results answering every call
    roles = [m["role"] for m in req["messages"]]
    assert roles == ["user", "assistant"] * 4 + ["user"]
    parallel = req["messages"][-1]["content"]
    assert [b["tool_use_id"] for b in parallel] == ["t4", "t5"]
    assert list((tmp_path / "runs").glob("run-*.jsonl"))


def test_lessons_reach_system_prompt(bridge, tmp_path):
    mem = Memory(tmp_path / "lessons.md")
    mem.add("Trees drop ~8 Wood each")
    claude = ScriptedClaude([[NS(type="text", text="ok")]])
    Agent(bridge, mem, ClaudeBackend(client=claude), verbose=False).run("noop", max_turns=1)
    assert "Trees drop ~8 Wood each" in claude.requests[0]["system"]


def test_haiku_skips_fallbacks():
    claude = ScriptedClaude([[NS(type="text", text="ok")]])
    b = ClaudeBackend(model="claude-haiku-5-5", client=claude)
    b.start("sys", "hi")
    b.step()
    assert "fallbacks" not in claude.requests[0]


def test_open_model_history_is_trimmed():
    b = OllamaBackend(keep_results=2)
    b.start("sys", "Goal: g\n\nWhat you see right now:\nYOU (1,2) ...big map...")
    for i in range(4):
        b.messages.append({"role": "assistant", "content": "", "tool_calls": []})
        b.add_results([(Call(f"c{i}", "observe", {}), f"YOU ({i},0) map...", False)])
    view = b._view()
    tool_msgs = [m["content"] for m in view if m["role"] == "tool"]
    assert tool_msgs[:2] == ["[old observation removed; call observe for a fresh one]"] * 2
    assert tool_msgs[2:] == ["YOU (2,0) map...", "YOU (3,0) map..."]
    assert "initial observation removed" in view[1]["content"] and "big map" not in view[1]["content"]
    assert "big map" in b.messages[1]["content"]  # stored history untouched


def test_repl_parse():
    assert parse("move_to 10 20 1") == ("move_to", {"x": 10, "y": 20, "tolerance": 1})
    assert parse('craft "WorkBench" 2') == ("craft", {"item": "WorkBench", "count": 2})
    assert parse("wait 1.5") == ("wait", {"seconds": 1.5})
    with pytest.raises(ValueError):
        parse("mine 3")


def test_until_house_pushes_back_then_stops_when_verified(bridge, tmp_path):
    turns = [
        [NS(type="text", text="All done!")],  # premature claim: must be pushed back
        [call(1, "check_house", {"x": 53, "y": 98})],  # fails: no room yet
        [call(2, "mine_nearest", {"target": "Trees", "count": 5}), call(14, "mine_nearest", {"target": "Trees", "count": 5})],
        [call(3, "craft", {"item": "WorkBench", "count": 2})],
        [call(4, "place", {"item": "WorkBench", "x": 51, "y": 99})],
        [call(5, "craft", {"item": "WoodWall", "count": 8}), call(6, "craft", {"item": "WoodenDoor", "count": 1})],
        [call(7, "craft", {"item": "WoodenChair", "count": 1}), call(8, "craft", {"item": "WoodenTable", "count": 1})],
        [call(9, "build_room", {"x": 50, "y": 100, "width": 8, "height": 6, "block": "Wood", "wall": "WoodWall", "door": "WoodenDoor"})],
        [call(10, "place", {"item": "WorkBench", "x": 52, "y": 99}), call(11, "place", {"item": "WoodenChair", "x": 54, "y": 99}),
         call(12, "place", {"item": "WoodenTable", "x": 55, "y": 99})],
        [call(13, "check_house", {"x": 53, "y": 98})],
        [NS(type="text", text="should never be requested")],
    ]
    claude = ScriptedClaude(turns)
    bridge.skill("wait", {"seconds": 0})  # warm-up no-op
    agent = Agent(bridge, Memory(tmp_path / "l.md"), ClaudeBackend(client=claude), verbose=False, until="house")
    stats = agent.run("Build a wooden house with a chair, table and work bench", max_turns=20)
    assert agent.verified and "HOUSE OK" in agent.last_check
    assert stats.turns == 10  # stopped right after the passing check
    pushback = claude.requests[1]["messages"][-1]["content"]
    assert "Not done yet" in pushback
