# Source: "how i shipped 2,500 PRs last month to production"

lauren (@poteto), works on GrokBot at SpaceX AI (ex-Cursor, ex-React team). 38-min talk posted on X 2026-09-21, originally prepared for Cursor Compile London.
https://x.com/poteto/status/2102050467505430555 · transcribed locally with Whisper 2026-10-20 (transcript not stored; this is a summary in our words).

## The thesis
Agent throughput is capped by **trust**. You can't go from babysitting 1–5 agents to running many in parallel until you trust their work while you're away. Spawn many before that and you get slop PRs and regressions. Her framing: you are the bottleneck, so move what you know out of your head and into the environment. Analogy: not a "software factory" but a **Michelin kitchen**. You no longer cook each component, but you own the result and the setup: equipment, training, and fixing whatever cooks keep tripping on.

## Trust builder 1: verification (correctness)
- Teach the agent to **run the thing and gather evidence** that it works (her skill drives the app through Chrome DevTools Protocol: traces, heap snapshots, hotspots).
- A verification skill has two parts:
  1. **A CLI inside the skill directory.** Agents call the same tool every time instead of writing fresh one-off scripts that differ between sessions. Invest in making it good.
  2. **A feature map.** It's like a site map: what the product does, where each feature lives, how to reach it. Without it, an agent given a vague report ("screenshot + ???") can run the app but has to guess what was meant. It's stored in the skill and kept up to date by an automation.
- Formal methods (Lean, TLA+) are the far end of the scale. Verification skills already get you most of the way.

## Trust builder 2: skills that encode how good engineers work (quality)
Playbooks for debugging, feature development and prototyping (her "Pstack" plugin). Verification proves something works; these make it good.

## The trust ladder: where a correction should land (strongest first)
1. **The codebase / architecture itself.** Agents extend whatever patterns they see, so the codebase is the best memory. Make the mistake *categorically impossible* (data structures, boundaries, one paved path).
2. **Static analysis.** Linters, compiler diagnostics, CI. If an agent keeps making the same mistake, write a rule.
3. **Guidance.** Rules, review bots, skills. Usually followed, sometimes skipped.
4. **Style guide / human review.** Weakest. Doesn't scale with PR volume. Use it to find what's missing from 1–3, never as the main defense.

When you correct an agent, ask which rung is the most effective place to fix it, and climb as high as you can.

## Gardening
- **Anti-patterns spread like a virus.** One workaround or explanatory comment gets copied everywhere within days, and it becomes the de facto pattern.
- Her example: agents used code comments as excuses to bandage over problems instead of fixing them, so her framework **bans comments**.
- Every team needs a **gardener**:
  - Delete existing debt.
  - Enforce a single blessed path.
  - When you spot a bad pattern, write a lint rule first. That stops the bleeding even before cleanup.
  - Then have agents clean up, so the base is always something you'd be happy to see copied.
- Lock the environment down until it's annoying for humans but conventional for agents, so even a low-context agent, or a PM or designer driving one, does the right thing by default.

## Outer loop
Connect the agent to event sources (Slack, Sentry, Datadog) and let routines kick off cloud agents automatically: reproduce bug reports, open PRs. Little bespoke infrastructure is needed once the trust layers exist.
