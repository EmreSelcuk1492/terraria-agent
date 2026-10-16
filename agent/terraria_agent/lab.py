"""Publish runs to lab.emrecanselcuk.com (Supabase lab_* tables), so the public page updates itself.

Every runner calls `publish_quietly(...)` when it finishes; a missing env file or a network error never fails a run.
Secrets live in ~/.config/lab/env (LAB_SUPABASE_URL, LAB_SUPABASE_SERVICE_ROLE_KEY), never in this repo.

    python -m terraria_agent.lab backfill      # every run under agent/runs + notes from docs/lab
    python -m terraria_agent.lab notes         # findings + method notes only
    python -m terraria_agent.lab run <dir>     # one scenarios/<stamp> or llm/<stamp> directory, or a journal file
"""

from __future__ import annotations

import json
import re
import sys
from datetime import datetime
from pathlib import Path
from typing import Any

import httpx

ROOT = Path(__file__).resolve().parent.parent          # agent/
REPO = ROOT.parent
RUNS = ROOT / "runs"
NOTES = REPO / "docs" / "lab"
PROJECT = "terraria"
TZ = "-07:00"  # runs are stamped in local time (Seattle); the site shows times as written

# Runs made on a harness later found broken: published, but flagged so charts leave them out and say why.
INVALID = {
    "llm:20261015-143917": "planner returned empty programs (fixed: programs need at least one step)",
    "llm:20261015-144106": "two benchmark processes drove one game (fixed: per-instance lock)",
    "llm:20261015-144120": "two benchmark processes drove one game (fixed: per-instance lock)",
    "llm:20261015-144634": "the check wanted the item named Wood while the model held BorealWood (fixed: wood variants count)",
    "llm:20261015-145058": "tasks shared one cut-down forest and one start sat on a floating island",
    "llm:20261015-150008": "start finder put tasks underground (fixed the same afternoon)",
    "survival:20261015-140428": "spawn location and the Mac character (before the arena and fresh_player)",
    "nav:20261015-142819": "the detour course was unsolvable (replaced by the tunnel course)",
    "nav:20261015-142919": "the detour course was unsolvable (replaced by the tunnel course)",
    "llm:20261015-181033": "incomplete: stopped before copper_bars when the host ran low on memory (6/7 run, wood to door all first-attempt)",
    "llm:20261015-174937": "tasks shared forests (patches 120 tiles apart, tree search 180 each way): the fifth task found none; stopped and re-placed",
    "llm:20261015-172952": "the game container was killed mid-run (Docker ran out of memory with four worlds starting at once)",
    "survival:20261015-174409": "one of three game instances crashed and its worker failed 27 jobs as unreachable (fixed: jobs go back to the queue)",
}


def _harness_problem(res: list[dict]) -> str | None:
    """Results the harness lost (instance down, setup failed) say nothing about the agent: flag the run."""
    errors = sum(1 for r in res if r.get("error"))
    return f"harness errors in {errors}/{len(res)} results (not agent failures)" if res and errors > 0.2 * len(res) else None


def _env() -> dict[str, str] | None:
    f = Path.home() / ".config" / "lab" / "env"
    if not f.exists():
        return None
    out = {}
    for line in f.read_text().splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            k, v = line.split("=", 1)
            out[k.strip().removeprefix("export ")] = v.strip().strip("'\"")
    return out


class Lab:
    def __init__(self) -> None:
        env = _env()
        if not env:
            raise RuntimeError("no ~/.config/lab/env")
        key = env["LAB_SUPABASE_SERVICE_ROLE_KEY"]
        self.http = httpx.Client(base_url=env["LAB_SUPABASE_URL"] + "/rest/v1", timeout=30,
                                 headers={"apikey": key, "Authorization": f"Bearer {key}",
                                          "Prefer": "resolution=merge-duplicates,return=minimal"})

    def upsert(self, table: str, rows: list[dict[str, Any]], on: str) -> None:
        if not rows:
            return
        r = self.http.post(f"/{table}", params={"on_conflict": on}, json=rows)
        if r.status_code >= 300:
            raise RuntimeError(f"{table}: {r.status_code} {r.text[:300]}")

    def delete_results(self, run_id: str) -> None:
        self.http.delete("/lab_results", params={"run_id": f"eq.{run_id}"})


