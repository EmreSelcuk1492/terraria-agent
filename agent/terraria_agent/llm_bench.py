"""LLM track: a local open model plans programs of bridge skills, the game verifies them, a library keeps what works.

Per task: the planner (gpt-oss:20b via Ollama by default, structured JSON output) sees the goal, the game state,
the skills it may call and the closest library skills, and writes a program. The executor runs it in the real game
(world frozen while the model thinks), the verifier checks the task's goal against the live inventory, and a failure
goes back to the model with the failing call, its message and the new state, up to --attempts times. A program
that passes is saved to the library under the model's chosen name; later tasks can call it as one step.

Two modes keep the claim honest:
- macro:     every skill, including obtain (one call that gathers and crafts whole chains)
- primitive: no macros; the model composes mine_nearest / craft / place / move_to into chains itself

    python -m terraria_agent.llm_bench --bridge http://127.0.0.1:18979 --mode primitive --library fresh
    python -m terraria_agent.llm_bench --bridge http://127.0.0.1:18979 --mode primitive --library learned

Each task starts from the same state (fresh character, untouched terrain, peaceful), so runs are comparable.
Results: runs/llm/<stamp>/results.json + report.md.
"""

from __future__ import annotations

import argparse
import json
import time
from collections import Counter
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

import httpx

from .bridge import BridgeClient, BridgeError, exclusive
from .director import ROOT
from .skill_library import SkillLibrary
from .tools import TOOLS

MACROS = {"obtain", "build_house", "mine_vein", "collect_life_crystals", "loot_chests", "store_items"}
NOT_PLANNABLE = {"observe", "remember", "set_spawn", "dig_in"}


@dataclass
class Task:
    name: str
    goal: str
    check: dict[str, Any]
    give: dict[str, int] = field(default_factory=dict)  # prerequisites handed over at the start


TASKS = [
    Task("wood", "Hold at least 30 Wood (any kind: Wood, BorealWood, PalmWood...).", {"have": {"Wood": 30}}),
    Task("workbench", "Hold a work bench (any wood variant counts, e.g. BorealWoodWorkBench).", {"have": {"WorkBench": 1}}),
    Task("hammer", "Hold a wooden hammer (any wood variant counts).", {"have": {"WoodenHammer": 1}}),
    Task("stone", "Hold at least 30 StoneBlock.", {"have": {"StoneBlock": 30}}),
    Task("chair_table", "Hold a wooden chair and a wooden table (any wood variant counts).", {"have": {"WoodenChair": 1, "WoodenTable": 1}}),
    Task("door", "Hold a wooden door (any wood variant counts).", {"have": {"WoodenDoor": 1}}),
    Task("furnace", "Hold a Furnace.", {"have": {"Furnace": 1}}, give={"Torch": 5}),
    Task("copper_bars", "Hold at least 3 CopperBar or 3 TinBar (whichever ore this world has).",
         {"any": [{"have": {"CopperBar": 3}}, {"have": {"TinBar": 3}}]}, give={"Torch": 5}),
]


# Terraria's recipe groups: "Wood" in a recipe takes any of these. A snow-biome start gives BorealWood, and a check
# that wanted the item literally named Wood failed a run where the model had done the job (31 BorealWood).
GROUPS = {"Wood": ("Wood", "BorealWood", "RichMahogany", "Ebonwood", "Shadewood", "PalmWood", "Pearlwood", "AshWood")}


# Wood furniture and tools come in one variant per wood ("BorealWoodWorkBench", "BorealWoodHammer"): in a snow
# start the model rightly crafted those, since a plain WorkBench needs plain Wood. Checks accept any variant.
WOOD_PREFIXES = ("BorealWood", "RichMahogany", "Ebonwood", "Shadewood", "PalmWood", "Pearlwood", "AshWood")
WOOD_VARIANTS = ("WorkBench", "WoodenHammer", "WoodenChair", "WoodenTable", "WoodenDoor", "WoodenSword", "WoodenBow")


def held(inv: Counter, item: str) -> int:
    names = set(GROUPS.get(item, (item,)))
    if item in WOOD_VARIANTS:
        suffix = item.removeprefix("Wooden")
        names |= {pre + suffix for pre in WOOD_PREFIXES}
    return sum(inv[i] for i in names)


def verify(check: dict[str, Any], inv: Counter) -> bool:
    if "any" in check:
        return any(verify(c, inv) for c in check["any"])
    return all(held(inv, k) >= v for k, v in check.get("have", {}).items())


PROGRAM_SCHEMA = {
    "type": "object",
    "properties": {
        "thought": {"type": "string"},
        "name": {"type": "string"},
        "description": {"type": "string"},
        "program": {"type": "array", "minItems": 1, "items": {"type": "object", "properties": {
            "skill": {"type": "string"}, "args": {"type": "object"}}, "required": ["skill", "args"]}},
    },
    "required": ["thought", "name", "description", "program"],
}


