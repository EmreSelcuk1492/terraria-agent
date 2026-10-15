"""Lessons that survive between runs. Loaded into the system prompt at start, appended via the `remember` tool."""

from __future__ import annotations

from pathlib import Path


class Memory:
    def __init__(self, path: Path):
        self.path = path

    def load(self) -> list[str]:
        if not self.path.exists():
            return []
        return [line[2:].strip() for line in self.path.read_text().splitlines() if line.startswith("- ")]

    def add(self, lesson: str) -> None:
        lesson = " ".join(lesson.split())
        if not lesson or lesson in self.load():
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.path.open("a") as f:
            f.write(f"- {lesson}\n")
