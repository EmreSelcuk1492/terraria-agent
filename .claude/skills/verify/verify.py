#!/usr/bin/env python3
"""Verification CLI for terraria-agent: the one way agents gather evidence, instead of ad-hoc scripts.

    python3 .claude/skills/verify/verify.py            = check
    python3 .claude/skills/verify/verify.py check      garden lint + shell syntax + Python compile + JSON + feature map + offline tests
    python3 .claude/skills/verify/verify.py smoke      the Director's whole 19-step plan against the mock world (~10 s, no game, never publishes)
    python3 .claude/skills/verify/verify.py status     read-only: running sim containers and queues (check before touching agent/ or sim/)
    python3 .claude/skills/verify/verify.py ab DIR [--floor-steps S --floor-deaths D]
                                                       score a Director A/B (or A/A) night with sim/ab_report.sh and judge it
    python3 .claude/skills/verify/verify.py map [--check]   regenerate FEATURE_MAP.md from the tree (--check: fail if stale)

Nothing here starts a world, publishes to the lab, or writes outside .claude/skills/verify/ and pytest's cache.
Exit 1 when a check fails.
"""

from __future__ import annotations

import argparse
import ast
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
AGENT = ROOT / "agent"
MAP = HERE / "FEATURE_MAP.md"


def python() -> str:
    venv = AGENT / ".venv" / "bin" / "python"
    return str(venv) if venv.exists() else sys.executable


def tracked() -> list[str]:
    out = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    return sorted(p for p in out.split("\0") if p and (ROOT / p).is_file())


def step(name: str, ok: bool, detail: str = "") -> bool:
    print(f"{'ok  ' if ok else 'FAIL'} {name}" + (f"\n{detail.rstrip()}" if detail and not ok else ""), flush=True)
    return ok


def run(cmd: list[str], cwd: Path = ROOT, env: dict | None = None) -> tuple[bool, str]:
    p = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, env={**os.environ, **(env or {})})
    return p.returncode == 0, p.stdout + p.stderr


# -- check ----------------------------------------------------------------------------------------------------------

def check(args: argparse.Namespace) -> int:
    files = tracked()
    results = [step("garden lint", *run([sys.executable, str(ROOT / ".claude/skills/garden/lint.py")]))]

    bad = [f"{f}: {o.strip()}" for f in files if f.endswith(".sh") and not f.startswith("third_party/")
           for ok, o in [run(["bash", "-n", f])] if not ok]
    results.append(step("shell syntax (bash -n)", not bad, "\n".join(bad)))

    errs = []
    for f in files:
        if f.endswith(".py") and not f.startswith("third_party/"):
            try:
                compile((ROOT / f).read_text(), f, "exec")
            except SyntaxError as e:
                errs.append(f"{f}:{e.lineno} {e.msg}")
    results.append(step("python compiles", not errs, "\n".join(errs)))

    errs = []
    for f in files:
        if f.endswith(".json") and not f.startswith("third_party/"):
            try:
                json.loads((ROOT / f).read_text())
            except (json.JSONDecodeError, UnicodeDecodeError) as e:
                errs.append(f"{f}: {e}")
    results.append(step("json parses", not errs, "\n".join(errs)))

    fresh = render_map()
    results.append(step("feature map current", MAP.exists() and MAP.read_text() == fresh,
                        "FEATURE_MAP.md is stale: run `python3 .claude/skills/verify/verify.py map`"))

    if not args.no_tests:
        results.append(step("offline tests (pytest, mock world)", *run([python(), "-m", "pytest", "-q"], cwd=AGENT, env={"TA_TELEMETRY": "0"})))
    print("verify: all checks pass" if all(results) else "verify: FAILED")
    return 0 if all(results) else 1


# -- smoke ----------------------------------------------------------------------------------------------------------

def smoke(_: argparse.Namespace) -> int:
    """The cheapest end-to-end run there is: no container, no model. Goes through pytest so the no-publish guard holds
    (`python -m terraria_agent.director` would publish to the public lab: that path is for real runs only)."""
    ok, out = run([python(), "-m", "pytest", "-q", "tests/test_director.py::test_starter_plan_completes"], cwd=AGENT, env={"TA_TELEMETRY": "0"})
    step("smoke: Director 19-step plan on mock_bridge", ok, out)
    if ok:
        print((out.strip().splitlines() or [""])[-1])
    return 0 if ok else 1


