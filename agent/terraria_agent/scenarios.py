"""Scenario tests: short, repeatable situations against a real game (or the mock), scored pass/fail.

A full Director run takes minutes and mixes everything (planning, building, combat, luck with the night). A scenario
pins one situation down: POST /scenario puts the world in a known state (enemies at exact offsets, health, time,
items, no natural spawns), the Director's real safety loop plays it for a few seconds while the character works,
and checks decide pass or fail. Run the suite after every change to the survival rules, several times, on
several game instances at once:

    python -m terraria_agent.scenarios --bridge http://127.0.0.1:18979 --bridge http://127.0.0.1:18980 --repeat 3
    python -m terraria_agent.scenarios --only zombie_half_hp --bridge http://127.0.0.1:7979

Results: runs/scenarios/<stamp>/results.json + report.md. Exit code 1 if any scenario's pass rate is below 100%.
"""

from __future__ import annotations

import argparse
import json
import queue
import tempfile
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .bridge import BridgeClient, BridgeError, exclusive
from .director import ROOT, Director

# What the character is busy with when trouble arrives: a long gathering job, like a real plan step.
WORK = ("obtain", {"item": "Wood", "count": 500})


@dataclass
class Scenario:
    name: str
    why: str
    setup: dict[str, Any]
    seconds: float
    max_deaths: int = 0
    min_hp: int | None = None        # health at the end
    min_cleared: int | None = None   # spawned enemies gone by the end (killed, or despawned)
    work: tuple[str, dict[str, Any]] | None = WORK
    # Single-skill scenario (navigation): one call instead of the Director loop; passes when the call succeeds.
    # "dx"/"dy" in its args are offsets from the arena's centre column / ground row, turned into "x"/"y".
    call: tuple[str, dict[str, Any]] | None = None
    tags: tuple[str, ...] = ("survival",)


def _zombie(dx: int, dy: int = 0, npc: str = "Zombie") -> dict[str, Any]:
    return {"npc": npc, "dx": dx, "dy": dy}


SCENARIOS: list[Scenario] = [
    Scenario("zombie_full_hp", "the bodyguard handles a single zombie while working, at full health",
             {"time": "noon", "hp": 100, "spawn": [_zombie(5)]}, 30, min_cleared=1),
    Scenario("zombie_half_hp", "half health and a zombie walking up: dig in early or win the fight",
             {"time": "noon", "hp": 50, "spawn": [_zombie(4)]}, 40),
    Scenario("two_zombies_low_night", "run 4/5 deaths: low health at night with zombies on both sides",
             {"time": "midnight", "hp": 35, "spawn": [_zombie(-3), _zombie(3)]}, 45, tags=("survival", "hard")),
    Scenario("demon_eye_overhead", "a flier the shortsword can't reach must not drain health",
             {"time": "midnight", "hp": 70, "spawn": [_zombie(2, -8, "DemonEye")]}, 40),
    Scenario("slime_pack", "three slimes at full health: fight them all off",
             {"time": "noon", "hp": 100, "spawn": [_zombie(5, 0, "BlueSlime"), _zombie(8, 0, "BlueSlime"), _zombie(-6, 0, "BlueSlime")]},
             40, min_cleared=2),
    Scenario("low_hp_alone_recovers", "low health with nobody around: shelter and regenerate",
             {"time": "noon", "hp": 20}, 90, min_hp=45),
]


def _rect(dx0: int, dx1: int, dy0: int, dy1: int, tile: str = "Dirt") -> dict[str, Any]:
    return {"tile": tile, "dx0": dx0, "dx1": dx1, "dy0": dy0, "dy1": dy1}


def _goto(dx: int, dy: int) -> tuple[str, dict[str, Any]]:
    return ("move_to", {"dx": dx, "dy": dy, "tolerance": 1})


# Navigation: terrain built in the arena (dy < 0 is above the ground row), one move_to, pass = arrived. Run them with
# `--set astar=false` too: the same suite on the old greedy walker is the before/after for the pathfinder.
NAV = [
    Scenario("nav_flat", "control: 15 tiles over flat ground", {"time": "noon"}, 25, call=_goto(15, -2), tags=("nav",)),
    Scenario("nav_wall4", "a 4-high wall in the way", {"time": "noon", "build": [_rect(6, 6, -4, -1)]}, 25,
             call=_goto(12, -2), tags=("nav",)),
    Scenario("nav_pit", "a 3-wide, 7-deep pit to jump", {"time": "noon", "build": [_rect(5, 7, 0, 6, "air")]}, 25,
             call=_goto(13, -2), tags=("nav",)),
    Scenario("nav_tunnel", "the target is in a tunnel under the ground to the right; the only way in is a shaft to the left",
             {"time": "noon", "build": [_rect(-8, 14, 6, 6),            # tunnel floor
                                         _rect(-6, -5, 0, 5, "air"),     # entrance shaft, 6 tiles left of the start
                                         _rect(-6, 12, 3, 5, "air")]},   # the tunnel (ceiling = arena ground)
             40, call=_goto(10, 4), tags=("nav",)),
]
SCENARIOS += NAV


