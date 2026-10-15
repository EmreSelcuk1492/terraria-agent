"""The Director: plays the first in-game days of a fresh world on its own.

A local model is too slow and too unreliable to drive every step (each turn costs seconds and it loops on
failures), so the plan lives here: an ordered list of steps, each with a check against the live game state
("have 120 Wood", "a valid NPC house exists", "max life >= 160") and the bridge macro skills that achieve it.
The skills do the hard part in game (gathering, crafting chains, tunneling, building, fighting).

On top of the plan:
- rules that always win: wait out death, heal or shelter at low health, avoid the surface at night
  (do underground work then, or stay home), climb back up before surface work;
- recovery: when a step keeps failing, the LLM advisor (optional) reads the failure and the surroundings and
  picks one corrective tool call; a step that still fails is parked for a while and the plan moves on;
- narration in the in-game chat, a journal (markdown) and a memo file that survives restarts.

    python -m terraria_agent.director                     # default: gpt-oss:20b advisor via Ollama
    python -m terraria_agent.director --advisor none      # rules only
"""

from __future__ import annotations

import argparse
import json
import re
import time
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from .bridge import BridgeClient, BridgeError
from .tools import TOOLS

ROOT = Path(__file__).resolve().parent.parent

TIER1 = ("Copper", "Tin")      # a world has one of each pair
TIER2 = ("Iron", "Lead")

Call = tuple[str, dict[str, Any]]


# ---- game state ----------------------------------------------------------------------------------------


@dataclass
class Ctx:
    """One observation, with helpers the step checks read."""

    state: dict[str, Any]
    inv: Counter = field(default_factory=Counter)

    @classmethod
    def of(cls, state: dict[str, Any]) -> "Ctx":
        inv: Counter = Counter()
        for slot in state.get("inventory", []):
            inv[slot["item"]] += slot["stack"]
        return cls(state, inv)

    @property
    def player(self) -> dict[str, Any]:
        return self.state.get("player", {})

    @property
    def world(self) -> dict[str, Any]:
        return self.state.get("world", {})

    def has(self, item: str, n: int = 1) -> bool:
        return self.inv[item] >= n

    def count(self, *items: str) -> int:
        return sum(self.inv[i] for i in items)

    @property
    def is_day(self) -> bool:
        return bool(self.world.get("is_day", True))

    @property
    def underground(self) -> bool:
        # Main.worldSurface sits well below the actual ground; spawn height is a better reference.
        spawn_y = (self.world.get("spawn") or [0, 10**9])[1]
        return self.player.get("y", 0) > min(self.world.get("surface_y", 10**9), spawn_y + 15)

    @property
    def hp_frac(self) -> float:
        return self.player.get("hp", 100) / max(1, self.player.get("max_hp", 100))

    def metal(self, pair: tuple[str, str]) -> str | None:
        """Which ore of a pair this world has, judged by what we hold (None until we have found some)."""
        a, b = pair
        ca, cb = self.count(f"{a}Ore", f"{a}Bar"), self.count(f"{b}Ore", f"{b}Bar")
        if ca == cb == 0:
            return None
        return a if ca >= cb else b

    def bars(self, pair: tuple[str, str]) -> int:
        m = self.metal(pair)
        return self.inv[f"{m}Bar"] if m else 0

    def ore_and_bars(self, pair: tuple[str, str]) -> int:
        """Ore-equivalents held (a bar is 3 ore for these tiers)."""
        return sum(self.inv[f"{m}Ore"] + 3 * self.inv[f"{m}Bar"] for m in pair)


@dataclass
class Memo:
    """What the director knows that the game state doesn't say directly. Saved per world."""

    houses: list[dict[str, Any]] = field(default_factory=list)   # {x, y, material, npc_valid}
    done: list[str] = field(default_factory=list)                 # one-shot steps finished
    parked: dict[str, float] = field(default_factory=dict)        # step -> unix time it may retry
    tries: dict[str, int] = field(default_factory=dict)
    stats: dict[str, int] = field(default_factory=dict)

    @classmethod
    def load(cls, path: Path) -> "Memo":
        if path.exists():
            try:
                return cls(**json.loads(path.read_text()))
            except (TypeError, ValueError):
                pass
        return cls()

    def save(self, path: Path) -> None:
        path.write_text(json.dumps(self.__dict__, indent=1))

    @property
    def home(self) -> dict[str, Any] | None:
        return self.houses[0] if self.houses else None

    def bump(self, key: str, n: int = 1) -> None:
        self.stats[key] = self.stats.get(key, 0) + n