class OllamaPlanner:
    """One JSON-schema-constrained chat call per plan; keeps the task's conversation for retries."""

    def __init__(self, model: str = "gpt-oss:20b", base_url: str = "http://127.0.0.1:11434", think: str = "low"):
        self.model, self.think = model, think
        self.http = httpx.Client(base_url=base_url, timeout=httpx.Timeout(10.0, read=600.0))
        self.calls = 0
        self.seconds = 0.0
        self.tokens = 0

    def plan(self, messages: list[dict[str, str]]) -> dict[str, Any]:
        t0 = time.time()
        body = {"model": self.model, "messages": messages, "stream": False, "format": PROGRAM_SCHEMA,
                "options": {"num_ctx": 16384, "temperature": 0.2}}
        if self.think:
            body["think"] = self.think
        r = self.http.post("/api/chat", json=body).json()
        self.calls += 1
        self.seconds += time.time() - t0
        self.tokens += int(r.get("eval_count", 0)) + int(r.get("prompt_eval_count", 0))
        if "error" in r:
            raise RuntimeError(f"ollama: {r['error']}")
        return json.loads(r["message"]["content"])


def skill_menu(mode: str) -> str:
    lines = []
    for t in TOOLS:
        n = t["name"]
        if n in NOT_PLANNABLE or (mode == "primitive" and n in MACROS):
            continue
        params = ", ".join(t["input_schema"]["properties"])
        lines.append(f"- {n}({params}): {t['description']}")
    return "\n".join(lines)


SYSTEM = """You control a Terraria character through skills that run in the real game. For each task, write a
program: an ordered list of skill calls that, run one after another, achieves the goal. Rules:
- Use only the skills listed below (exact names and argument names), or a library skill as {"skill": "lib:<name>", "args": {}}.
- Item and tile names are internal names: Wood, StoneBlock, WorkBench, WoodenHammer, CopperOre, CopperBar, Trees, Stone.
- craft walks to or places the station a recipe needs; it does not gather ingredients, so gather them first.
- Keep programs short and robust; prefer one call with a count over many single calls.
- Prefer skills that find things themselves (mine_nearest, craft) over absolute x/y coordinates: only programs
  without coordinates are saved to the library, because coordinates don't carry over to the next place.
- name: a short snake_case name describing what the program does in general (it is saved for reuse if it works).
- description: one sentence saying what it achieves.
Skills:
{menu}
"""


def state_text(st: dict[str, Any]) -> str:
    inv = Counter()
    for it in st.get("inventory", []):
        inv[it.get("item")] += int(it.get("stack", 0))
    p = st.get("player", {})
    near = json.dumps(st.get("nearby", {}), separators=(",", ":"))[:1500]
    return (f"Player at ({p.get('x')},{p.get('y')}), hp {p.get('hp')}/{p.get('max_hp')}, time {st.get('world', {}).get('time')}.\n"
            f"Inventory: {', '.join(f'{v} {k}' for k, v in inv.items()) or 'empty'}.\n"
            f"Craftable now: {', '.join(st.get('craftable_now', [])[:30]) or 'nothing'}.\n"
            f"Nearby: {near}")


def inventory(st: dict[str, Any]) -> Counter:
    inv = Counter()
    for it in st.get("inventory", []):
        inv[it.get("item")] += int(it.get("stack", 0))
    return inv


@dataclass
class TaskResult:
    task: str
    passed: bool
    attempts: int
    llm_calls: int
    llm_seconds: float
    game_seconds: float
    skill_calls: int
    used_library: list[str]
    saved_as: str | None
    final_program: list[dict[str, Any]]
    log: list[str]
    error: str | None = None


class Executor:
    def __init__(self, bridge: BridgeClient, library: SkillLibrary | None, mode: str):
        self.bridge, self.library, self.mode = bridge, library, mode
        self.skill_calls = 0
        self.game_seconds = 0.0
        self.used: list[str] = []
        self.log: list[str] = []

    def run(self, program: list[dict[str, Any]], depth: int = 0) -> tuple[bool, str]:
        for i, step in enumerate(program):
            name, args = str(step.get("skill", "")), step.get("args") or {}
            if name.startswith("lib:"):
                sk = self.library.get(name) if self.library else None
                if sk is None or depth >= 3:
                    return False, f"step {i + 1}: no library skill '{name}'"
                self.used.append(sk.name)
                ok, msg = self.run(sk.program, depth + 1)
                ok = ok and verify(sk.check, inventory(self.bridge.state()))
                self.library.record_use(sk.name, ok)
                if not ok:
                    return False, f"step {i + 1}: library skill {name} did not reach its check {json.dumps(sk.check)}: {msg}"
                continue
            if name in NOT_PLANNABLE or (self.mode == "primitive" and name in MACROS) or name not in _SKILLS:
                return False, f"step {i + 1}: '{name}' is not an available skill"
            t0 = time.time()
            try:
                r = self.bridge.skill(name, args)
            except BridgeError as e:
                return False, f"step {i + 1} {name}: {e}"
            self.skill_calls += 1
            self.game_seconds += time.time() - t0
            ok = bool(r.get("ok"))
            msg = r.get("message") or r.get("error") or ""
            self.log.append(f"{'ok ' if ok else 'ERR'} {name} {json.dumps(args)} -> {msg[:200]}")
            if not ok:
                return False, f"step {i + 1} {name} {json.dumps(args)} failed: {msg[:400]}"
        return True, "program finished"


