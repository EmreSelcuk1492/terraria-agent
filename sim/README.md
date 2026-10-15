# sim: the real game in a Linux container (work in progress)

Goal: run the real tModLoader client with no visible window (Xvfb virtual screen, Mesa software GL),
one world per container, so the Director can be tested on many real worlds in parallel.

## Status (2026-10-15)

- Image builds natively on Apple Silicon (linux/arm64, 573 MB): `docker build -t terraria-sim sim/`
- tModLoader starts inside it: .NET 8, the Linux-arm64 FNA/SDL libraries, and the Xvfb screen all load.
- **Blocked at the ownership check.** tModLoader verifies Terraria ownership through Steam (needs the Steam
  client running alongside) or through a GOG install of Terraria. Neither exists inside the container, so the game exits.
  Not working around that check; see options below.

## Options

1. GOG copy of Terraria: tModLoader's supported non-Steam path; no launcher needed, so many containers can run.
2. Steam client inside the container, logged in: works for one instance; Steam allows one running game per account.
3. One real instance on the Mac with a virtual display, no containers: no parallel worlds.

## Run (once ownership is solved)

See the `docker run` in the commit message for the mount layout: tModLoader folder read-only, Terraria Content read-only,
a writable logs dir, and a per-world save dir with Mods/, Players/, Worlds/. The bridge is exposed on container port 8979.