# ---- the plan ------------------------------------------------------------------------------------------


@dataclass
class Step:
    key: str
    title: str
    why: str
    done: Callable[[Ctx, Memo], bool]
    # Alternatives: each is a list of calls; the next alternative is tried when one fails.
    plan: Callable[[Ctx, Memo], list[list[Call]]]
    underground: bool = False   # fine (better, even) to do at night
    once: bool = False          # mark done when the plan succeeds, the state can't tell
    surface: bool = True        # needs to be on the surface (climb up first)


def _house(material: str, extras: list[str], new: bool) -> Call:
    return ("build_house", {"x": None, "y": None, "material": material, "width": 12, "height": 7,
                            "extras": extras, "new_house": new})


def _go_home(m: Memo) -> list[Call]:
    h = m.home
    return [("move_to", {"x": h["x"] + 4, "y": h["y"] - 2, "tolerance": 2})] if h else []


def _metal_alts(pair: tuple[str, str], c: Ctx, count: int) -> list[list[Call]]:
    known = c.metal(pair)
    order = [known] if known else list(pair)
    return [[("mine_vein", {"target": m, "count": count, "radius": 60})] for m in order] + \
           [[("tunnel_to", {"x": c.player.get("x", 0) + 6, "y": c.player.get("y", 0) + 30, "tolerance": 3}),
             ("mine_vein", {"target": "ore", "count": count, "radius": 60})]]


def _gear(pair: tuple[str, str], piece: str) -> Callable[[Ctx, Memo], list[list[Call]]]:
    def plan(c: Ctx, m: Memo) -> list[list[Call]]:
        metal = c.metal(pair) or pair[0]
        other = pair[1] if metal == pair[0] else pair[0]
        return [[("obtain", {"item": f"{metal}{piece}", "count": 1}), ("equip_best", {})],
                [("obtain", {"item": f"{other}{piece}", "count": 1}), ("equip_best", {})]]
    return plan


def _has_gear(pair: tuple[str, str], piece: str) -> Callable[[Ctx, Memo], bool]:
    return lambda c, m: any(c.has(f"{metal}{piece}") or _wearing(c, f"{metal}{piece}") for metal in pair)


def _wearing(c: Ctx, item: str) -> bool:
    return item in (c.state.get("equipment") or [])