def _stamp_time(stamp: str) -> str:
    return datetime.strptime(stamp[:15], "%Y%m%d-%H%M%S").strftime(f"%Y-%m-%dT%H:%M:%S{TZ}")


def project_row() -> dict[str, Any]:
    return {"slug": PROJECT, "title": "Terraria Agent Bench", "status": "active", "started": "2026-10-13", "sort": 10,
            "tagline": "A local open model plays real Terraria through a bridge mod; a benchmark checks every claim in the live game.",
            "meta": {"stack": ["tModLoader (C#)", "Python", "gpt-oss:20b via Ollama", "Docker + Xvfb", "Supabase"],
                     "credits": [{"name": "TerraBlind", "by": "Reisenbug", "license": "MIT", "url": "https://github.com/Reisenbug/TerraBlind"}],
                     "repo": "private for now"}}


# -- run readers ---------------------------------------------------------------------------------------------------

def scenario_run(d: Path) -> tuple[dict, list[dict]]:
    j = json.loads((d / "results.json").read_text())
    res = j["results"] if isinstance(j, dict) else j
    config = j.get("config", {}) if isinstance(j, dict) else {}
    kind = "nav" if res and all(r["scenario"].startswith("nav_") for r in res) else "survival"
    run_id = f"{PROJECT}:{kind}:{d.name}"
    counts: dict[str, int] = {}
    results = []
    for r in res:
        counts[r["scenario"]] = counts.get(r["scenario"], 0) + 1
        results.append({"run_id": run_id, "item": r["scenario"], "attempt": r.get("attempt", counts[r["scenario"]]), "passed": r["passed"],
                        "metrics": {"deaths": r["deaths"], "hp_end": r["hp_end"], "cleared": r["cleared"], "spawned": r["spawned"],
                                    "seconds": r["seconds"], "failures": r.get("failures", [])}})
    passed = sum(r["passed"] for r in res)
    label = ("A* off (greedy)" if config.get("astar") is False else "A* on") if kind == "nav" else "survival suite"
    run = {"id": run_id, "project": PROJECT, "kind": kind, "label": label, "started_at": _stamp_time(d.name), "config": config,
           "summary": {"passed": passed, "attempts": len(res), "deaths": sum(r["deaths"] for r in res), "items": len(counts),
                       **({"invalid": why} if (why := INVALID.get(f"{kind}:{d.name}") or _harness_problem(res)) else {})},
           "report_md": (d / "report.md").read_text() if (d / "report.md").exists() else "", "host": "sim"}
    return run, results


def llm_run(d: Path) -> tuple[dict, list[dict]] | None:
    if not (d / "results.json").exists():
        return None
    j = json.loads((d / "results.json").read_text())
    meta, res = j.get("meta", {}), j["results"]
    stamp = d.name[:15]
    run_id = f"{PROJECT}:llm:{stamp}"
    results = [{"run_id": run_id, "item": r["task"], "attempt": 1, "passed": r["passed"],
                "metrics": {k: r[k] for k in ("attempts", "llm_calls", "llm_seconds", "game_seconds", "skill_calls", "used_library", "saved_as")}}
               for r in res]
    host = "mac" if meta.get("bridge", "http://127.0.0.1:7979").endswith(":7979") else "sim"  # 7979 is the Mac game
    run = {"id": run_id, "project": PROJECT, "kind": "llm", "label": f"{meta.get('mode', '')} · library {meta.get('library', '')}",
           "started_at": _stamp_time(stamp), "config": meta, "host": host,
           "summary": {"passed": sum(r["passed"] for r in res), "attempts": len(res), "pass": meta.get("library"),
                       "llm_seconds": round(sum(r["llm_seconds"] for r in res), 1), "llm_calls": sum(r["llm_calls"] for r in res),
                       "saved": [r["saved_as"] for r in res if r.get("saved_as")],
                       **({"invalid": INVALID[f"llm:{stamp}"]} if f"llm:{stamp}" in INVALID else {})},
           "report_md": (d / "report.md").read_text() if (d / "report.md").exists() else ""}
    return run, results


