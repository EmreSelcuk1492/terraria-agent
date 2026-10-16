# What is and isn't measured
**Real game, real inputs.** Movement, fighting and item use go through the player's controls each tick, the way a keyboard would. The navigator from TerraBlind (Reisenbug, MIT) runs with its free-item helpers switched off.

**Our shortcuts.** Mining waits a simulated swing time, then breaks the tile directly; the dig-in bunker places its roof blocks instantly from the inventory. Both are next to move to real item use.

**Test setup, not skills.** Scenarios teleport, spawn enemies and reset the character to set a known start. The agent never calls these.

**The model's job.** gpt-oss:20b, running locally, writes programs over skills written in C#. It plans and recovers; it does not press keys itself.

**Small numbers.** Suites run each scenario 1 to 5 times, so one attempt moves a pass rate a lot. Runs made on a harness later found broken stay listed, marked, and out of the charts.

**Game time, not wall time.** From 2026-10-15 evening, scenario limits and timings count the game's own ticks. Earlier suites ran at ~20 ticks/s against wall-clock limits, so a "40 s" scenario gave the agent about 14 s of game. Suites run headless, often fast-forwarded (several game ticks per frame); the same task takes the same number of ticks at any speed.
