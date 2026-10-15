# Credits

## TerraBlind, by Reisenbug

[github.com/Reisenbug/TerraBlind](https://github.com/Reisenbug/TerraBlind) (mod author name "Stardust"; companion agent repo [Reisenbug/Tairaria](https://github.com/Reisenbug/Tairaria)). MIT, as stated in its README.

A tModLoader mod with perception, pathfinding, building and combat primitives over local HTTP, and a code-only pipeline that goes from a fresh world to a dead Wall of Flesh. Its frame-priced physics navigation, stuck sentinel, housing checks and pitfall notes are well ahead of where this project started, and we learn from them directly.

How we use it:
- `third_party/TerraBlind` is a git submodule pinned to upstream commit `26361f4` (2026-09-23). It is never edited in place.
- `scripts/build-terrablind.sh` copies it into tModLoader's ModSources, applies `third_party/patches/terrablind-*.patch`, and builds `TerraBlind.tmod`. The only patch turns its gameplay concessions off by default (infinite wood, free starting weapon, platform auto-crafting), so loading it never edits our player's inventory.
- `docs/learning/terrablind.md` is our English study of its design: how it navigates, its endpoints, its known pitfalls, and a port plan.
- Any code we port into `bridge/` keeps a header comment naming the TerraBlind source file it came from.

If you like this project, go star TerraBlind too.

## Also referenced

- [DragonLens](https://github.com/ScalarVector1/DragonLens) by ScalarVector1: god mode, spawn control and fast-forward for testing. TerraBlind's pipeline depends on it.
- [TerrarAI](https://github.com/dkoleber/TerrarAI) by Derek Koleber (2019, MIT): an early Terraria RL bridge; its wiki's write-up on why Terraria is hard to train RL on helped shape the LLM-plus-skills approach here.
