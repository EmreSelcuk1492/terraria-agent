---
name: verify
description: Gather evidence that a change to terraria-agent works, with the repo's own CLI instead of ad-hoc scripts. Runs the fast checks (lint, syntax, offline tests), a seconds-long smoke run of the Director on the mock world, a read-only sim/queue status, and scores Director A/B or A/A nights against the experiment protocol. Use before claiming anything works, before committing, before and after an overnight queue, and when asked "did it win", "is the sim running", "where does X live" (see FEATURE_MAP.md).
---

# /verify

Say "verified: `verify.py check` all pass, smoke 19/19 on mock" or "night9 A/A: steps delta X, deaths delta Y", never "should work".

## The CLI (`.claude/skills/verify/verify.py`)

| Command | What it proves | Cost |
|---|---|---|
| `python3 .claude/skills/verify/verify.py` (= `check`) | garden lint, `bash -n` on every script, every .py compiles, every .json parses, FEATURE_MAP.md is current, offline pytest against the mock world | ~30 s, CI runs exactly this |
| `... smoke` | the Director completes its 19-step plan against `mock_bridge.py` (through pytest, so it can't publish) | ~10 s |
| `... status` | read-only: `tsim-*` containers, running queues/runners, tail of the newest `queue.log`; says BUSY or idle | instant |
| `... ab DIR [--floor-steps S --floor-deaths D]` | runs `sim/ab_report.sh DIR`, prints new minus old, and a verdict against the noise floor | instant, read-only |
| `... map [--check]` | regenerates [FEATURE_MAP.md](FEATURE_MAP.md) from the tree (components, entry points, how to run, owning files) | instant |

Real-game evidence (scenario suites, Director runs) needs Docker and minutes to hours: start those only when Emre asks, and never while `status` says BUSY. Never run `python -m terraria_agent.director|scenarios|llm_bench` against the mock to "test": real entry points publish to the public lab.

## Experiment protocol (Director / survival A/Bs)

1. **Baseline** = the last code that won (now: night7's Director, HEAD 50e919d after the refuge revert). The `old` arm is a frozen copy of `agent/terraria_agent` taken when the queue launches; the `new` arm runs this checkout. Do not edit `agent/` or the queued `sim/nightN.sh` until the queue logs `all done`.
2. **Same conditions on both arms:** pinned world (`SIM_SAVES=$c/bench-world`), same bridge build (`SIM_MOD`, a `mods-nightN` dir), same player/world, `--fresh`, `--start-at dawn`, `--advisor none`, `--hours 1`, `TA_TELEMETRY=0`, staggered starts, <= 3 worlds. Change one thing per A/B.
3. **Score** with `verify.py ab ~/Library/Caches/terraria-sim/nightN`: avg plan steps (higher is better), avg deaths (lower), homes. Runs stopped early (exit 143, a code swap) are excluded by `ab_report.sh`.
4. **A/A control** before trusting an A/B: both arms on identical code (night9). Its deltas are the noise floor; write them here:
   - noise floor: *pending night9 (2026-10-21)*. Until it exists, an A/B win needs a gap larger than night8's loss (3 steps, 31 deaths) to be called.
5. **Judge:** new wins only if steps rise by more than the floor and deaths don't rise by more than the floor, with >= 5 finished runs per arm. Within the floor = no evidence, keep the baseline. A loss is reverted with the numbers in the revert message (`Revert X: nightN A/B lost (deaths A vs B); nightM stays the baseline`), and the handoff (`docs/next-session.md`) gets the table.
6. **Survival suites run at 1x** (lint-enforced); fast-forward only tracks that freeze the world while the agent thinks (LLM track). Timings are game ticks, not wall time.
7. **Broken harness != agent failure:** flag the run in `lab.INVALID` with the reason, never delete it.

## Feature map

[FEATURE_MAP.md](FEATURE_MAP.md) is generated: edit `COMPONENTS` in `verify.py`, then `verify.py map`. CI fails when it is stale, so a new module or script shows up in the map in the same commit.
