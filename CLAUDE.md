# terraria-agent

Claude/local models play real Terraria through a tModLoader bridge mod. `agent/` Python, `bridge/` C#, `sim/` Docker worlds + overnight queues, `docs/lab` feeds the public lab (lab.emrecanselcuk.com). Map of everything: `.claude/skills/verify/FEATURE_MAP.md`. Handoff: `docs/next-session.md`.

## Trust ladder

Agent work is trusted on evidence, and corrections are promoted to the strongest rung that holds them (impossible in code -> lint/test/CI -> this file/skills -> memory).

- **Verify** (`.claude/skills/verify/`): `python3 .claude/skills/verify/verify.py` before any commit or "it works" claim (CI runs the same); `verify.py smoke` for a mock end-to-end run; `verify.py status` before touching `agent/` or `sim/`; `verify.py ab <night dir>` to score an A/B. The experiment protocol lives in its SKILL.md.
- **Garden** (`.claude/skills/garden/`): `python3 .claude/skills/garden/lint.py`; every correction gets a ledger row in `corrections.md` and, where greppable, a rule in `rules.json`.

## Rules (rung 3: judgment the checks can't make)

- A queue may be running (`verify.py status`). While it runs: no edits to `agent/terraria_agent/`, the queued `sim/nightN.sh`, `mods-*` builds or the bench world; no new worlds; never `docker rm` a `tsim-*` you didn't start. The `new` arm runs this checkout.
- A/B: one change per night against the last winning baseline, identical conditions both arms, judged against the A/A noise floor. A loser is reverted with its numbers in the commit message; the baseline only moves on a win.
- The lab is public. Only real command-line runs publish; never run `terraria_agent.lab backfill|scrub|notes` or `clips` without Emre asking. Every lab write goes through `Lab.upsert` (scrubs home paths); don't add another path.
- A run on a broken harness is flagged in `lab.INVALID` with the reason, never deleted or rerun silently.
- <= 3 sim worlds at once on Docker's 8 GB; survival suites at 1x.
- New queue = copy the newest `sim/nightN.sh` (pinned world, frozen old arm, `TA_SESSION`), not an older one.
- Commits: one line, `scope: lowercase summary` (scopes: sim, director, bridge, survival, skills, bench, lab, showcase, docs).
