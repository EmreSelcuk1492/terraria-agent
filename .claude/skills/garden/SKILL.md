---
name: garden
description: Apply the trust ladder (poteto's "2,500 PRs" talk) to terraria-agent. Promote every correction Emre makes to the strongest rung that holds it (make it impossible in code -> lint/test/CI -> CLAUDE.md line -> memory), run the repo lint, log the correction in the ledger, and sweep for spreading anti-patterns. Use when Emre corrects how runs, experiments, the lab or the code should work ("the lab is public", "revert, the A/B lost", "survival runs at 1x"), says "garden" or "lint", or before anything is published to the lab.
---

# /garden

Read [source.md](source.md) for the talk. One line: **a correction Emre has to make twice is a failure of the environment, not of the agent.** Fix the environment at the highest rung that holds, then log it in [corrections.md](corrections.md).

## Modes

- `/garden` or `/garden lint`: `python3 .claude/skills/garden/lint.py`. Report `file:line [rule]`, fix each file. CI runs the same command (through `verify.py check`).
- `/garden learn <correction>`: climb the ladder below, implement, seed-test, then add a ledger row.
- `/garden sweep`: run lint, then grep for any pattern repeated 3+ times that contradicts a ledger row or a rule (copied workarounds, stale numbers in docs, an old queue script used as a template). Propose a rule for whatever is spreading.

## The ladder (climb as high as the correction allows)

| Rung | Here | When |
|---|---|---|
| 1 · Impossible | Code/architecture with one paved path: `Lab.upsert` -> `scrub()` for every lab write, `Director.publish` off unless `main()`, the pytest guard in `agent/tests/conftest.py`, per-instance locks, `SIM_MEM` caps, `INVALID` flags in `lab.py` | The mistake has a single choke point you can own |
| 2 · Static check | A rule in [rules.json](rules.json) (`forbid` regex, `require` co-occurrence, or a structural check in `lint.py`), or a test in `agent/tests/`. Seed a bad file to prove it fires, then confirm the repo is clean | Greppable or testable: paths, secrets, flags in queue scripts, schema of published files, parity between bridge and agent |
| 3 · Guidance | A line in `CLAUDE.md` or the verify skill's experiment protocol | Judgment: whether an A/B won, when to revert, what a run means |
| 4 · Memory | A feedback memory | True for one session or one person only |

Never stop at rung 4 if rung 2 would hold.

## Constraints for gardening this repo

- **No behaviour changes during a queue.** Run `python3 .claude/skills/verify/verify.py status` first. A rung-1 fix inside `agent/terraria_agent/` or a queued `sim/nightN.sh` waits until the queue is done and the next A/B can measure it; until then land the rung-2 check, allowlist current debt in `rules.json` with a reason, and log the deferred rung-1 fix in the ledger.
- **New checks must pass on current code.** Fix the drift (docs) or allowlist it with a reason and a ledger row; never leave CI red.
- **Lean.** Stdlib Python only in `lint.py`; CI stays what runs in under a minute without the game.

## Gardener rules

- **Anti-patterns spread by copying.** Every `nightN.sh` is copied from the last one, so a wrong flag in one queue lands in the next. Fix the newest queue (the template) and add a `require`/`forbid` rule over `sim/night*.sh`.
- **One blessed path per job:** worlds start via `sim/run.sh`, suites via `sim/scenarios.sh`, LLM A/Bs via `sim/llm_ab.sh`, scoring via `verify.py ab`, publishing via `Lab.upsert`, evidence via `verify.py`. No one-off scripts that duplicate these.
- **Stop the bleeding first:** write the rule now, clean up after.
