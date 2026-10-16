# Recordings of real runs on lab.emrecanselcuk.com

Status: LLM track recorded and live since 2026-10-16 (A/B rec1: 16 clips). Survival and Director recording still to do. Replaces the agent's-eye map replay, which was dropped: footage of the real game beats a
tile map rebuilt from `/state`.

## Goal
Every run page can play the actual game for each attempt. The Terraria page opens on the newest clip worth watching
(a death, a fail, or a first pass). The numbers still come from results.json; the clip is the proof next to them.

## Capture
- **Sims (Linux containers).** The game already draws to Xvfb. Run `ffmpeg -f x11grab -video_size 1280x720 -framerate 30 -i :99`
  inside the container for the length of one attempt, then encode H.264 at CRF 28, 720p, without audio.
  - Start and stop per attempt from the runner over a small control endpoint, or `docker exec` start/stop.
  - Needs `TA_RENDER=1` and render 1, because headless mode draws 1 frame in 1000.
- **Mac game (7979).** Use `ffmpeg -f avfoundation` on the game window region. The screen must stay unlocked, as for any Mac run.
- **Which runs to record.**
  - Scenario suites at 1x: every attempt (they're short, 40–90 s).
  - LLM tasks: the game time only. Cut the planner's thinking gaps, since the world is frozen then anyway.
  - Director: record the whole run, and keep 20–40 s highlights around step completions and deaths. The journal has the timestamps.
- **Cost.** Rendering brings sims back to ~21 ticks/s, so recorded suites are slower. Record one bridge per suite and leave the rest headless. Keep the 1x rule for survival.

## Storage and publish
- Upload to Supabase Storage bucket `lab-clips` (public read), at path `<run-id>/<item>-<attempt>.mp4`, plus a poster jpg taken at the most eventful second.
- `lab.py` adds `{video, poster, seconds}` per attempt. Use the existing nullable `lab_results.frames` / `lab_runs.frames` columns, or rename them to `media` in a migration.
- Size budget: about 1–3 MB per 60 s clip at CRF 28, 720p. Check against the Supabase storage quota before backfilling many runs.

## Site
- **Run page.** One `<video controls muted playsinline preload="none">` per attempt, with its poster, next to that attempt's row. Pick an attempt with the attempt chips.
- **/terraria.** A clip section under the hero ("Latest from the sims"). It autoplays muted only on hover or when in view, and never under reduced motion.
- **Hotbar.** Add a slot with a camera or map icon once clips exist.

## Order
1. Get an ffmpeg x11grab clip from one sim scenario attempt by hand, and check the size and look.
2. Add the runner hook in `scenarios.py` (start/stop around `run_one`), then upload and publish in `lab.py`.
3. Add the site run-page player, then the /terraria section.
4. Run one recorded survival suite at 1x (one bridge), when compute allows.