def starter_plan() -> list[Step]:
    """Day one to three of a fresh world: shelter, light, tools, a base, armor, exploration."""
    return [
        Step("gear", "Check gear", "wear the best of what we start with",
             lambda c, m: "gear" in m.done, lambda c, m: [[("equip_best", {})]], once=True),
        Step("wood", "Chop wood", "wood is the base of everything early: benches, walls, furniture, torches",
             lambda c, m: c.has("Wood", 60) or (bool(m.houses) and c.has("Wood", 20)),
             lambda c, m: [[("obtain", {"item": "Wood", "count": 150})]]),
        Step("torches", "Make torches", "light for the house and the mines (Gel from slimes + Wood)",
             lambda c, m: c.has("Torch", 15),
             lambda c, m: [[("obtain", {"item": "Torch", "count": 20})]]),
        Step("home", "Build a home", "a closed, lit, furnished room keeps us safe at night and lets the Guide move in",
             lambda c, m: bool(m.houses),
             lambda c, m: [[("move_to", {"x": c.world.get("spawn", [0, 0])[0], "y": c.world.get("spawn", [0, 0])[1] - 2, "tolerance": 3}),
                            _house("wood", [], False)],
                           [_house("wood", [], False)]]),
        Step("stone", "Dig for stone", "a furnace needs 20 stone, and stone makes a sturdier workshop",
             lambda c, m: c.has("StoneBlock", 50),
             lambda c, m: [[("obtain", {"item": "StoneBlock", "count": 70})]], underground=True, surface=False),
        Step("ore1", "Mine copper or tin", "the first metal: better pickaxe, sword and armor",
             lambda c, m: c.ore_and_bars(TIER1) >= 45,
             lambda c, m: _metal_alts(TIER1, c, 50), underground=True, surface=False),
        Step("ore2", "Mine iron or lead", "an anvil needs 5 iron or lead bars, and iron tools mine faster",
             lambda c, m: c.ore_and_bars(TIER2) >= 45,
             lambda c, m: _metal_alts(TIER2, c, 50), underground=True, surface=False),
        Step("crystals", "Find life crystals", "each one is +20 max life for good",
             lambda c, m: c.player.get("max_hp", 100) >= 160 or "crystals" in m.done,
             lambda c, m: [[("collect_life_crystals", {"max": 3, "radius": 100})],
                           [("tunnel_to", {"x": c.player.get("x", 0) - 8, "y": max(c.player.get("y", 0) + 40, c.world.get("rock_layer_y", 0)), "tolerance": 3}),
                            ("collect_life_crystals", {"max": 3, "radius": 100})]],
             underground=True, surface=False, once=True),
        Step("anvil", "Smelt bars and make an anvil", "the anvil unlocks every metal tool and armor piece",
             lambda c, m: c.has("IronAnvil") or c.has("LeadAnvil") or "anvil" in m.done,
             lambda c, m: [[("obtain", {"item": f"{c.metal(TIER2) or 'Iron'}Anvil", "count": 1})],
                           [("obtain", {"item": f"{'Lead' if c.metal(TIER2) == 'Iron' else 'Iron'}Anvil", "count": 1})]],
             once=True),
        Step("workshop", "Build a stone workshop", "a second NPC house with the furnace and anvil in it",
             lambda c, m: len(m.houses) >= 2,
             lambda c, m: [_go_home(m) + [_house("stone", ["Furnace", f"{c.metal(TIER2) or 'Iron'}Anvil"], True)]]),
        Step("pickaxe", "Craft a better pickaxe", "faster digging, and the next ores need more pick power",
             _has_gear(TIER2, "Pickaxe"), _gear(TIER2, "Pickaxe")),
        Step("sword", "Craft a broadsword", "the starter shortsword barely scratches zombies",
             lambda c, m: _has_gear(TIER2, "Broadsword")(c, m) or _has_gear(TIER1, "Broadsword")(c, m),
             _gear(TIER2, "Broadsword")),
        Step("helmet", "Craft a helmet", "armor: less damage from everything", _has_gear(TIER1, "Helmet"), _gear(TIER1, "Helmet")),
        Step("greaves", "Craft greaves", "more armor", _has_gear(TIER1, "Greaves"), _gear(TIER1, "Greaves")),
        Step("chainmail", "Craft chainmail", "the biggest armor piece", _has_gear(TIER1, "Chainmail"), _gear(TIER1, "Chainmail")),
        Step("loot", "Raid nearby chests", "surface and cave chests hold potions, coins and gear",
             lambda c, m: "loot" in m.done,
             lambda c, m: [[("loot_chests", {"radius": 150, "max": 8}), ("equip_best", {})]], once=True, surface=False),
        Step("village", "Build a house for the Merchant", "a third valid house: new NPCs move in once they have a home",
             lambda c, m: len(m.houses) >= 3,
             lambda c, m: [_go_home(m) + [_house("wood", ["Chest"], True)]]),
        Step("lights", "Finish the houses", "every house needs a light, a table, a chair and a door before an NPC moves in",
             lambda c, m: all(h.get("npc_valid") for h in m.houses),
             lambda c, m: [[("obtain", {"item": "Torch", "count": 5})] +
                           [("build_house", {"x": h["x"], "y": h["y"], "material": "stone" if h["material"].startswith("Stone") else "wood",
                                             "width": h.get("x2", h["x"] + 11) - h["x"] + 1, "height": 7, "extras": [], "new_house": False})
                            for h in m.houses if not h.get("npc_valid")]]),
        Step("store", "Put the loot away", "a tidy inventory: loot goes in the chest at home",
             lambda c, m: "store" in m.done,
             lambda c, m: [_go_home(m) + [("store_items", {"keep": []})]], once=True),
    ]


# ---- the director --------------------------------------------------------------------------------------


ADVISOR_PROMPT = """\
You are the recovery advisor for a Terraria bot. A scripted plan drives the game with high-level tools; one step
just failed. Read the failure and the surroundings, then make exactly ONE tool call that fixes the cause so the step
can be retried (for example: attack a nearby enemy, tunnel_to or move_to somewhere better, obtain a missing
ingredient, dig_down for stone, return_to_surface, mine a blocking tile). Do not repeat the call that just failed
unchanged. Coordinates are tiles; y grows downward. Reply with the tool call only."""


