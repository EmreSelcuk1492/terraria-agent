"""Put screen recordings of a run on lab.emrecanselcuk.com, next to the results they show.

Sims started with REC=1 TA_RENDER=1 (sim/run.sh) record one clip per container. The LLM track gives every task a
fresh container (--reset-world), so the clips that started after a pass began are its tasks, in order.

    python -m terraria_agent.clips llm <runs/llm/<stamp>-...> [...]   # match, poster, upload, link
    python -m terraria_agent.clips llm <dir> --dry-run                # only show the matching

Clips go to the public Storage bucket lab-clips as terraria/<kind>-<stamp>/<item>-<attempt>.mp4 + .jpg, and
lab_results.media = {video, poster, seconds}. Needs ffmpeg on the Mac.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
import time
from datetime import datetime
from pathlib import Path

import httpx

from .lab import PROJECT, Lab, _env

REC = Path.home() / "Library" / "Caches" / "terraria-sim" / "recordings"
BUCKET = "lab-clips"
MAX_MB = 45  # the bucket refuses files over 50 MB


def seconds(f: Path) -> float:
    out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(f)],
                         capture_output=True, text=True).stdout.strip()
    return float(out or 0)


def clips_since(t0: float, until: float | None = None, prefix: str = "llm-") -> list[Path]:
    fs = [f for f in REC.glob(f"{prefix}*.mp4") if f.stat().st_birthtime >= t0 and (until is None or f.stat().st_birthtime < until)]
    return sorted(fs, key=lambda f: f.stat().st_birthtime)


def poster(clip: Path, out: Path) -> None:
    """The last moments show how the task ended."""
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-sseof", "-1.5", "-i", str(clip), "-frames:v", "1", "-q:v", "4", str(out)], check=True)


def shrink(clip: Path, out: Path) -> Path:
    if clip.stat().st_size <= MAX_MB * 2**20:
        return clip
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", str(clip), "-vf", "scale=640:-2", "-c:v", "libx264", "-preset", "slow",
                    "-crf", "32", "-movflags", "+faststart", "-an", str(out)], check=True)
    return out


def faststart(clip: Path, out: Path) -> Path:
    """Fragmented recordings play, but a plain mp4 with the index up front starts faster on the web."""
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", str(clip), "-c", "copy", "-movflags", "+faststart", str(out)], check=True)
    return out


def upload(lab: Lab, path: str, f: Path, mime: str) -> str:
    env = _env()
    for attempt in range(4):  # large uploads over a home connection drop now and then (TLS bad record mac)
        try:
            r = lab.http.post(f"{env['LAB_SUPABASE_URL']}/storage/v1/object/{BUCKET}/{path}", content=f.read_bytes(),
                              headers={"Content-Type": mime, "x-upsert": "true", "Prefer": ""}, timeout=300)
            break
        except httpx.HTTPError:
            if attempt == 3:
                raise
            time.sleep(3 * (attempt + 1))
    if r.status_code >= 300:
        raise RuntimeError(f"upload {path}: {r.status_code} {r.text[:200]}")
    return f"{env['LAB_SUPABASE_URL']}/storage/v1/object/public/{BUCKET}/{path}"


def llm(run_dir: Path, dry: bool) -> None:
    j = json.loads((run_dir / "results.json").read_text())
    tasks = [r["task"] for r in j["results"]]
    stamp = run_dir.name[:15]
    t0 = datetime.strptime(stamp, "%Y%m%d-%H%M%S").timestamp()
    t1 = (run_dir / "results.json").stat().st_mtime
    clips = clips_since(t0, t1)
    print(f"{run_dir.name}: {len(tasks)} tasks, {len(clips)} clips")
    for t, c in zip(tasks, clips):
        print(f"  {t:14} {c.name}  {seconds(c):6.1f}s  {c.stat().st_size / 2**20:5.1f} MB")
    if len(clips) != len(tasks):
        sys.exit("clip count doesn't match the tasks; nothing linked (check REC/TA_RENDER and resets)")
    if dry:
        return
    lab, run_id = Lab(), f"{PROJECT}:llm:{stamp}"
    with tempfile.TemporaryDirectory() as tmp:
        for t, c in zip(tasks, clips):
            base = f"{PROJECT}/llm-{stamp}/{t}-1"
            v = shrink(faststart(c, Path(tmp) / f"{t}.mp4"), Path(tmp) / f"{t}-small.mp4")
            p = Path(tmp) / f"{t}.jpg"
            poster(v, p)
            media = {"video": upload(lab, base + ".mp4", v, "video/mp4"), "poster": upload(lab, base + ".jpg", p, "image/jpeg"),
                     "seconds": round(seconds(v), 1)}
            r = lab.http.patch("/lab_results", params={"run_id": f"eq.{run_id}", "item": f"eq.{t}", "attempt": "eq.1"}, json={"media": media})
            if r.status_code >= 300:
                raise RuntimeError(f"link {t}: {r.status_code} {r.text[:200]}")
            print(f"  linked {t}")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(prog="terraria_agent.clips", description=__doc__.split("\n")[0])
    ap.add_argument("kind", choices=["llm"])
    ap.add_argument("dirs", nargs="+", type=Path)
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args(argv)
    for d in a.dirs:
        llm(d, a.dry_run)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