def director_run(journal: Path, name: str, host: str) -> dict:
    t = journal.read_text()
    stamp = re.search(r"journal-(\d{8}-\d{6})", journal.name).group(1)
    stats = json.loads(m.group(1)) if (m := re.search(r"stats: (\{.*\})", t)) else {}
    track = "build" if "Track: build" in t else "survival"
    return {"id": f"{PROJECT}:director:{stamp}", "project": PROJECT, "kind": "director", "label": f"{name} · {track} track",
            "started_at": _stamp_time(stamp), "host": host, "config": {"track": track},
            "summary": {"name": name, "track": track, "steps_done": int(re.search(r"Plan steps complete: (\d+)/", t).group(1)) if "Plan steps complete" in t else 0,
                        "steps_total": 19, "deaths": stats.get("deaths", 0), "kills": stats.get("enemies_killed", 0),
                        "minutes": int(m.group(1)) if (m := re.search(r"Summary after (\d+) minutes", t)) else 0,
                        **({"note": "deaths counted once per poll (fixed before run 3)"} if name == "Run 1" else {})},
            "report_md": t}


DIRECTOR = [("run1", "Run 1", "sim"), ("run2", "Run 2", "sim"), ("run3", "Run 3", "sim"), ("run4", "Run 4", "sim"),
            ("run5", "Run 5", "sim"), ("run6", "Run 6", "sim"), ("mac", "Mac run", "mac")]


# -- notes --------------------------------------------------------------------------------------------------------

def notes() -> list[dict]:
    rows = []
    f = NOTES / "findings.json"
    if f.exists():
        for x in json.loads(f.read_text()):
            rows.append({"project": PROJECT, "slug": f"finding-{x['id']:02d}", "kind": "finding", "title": x["finding"], "sort": x["id"],
                         "body_md": "", "happened_at": x.get("at"),
                         "data": {k: x.get(k) for k in ("area", "found_by", "fix", "effect", "commit")}})
    for i, md in enumerate(sorted(NOTES.glob("*.md"))):
        text = md.read_text()
        title = text.splitlines()[0].lstrip("# ").strip() if text else md.stem
        rows.append({"project": PROJECT, "slug": md.stem, "kind": "method" if md.stem.startswith("method") else "update",
                     "title": title, "body_md": "\n".join(text.splitlines()[1:]).strip(), "sort": i, "data": {}, "happened_at": None})
    return rows


# -- publishing ---------------------------------------------------------------------------------------------------

def publish(runs: list[tuple[dict, list[dict]]], with_notes: bool = False) -> int:
    lab = Lab()
    lab.upsert("lab_projects", [project_row()], "slug")
    for run, results in runs:
        lab.upsert("lab_runs", [run], "id")
        lab.delete_results(run["id"])
        lab.upsert("lab_results", results, "run_id,item,attempt")
    if with_notes:
        lab.upsert("lab_notes", notes(), "project,slug")
    return len(runs)


def publish_quietly(path: Path | str) -> None:
    """For runners: publish one finished run; never raise."""
    try:
        p = Path(path)
        item = scenario_run(p) if p.parent.name == "scenarios" else llm_run(p) if p.parent.name == "llm" else None
        if item:
            publish([item])
            print(f"published to lab: {item[0]['id']}", flush=True)
    except Exception as e:  # noqa: BLE001  a run must never fail on publishing
        print(f"(lab publish skipped: {e})", flush=True)


def publish_director_quietly(journal: Path, name: str, host: str) -> None:
    try:
        publish([(director_run(journal, name, host), [])])
    except Exception as e:  # noqa: BLE001
        print(f"(lab publish skipped: {e})", flush=True)


def backfill() -> int:
    items: list[tuple[dict, list[dict]]] = []
    for sub, name, host in DIRECTOR:
        d = RUNS if sub == "run6" else RUNS / sub
        for j in sorted(d.glob("journal-*.md")):
            if sub == "run6" and not j.name.startswith("journal-20261015-1343"):
                continue
            items.append((director_run(j, name, host), []))
    for d in sorted((RUNS / "scenarios").glob("*/")):
        if (d / "results.json").exists():
            items.append(scenario_run(d))
    for d in sorted((RUNS / "llm").glob("*/")):
        if (it := llm_run(d)):
            items.append(it)
    return publish(items, with_notes=True)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "backfill"
    if cmd == "backfill":
        print(f"published {backfill()} runs + notes")
    elif cmd == "notes":
        Lab().upsert("lab_notes", notes(), "project,slug")
        print("notes published")
    elif cmd == "run":
        publish_quietly(sys.argv[2])
