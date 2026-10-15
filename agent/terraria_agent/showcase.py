"""A narrated tour of what the agent can do, for watching (or recording) on a real screen.

    python -m terraria_agent.showcase                 # Mac game on 7979
    python -m terraria_agent.showcase --acts nav,llm

Acts: nav (greedy vs A* vs TerraBlind's tb_nav on the tunnel course), fight (a slime pack), bunker (low health at
midnight, dig in), llm (the local model plans a crafting task from primitive skills, its reasoning in chat).
Every act uses the scenario arena, which flattens a strip 80 tiles east of spawn: back the world up first.
"""

from __future__ import annotations

import argparse
import json
import textwrap
import time

from .bridge import BridgeClient, BridgeError
from . import llm_bench
from .scenarios import NAV
from .skill_library import SkillLibrary
from .director import ROOT


class Show:
    def __init__(self, bridge: BridgeClient, pace: float):
        self.b, self.pace = bridge, pace

    def say(self, text: str) -> None:
        print(f"  > {text}", flush=True)
        for line in textwrap.wrap(text, 140):
            try:
                self.b.say(line)
            except BridgeError:
                pass
            time.sleep(0.4)

    def title(self, act: str, detail: str) -> None:
        print(f"\n== {act}: {detail}", flush=True)
        self.b.status(f"Showcase: {act}", detail)
        self.say(f"--- {act}: {detail}")
        time.sleep(self.pace)

    def setup(self, **kw) -> dict:
        for _ in range(30):
            if not self.b.state().get("player", {}).get("dead"):
                break
            time.sleep(1)
        r = self.b._post("/scenario", {"arena": True, "fresh_player": True, "clear": True, "natural_spawns": False, **kw})
        if not r.get("ok"):
            raise BridgeError(r.get("error", "setup failed"))
        time.sleep(self.pace / 2)
        return r

    def skill(self, name: str, args: dict) -> tuple[bool, str]:
        r = self.b.skill(name, args)
        ok, msg = bool(r.get("ok")), r.get("message") or r.get("error") or ""
        print(f"    {'ok ' if ok else 'ERR'} {name} {json.dumps(args)} -> {msg[:200]}", flush=True)
        return ok, msg

    # -- acts

    def nav(self) -> None:
        course = next(s for s in NAV if s.name == "nav_tunnel")
        _, args = course.call
        for label, cfg, skill in (("greedy walker", {"astar": False}, "move_to"),
                                  ("A* pathfinder", {"astar": True}, "move_to"),
                                  ("TerraBlind navigator", {"astar": True}, "tb_nav")):
            self.title("Navigation", f"{label}: the target is in a tunnel, the only door is a shaft behind us")
            self.b._post("/config", cfg)
            r = self.setup(**course.setup)
            ax, ag = r["arena"]
            target = {"x": ax + args["dx"], "y": ag + args["dy"]}
            ok, msg = self.skill(skill, {**target, "tolerance": 1} if skill == "move_to" else target)
            self.say(f"{label}: {'made it' if ok else 'stuck'}. {msg[:110]}")
            time.sleep(self.pace)

    def fight(self) -> None:
        self.title("Combat", "three slimes; the shortsword finally knows its reach (1.8 tiles, not 14)")
        self.setup(time="noon", spawn=[{"npc": "BlueSlime", "dx": d} for d in (5, 8, -6)])
        ok, msg = self.skill("attack", {"target": "nearest", "max_kills": 3})
        self.say(f"Result: {msg[:120]}")
        time.sleep(self.pace)

    def bunker(self) -> None:
        self.title("Survival", "midnight, half health (where the Director digs in), a Demon Eye and a zombie closing in")
        self.setup(time="midnight", hp=50, spawn=[{"npc": "DemonEye", "dx": 3, "dy": -8}, {"npc": "Zombie", "dx": 12}])
        ok, msg = self.skill("dig_in", {"wait_seconds": 25, "until_hp": 0.6})
        self.say(f"Bunker: {msg[:130]}")
        time.sleep(self.pace)

    def llm(self, model: str) -> None:
        task = next(t for t in llm_bench.TASKS if t.name == "hammer")
        self.title("The model thinks", f"{model}, running locally, plans '{task.goal}' from primitive skills only")
        self.b._post("/config", {"freeze": True, "peaceful": True})
        lib = SkillLibrary(ROOT / "skills" / "library-showcase.json")
        planner = llm_bench.OllamaPlanner(model)
        orig = planner.plan

        def narrated(messages):
            self.say(f"[{model}] thinking...")
            plan = orig(messages)
            self.say(f"[{model}] {plan.get('thought', '')[:260]}")
            steps = "; ".join(f"{s['skill']} {json.dumps(s.get('args', {}))}" for s in plan.get("program", []))
            self.say(f"[{model}] program '{plan.get('name')}': {steps[:260]}")
            return plan

        planner.plan = narrated
        r = llm_bench.run_task(task, self.b, planner, lib, "primitive", 3, True,
                               {"fresh_player": True, "surface_at": 150, "clear": True, "natural_spawns": False, "time": "noon"})
        self.say(f"Verified by the game: {'PASS' if r.passed else 'FAIL'} after {r.attempts} attempt(s), "
                 f"{r.llm_seconds:.0f}s thinking. {'Saved to the skill library as ' + r.saved_as if r.saved_as else ''}")
        self.b._post("/config", {"freeze": False, "peaceful": False})


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="terraria_agent.showcase", description=__doc__.split("\n")[0])
    ap.add_argument("--bridge", default="http://127.0.0.1:7979")
    ap.add_argument("--acts", default="nav,fight,bunker,llm")
    ap.add_argument("--pace", type=float, default=3.0, help="seconds between beats")
    ap.add_argument("--model", default="gpt-oss:20b")
    a = ap.parse_args(argv)
    b = BridgeClient(a.bridge, skill_timeout=900)
    b._post("/config", {"guard": True, "background": True, "freeze": False, "peaceful": False, "god": False})
    show = Show(b, a.pace)
    show.say("Showcase: A* pathfinding, real combat, the dig-in bunker, and a local model writing its own skills.")
    for act in a.acts.split(","):
        getattr(show, act.strip())(*([a.model] if act.strip() == "llm" else []))
    b._post("/scenario", {"clear": True, "natural_spawns": True})
    b.status("Showcase over")
    show.say("That's the show. Every number behind it comes from the scenario suite.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