# -- status ---------------------------------------------------------------------------------------------------------

def status(_: argparse.Namespace) -> int:
    busy = False
    if shutil.which("docker"):
        ok, out = run(["docker", "ps", "--filter", "name=tsim-", "--format", "{{.Names}}\t{{.Status}}"])
        lines = [l for l in out.splitlines() if l.strip()] if ok else []
        print("sim containers: " + (", ".join(lines) if lines else "none" if ok else "docker not answering"))
        busy |= bool(lines)
    ok, out = run(["pgrep", "-fl", r"sim/(night|day)[^ ]*\.sh|sim/scenarios\.sh|sim/llm_ab\.sh|terraria_agent\.(director|scenarios|llm_bench)"])
    procs = [l for l in out.splitlines() if l.strip() and "verify.py" not in l]
    print("queues/runners: " + ("\n  " + "\n  ".join(procs) if procs else "none"))
    busy |= bool(procs)
    cache = Path.home() / "Library" / "Caches" / "terraria-sim"
    logs = sorted(cache.glob("*/queue.log"), key=lambda p: p.stat().st_mtime) if cache.exists() else []
    if logs:
        tail = logs[-1].read_text().splitlines()[-3:]
        print(f"latest queue log ~/{logs[-1].relative_to(Path.home())}:\n  " + "\n  ".join(tail))
    if busy:
        print("\nBUSY: a run is live. Don't edit agent/terraria_agent, the queued sim/*.sh or mods-* builds, don't start worlds,"
              " and don't docker rm tsim-* (the 'new' arm runs this checkout's agent/; a mid-night swap voids runs).")
    else:
        print("\nidle: no sim running")
    return 0


# -- ab -------------------------------------------------------------------------------------------------------------

def ab(args: argparse.Namespace) -> int:
    ok, out = run(["bash", str(ROOT / "sim/ab_report.sh"), str(Path(args.dir).expanduser())])
    print(out.rstrip())
    arms = {}
    for name, block in re.findall(r"== (\w+)\n(.*?)(?=\n== |\Z)", out, re.S):
        if m := re.search(r"finished (\d+): avg steps ([\d.]+), avg deaths ([\d.]+)", block):
            arms[name] = tuple(float(x) for x in m.groups())
    if not {"old", "new"} <= set(arms):
        print("\nverdict: incomplete (an arm has no finished run yet)")
        return 1
    (n_old, s_old, d_old), (n_new, s_new, d_new) = arms["old"], arms["new"]
    ds, dd = s_new - s_old, d_new - d_old
    print(f"\nnew - old: steps {ds:+.1f}, deaths {dd:+.1f}  (finished runs: old {n_old:.0f}, new {n_new:.0f})")
    if args.floor_steps is None or args.floor_deaths is None:
        print("verdict: no noise floor given. If this was an A/A night, these deltas ARE the floor; record them in"
              " .claude/skills/verify/SKILL.md (Experiment protocol) and pass --floor-steps/--floor-deaths next time.")
        return 0
    if min(n_old, n_new) < args.min_runs:
        print(f"verdict: too few finished runs (< {args.min_runs} per arm): inconclusive, keep the baseline")
    elif ds > args.floor_steps and dd <= args.floor_deaths:
        print("verdict: new beats the baseline beyond the A/A noise floor: it can become the new baseline")
    elif ds < -args.floor_steps or dd > args.floor_deaths:
        print("verdict: new loses beyond the noise floor: revert it, keep the baseline, put the numbers in the revert message")
    else:
        print("verdict: within the noise floor: no evidence either way, keep the baseline")
    return 0


# -- feature map ----------------------------------------------------------------------------------------------------

