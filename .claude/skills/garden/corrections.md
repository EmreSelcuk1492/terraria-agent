# Corrections ledger

Every correction and where it landed on the trust ladder (see `/garden`). Seeded 2026-10-20 from the git log. Rung 1 = impossible in code, 2 = lint/test/CI, 3 = CLAUDE.md/skill, 4 = memory.

| Date | Correction | Rung | Artifact |
|---|---|---|---|
| 2026-10-15 | Survival suites run at 1x: fast-forward costs fights reaction time (01c0209) | 2 | `survival-at-1x` (fixed the drifted example in sim/README.md) |
| 2026-10-15 | Benchmarks use the pinned world, not the live Mac save (74960f0) | 2 | `queue-pins-world`; `scenarios.sh`/`llm_ab.sh` already default it (1). Deferred rung 1: make `sim/run.sh` default `SIM_SAVES` to bench-world (behaviour change: after night9) |
| 2026-10-15 | Library skills must be coordinate-free (74960f0) | 1 | `llm_bench` refuses to save them; `test_programs_with_coordinates_are_not_saved` |
| 2026-10-15 | Two benchmark processes drove one game (3fe86b9) | 1 | per-instance run lock (`scenarios.exclusive`) |
| 2026-10-15 | Runs on a harness later found broken stay listed, flagged, out of charts | 1 + 3 | `lab.INVALID` + `_harness_problem`; CLAUDE.md line |
| 2026-10-15 | A test run against the mock world published a 19/19 Director run (fa2653f) | 1 | `Director.publish` off unless `main()`; new: `agent/tests/conftest.py` makes `Lab` unconstructible under pytest; `verify.py smoke` goes through pytest, never `-m terraria_agent.director` |
| 2026-10-15 | A finding was published with `"commit": "pending"` (ebfac49) | 2 | `findings-commit-pending` + structural `findings-commit` (every ref resolves in git) + `findings-schema` |
| 2026-10-15 | Four worlds at once OOM-killed a benchmark world | 1 + 3 | `SIM_MEM` cap, `STAGGER`; CLAUDE.md "<= 3 worlds" |
| 2026-10-19 | Runs stopped by a mid-night code swap (exit 143) don't count | 1 + 3 | `ab_report.sh` skips them; CLAUDE.md "don't edit agent/ during a queue"; `verify.py status` |
| 2026-10-20 | The lab is public: publish home paths as ~ (276e338) | 1 + 2 | `scrub()` inside `Lab.upsert` (1); `no-home-paths` over every tracked file + `test_lab.py` (scrub at any depth, upsert sends scrubbed rows) (2) |
| 2026-10-20 | House refuge lost the night8 A/B (65.5 vs 34.0 deaths): revert, night7 stays the baseline (50e919d) | 3 | CLAUDE.md experiment protocol; `verify.py ab` verdicts |
| 2026-10-20 | Measure an A/A noise floor before trusting the next A/B (night9) | 3 | verify SKILL.md protocol; `verify.py ab` prints the floor to record |
| 2026-10-20 | Secrets live in ~/.config/lab/env and agent/.env, never in the repo (lab.py) | 2 | `no-secrets` |
| 2026-10-20 | SkillFactory.cs "keep in sync with tools.py" was a comment (rung 3) | 2 | structural `skill-parity`. Debt: `close_doors` allowlisted (Director-only) |
