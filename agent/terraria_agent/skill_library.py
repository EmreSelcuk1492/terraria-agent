"""Skill library: programs the planner wrote that passed a check in the real game, kept for reuse.

A skill is a named program (a list of bridge skill calls, possibly calling other library skills) plus the check it
was verified against. The planner sees the closest few for each new task and can call one as a single step
("lib:make_workbench"), so tasks get cheaper as the library grows (Voyager's skill library, over this bridge).
Retrieval is keyword overlap: small, deterministic and inspectable; good enough for a library of tens of skills.
"""

from __future__ import annotations

import json
import re
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

_STOP = {"a", "an", "the", "of", "to", "and", "or", "in", "on", "at", "for", "with", "get", "have", "make", "some", "least"}


def tokens(text: str) -> set[str]:
    # Split CamelCase item names too: "CopperBar" -> copper, bar.
    text = re.sub(r"([a-z])([A-Z])", r"\1 \2", text)
    return {t for t in re.findall(r"[a-z0-9]+", text.lower()) if t not in _STOP}


@dataclass
class LibSkill:
    name: str
    description: str
    task: str
    program: list[dict[str, Any]]
    check: dict[str, Any]
    created: float = field(default_factory=time.time)
    uses: int = 0
    successes: int = 0

    def summary(self) -> str:
        steps = "; ".join(f"{s['skill']} {json.dumps(s.get('args', {}), separators=(',', ':'))}" for s in self.program)
        return f"lib:{self.name}: {self.description} (verified: {json.dumps(self.check)}; used {self.successes}/{self.uses}) = [{steps}]"


class SkillLibrary:
    def __init__(self, path: Path | None):
        self.path = path
        self.skills: dict[str, LibSkill] = {}
        if path and path.exists():
            for d in json.loads(path.read_text()):
                self.skills[d["name"]] = LibSkill(**d)

    def save(self) -> None:
        if self.path:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.path.write_text(json.dumps([asdict(s) for s in self.skills.values()], indent=1))

    def get(self, name: str) -> LibSkill | None:
        return self.skills.get(name.removeprefix("lib:"))

    def search(self, query: str, k: int = 5) -> list[LibSkill]:
        q = tokens(query)
        scored = []
        for s in self.skills.values():
            overlap = len(q & tokens(f"{s.name} {s.description} {s.task} {json.dumps(s.check)}"))
            if overlap:
                scored.append((overlap + 0.1 * s.successes, s))
        scored.sort(key=lambda x: -x[0])
        return [s for _, s in scored[:k]]

    def add(self, name: str, description: str, task: str, program: list[dict[str, Any]], check: dict[str, Any]) -> LibSkill:
        name = re.sub(r"[^a-z0-9_]+", "_", name.lower()).strip("_") or "skill"
        base, n = name, 2
        while name in self.skills and self.skills[name].program != program:
            name, n = f"{base}_{n}", n + 1
        s = self.skills.get(name) or LibSkill(name, description, task, program, check)
        s.uses += 1
        s.successes += 1
        self.skills[name] = s
        self.save()
        return s

    def record_use(self, name: str, ok: bool) -> None:
        s = self.get(name)
        if s:
            s.uses += 1
            s.successes += ok
            self.save()