# Curated: what each component does, how to run it, which files own it. render_map() fails if an owning file goes missing.
COMPONENTS = [
    ("Bridge mod (C#, tModLoader)", "HTTP JSON on 127.0.0.1:7979; runs skills by setting player controls each tick",
     "`scripts/build-mod.sh` (Mac game) · `scripts/build-sim-mod.sh <out.tmod>` (sim)",
     ["bridge/TerrariaAgentBridge/BridgeServer.cs", "bridge/TerrariaAgentBridge/SkillFactory.cs", "bridge/TerrariaAgentBridge/Skills/"]),
    ("Agent loop (Claude tool use)", "observe -> model picks a skill -> bridge runs it -> result back; lessons.md memory",
     "`cd agent && .venv/bin/python -m terraria_agent --goal \"...\"`",
     ["agent/terraria_agent/agent.py", "agent/terraria_agent/tools.py", "agent/terraria_agent/backends.py", "agent/terraria_agent/prompts.py"]),
    ("Director (hands-off early game)", "19 checked steps + always-win rules; local advisor only on failure",
     "`cd agent && .venv/bin/python -m terraria_agent.director --bridge URL --advisor none --start-at dawn --hours 1` (publishes to the lab)",
     ["agent/terraria_agent/director.py", "agent/tests/test_director.py"]),
    ("Scenario suites (survival, nav, station)", "short repeatable situations, pass/fail, JSON + markdown report",
     "`sim/scenarios.sh N --repeat 3 --tag survival` (1x) · `--list` for names",
     ["agent/terraria_agent/scenarios.py", "bridge/TerrariaAgentBridge/Scenario.cs", "sim/scenarios.sh"]),
    ("LLM track + skill library", "gpt-oss:20b writes skill programs, the game verifies, a library keeps coordinate-free winners",
     "`sim/llm_ab.sh primitive --headless --speed 4`",
     ["agent/terraria_agent/llm_bench.py", "agent/terraria_agent/skill_library.py", "agent/skills/", "sim/llm_ab.sh"]),
    ("Sim (real game in Docker)", "headless tModLoader per container, pinned bench world, <=3 worlds on 8 GB",
     "`sim/setup.sh <gog installer>` once · `sim/run.sh NAME PORT \"Test 1\" \"The Burning Dam of Taxes\" --fresh`",
     ["sim/run.sh", "sim/Dockerfile", "sim/entrypoint.sh", "sim/README.md"]),
    ("Overnight queues + A/B scoring", "nightN.sh = one night's experiment; ab_report.sh scores old vs new arms",
     "`nohup caffeinate -i -s sim/nightN.sh > .../queue.out &` · `verify.py ab DIR`",
     ["sim/ab_report.sh", "docs/next-session.md"]),
    ("Public lab (lab.emrecanselcuk.com)", "Supabase lab_* rows; every write via Lab.upsert -> scrub(); clips in a public bucket",
     "runners publish on finish · `python -m terraria_agent.lab notes|backfill|scrub` (ask first: public)",
     ["agent/terraria_agent/lab.py", "agent/terraria_agent/clips.py", "docs/lab/findings.json", "docs/lab/method.md", "agent/tests/test_lab.py"]),
    ("Bench page datasets", "docs/bench page + JSON rebuilt from run logs", "`python3 scripts/export_bench.py --since STAMP`",
     ["scripts/export_bench.py", "docs/bench/index.html"]),
    ("Mock world", "fake flat world on the same protocol: tests and smoke runs, no game", "`cd agent && .venv/bin/python mock_bridge.py`",
     ["agent/mock_bridge.py"]),
    ("Telemetry (PostHog)", "traces/generations/spans for every model + skill call; off under pytest or TA_TELEMETRY=0", "automatic",
     ["agent/terraria_agent/telemetry.py"]),
    ("Mac unattended play", "wait for unlock, launch into the world, start the Director; quit/rebuild/relaunch",
     "`scripts/autoplay.sh HOURS PLAYER WORLD` · `scripts/cycle.sh`", ["scripts/autoplay.sh", "scripts/cycle.sh"]),
]


def _summary_py(path: Path) -> str:
    try:
        doc = ast.get_docstring(ast.parse(path.read_text())) or ""
    except SyntaxError:
        return ""
    lines = [l.strip() for l in doc.splitlines() if l.strip() and not l.strip().startswith(("python", ">", "-", "$"))]
    return lines[0] if lines else ""