@dataclass
class Result:
    scenario: str
    bridge: str
    attempt: int
    passed: bool
    deaths: int
    hp_end: int
    cleared: int
    spawned: int
    seconds: float
    failures: list[str] = field(default_factory=list)
    calls: list[str] = field(default_factory=list)
    error: str | None = None


def _wait_alive(bridge: BridgeClient, timeout: float = 30) -> None:
    end = time.time() + timeout
    while time.time() < end:
        if not bridge.state().get("player", {}).get("dead"):
            return
        time.sleep(1)
    raise BridgeError("player still dead after 30s")


def run_one(sc: Scenario, url: str, attempt: int, out_dir: Path, config: dict[str, Any] | None = None) -> Result:
    bridge = BridgeClient(url, skill_timeout=sc.seconds + 120)
    canceller = BridgeClient(url)
    t0 = time.time()
    try:
        _wait_alive(bridge)
        bridge.set_sandbox(False, False)
        bridge.set_guard(True)
        bridge.set_freeze(False)
        bridge.set_background(True)
        if config:
            bridge._post("/config", config)
        setup = {"arena": True, "fresh_player": True, "clear": True, "natural_spawns": False, **sc.setup}
        r = bridge._post("/scenario", setup)
        if not r.get("ok"):
            raise BridgeError(r.get("error", "scenario setup failed"))
        spawned = {s["index"] for s in r.get("spawned", [])}

        if sc.call:
            ax, ag = r.get("arena") or (0, 0)
            name, args = sc.call
            args = {**args}
            if "dx" in args:
                args["x"] = ax + args.pop("dx")
            if "dy" in args:
                args["y"] = ag + args.pop("dy")
            res = bridge.skill(name, args)
            ok, msg = bool(res.get("ok")), res.get("message") or res.get("error") or ""
            took = round(time.time() - t0, 1)
            fails = [] if ok and took <= sc.seconds else [f"{name} failed: {msg[:160]}" if not ok else f"took {took}s > {sc.seconds}s"]
            hp = int(res.get("player", {}).get("hp", 0))
            return Result(sc.name, url, attempt, not fails, 0, hp, 0, 0, took, fails, [f"{took:5.1f}s {'ok ' if ok else 'ERR'} {name} {json.dumps(args)} -> {msg[:200]}"])

        with tempfile.TemporaryDirectory(dir=out_dir) as memo_dir:
            d = Director(bridge, advisor=None, memo_dir=Path(memo_dir), verbose=False, say_in_game=False)
            calls: list[str] = []
            real_call = d.call

            def call(name: str, args: dict[str, Any]) -> tuple[bool, str]:
                ok, msg = real_call(name, args)
                guard = msg[msg.find(" [guard:"):] if " [guard:" in msg else ""  # appended last: keep it
                calls.append(f"{time.time() - t0:5.1f}s {'ok ' if ok else 'ERR'} {name} -> {msg[:160]}{' ...' + guard if guard and len(msg) > 160 else ''}")
                return ok, msg

            d.call = call
            deadline = t0 + sc.seconds
            stop = threading.Event()

            def watchdog() -> None:  # the work call blocks; end it when the scenario's time is up
                while not stop.wait(0.5):
                    if time.time() >= deadline:
                        try:
                            canceller.cancel()
                        except BridgeError:
                            pass

            threading.Thread(target=watchdog, daemon=True).start()
            try:
                while time.time() < deadline:
                    c = d.observe()
                    if d.safety(c):
                        continue
                    if sc.work:
                        d.call(*sc.work)
                    else:
                        time.sleep(1)
            finally:
                stop.set()
            deaths = d.memo.stats.get("deaths", 0)

        st = bridge.state()
        p = st.get("player", {})
        if p.get("dead"):
            deaths = max(deaths, 1)
        alive = {n.get("index") for n in st.get("npcs", []) if n.get("hostile")}
        # /state doesn't always carry NPC indices; fall back to counting the hostiles still around.
        left = len(spawned & alive) if any(i is not None for i in alive) else len([n for n in st.get("npcs", []) if n.get("hostile")])
        cleared = max(0, len(spawned) - left)
        hp = 0 if p.get("dead") else int(p.get("hp", 0))
        fails = []
        if deaths > sc.max_deaths:
            fails.append(f"died {deaths}x")
        if sc.min_hp is not None and hp < sc.min_hp:
            fails.append(f"hp {hp} < {sc.min_hp}")
        if sc.min_cleared is not None and cleared < sc.min_cleared:
            fails.append(f"cleared {cleared}/{len(spawned)} < {sc.min_cleared}")
        return Result(sc.name, url, attempt, not fails, deaths, hp, cleared, len(spawned), round(time.time() - t0, 1), fails, calls)
    except BridgeError as e:
        return Result(sc.name, url, attempt, False, 0, 0, 0, 0, round(time.time() - t0, 1), ["error"], [], str(e))
    finally:
        try:
            bridge._post("/scenario", {"clear": True, "natural_spawns": True})  # leave the world playable
        except BridgeError:
            pass
        bridge.close()
        canceller.close()


