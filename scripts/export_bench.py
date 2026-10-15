#!/usr/bin/env python3
"""Rebuild the results page's datasets from the run logs, so every number on it traces back to a file here.

    python3 scripts/export_bench.py --since 20261015-1459     # writes docs/bench/llm_ab.json

llm_ab.json: the primitive-mode LLM-track runs at or after --since (the sim/llm_ab.sh batch), numbered in order:
the n-th fresh run and the n-th learned run are one repeat.
"""

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RUNS = ROOT / "agent" / "runs"
OUT = ROOT / "docs" / "bench"


def llm_ab(since: str) -> list[dict]:
    rows, reps = [], {"fresh": 0, "learned": 0}
    for d in sorted((RUNS / "llm").glob("*-primitive-*")):
        f = d / "results.json"
        if not f.exists():
            continue
        if d.name[:13] < since:
            continue
        j = json.loads(f.read_text())
        meta = j.get("meta", {})
        p = meta.get("library")
        reps[p] += 1
        for r in j["results"]:
            rows.append({"key": f"{p} #{reps[p]} {r['task']}", "pass": p, "rep": reps[p], "task": r["task"], "passed": r["passed"],
                         "attempts": r["attempts"], "llm_calls": r["llm_calls"], "llm_seconds": r["llm_seconds"],
                         "game_seconds": r["game_seconds"], "used_library": ", ".join(r["used_library"]),
                         "saved_as": r["saved_as"] or "", "report": str(d.relative_to(ROOT) / "report.md")})
    return rows


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--since", required=True, help="first run directory stamp to include, e.g. 20261015-1459")
    rows = llm_ab(ap.parse_args().since)
    (OUT / "llm_ab.json").write_text(json.dumps(rows, indent=1))
    print(f"llm_ab.json: {len(rows)} task attempts")
