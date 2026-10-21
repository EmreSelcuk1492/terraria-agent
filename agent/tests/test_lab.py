"""The lab is public: what leaves for it is scrubbed, and tests can't reach it at all."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from terraria_agent import lab  # noqa: E402


def test_scrub_rewrites_home_paths_at_any_depth():
    home = lab.HOME
    row = {"report_md": f"log at {home}/Library/Caches/x", "config": {"runs": [f"{home}/a", 3, None]}, "n": 1}
    out = lab.scrub(row)
    assert home not in repr(out)
    assert out["report_md"] == "log at ~/Library/Caches/x" and out["config"]["runs"] == ["~/a", 3, None] and out["n"] == 1


def test_upsert_sends_scrubbed_rows():
    sent = []

    class FakeHttp:
        def post(self, url, params=None, json=None):
            sent.append(json)
            return type("R", (), {"status_code": 201, "text": ""})()

    client = object.__new__(lab.Lab)  # skip __init__: no env, no network
    client.http = FakeHttp()
    client.upsert("lab_runs", [{"id": "x", "report_md": f"{lab.HOME}/secret/layout"}], "id")
    assert sent and lab.HOME not in repr(sent[0])


def test_lab_cannot_be_built_under_pytest():
    with pytest.raises(RuntimeError, match="never publish"):
        lab.Lab()


def test_publish_quietly_fails_closed(tmp_path, capsys):
    d = tmp_path / "scenarios" / "20261020-000000"
    d.mkdir(parents=True)
    (d / "results.json").write_text('{"results": [], "config": {}}')
    lab.publish_quietly(d)
    assert "lab publish skipped" in capsys.readouterr().out