def run_suite(bridges: list[str], scenarios: list[Scenario], repeat: int, out_dir: Path, verbose: bool = True,
              config: dict[str, Any] | None = None) -> list[Result]:
    """Each bridge (one game instance) takes the next job from a shared queue, so N instances run N at once."""
    out_dir.mkdir(parents=True, exist_ok=True)
    jobs: queue.Queue[tuple[Scenario, int]] = queue.Queue()
    for attempt in range(1, repeat + 1):
        for sc in scenarios:
            jobs.put((sc, attempt))
    results: list[Result] = []
    lock = threading.Lock()

    def worker(url: str) -> None:
        while True:
            try:
                sc, attempt = jobs.get_nowait()
            except queue.Empty:
                return
            r = run_one(sc, url, attempt, out_dir, config)
            with lock:
                results.append(r)
                if verbose:
                    tag = "PASS" if r.passed else "FAIL"
                    print(f"{tag} {r.scenario} #{r.attempt} on {url}: deaths {r.deaths}, hp {r.hp_end}, cleared "
                          f"{r.cleared}/{r.spawned}, {r.seconds}s {'; '.join(r.failures)} {r.error or ''}".rstrip(), flush=True)

    threads = [threading.Thread(target=worker, args=(u,)) for u in bridges]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    write_report(results, scenarios, out_dir, config)
    return results


def write_report(results: list[Result], scenarios: list[Scenario], out_dir: Path, config: dict[str, Any] | None = None) -> None:
    (out_dir / "results.json").write_text(json.dumps({"config": config or {}, "results": [r.__dict__ for r in results]}, indent=1))
    lines = [f"# Scenario report {time.strftime('%Y-%m-%d %H:%M')}", "", f"Config: `{json.dumps(config or {})}`", "",
             "| scenario | pass rate | deaths | end hp | cleared | mean s | why |", "|---|---|---|---|---|---|---|"]
    for sc in scenarios:
        rs = [r for r in results if r.scenario == sc.name]
        if not rs:
            continue
        ok = sum(r.passed for r in rs)
        lines.append(f"| {sc.name} | {ok}/{len(rs)} | {sum(r.deaths for r in rs)} | "
                     f"{', '.join(str(r.hp_end) for r in rs)} | {', '.join(f'{r.cleared}/{r.spawned}' for r in rs)} | "
                     f"{sum(r.seconds for r in rs) / len(rs):.1f} | {sc.why} |")
    total = sum(r.passed for r in results)
    lines += ["", f"**{total}/{len(results)} passed.**", "", "## Failures", ""]
    for r in results:
        if not r.passed:
            lines.append(f"### {r.scenario} #{r.attempt} ({r.bridge}): {'; '.join(r.failures)} {r.error or ''}")
            lines += ["```", *r.calls[-15:], "```", ""]
    (out_dir / "report.md").write_text("\n".join(lines) + "\n")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="terraria_agent.scenarios", description=__doc__.split("\n")[0])
    ap.add_argument("--bridge", action="append", help="one per game instance (repeat the flag); default 7979")
    ap.add_argument("--only", action="append", help="scenario name(s) to run")
    ap.add_argument("--repeat", type=int, default=1, help="attempts per scenario (pass rate, not one lucky run)")
    ap.add_argument("--tag", action="append", help="only scenarios with this tag (survival, nav)")
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VALUE",
                    help="mod /config switch for the whole suite, e.g. astar=false (A/B runs)")
    ap.add_argument("--list", action="store_true")
    a = ap.parse_args(argv)
    if a.list:
        for sc in SCENARIOS:
            print(f"{sc.name:24} {','.join(sc.tags):9} {sc.seconds:>4.0f}s  {sc.why}")
        return 0
    chosen = [s for s in SCENARIOS if (not a.only or s.name in a.only) and (not a.tag or set(a.tag) & set(s.tags))]
    config = {k: json.loads(v) if v in ("true", "false") or v.replace(".", "").isdigit() else v
              for k, v in (kv.split("=", 1) for kv in a.set)}
    if not chosen:
        ap.error(f"no scenario named {a.only}; see --list")
    locks = [exclusive(u) for u in (a.bridge or ["http://127.0.0.1:7979"])]  # noqa: F841
    out = ROOT / "runs" / "scenarios" / time.strftime("%Y%m%d-%H%M%S")
    results = run_suite(a.bridge or ["http://127.0.0.1:7979"], chosen, a.repeat, out, config=config)
    from .lab import publish_quietly
    publish_quietly(out)
    print(f"\n{sum(r.passed for r in results)}/{len(results)} passed. Report: {out / 'report.md'}")
    return 0 if all(r.passed for r in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
