#!/usr/bin/env python3
"""Repo lint for /garden: every rule is a correction promoted to rung 2 (see corrections.md).

    python3 .claude/skills/garden/lint.py            lint every tracked file in every rule's scope
    python3 .claude/skills/garden/lint.py <file>...  lint just these files (forbid/require rules only)

Stdlib only, no game or network needed, so CI and agents run the same thing. Exit 1 on any finding.
"""

from __future__ import annotations

import fnmatch
import json
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
CFG = json.loads((HERE / "rules.json").read_text())


def tracked() -> list[str]:
    out = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    return [p for p in out.split("\0") if p and (ROOT / p).is_file()]


def match(path: str, globs: list[str]) -> bool:
    return any(fnmatch.fnmatch(path, g) for g in globs)


def text(path: str) -> str | None:
    try:
        return (ROOT / path).read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        return None  # binary


def lint_rules(files: list[str]) -> list[str]:
    out = []
    for f in files:
        if match(f, CFG["ignore"]) or (t := text(f)) is None:
            continue
        for r in CFG["forbid"]:
            if not match(f, r["scope"]) or match(f, r.get("exclude", [])):
                continue
            rx = re.compile(r["pattern"])
            for i, line in enumerate(t.splitlines(), 1):
                if rx.search(line):
                    out.append(f"{f}:{i} [{r['id']}] {r['message']}\n    {line.strip()[:160]}")
        for r in CFG["require"]:
            if match(f, r["scope"]) and re.search(r["when"], t) and not re.search(r["pattern"], t):
                out.append(f"{f} [{r['id']}] {r['message']}")
    return out


def lint_findings() -> list[str]:
    """docs/lab/findings.json is published as-is by lab.notes(): keep its shape and its commit refs real."""
    out, f = [], "docs/lab/findings.json"
    try:
        rows = json.loads((ROOT / f).read_text())
    except (OSError, json.JSONDecodeError) as e:
        return [f"{f} [findings-schema] unreadable: {e}"]
    need = {"id", "area", "found_by", "finding", "fix", "effect", "commit", "at"}
    ids = [x.get("id") for x in rows]
    if len(ids) != len(set(ids)):
        out.append(f"{f} [findings-schema] duplicate ids")
    for x in rows:
        if missing := need - set(x):
            out.append(f"{f} [findings-schema] finding {x.get('id')} lacks {sorted(missing)}")
        c = str(x.get("commit") or "").split()[0] if x.get("commit") else ""
        ok = bool(c) and subprocess.run(["git", "cat-file", "-e", f"{c}^{{commit}}"], cwd=ROOT, capture_output=True).returncode == 0
        if not ok:
            out.append(f"{f} [findings-commit] finding {x.get('id')} cites {c or 'no commit'}, which is not in this repo's history")
    return out


def lint_skill_parity() -> list[str]:
    """SkillFactory.cs says 'Keep in sync with tools.py': a skill the bridge runs but Claude can't call (or the reverse) is drift."""
    cs = set(re.findall(r'^\s*"([a-z_]+)" =>', (ROOT / "bridge/TerrariaAgentBridge/SkillFactory.cs").read_text(), re.M))
    py = set(re.findall(r'_tool\(\s*"([a-z_]+)"', (ROOT / "agent/terraria_agent/tools.py").read_text()))
    allow = CFG["allow"]["skill-parity"]
    out = [f"bridge/TerrariaAgentBridge/SkillFactory.cs [skill-parity] '{n}' has no tool in agent/terraria_agent/tools.py (or allowlist it in rules.json with a reason)"
           for n in sorted(cs - py - set(allow["bridge_only"]))]
    out += [f"agent/terraria_agent/tools.py [skill-parity] tool '{n}' is not a bridge skill" for n in sorted(py - cs - set(allow["agent_only"]))]
    out += [f".claude/skills/garden/rules.json [skill-parity] allowlisted '{n}' is fixed now: drop it from bridge_only"
            for n in sorted(set(allow["bridge_only"]) & py)]
    return out


def main(argv: list[str]) -> int:
    if argv:
        findings = lint_rules([str(Path(a).resolve().relative_to(ROOT)) for a in argv])
    else:
        findings = lint_rules(tracked()) + lint_findings() + lint_skill_parity()
    if findings:
        print("\n".join(findings))
        print(f"\n{len(findings)} finding(s)")
        return 1
    print("garden: clean")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