_SKILLS = {t["name"] for t in TOOLS}


def run_task(task: Task, bridge: BridgeClient, planner: OllamaPlanner, library: SkillLibrary | None, mode: str,
             attempts: int, use_library: bool, start: dict[str, Any]) -> TaskResult:
    setup = {**start, "give": task.give} if task.give else start
    for _ in range(30):  # a death in the previous task: wait out the respawn
        if not bridge.state().get("player", {}).get("dead"):
            break
        time.sleep(1)
    r = bridge._post("/scenario", setup)
    if not r.get("ok"):
        raise BridgeError(r.get("error", "setup failed"))
    calls0, secs0 = planner.calls, planner.seconds
    ex = Executor(bridge, library if use_library else None, mode)
    hits = library.search(f"{task.name} {task.goal} {json.dumps(task.check)}") if (library and use_library) else []
    lib_text = "\n".join(s.summary() for s in hits) or "(empty)"
    messages = [{"role": "system", "content": SYSTEM.replace("{menu}", skill_menu(mode))},
                {"role": "user", "content": f"Task: {task.goal}\nGoal check (verified against your inventory): {json.dumps(task.check)}\n"
                                            f"Library skills you can call:\n{lib_text}\n\nState:\n{state_text(bridge.state())}"}]
    program: list[dict[str, Any]] = []
    plan: dict[str, Any] = {}
    for attempt in range(1, attempts + 1):
        try:
            plan = planner.plan(messages)
        except (RuntimeError, json.JSONDecodeError, httpx.HTTPError) as e:
            ex.log.append(f"planner error: {e}")
            continue
        program = plan.get("program") or []
        ex.log.append(f"attempt {attempt}: {plan.get('name')}: {json.dumps(program)[:600]}")
        ok, msg = ex.run(program)
        st = bridge.state()
        if ok and verify(task.check, inventory(st)):
            saved = None
            located = any(k in (st.get("args") or {}) for st in program for k in ("x", "y"))
            if located:
                ex.log.append("  (passed, not saved: the program uses absolute coordinates, which don't generalise)")
            if library is not None and use_library and not located and not (len(program) == 1 and program[0]["skill"].startswith("lib:")):
                saved = library.add(plan.get("name") or task.name, plan.get("description") or task.goal, task.goal, program, task.check).name
            return TaskResult(task.name, True, attempt, planner.calls - calls0, round(planner.seconds - secs0, 1),
                              round(ex.game_seconds, 1), ex.skill_calls, ex.used, saved, program, ex.log)
        why = msg if not ok else f"the program finished but the goal check {json.dumps(task.check)} is not met"
        if not program:
            why = "the program was empty; write at least one skill call"
        ex.log.append(f"  -> {why[:300]}")
        messages += [{"role": "assistant", "content": json.dumps(plan)},
                     {"role": "user", "content": f"That did not work: {why}\n\nState now:\n{state_text(st)}\n\nWrite a corrected program."}]
    return TaskResult(task.name, False, attempts, planner.calls - calls0, round(planner.seconds - secs0, 1),
                      round(ex.game_seconds, 1), ex.skill_calls, ex.used, None, program, ex.log)