def _summary_sh(path: Path) -> str:
    for line in path.read_text().splitlines()[1:6]:
        if line.startswith("#") and line.strip("# ").strip():
            return line.lstrip("# ").strip()
    return ""


def render_map() -> str:
    files = tracked()
    missing = [o for *_, owns in COMPONENTS for o in owns if not (ROOT / o).exists()]
    out = ["# Feature map", "",
           "Generated by `python3 .claude/skills/verify/verify.py map` from the tree (CI fails when it is stale).",
           "Edit COMPONENTS in verify.py for the curated table; the inventories below come from docstrings and header comments.", "",
           "## Components", "", "| Component | What it does | How to run | Owning files |", "|---|---|---|---|"]
    for name, what, how, owns in COMPONENTS:
        out.append(f"| {name} | {what} | {how} | {', '.join(f'`{o}`' for o in owns)} |")
    if missing:
        out += ["", "**Missing owning files (fix COMPONENTS):** " + ", ".join(missing)]

    out += ["", "## Python modules (`agent/terraria_agent/`)", "", "| Module | Entry point | Summary |", "|---|---|---|"]
    for f in files:
        if f.startswith("agent/terraria_agent/") and f.endswith(".py"):
            p = ROOT / f
            mod = Path(f).stem
            entry = ("`python -m terraria_agent`" if mod == "__main__" else
                     f"`python -m terraria_agent.{mod}`" if "__name__ == \"__main__\"" in p.read_text() else "")
            out.append(f"| `{mod}` | {entry} | {_summary_py(p)} |")

    out += ["", "## Shell + scripts", "", "| File | Summary |", "|---|---|"]
    for f in files:
        if (f.startswith("sim/") and f.endswith(".sh")) or f.startswith("scripts/"):
            p = ROOT / f
            out.append(f"| `{f}` | {_summary_py(p) if f.endswith('.py') else _summary_sh(p)} |")

    skills = re.findall(r'^\s*"([a-z_]+)" =>', (ROOT / "bridge/TerrariaAgentBridge/SkillFactory.cs").read_text(), re.M)
    out += ["", "## Bridge skills (`SkillFactory.cs`)", "", ", ".join(f"`{s}`" for s in skills), "",
            "C# sources: " + ", ".join(f"`{Path(f).name}`" for f in files if f.startswith("bridge/") and f.endswith(".cs"))]

    out += ["", "## Tests (`agent/tests/`, offline, mock world)", ""]
    for f in files:
        if f.startswith("agent/tests/test_") and f.endswith(".py"):
            n = len(re.findall(r"^def test_", (ROOT / f).read_text(), re.M))
            out.append(f"- `{Path(f).name}` ({n} tests): {_summary_py(ROOT / f)}")

    out += ["", "## What the lab publishes (public)", "",
            "- `docs/lab/findings.json` and `docs/lab/*.md` (via `lab.notes()`), run rows from `agent/runs/` (gitignored), clips from"
            " `~/Library/Caches/terraria-sim/recordings`.", "- Not published: everything else in the repo (private).", ""]
    return "\n".join(out)


def feature_map(args: argparse.Namespace) -> int:
    fresh = render_map()
    if args.check:
        return 0 if step("feature map current", MAP.exists() and MAP.read_text() == fresh, "stale: run verify.py map") else 1
    MAP.write_text(fresh)
    print(f"wrote {MAP.relative_to(ROOT)}")
    return 0


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(prog="verify.py", description=__doc__.split("\n")[0])
    sub = ap.add_subparsers(dest="cmd")
    c = sub.add_parser("check")
    c.add_argument("--no-tests", action="store_true")
    sub.add_parser("smoke")
    sub.add_parser("status")
    a = sub.add_parser("ab")
    a.add_argument("dir")
    a.add_argument("--floor-steps", type=float)
    a.add_argument("--floor-deaths", type=float)
    a.add_argument("--min-runs", type=int, default=5)
    m = sub.add_parser("map")
    m.add_argument("--check", action="store_true")
    args = ap.parse_args(argv)
    if not args.cmd:
        args = ap.parse_args(["check", *argv])
    return {"check": check, "smoke": smoke, "status": status, "ab": ab, "map": feature_map}[args.cmd](args)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
