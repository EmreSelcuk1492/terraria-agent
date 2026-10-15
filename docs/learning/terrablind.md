# Learning from TerraBlind

TerraBlind by Reisenbug (https://github.com/Reisenbug/TerraBlind), MIT per its README. Code quoted or adapted here keeps that attribution.

Studied at submodule commit `26361f4` (`third_party/TerraBlind`). All TerraBlind paths below are relative to that directory; our paths are relative to the repo root. Most of its comments are Chinese; quotes here are translations.

Coordinate note before anything else: TerraBlind's nav goal `(gx, gy)` is the cell the player's **feet** occupy (ground is `gy+1`, target pixel `feetY = (gy+1)*16`). Our `Game.TileY` is the **center** row (`bridge/TerrariaAgentBridge/Game.cs:16`), which is one row higher for a standing player. So TerraBlind `gy` = our `y + 1`.

---

## 1. Navigation

There are four generations of nav in `Nav/` (grid `PathPlanner`, `NavCoordinator`, `SegmentedNavCoordinator`, `ActionGraphPlanner`). The live one is **`RecedingNav`** driving **`StateSpacePlanner`** over a **`MazeWand` cost field**. CAPABILITIES.md lists the others as unused.

### 1.1 The cost field (the "compass"), `Nav/MazeWand.cs`

- `BuildField(gx, gy, sx, sy)`: Dijkstra **from the goal**, 4-connected, one node per tile, physics ignored. Result is `Dictionary<(x,y), H>`: remaining cost to the goal from each cell. Box = start/goal bounding box plus a 400-cell margin (`FieldMargin`) for nav. Takes ~0.5 to 1.5 s, so it is always built on `Task.Run`; the player stands still until `_fieldReady`.
- Two cache slots (`_cachedField`, `_prevField`) keyed by goal, so two live goals do not evict each other.
- Rebuilt in the background when: 40 tiles were altered by our digs/places (`RebuildAltered`), best pickaxe power changed (`FieldPickStale`), or the player is off the field. Invalidated on world unload.
- `StepCost(cx,cy,nx,ny)` prices one step, calibrated to **frames** ("walk 3 px/frame, fall 10 px/frame; copper pick on stone is 45 frames/tile, one walk tile is 5.3 frames = 3, so a side dig is 26"):

| Constant | Value | Meaning |
|---|---|---|
| `MoveSide / MoveDown / MoveUp` | 3 / 1 / 9 | walk, fall, climb within jump reach |
| `DigSide/DigDown/DigUp` | 26 per solid cell | body is 3 rows, so entering a cell can mean digging 1 to 3 cells |
| `DigUpLift` | +19 once | digging up also needs something to stand on |
| `JumpReach` | 6 | climbing more than 6 cells above any floor costs extra |
| `JPlaceUp` / `PillarUp` | 15 / 45 | climb with a platform anchor nearby / in anchorless air |
| Air penalty | `AirCost`, free up to 10 cells sideways, +6 per cell beyond | stops the field "flying" across chasms |
| Medium | water +6, honey +20, cobweb +12 | surcharges, not bans |
| `PlateCost` | 400 | pressure plates: avoid, but not forbidden |
| `Impassable` | `int.MaxValue` | lava in any of the 3 body rows, locked doors, a solid cell the current pick cannot mine, a 1-wide slit with unwidenable neighbours. The edge is **skipped**, never added (overflow, see pitfall 6) |

- `StandPenalty` (via `Core/CellKind.cs`) adds the cost of building a foothold to stand in a cell; lava cells are impassable.
- `FieldDir` picks the neighbour minimizing `StepCost + H[neighbour]`, not just lowest H ("lowest H gets lured up an expensive pillar into a well").

### 1.2 The physics simulator, `Perception/PhysicsSimulator.cs`

A per-frame copy of vanilla `Player.HorizontalMovement` and jump code that calls **vanilla's own collision** (`Collision.WalkDownSlope`, `StepDown`, `StepUp`, `TileCollision`, `SlopeCollision`, `WetCollision`). That is why it handles slopes and half bricks without special cases.

- Body is 20 x 42 px (`PlayerW/PlayerH`).
- `Params.FromPlayer(p)` builds dry / wet / honey sets. Dry: gravity 0.4, maxFall 10, jumpSpeed 5.01, jumpHeight 15. Wet: 0.2 / 5 / 6.01 / 30. Honey: 0.1 / 3 / 5.01 / 15. Run speeds come from `CaptureBaked`, which must be called in `ModPlayer.PostUpdate` because `maxRunSpeed`/`accRunSpeed`/`runAcceleration` are only final late in `Player.Update` (`Perception/StateSnapshotPlayer.cs:851`).
- Horizontal: pressing toward motion adds `AccRun` (0.08) up to `MaxRun`; reversing first subtracts `RunSlowdown` (0.2); released on ground decays 0.2/frame; released in air decays 0.1/frame (DECISIONS 2026-05-23, fixed a systematic 1 to 2 tile overshoot).
- Jump: first `Jump` frame sets `vy = -JumpSpeed`; while held and `JumpFramesLeft > 0` vy stays `-JumpSpeed`; release zeroes the hold. Leaving water clamps remaining hold to `JumpHeight/5`. Head bump zeroes the hold.
- Wet movement scales unclipped motion by 0.5 (honey 0.25).
- `Grounded` = falling and vertically clipped this frame.

### 1.3 The A* planner, `Nav/StateSpacePlanner.cs` (`Plan`, line 248)

- Node = real physics state `SSNode {Px, Py, Vx, Vy, Grounded}`, not a grid cell. Only grounded nodes expand.
- Expansion (`ExpandRaw`, line 723): for `dir` in {toward goal, away, 0} and `hold` in `BuildHoldOptions()` = `{0, 2, 4, ..., Player.jumpHeight}`, run `SimulateSegment` until it lands (plus one settle frame) or, for walks, until it covered `WalkStridePx = 112` px or hit a wall. Also: platform drop-through edges (`SimulateDrop`, holding Down), split long falls, and "on-demand" edges (dig, pillar, jump-place, bridge) only where the field wants to go but physics blocks it (`OnDemandPlatformEdges`).
- **Edge cost = number of simulated frames.** Place edges add a bias (`JumpPlaceCost = BridgeCost = 30`). Dig edges cost from `Core/DigTable.CostFrames`: `ceil(100/dmg) swings * useTime * pickSpeed + trouble`.
- Heuristic = field H (min over an 8x8 block, `HBlockSize`) times `DistStepCost = 8` frames per cell; weight 1.0.
- Dedup: labels per `(StandCell, Grounded)` with velocity dominance (a label with lower g and at least the same |vx| in the same sign dominates). `coarseStates: true` keeps one label per cell so the open list can actually empty (used by trap escape to prove unreachability).
- `ReachedGoal`: grounded and within **12 px** of goal center/feet on both axes.
- Budget 20000 expansions. If not found, returns a **partial** plan to the best grounded node seen ("walk there and replan").
- Rejects edges that land in lava, pass through lava without a block to levee with, or report a "grounded" cell with no real floor under either body column.

### 1.4 The receding-horizon driver, `Nav/RecedingNav.cs`

Per-frame `Tick()` from `SetControls`. Each cycle, once the previous step finished and the player is grounded (`velocity.Y == 0`):

1. **Termination by world facts** (checked first every frame):
   - `Snap` (default): goal snapped down to standable ground (`SnapGoalToStandable`, up to 40 rows). Done when `|centerX - (gx*16+8)| <= 24` and `|feetY - (gy+1)*16| <= 24` (`GoalDistPx = 24`, **1.5 tiles**, no grounded check).
   - `Stand`: no snap, tolerance 8 px and must be grounded. Within 20 cells (`StandSwitch`) the last stretch is handed to A* with `goalSnapCap: 0`; three failed searches from three **different** cells gives `unreachable`.
   - `Mine` (`exact`): done when the goal tile is no longer solid.
   - `Reach`: done when grounded and `Reach.CanInteract(goal)`; if still sliding (`|vx| > 0.05`) it brakes with `SettleAt` first.
2. `StuckSentinel.Tick` (section 2). On give-up it first tries `TrapEscape` A*.
3. **Greedy one-edge step**, `StateSpacePlanner.StepAlongField` (line 2533): expand the same physics edges from the real current state, settle each landing (`SettleNode`, idle frames until grounded, so the label is the cell the next cycle will actually read), then score

   `total = frames * 0.5 + H[landing] + missPenalty - 18 * align + 0.5 * dev^1.5`

   where `align` is the dot product with the field's traced line direction at 6/20/80 cells (weights 0.3/1.0/0.4) and `dev` is distance from that line. Pick the minimum, dispatch **one** edge, replan next cycle from the real position. Drift never accumulates.
   - `missPenalty`: after each edge, `ReportEdge` compares planned vs real landing; misses are charged per `(from, to)` edge and decay by 0.93 per cycle (no blacklist).
   - `PUSH`: if the pick raises H by more than 20 (`PushSlack`) or bounces back to a recent cell, take the cheapest unvisited candidate instead.
4. **A* fallback**, only when greedy is provably stuck: `Trap.Hit` fires when **no** physics candidate lowers H (`Nav/Trap.cs`; "a 4-neighbour local minimum check found zero cells in 2.31M, because Dijkstra H always relaxes from a lower neighbour; what traps you is physics"). Then, in order:
   - `TrapEscape.TryEscape` (`Nav/TrapEscape.cs`): background A* (`coarseStates`, budget 20000) to a nearby cell whose H is at least 60 lower (`MinDrop`); only a `Found` result is dispatched; 3 failures per pit.
   - `Unstick.Handle("digdown", FootColUnmineable)` when the column under the feet is unmineable.
   - `Commitment.Begin` (`Nav/Commitment.cs`): pick a meaningfully lower-H cell 4 to 14 cells away and route to it even though H rises on the way; ends on arrival or no progress.
5. No physics edge at all: try escape steps (walk or hop either way), then place a platform under/beside the feet, else `walled_in`.

### 1.5 Executing an edge

`DispatchPlan` turns the plan into `ExecStep`s; `TickSteps` (line 2166) starts each step **from rest on the ground** and runs a watchdog: soft timeout `est*1.75+60` frames (reset by >2 px motion, cap 3600), hard timeout `est*4+300` (cap 7200). Walk-only steps use `WalkTick` to a column (4 px). Jump/place steps **replay the planned input frames** (`ApplyControls`, line 3557) with closed-loop checks:

- drift > 24 px while grounded: replan (cooldown 10 frames, at most 40 replans)
- planned |vx| >= 1.5 but real < 0.4 for 18 frames: replan "stuck"
- one-frame position jump > 160 px: abort (teleport)
- falling 24+ px below plan: plunge rescue (place a platform)
- a `Place` frame stalls until the tile exists (60 frames), then `PlaceFailed`

Bridge, pillar and dig steps are delegated to `BridgeBuilder`, `SkillExecutor.StartPillarJump`, `MineCoordinator`.

### 1.6 Precise stop, `Actions/SettleAt.cs`

Nav stops within 24 px; to stand on an exact column use `SettleAt`. Each frame: `slide = vx^2 / (2 * runSlowdown)` (vanilla's linear ground friction, read live from `p.runSlowdown`). If the slide would reach the target, release (or press opposite if overshoot > tolerance), else push toward it. Done when `|center - (col*16+8)| <= 8`, `|vx| <= 0.05`, grounded, for 5 consecutive frames; 300-frame timeout. `StartSpan(l, r)` targets "body occupies exactly columns l..r".

---

## 2. Stuck detection and survival reflex

### 2.1 `Debug/StuckSentinel.cs` (called every frame by `RecedingNav`)

Every 30 ticks (`FlatWindow`, ~0.5 s) sample four signals against the previous sample:

| Signal | Progress if |
|---|---|
| net displacement | > 24 px (`MovedPx`) |
| field H at `StandCell` | dropped |
| sum of `p.hitTile.data[i].damage` | increased (pick is landing hits, so standing still while mining is legal) |
| count of solid tiles in a 9x9 box around the body | changed (a placed block or finished dig) |

All four flat = one anomaly. Response ladder: anomalies 1 to 3 stop the planner and take an 18-tick **safe step** toward the side with more open body-height cells two columns out (with a short hop for lips/half bricks), alternating direction each time. The 4th anomaly in one episode returns "give up" (~6 to 8 s worst case). The episode resets after 300 calm ticks, or as soon as H drops below where it started. `RecedingNav` then tries `TrapEscape` once before reporting `stuck`.

Executor-level stall checks are separate (section 1.5): velocity-blocked 18 frames, drift 24 px, step watchdog.

### 2.2 `Flow/SurvivalReflex.cs` (every frame from `HttpServerSystem.PostUpdateEverything`, unconditional)

- Logs any one-frame move > 160 px as a `[warp]`.
- If `Config.FreezeLavaUnderFeet` (default **off**): `Concessions.FreezeLavaBeneath` turns the lava surface the player is about to land on into blocks (needs Concessions enabled too).
- `TouchesLava`: any `LiquidAmount > 0` lava cell under the hitbox counts (`lavaWet` is false at shallow depths).
- In lava: takes the Jump axis lock and holds jump; `LavaLevee` places one block (from inventory) in the cell under the feet via `PlaceAction`, 6-frame cooldown, only if it has a 4-neighbour anchor; releases the Move/Use lock as soon as the feet are on something.
- In lava or HP < 50%: `p.QuickHeal()` every frame (vanilla no-ops on potion cooldown).
- Melee reflex is compiled out (`MeleeReflexEnabled = false`).
- Pushes one `survival` event `{kind: lava|low_hp, hp, max_hp}` per episode on the `/ws` channel.

---

## 3. Housing

### 3.1 Site scan, `Core/Predicates.cs` `ScanHouse` (line 231), `/scan_house`

Spiral outward from `(fromX, fromY)`: columns at distance 0, then -d, +d up to `range`; per column, rows at offset 0, then -dy, +dy up to 60 (`VertScan`). First hit wins (nearest). A candidate `(x, y)` = bottom-left corner of a `w x h` box (x to x+w-1, y-h+1 to y) passes when:

1. `LadderLen(x, y) >= 0`: straight down from the corner, every cell **Vacant** until ground within 20 rows (a ladder can be built up to it). The site may float.
2. Every cell of the box is **Vacant** = `!HasTile && WallType == 0` (no trees, grass, or existing background wall).
3. Optional `needLava`: lava below every column (hell house).
4. `EvilNearby`: none of the four corners of the box grown by 100 tiles is corruption/crimson (corner sampling, not a full scan).

### 3.2 `room_check`, `Predicates.RoomJson` (line 317)

Calls vanilla `WorldGen.StartRoomCheck(x, y)` from a point **inside** the room, then reads `WorldGen.houseTile[]` against `TileID.Sets.RoomNeeds.CountsAsDoor/Table/Chair/Torch`. Returns `legal`, `shape_ok`, vanilla `roomCheckFailureReason`, `has`, `missing`, tile count and bounds. It does **not** call `RoomNeeds` or `ScoreRoom`, so `legal: true` can still be rejected by the game (evil tiles, occupied, no standing spot).

### 3.3 Build order, `Build/HouseBuilder.cs`

One orchestrator for both houses: `Rooms=4` gives the 21-wide surface house (5 per room + 1 pillar column, `PillarH = 9`, rooms 10 high), `Rooms=1` the 6-wide hell room. It only starts existing async primitives and waits for each.

```
Clear      dig every block and platform in the site box (keeps the corner cell the player stands on)
Lift       stand on the corner (SettleAt / DropDown), platform-ladder up if floating
Floor      BridgeBuilder lays Width platforms from x0+dir
MainPillar PillarUp 9 high at the far end
HopTop     SettleAt under it, HopUp onto the top, SettleAt
Roof       BridgeBuilder back along the roof row
MoveOver   step off, DropDown to the floor
Support    one PillarUp (8 high, stops under the roof) per room
Bench      SettleAt, PlaceAction work bench
Craft      tables, chairs, walls (24 per room), check torches (cannot be crafted without gel)
Tables/Chairs  WalkPlace along the floor (single room: PlaceAction next to the bench)
Walls      per room, in a fixed WallOrder (perimeter then interior; "vanilla wall merging depends on order"), then a torch
Audit      AuditHouse: every furniture cell, every wall cell, floor/roof/pillar lines checked tile by tile
Fix        FixStruct (dig blockers in the cavity, PlaceAnywhere missing blocks, PlaceWalls missing walls) / FixCraft
           re-audit; give up after 2 rounds with no reduction (FixStallMax) or 12 rounds total
Reclaim    walk to items ThrowItems dropped to make inventory room
```

The structure is platforms, not blocks: the frame is a platform floor, platform roof and platform pillars, with wood walls inside. Single-room houses then assign the Demolitionist via `AssignHome.Try`.

### 3.4 Compared to ours

| | TerraBlind | Ours (`bridge/TerrariaAgentBridge/Skills/House.cs`, `Skills/Macros.cs` `BuildHouse`) |
|---|---|---|
| Site | nearest fully Vacant box, floating allowed with ladder, evil check | `FindSite` (`Macros.cs:417`): flattest W-wide ground within 30 tiles, scores only `Game.Solid` cells and liquid inside the box; no evil check, no wall/tree check |
| Placement | real item use (swing, cursor, reach, anchors) through primitives | `WorldGen.PlaceTile(..., forced: true)` / `PlaceWall` directly (`House.cs:188`, `:199`), with reach check and walking |
| Frame | platforms | solid blocks with a door gap (`House.cs:44`) |
| Verification | per-cell audit plus repair loop until no progress | `CheckHouse` flood fill plus `Housing.Vanilla` (`House.cs:237`), which is **stricter** than their `room_check`: it also runs `RoomNeeds` and `ScoreRoom` |
| Recovery | repairs missing cells in place | on failure, `BuildHouse` fails with "call build_house again" |

Our verification is the better half; their site scan and audit-and-repair loop are the parts worth taking.

---

## 4. Endpoint reference (verified in `Infra/HttpServerSystem.cs`)

Server: `http://127.0.0.1:17878/` (line 72). Use `127.0.0.1`, not `localhost`. Ours is on 7979, no clash. Facts that matter for a client:

- **No method check**: every route answers any HTTP method. Status routes are conventionally GET.
- **Bodies are regex-parsed**, not JSON-parsed; spaces are stripped first in most routes. Send plain JSON with integer literals and literal `true`.
- **Handlers run on the HttpListener thread**, not the game thread, except a few queued ones (`/start_run`, `/hell_run`, `/teleport`, `/swap`, `/interact`, `/loot_all`, `/quick_heal`). `/craft`, `/nav_recede`, `/settle`, `/mine` mutate game state off-thread.
- Async pattern: `POST /x` returns `{accepted}` or `{ok}`, poll `/x_status` (nav: `/nav_recede_done`), cancel with `/x_stop`. Unknown path: 404 `{"error":"not_found"}`.
- Event push channel: WebSocket at `/ws`, messages `{"type": T, "data": {...}}`. `nav_done {"result": LastStop}` fires when nav stops.

### Navigation

| Route | Request | Response |
|---|---|---|
| `POST /nav_recede` (line 2096) | `{"gx":int,"gy":int}` plus optional `"stand":true` / `"reach":true` / `"exact":true`. Precedence stand > reach > exact > snap. `gy` = feet row. | `{"ok":true}`; 400 `{"ok":false,"reason":"bad_request"}`. Restarting the same goal and mode while active is a no-op. |
| `/nav_recede_done` (line 2172) | none | running: `{"done":false,"status":"running"}`; success: `{"done":true,"status":"done"}`; else `{"done":false,"status":"failed","reason":R}` with R in `stopped`, `walled_in`, `stuck`, `unreachable`, `exception`, `never_ran` |
| `/nav_recede_stop` (line 2181) | none | `{"ok":true}` (sets reason `stopped`) |
| `POST /field_warm` (line 2119) | `{"gx","gy"}` | `{"ok":true,"cached":bool}`; builds the field in the background |

There is **no** `/nav_recede_status`. Tolerances: snap 24 px, stand 8 px plus grounded.

### Precise positioning

| Route | Request | Response |
|---|---|---|
| `POST /settle` (line 2003) | `{"col":int}` | `{"accepted":true,"note":"poll /settle_status"}`; 400 `{"accepted":false,"reason":"bad_params","usage":...}` |
| `/settle_status` | none | `{"outcome":"idle|running|done|timeout|stopped","running":bool,"col":int,"reason":str,"origin":[cx,cy],"on_ground":bool,"vel_x":float}` |
| `/settle_stop` | none | `{"ok":true}` |
| `POST /hop_up` | `{"row":int,"col":int?}` | `{"accepted":...}`; `/hop_up_status` outcome `idle|running|done|timeout|stopped` |
| `/origin` (line 2085) | none | `{"cx","cy","pos":[px,py]}`; `cx` = column covering most body pixels (tie: left), `cy = (pos.Y+height-2)/16` = feet row |

### Housing

| Route | Request | Response |
|---|---|---|
| `POST /scan_house` (line 2797) | `{"from_x","from_y","w","h","range"}`, defaults: player origin, 21, 10, 200 | `{"found":bool,"from":[x,y],"want":{"w","h"},"at":[x,y],"top":int,"right":int,"scanned":int}` (`at`/`top`/`right` only when found; `at` = bottom-left). Also draws the box overlay. CAPABILITIES.md mentions `rope_h`; the handler does not read it. |
| `POST /room_check` (line 2828) | `{"x","y"}` = a cell inside the room | `{"legal","shape_ok","reason","has":{"door","table","chair","torch"},"missing":[...],"tiles":int,"bounds":[x1,y1,x2,y2]}`; 400 `{"error":"bad_params"}` |
| `POST /build_house` | see line 2769 | async, `/build_house_status` = `HouseBuilder.StatusJson`: `{"outcome","running","phase","reason","rooms","room_idx","x0","floor_row","width","dir"}` |

### Perception

| Route | Request | Response |
|---|---|---|
| `/state` (line 290) | none | Snapshot built every frame in `PostUpdate` (one frame stale). Top level: `tick`, `walk_to_edge_done`, `player{hp,max_hp,mana,max_mana,pos{x,y}(top-left px),vel,width,height,direction,on_ground,in_liquid,biome,defense,minion_slots,max_minion_slots,coins}`, `world{day,clock,raining,rain_intensity,sandstorm,hardmode,blood_moon,eclipse,active_event,downed{...}}`, `equipment{selected_slot,inventory_open,chest_open,last_interact,smart_cursor,held_item,items[]}` (non-empty slots with absolute `slot`), `camera`, `movement{jump_speed,jump_height,gravity,max_run_speed,acc_run_speed,wing_time_max,no_fall_dmg,lava_immune,lava_time,extra_jumps}`, `buffs`, `enemies`, `town_npcs`, `objects`, `detected_tiles`, `available_recipes`, `nearby_stations`, `dropped_items` |
| `POST /terrain` (line 1655) | `{"cx","cy","w","h"}`, defaults w=40 h=20; `cx=cy=0` means player center column and the row at the feet bottom edge | `{"cx","cy","w","h","x0","y0","rows":[str,...]}`, `x0=cx-w/2`, `y0=cy-h/2`. Chars: `.` no tile, `#` solid, `-` platform (solidTop), `+` non-solid tile (tree, grass, vine), `?` out of world. Liquids are not shown. |
| `POST /have` | `{"id":int,"n":int?}` | `{"id","have","want","enough"}` (counts slots 0 to 57 plus the mouse item) |

### Items

| Route | Request | Response |
|---|---|---|
| `POST /recipe` (line 933) | `{"name":str}`, internal name (with or without spaces) or display name | `{"recipes":[{"makes","internal","amount","ingredients":[{"name","internal","need","have"}],"stations":[{"tile","items":[{"name","internal","have"}],"total"}]}],"found":int}`; up to 12 recipes; 404 when `found` is 0. Works for uncraftable-right-now items. |
| `POST /craft` (line 831) | `{"item_id":int}` or `{"item_name":str}`, `"amount":int` (default 1) | name must be in **currently available** recipes (after `Recipe.FindRecipes()`), so station proximity is enforced by vanilla. Success `{"ok":true,"crafted","wanted","overflow","stop","free_slots"}`; partial `{"ok":false,"error":"partial",...}`; `{"ok":false,"error":"not_available"|"inventory_full","crafted":0,"item_id",...}`; unknown 400 `{"error":"item_not_found","name_matched","available_count","free_slots","raw_name","available_names":[...]}`. Synchronous and instant (no walking). If fewer than 2 free slots it **throws items on the ground** first (`ThrowItems.MakeRoom(2)`). |
| `POST /mine` (line 497) | `{"dir":"left|right|up|down","target_wx":int,"target_wy":int}` | `{"ok":true}`; 400 `{"error":"bad_params","usage"}`. Start cell = player center column, feet row. |
| `/mine_status` | none | `{"outcome","running","mined","reason","target":[x,y]?}`, outcome in `idle, running, done, no_pickaxe, no_tiles, out_of_box, out_of_reach, stalled, stopped, unbreakable` |
| `/mine_stop` | none | `{"ok":true}` |

---

## 5. Their known pitfalls (CAPABILITIES.md, translated), applied to us

1. **`IsPassable` is not `Vacant`.** Trees and grass do not block movement but occupy the cell, so nothing can be placed there. Check `!HasTile && WallType == 0` before placing. They hit this twice. **Partly applies.** `Place` checks `HasTile` correctly (`Skills/Actions.cs:57`) and `BuildRoom` clears occupied cells (`Skills/House.cs:141`, `:147`), but `BuildHouse.FindSite` scores a site by `Game.Solid` only (`Skills/Macros.cs:440`), so trees and existing walls inside a site are invisible to site choice.
2. **Nav tolerance is 1.5 tiles.** For exact standing, nav then `/settle`. **Applies.** `MoveTo` succeeds at `|dx| <= tolerance` and `|dy| <= tolerance + 2` with no grounded or speed check (`Skills/MoveTo.cs:33`); `BuildRoom` walks with tolerance 1 to 2 (`Skills/House.cs:68`, `:87`). We have no settle step.
3. **Primitives default to the column the player is standing in** (`rope_ladder`, `pillar`). Drift one column and the structure goes next door. Pass coordinates or settle first. **Mostly does not apply**: our pillar picks a free column from the body's actual left/right columns (`Skills/Explore.cs:455`). `EnsureStation` places relative to the center column (`Skills/Macros.cs:86`) but tries several offsets.
4. **`ItemID.Search` is the internal name**; `createItem.Name` returns the localized (Chinese) name, never match on it. **Does not apply**: `Game.ResolveItem` tries `ItemID.Search` first (`Game.cs:27`). The display-name fallback (`Game.cs:31`) would be localized on a non-English client.
5. **Rope is "绳", not "绳子"** in the Chinese localization table. **Does not apply** (we use internal names).
6. **`StepCost`'s `Impassable = int.MaxValue` overflows to a large negative when added**; consumers must `continue` first. **Not today** (we have no cost sums), but it applies to any port of `MazeWand.StepCost`: keep their skip-before-add in `BuildField` and `FieldDir`.
7. **The player is 20 px wide and 42 px tall**: 2 columns, 3 rows. Blocks and platforms cannot be placed inside the hitbox; ropes can. **Partly applies.** Placement checks the hitbox (`Skills/Actions.cs:58`, `Skills/House.cs:184`). `MoveTo.Drive` checks only rows `feetY` and `feetY-1` of one column ahead for a wall (`Skills/MoveTo.cs:76`), so a block at head height (`feetY-2`) or in the second body column is not seen as an obstacle.

---

## 6. Lessons from DECISIONS.md worth adopting

1. **Plan in real physics states, not grid cells** (2026-06-01). Their grid A* never stopped producing half-brick, slope and sub-pixel bugs (`FromPx` mapping one position to 4 nodes caused replan storms, 2026-05-29). Moving to a state-space search over a simulator that calls vanilla collision removed that class of bugs.
2. **Use vanilla's own code for the model.** The simulator calls `Collision.*`; room checks call `StartRoomCheck`; settle reads `runSlowdown` live. Every hand-written approximation in their history (air friction, StepUp on descent, water halving) became a bug entry.
3. **Fine hold options.** `{0, 8, 12, 15}` could not reach heights between 12 and 15; `{0, 2, ..., Player.jumpHeight}` fixed "can't jump up". Read `jumpHeight`, don't hardcode 15.
4. **Air release decays vx by 0.1/frame, ground by 0.2.** Ignoring it put landings 1 to 2 tiles off.
5. **Launch gate must match landing tolerance.** Requiring the simulated landing to hit the exact column (`bestDistCx == 0`) while accepting landings 3 columns off deadlocked the player forever when the best reachable column was 1 off (2026-05-29).
6. **Landing on a slope never shows a positive vy frame** (`SlopeCollision` snaps vy to 0). Detect landing by "has left the ground" not "was falling". Standing on a half brick puts the feet 0.5 tile lower, so a +1 step is really 1.5 and needs a jump (2026-05-30).
7. **Open-loop replay always drifts; close the loop, but replan only when grounded.** Mid-air is not an expansion point.
8. **Debounce state flags.** `p.wet` flickering at the waterline caused replan storms.
9. **Measure costs.** 10 s gives ~135 tiles walked, ~22 mined, ~24 pillared, so mine ~6x walk. Their field constants are frame-calibrated, not guessed.
10. **Placement rules, from source** (2026-05-18): a platform can go in air or a `tileCut` cell if any tile or wall is in the 3x3 around it; a block needs a solid or platform 4-neighbour or a wall in its own cell.
11. **Every primitive returns a usable failure, never a silent return**; termination reads world facts with three outcomes: done, no progress, timeout (PERCEPTION_DESIGN.md, "hand"). Our skills already fail with messages; "no progress" as a distinct, early outcome is the part to copy.
12. **Log the number behind every decision**, not just the verdict. Their logs (`[recede-cands]`, `[ss-land] dx dy`, `[costcmp]`) are what made the fixes above possible.
13. **LLM planners hallucinate game knowledge** (PERCEPTION_DESIGN.md, "brain"): feed real item data and a closed op list into the prompt, feed execution errors back verbatim, and add a self-check. Same direction as our Director plus advisor split.

---

## 7. Ranked port plan

| # | What | Their source | Our target | Why first |
|---|---|---|---|---|
| 1 | **Settle to a column** | `Actions/SettleAt.cs` (148 lines, no deps beyond `p.runSlowdown`) | new `bridge/TerrariaAgentBridge/Skills/Settle.cs`, used as the last phase of `MoveTo` when tolerance is 0, and by `BuildRoom` before placing | Smallest port, fixes pitfall 2 directly |
| 2 | **Stuck sentinel signals** | `Debug/StuckSentinel.cs` | replace the distance-only stall in `Skills/MoveTo.cs:44-50` and `TunnelTo` (`Skills/Explore.cs:330`) | Mining no longer reads as stuck (dig damage signal); safe-step ladder before failing |
| 3 | **Physics simulator** | `Perception/PhysicsSimulator.cs` plus `CaptureBaked` call in `PostUpdate` | new `bridge/TerrariaAgentBridge/Nav/PhysicsSimulator.cs`; add `PostUpdate` to `AgentPlayer.cs` | Prerequisite for 4 and 5; standalone and testable |
| 4 | **Jump selection by simulation** | `SimulateSegment` and `BuildHoldOptions` in `Nav/StateSpacePlanner.cs` | replace the fixed 18-frame jump in `Skills/MoveTo.cs:79-90` | Picks the hold that lands on the target; ends blind jumping |
| 5 | **Cost field + greedy one-edge receding nav** | `Nav/MazeWand.cs` (`BuildField`, `StepCost`, `StandPenalty`), `StateSpacePlanner.StepAlongField`, `Nav/RecedingNav.cs` termination rules | new `bridge/TerrariaAgentBridge/Nav/` (CostField, Receding); `move_to` and `tunnel_to` become thin wrappers | The real upgrade; port walk/jump/drop/dig edges first, place edges later |
| 6 | **A* escape** | `StateSpacePlanner.Plan`, `Nav/TrapEscape.cs`, `Nav/Commitment.cs` | same `Nav/` folder | Only after 5 is stable |
| 7 | **House site scan + audit loop** | `Predicates.ScanHouse`, `Vacant`, `EvilNearby`, `LadderLen`; `HouseBuilder.AuditHouse` / `FixStruct` | `Skills/Macros.cs` `FindSite` (add Vacant and evil checks), `BuildHouse` default stage (repair until no progress instead of failing) | Fixes pitfall 1; keeps our stricter `Housing.Vanilla` as the verdict |
| 8 | **Lava contact check** | `SurvivalReflex.TouchesLava` | `SkillRunner` guard / `Game.Lava` callers | `lavaWet` misses shallow lava |

Interim option instead of porting 5 and 6: call TerraBlind's `/nav_recede` from a proxy skill. Two requirements: `AgentPlayer.SetControls` must **not** clear controls while that proxy runs (it clears every flag whenever `SkillRunner.IsRunning`, `AgentPlayer.cs:18-31`), and the proxy must convert `gy = our y + 1`. Since TerraBlind's handlers run off the game thread, call it only while our freeze is off.

Every ported file gets a header: `// Adapted from TerraBlind <path> by Reisenbug (MIT), https://github.com/Reisenbug/TerraBlind`.

---

## 8. Side effects of loading TerraBlind next to our bridge

`scripts/build-terrablind.sh` applies `third_party/patches/terrablind-no-concessions.patch`, which sets `Concessions.Enabled = false` and gates `PlatformStock.Tick` on it. That removes: wood top-up to 9999 and the one-time item 65 grant (`Core/Concessions.cs`, `PostUpdate`), platform auto-crafting (`Actions/PlatformStock.cs`, which could also throw inventory items on the ground via `ThrowItems.MakeRoom`), `ClearLavaForPlacement`, and `FreezeLavaBeneath`. Unpatched builds would also make the Director's wood step permanently satisfied (`agent/terraria_agent/director.py:190`).

Still active after the patch, every frame, without being asked:

1. **`SurvivalReflex.Tick`** (`Flow/SurvivalReflex.cs`, from `Infra/HttpServerSystem.cs:93`): at HP < 50% or in lava it calls `QuickHeal()` every frame. That starts potion sickness before our guard heals at 40%/30%, so our `TryHeal` sees `potionDelay > 0` and returns false (`SkillRunner.cs:164`), and `SkillRunner.Tick` then fails the skill with "LOW HEALTH ... no healing potion" (`SkillRunner.cs:116`) even though a potion was just drunk. In lava it also starts `PlaceAction` to put a block from our inventory under the feet. **Biggest remaining conflict.**
2. **`PerceptionDiff.Tick`**: pushes salience events to `/ws` only. Harmless.
3. **`StateSnapshotPlayer.PostUpdate`** builds a full snapshot (enemies, NPCs, tiles, recipes, stations, drops) every frame and runs `PhysicsSimulator.CaptureBaked`. CPU cost only.
4. **`StateSnapshotPlayer.AddStartingItems`**: every newly created character gets 4 Torches. Not gated by the patch.
5. **Default keybinds** (`TerraBlind.cs:22`): J, K, U, I, H, B, N, P, O, L, `[`, M. Pressing them in game triggers TerraBlind actions (K = nav to mouse, H = scan a site then nav and build a 21x10 house, B/P/O = bridge/pillar/platform-down tests). Unbind them in Controls.
6. **Three craftable debug items** (`MazeWand`, `NavWand`, `RecordWand`), each 1 Dirt at a Work Bench. They appear in our `Observation.Craftable` list (`Observation.cs:255`) whenever the player has dirt near a bench.
7. **HTTP server** on 17878 and `/ws`, with handlers mutating state off the game thread; **logs** to `TerraBlindLogs/` with a new session per world load (their `jump_trace.log` reached 24 MB); **overlay** drawing (`ShowOverlay` default true); a chat line on world entry.
8. Defaults that stay off unless configured: `AlwaysGillsAndShine`, `FreezeLavaUnderFeet`, `AvoidDanger`, `FightBack`, `DodgeBoss`, `Combat.Enabled`, `Dodge.Enabled`, `WofFight.On`. `Player.tileRangeX/Y` (a static our `Game.InReach` reads) is only raised to 30 inside its own WoF pipeline.

### SetControls interaction

Both mods override `ModPlayer.SetControls`. tModLoader calls ModPlayer hooks in mod load order, which sorts by internal name before dependencies, so **TerraBlind's runs first and ours second** ("TerraB" sorts before "Terrar" under both ordinal and culture comparison; worth confirming with a one-line log).

- What TerraBlind's does every frame (`Perception/StateSnapshotPlayer.cs:456`): `AxisLock.Sweep`, `PlaceAction.Tick`, then its primitive chain. With nothing of its own running and no `/control` input pending, it writes no control flags. When `TbAutoActive()` (any of its primitives, its nav, or combat) it also clears `delayUseItem`, `mouseInterface`, `Main.HoveringOverAnNPC` and `Main.SmartInteractShowingGenuine`.
- While **our** skill runs, `AgentPlayer.SetControls` resets every control flag (`AgentPlayer.cs:20-31`) after TerraBlind wrote its own, so ours wins and TerraBlind primitives (including the lava levee) stall until their own timeouts. Not reset by us: `selectedItem` and `Main.mouseX/mouseY` (we only write the mouse when `Aim` is set, `AgentPlayer.cs:36-41`). TerraBlind can change `selectedItem` and aim the cursor earlier in the same frame (`RecedingNav.SmashPot`/`SmashWeb`, `Cursor.AimTile`), so a skill that relies on the held item should call `Game.Hold` every tick.
- While **TerraBlind** drives (its `/nav_recede`, `/settle`, `/mine`) and no skill of ours runs, our hook returns early and does not interfere.
- Rule: never run both at once. A proxy skill that waits on a TerraBlind primitive must make `AgentPlayer.SetControls` skip its clear.