def write_report(results: list[TaskResult], meta: dict[str, Any], out: Path) -> None:
    out.mkdir(parents=True, exist_ok=True)
    (out / "results.json").write_text(json.dumps({"meta": meta, "results": [asdict(r) for r in results]}, indent=1))
    lines = [f"# LLM track {time.strftime('%Y-%m-%d %H:%M')}", "", f"`{json.dumps(meta)}`", "",
             "| task | pass | attempts | LLM calls | LLM s | game s | skill calls | used library | saved as |",
             "|---|---|---|---|---|---|---|---|---|"]
    for r in results:
        lines.append(f"| {r.task} | {'yes' if r.passed else 'no'} | {r.attempts} | {r.llm_calls} | {r.llm_seconds} | {r.game_seconds} | "
                     f"{r.skill_calls} | {', '.join(r.used_library) or '-'} | {r.saved_as or '-'} |")
    n = len(results)
    lines += ["", f"**{sum(r.passed for r in results)}/{n} tasks passed; {sum(r.llm_calls for r in results)} LLM calls, "
                  f"{sum(r.llm_seconds for r in results):.0f}s thinking, {sum(r.game_seconds for r in results):.0f}s in game.**", "",
              "## Logs", ""]
    for r in results:
        lines += [f"### {r.task} ({'pass' if r.passed else 'FAIL'})", "```", *r.log[-20:], "```", ""]
    (out / "report.md").write_text("\n".join(lines) + "\n")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="terraria_agent.llm_bench", description=__doc__.split("\n")[0])
    ap.add_argument("--bridge", default="http://127.0.0.1:7979")
    ap.add_argument("--mode", choices=["primitive", "macro"], default="primitive")
    ap.add_argument("--library", choices=["off", "fresh", "learned"], default="fresh",
                    help="off: never save or use; fresh: start empty and save; learned: start from the saved library")
    ap.add_argument("--model", default="gpt-oss:20b")
    ap.add_argument("--think", default="low", help="reasoning effort for models that support it ('' to omit)")
    ap.add_argument("--attempts", type=int, default=3)
    ap.add_argument("--only", action="append")
    ap.add_argument("--library-path", type=Path, help="default skills/library-<mode>.json")
    ap.add_argument("--narrate", action="store_true", help="show the model's reasoning and programs in the in-game chat")
    ap.add_argument("--start-dx", type=int, default=150, help="start this many tiles east of spawn (untouched terrain)")
    a = ap.parse_args(argv)
    lock = exclusive(a.bridge)  # noqa: F841 (held for the whole run)
    lib_path = a.library_path or ROOT / "skills" / f"library-{a.mode}.json"
    if a.library == "fresh" and lib_path.exists():
        lib_path.rename(lib_path.with_suffix(f".{time.strftime('%Y%m%d-%H%M%S')}.json"))  # keep old libraries
    library = None if a.library == "off" else SkillLibrary(lib_path)
    bridge = BridgeClient(a.bridge, skill_timeout=900)
    bridge._post("/config", {"peaceful": True, "god": True, "freeze": True, "guard": True, "background": True})
    start = {"fresh_player": True, "surface_at": a.start_dx, "clear": True, "natural_spawns": False, "time": "noon"}
    planner = OllamaPlanner(a.model, think=a.think)
    if a.narrate:
        plan = planner.plan

        def narrated(messages):
            out = plan(messages)
            steps = "; ".join(f"{st['skill']} {json.dumps(st.get('args', {}), separators=(',', ':'))}" for st in out.get("program", []))
            for text in (f"[{a.model}] {out.get('thought', '')}", f"[{a.model}] {out.get('name')}: {steps}"):
                try:
                    bridge.say(text[:150])
                except BridgeError:
                    pass
            return out

        planner.plan = narrated
    tasks = [t for t in TASKS if not a.only or t.name in a.only]
    out = ROOT / "runs" / "llm" / f"{time.strftime('%Y%m%d-%H%M%S')}-{a.mode}-{a.library}"
    meta = {"model": a.model, "think": a.think, "mode": a.mode, "library": a.library, "attempts": a.attempts,
            "library_size_at_start": len(library.skills) if library else 0}
    results = []
    for i, t in enumerate(TASKS):
        if t not in tasks:
            continue
        # Each task on its own patch of ground (same patch every pass), so one task's tree cutting can't starve the next.
        place = {**start, "surface_at": a.start_dx + 120 * i}
        try:
            r = run_task(t, bridge, planner, library, a.mode, a.attempts, a.library != "off", place)
        except BridgeError as e:
            r = TaskResult(t.name, False, 0, 0, 0, 0, 0, [], None, [], [], str(e))
        results.append(r)
        if a.narrate:
            try:
                bridge.say(f"Task '{t.name}': {'PASS' if r.passed else 'FAIL'} (verified in game), {r.attempts} attempt(s)"
                           + (f", saved as lib:{r.saved_as}" if r.saved_as else "") + (f", reused {', '.join(r.used_library)}" if r.used_library else ""))
            except BridgeError:
                pass
        print(f"{'PASS' if r.passed else 'FAIL'} {t.name}: attempts {r.attempts}, LLM {r.llm_calls} calls/{r.llm_seconds}s, "
              f"game {r.game_seconds}s, {r.skill_calls} skills, library {r.used_library or '-'}, saved {r.saved_as or '-'} {r.error or ''}",
              flush=True)
        write_report(results, meta, out)
    meta["library_size_at_end"] = len(library.skills) if library else 0
    write_report(results, meta, out)
    print(f"\n{sum(r.passed for r in results)}/{len(results)} passed. Report: {out / 'report.md'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