class Director:
    def __init__(self, bridge: BridgeClient, advisor=None, memo_dir: Path = ROOT / "runs", verbose: bool = True,
                 say_in_game: bool = True):
        self.bridge = bridge
        self.advisor = advisor
        self.verbose = verbose
        self.say_in_game = say_in_game
        self.memo_dir = memo_dir
        self.memo_dir.mkdir(parents=True, exist_ok=True)
        self.plan = starter_plan()
        self.memo = Memo()
        self.memo_path = self.memo_dir / "director-memo.json"
        stamp = time.strftime("%Y%m%d-%H%M%S")
        self.journal_path = self.memo_dir / f"journal-{stamp}.md"
        self.log_path = self.memo_dir / f"director-{stamp}.jsonl"
        self.started = time.time()
        self.last_save = time.time()
        self.calls = 0
        self.last_potion = 0.0

    # -- io

    def _log(self, event: str, **data: Any) -> None:
        with self.log_path.open("a") as f:
            f.write(json.dumps({"t": round(time.time(), 2), "event": event, **data}) + "\n")

    def note(self, text: str, chat: bool = False) -> None:
        clock = time.strftime("%H:%M:%S")
        if self.verbose:
            print(f"[{clock}] {text}", flush=True)
        with self.journal_path.open("a") as f:
            f.write(f"- `{clock}` {text}\n")
        if chat and self.say_in_game:
            try:
                self.bridge.say(text[:150])
            except BridgeError:
                pass

    def observe(self) -> Ctx:
        st = self.bridge.state()
        if not st.get("ok", True) and "player" not in st:
            raise BridgeError(st.get("error", "no state"))
        return Ctx.of(st)

    def call(self, name: str, args: dict[str, Any]) -> tuple[bool, str]:
        self.calls += 1
        t0 = time.time()
        try:
            r = self.bridge.skill(name, args)
        except BridgeError as e:
            return False, str(e)
        ok = bool(r.get("ok"))
        msg = r.get("message") or r.get("error") or ""
        self._log("call", name=name, args=args, ok=ok, message=msg, seconds=round(time.time() - t0, 1))
        if self.verbose:
            print(f"    {'ok ' if ok else 'ERR'} {name} {json.dumps(args)} -> {msg[:220]}", flush=True)
        if ok and name == "build_house":
            self._record_house(msg)
        if "guard:" in msg:
            self.memo.bump("enemies_killed", msg.count("killed"))
        return ok, msg

    def _record_house(self, msg: str) -> None:
        m = re.search(r"floor row y=(-?\d+), walls x (-?\d+)\.\.(-?\d+), (\w+)", msg)
        if not m:
            return
        y, x1, x2, mat = int(m.group(1)), int(m.group(2)), int(m.group(3)), m.group(4)
        valid = "NPC-VALID" in msg
        for h in self.memo.houses:
            if h["x"] == x1 and h["y"] == y:
                h["npc_valid"] = valid
                break
        else:
            self.memo.houses.append({"x": x1, "y": y, "x2": x2, "material": mat, "npc_valid": valid})

    # -- rules that always come first

    def safety(self, c: Ctx) -> bool:
        """Handle danger. True if it acted (observe again before planning)."""
        p = c.player
        if p.get("dead"):
            self.note("Died. Waiting to respawn.", chat=False)
            self.memo.bump("deaths")
            time.sleep(4)
            self.call("wait", {"seconds": 10})
            return True
        if c.hp_frac < 0.35:
            potion = next((i for i in ("LesserHealingPotion", "HealingPotion", "Mushroom") if c.has(i)), None)
            if potion and time.time() - self.last_potion > 60:  # potion sickness: one per minute
                self.last_potion = time.time()
                self.note(f"Low health ({p.get('hp')}/{p.get('max_hp')}): using {potion}.", chat=True)
                self.call("use_item", {"item": potion})
                return True
            if self.memo.home and not c.underground:
                self.note(f"Low health ({p.get('hp')}/{p.get('max_hp')}): retreating home to recover.", chat=True)
                for name, args in _go_home(self.memo):
                    self.call(name, args)
            self.call("wait", {"seconds": 15})
            return True
        return False

    def runnable(self, c: Ctx) -> list[Step]:
        now = time.time()
        return [s for s in self.plan if not s.done(c, self.memo) and self.memo.parked.get(s.key, 0) <= now]

    def choose(self, c: Ctx) -> Step | None:
        todo = self.runnable(c)
        if not todo:
            return None
        if c.is_day:
            return todo[0]
        # Night: zombies and demon eyes own the surface. Underground work, else stay home.
        return next((s for s in todo if s.underground), None)

    # -- running a step

    def run_step(self, step: Step, c: Ctx) -> bool:
        idx = self.plan.index(step) + 1
        self.note(f"Step {idx}/{len(self.plan)}: {step.title}, {step.why}.", chat=True)
        if step.surface and c.underground:
            self.note("Climbing back to the surface first.")
            self.call("return_to_surface", {})
        tries = self.memo.tries.get(step.key, 0)
        for alt in step.plan(c, self.memo):
            ok, msg = self._run_calls(alt)
            if ok and not step.once and not self._met(step):
                ok, msg = False, f"the calls succeeded but the goal isn't met yet ({msg[:120]})"
            if ok:
                if step.once and step.key not in self.memo.done:
                    self.memo.done.append(step.key)
                self.memo.tries.pop(step.key, None)
                self.memo.bump("steps_done")
                self.note(f"Done: {step.title}.", chat=True)
                return True
            self.note(f"{step.title}: {msg[:200]}")
            if self._advise(step, msg):
                ok, msg = self._run_calls(alt)
                if ok:
                    if step.once and step.key not in self.memo.done:
                        self.memo.done.append(step.key)
                    self.note(f"Done after a fix: {step.title}.", chat=True)
                    return True
        tries += 1
        self.memo.tries[step.key] = tries
        if tries >= 2:
            wait = 600 * tries
            self.memo.parked[step.key] = time.time() + wait
            self.note(f"Parking '{step.title}' for {wait // 60} min after {tries} failed attempts; moving on.", chat=True)
        return False

    def _met(self, step: Step) -> bool:
        try:
            return step.done(self.observe(), self.memo)
        except BridgeError:
            return True  # can't tell; don't punish the step for a bridge hiccup

    def _run_calls(self, calls: list[Call]) -> tuple[bool, str]:
        msg = ""
        for name, args in calls:
            ok, msg = self.call(name, args)
            if not ok:
                return False, msg
        return True, msg

    def _advise(self, step: Step, failure: str) -> bool:
        """Ask the LLM for one corrective call and run it. True if it ran and succeeded."""
        if self.advisor is None:
            return False
        try:
            look = self.bridge.state()
            from .tools import ToolExecutor  # observation text renderer
            from .memory import Memory
            text, _ = ToolExecutor(self.bridge, Memory(self.memo_dir / "advisor-lessons.md")).run("observe", {})
        except Exception:
            text = json.dumps(look)[:4000] if "look" in locals() else ""
        self.advisor.start(ADVISOR_PROMPT, f"Step: {step.title} ({step.why}).\nFailure: {failure}\n\nSurroundings:\n{text}")
        try:
            turn = self.advisor.step()
        except Exception as e:  # the advisor is optional; never let it stop the run
            self.note(f"(advisor unavailable: {e})")
            return False
        if not turn.calls:
            return False
        call = turn.calls[0]
        if not isinstance(call.args, dict) or call.name in ("observe", "remember"):
            return False
        args = {k: v for k, v in call.args.items() if v is not None}
        self.note(f"Advisor: try {call.name} {json.dumps(args)}")
        self.memo.bump("advisor_fixes")
        ok, _ = self.call(call.name, args)
        return ok

    # -- main loop

    def run(self, hours: float = 3.0) -> None:
        world = self.bridge.health().get("world") or "world"
        self.memo_path = self.memo_dir / f"director-memo-{re.sub(r'[^A-Za-z0-9]+', '_', world)}.json"
        self.memo = Memo.load(self.memo_path)
        self.bridge.set_background(True)
        self.bridge.set_guard(True)
        self.bridge.set_freeze(False)
        self.journal_path.write_text(f"# Director journal: {world}\n\nStarted {time.strftime('%Y-%m-%d %H:%M')}.\n\n")
        self.note(f"Director online in '{world}'. Plan: {len(self.plan)} steps from first wood to a small village.", chat=True)
        deadline = self.started + hours * 3600
        idle = 0
        try:
            while time.time() < deadline:
                try:
                    if not self.bridge.health().get("in_world"):
                        self.note("Not in a world; waiting.")
                        time.sleep(10)
                        continue
                    c = self.observe()
                except BridgeError as e:
                    self.note(f"Bridge unreachable ({e}); waiting.")
                    time.sleep(15)
                    continue
                if self.safety(c):
                    continue
                step = self.choose(c)
                if step is None:
                    if not self.runnable(c):
                        idle += 1
                        if idle == 1:
                            self.note("Plan complete for now. Patrolling: more ore and chests while time remains.", chat=True)
                        self._free_roam(c)
                        continue
                    self._night(c)
                    continue
                idle = 0
                self.run_step(step, c)
                self.memo.save(self.memo_path)
                if time.time() - self.last_save > 300:
                    self.bridge.save()
                    self.last_save = time.time()
        except KeyboardInterrupt:
            self.note("Stopped by user.")
        finally:
            self.finish()

    def _night(self, c: Ctx) -> None:
        if self.memo.home and not c.underground:
            h = self.memo.home
            if abs(c.player.get("x", 0) - (h["x"] + 4)) > 6:
                self.note(f"Night ({c.world.get('time')}): heading home until morning.", chat=True)
                for name, args in _go_home(self.memo):
                    self.call(name, args)
        self.call("wait", {"seconds": 20})

    def _free_roam(self, c: Ctx) -> None:
        """Everything is done: keep improving (more metal, more life, more loot) until the time budget runs out."""
        p, w = c.player, c.world
        calls: list[Call] = []
        if c.is_day and "loot_exhausted" not in self.memo.done:
            calls.append(("loot_chests", {"radius": 200, "max": 5}))
        depth = max(p.get("y", 0) + 30, w.get("rock_layer_y", 0) - 10)
        if not c.underground or p.get("y", 0) < w.get("rock_layer_y", 0) - 30:
            calls.append(("tunnel_to", {"x": p.get("x", 0) + (12 if self.calls % 2 else -12), "y": depth, "tolerance": 3}))
        calls += [("mine_vein", {"target": "ore", "count": 40, "radius": 80}),
                  ("collect_life_crystals", {"max": 2, "radius": 120}),
                  ("equip_best", {})]
        wins = 0
        for name, args in calls:
            ok, msg = self.call(name, args)
            wins += ok and name != "equip_best"
            if name == "loot_chests" and not ok and "no unlooted chests" in msg:
                self.memo.done.append("loot_exhausted")
            if ok and name == "collect_life_crystals":
                self.note(f"Life crystal! {msg}", chat=True)
        self.memo.parked.clear()
        if not wins:
            time.sleep(5)

    def finish(self) -> None:
        try:
            c = self.observe()
            self.bridge.save()
        except BridgeError:
            c = None
        self.memo.save(self.memo_path)
        mins = (time.time() - self.started) / 60
        lines = [f"\n## Summary after {mins:.0f} minutes\n"]
        if c:
            p, w = c.player, c.world
            lines += [f"- Max life: {p.get('max_hp')}, defense: {w.get('defense', '?')}",
                      f"- Town NPCs: {', '.join(w.get('town_npcs') or []) or 'none'}"]
            keep = ["Wood", "StoneBlock", "Torch"] + [f"{m}{k}" for m in TIER1 + TIER2 for k in ("Ore", "Bar")]
            lines.append("- Holding: " + ", ".join(f"{c.inv[k]} {k}" for k in keep if c.inv[k]))
        done = [s.title for s in self.plan if c and s.done(c, self.memo)]
        lines.append(f"- Plan steps complete: {len(done)}/{len(self.plan)}: {', '.join(done)}")
        lines.append(f"- Houses: " + "; ".join(f"{h['material']} at x {h['x']}..{h.get('x2')}, y {h['y']}"
                                                + (" (NPC-valid)" if h.get('npc_valid') else "") for h in self.memo.houses))
        lines.append(f"- Skill calls: {self.calls}; stats: {json.dumps(self.memo.stats)}")
        with self.journal_path.open("a") as f:
            f.write("\n".join(lines) + "\n")
        self.note(f"Session over: {len(done)}/{len(self.plan)} plan steps done. Journal: {self.journal_path.name}", chat=True)
        try:
            self.bridge.set_background(False)
        except BridgeError:
            pass


def make_advisor(kind: str, model: str):
    if kind == "none":
        return None
    from .backends import OllamaBackend
    return OllamaBackend(model=model, keep_results=2, think="auto")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="terraria_agent.director", description=__doc__.split("\n")[0])
    ap.add_argument("--bridge", default="http://127.0.0.1:7979")
    ap.add_argument("--hours", type=float, default=3.0, help="time budget")
    ap.add_argument("--advisor", choices=["ollama", "none"], default="ollama")
    ap.add_argument("--model", default="gpt-oss:20b")
    ap.add_argument("--quiet-chat", action="store_true", help="don't narrate in the in-game chat")
    a = ap.parse_args(argv)
    bridge = BridgeClient(a.bridge, skill_timeout=900)
    Director(bridge, make_advisor(a.advisor, a.model), say_in_game=not a.quiet_chat).run(hours=a.hours)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
