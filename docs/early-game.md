# Early game knowledge (Terraria 1.4.4 / tModLoader, vanilla)

Scope: first 1 to 3 in-game days of a fresh small world. All item names are `ItemID` constants, tile names are `TileID` constants, NPC names are `NPCID` constants, wall names are `WallID` constants. Recipes, pick powers, housing rules, spawn rules, layer math and stats below were read from the decompiled tModLoader.dll (`Terraria.Recipe.SetupRecipes`, `Player.GetPickaxeDamage`, `WorldGen.CheckRoom/RoomNeeds/ScoreRoom`, `Main.UpdateTime_SpawnTownNPCs`, `NPC.SpawnAllowed_*`, `TerrainPass`). Strategy notes are general game knowledge.

Starting inventory: `CopperShortsword`, `CopperPickaxe`, `CopperAxe`. Guide (`NPCID.Guide`) is already in the world.

Time: `Main.dayTime` true for 54000 ticks (4:30 AM to 7:30 PM), night 32400 ticks. 60 ticks = 1 s. `Main.time` counts up within the current phase.

## 0. World ore pairs (critical)

Each world generates ONE ore of each pair (not both, except drunk/"for the worthy" style seeds). Check which exists before planning recipes.

| Tier | Option A | Option B | Bars needed per bar |
|---|---|---|---|
| 1 | `CopperOre` (tile `Copper`) | `TinOre` (tile `Tin`) | 3 ore |
| 2 | `IronOre` (tile `Iron`) | `LeadOre` (tile `Lead`) | 3 ore |
| 3 | `SilverOre` (tile `Silver`) | `TungstenOre` (tile `Tungsten`) | 4 ore |
| 4 | `GoldOre` (tile `Gold`) | `PlatinumOre` (tile `Platinum`) | 4 ore |

## 1. Recipes

Notation: `station` = TileID the player must stand near ("hand" = no station). `[group:Wood]` = any wood in `RecipeGroupID.Wood`: `Wood, Ebonwood, RichMahogany, Pearlwood, Shadewood, SpookyWood, PalmWood, BorealWood, AshWood`. `[group:IronBar]` = `IronBar` or `LeadBar`. Ingredients without a group tag require that exact item (e.g. `WorkBench` needs plain `Wood`; other woods have their own work benches).

### 1a. Basic building and stations

| Result (qty) | Ingredients | Station |
|---|---|---|
| `WorkBench` (1) | 10 `Wood` | hand |
| `WoodPlatform` (2) | 1 `Wood` | hand |
| `Torch` (3) | 1 `Gel`, 1 `Wood` [group:Wood] | hand |
| `Campfire` (1) | 10 `Wood` [group:Wood], 5 `Torch` | hand |
| `ManaCrystal` (1) | 5 `FallenStar` | hand |
| `RopeCoil` (1) | 10 `Rope` | hand |
| `WoodenTable` (1) | 8 `Wood` | `WorkBenches` |
| `WoodenChair` (1) | 4 `Wood` | `WorkBenches` |
| `WoodenDoor` (1) | 6 `Wood` | `WorkBenches` |
| `WoodWall` (4) | 1 `Wood` | `WorkBenches` |
| `StoneWall` (4) | 1 `StoneBlock` | `WorkBenches` |
| `Sign` (1) | 6 `Wood` [group:Wood] | `WorkBenches` |
| `TikiTorch` (1) | 1 `Torch`, 3 `Wood` [group:Wood] | `WorkBenches` |
| `Furnace` (1) | 20 `StoneBlock`, 4 `Wood` [group:Wood], 3 `Torch` | `WorkBenches` |
| `IronAnvil` (1) | 5 `IronBar` (exact) | `WorkBenches` |
| `LeadAnvil` (1) | 5 `LeadBar` (exact) | `WorkBenches` |
| `Chest` (1) | 8 `Wood`, 2 `IronBar` [group:IronBar] | `WorkBenches` |
| `Sawmill` (1) | 10 `Wood` [group:Wood], 2 `IronBar` [group:IronBar], 1 `Chain` | `WorkBenches` |
| `Chain` (15) | 1 `IronBar` [group:IronBar] | `Anvils` |
| `Loom` (1) | 12 `Wood` [group:Wood] | `Sawmill` |
| `Bench` (1) | 8 `Wood` [group:Wood] | `Sawmill` |
| `Silk` (1) | 7 `Cobweb` | `Loom` |
| `Bed` (1) | 15 `Wood`, 5 `Silk` | `Sawmill` |
| `GrayBrick` (1) | 2 `StoneBlock` | `Furnaces` |
| `GrayBrickWall` (4) | 1 `GrayBrick` | `WorkBenches` |
| `CookingPot` (1) | 10 `IronBar` [group:IronBar], 2 `Wood` [group:Wood] | `Anvils` |
| `Candle` (1) | 1 `GoldBar`, 1 `Torch` | `WorkBenches` |

Reverse crafts (auto-generated, same station as the forward recipe): 2 `WoodPlatform` -> 1 `Wood` (hand); 4 `WoodWall` -> 1 `Wood` (`WorkBenches`); 4 `StoneWall` -> 1 `StoneBlock` (`WorkBenches`). There is no recipe that makes `StoneBlock` from anything else; mine `Stone` tiles.

`Rope` has no crafting recipe (found in pots/chests, sold by Merchant). `Glowstick` alternatives for light: `Torch` (cheapest), `Campfire` (light + regen, but NOT a housing light), `TikiTorch`, `Candle`.

### 1b. Smelting (all at `Furnaces`)

| Bar | Ore | Bar | Ore |
|---|---|---|---|
| `CopperBar` | 3 `CopperOre` | `TinBar` | 3 `TinOre` |
| `IronBar` | 3 `IronOre` | `LeadBar` | 3 `LeadOre` |
| `SilverBar` | 4 `SilverOre` | `TungstenBar` | 4 `TungstenOre` |
| `GoldBar` | 4 `GoldOre` | `PlatinumBar` | 4 `PlatinumOre` |
| `Glass` (1) | 2 `SandBlock` [group:Sand] | `Bottle` (2) | 1 `Glass` |

### 1c. Tools, weapons, armor (all at `Anvils` unless noted; Wood is [group:Wood])

| Tier | Pickaxe | Axe | Hammer | Broadsword | Bow | Helmet | Chainmail | Greaves |
|---|---|---|---|---|---|---|---|---|
| Copper | `CopperPickaxe` 8 bar + 4 Wood | `CopperAxe` 6 + 3 Wood | `CopperHammer` 8 + 3 Wood | `CopperBroadsword` 6 | `CopperBow` 7 | `CopperHelmet` 12 | `CopperChainmail` 20 | `CopperGreaves` 16 |
| Tin | `TinPickaxe` 8 + 4 Wood | `TinAxe` 6 + 3 Wood | `TinHammer` 8 + 3 Wood | `TinBroadsword` 6 | `TinBow` 7 | `TinHelmet` 12 | `TinChainmail` 20 | `TinGreaves` 16 |
| Iron | `IronPickaxe` 10 + 3 Wood | `IronAxe` 8 + 3 Wood | `IronHammer` 8 + 3 Wood | `IronBroadsword` 8 | `IronBow` 7 | `IronHelmet` 15 | `IronChainmail` 25 | `IronGreaves` 20 |
| Lead | `LeadPickaxe` 10 + 3 Wood | `LeadAxe` 8 + 3 Wood | `LeadHammer` 8 + 3 Wood | `LeadBroadsword` 8 | `LeadBow` 7 | `LeadHelmet` 15 | `LeadChainmail` 25 | `LeadGreaves` 20 |
| Silver | `SilverPickaxe` 10 + 4 Wood | `SilverAxe` 8 + 3 Wood | `SilverHammer` 8 + 3 Wood | `SilverBroadsword` 8 | `SilverBow` 7 | `SilverHelmet` 15 | `SilverChainmail` 25 | `SilverGreaves` 20 |
| Tungsten | `TungstenPickaxe` 10 + 4 Wood | `TungstenAxe` 8 + 3 Wood | `TungstenHammer` 8 + 3 Wood | `TungstenBroadsword` 8 | `TungstenBow` 7 | `TungstenHelmet` 15 | `TungstenChainmail` 25 | `TungstenGreaves` 20 |

Bar counts are of that tier's own bar (exact, no group). Shortswords: `CopperShortsword` 5, `TinShortsword` 5, `IronShortsword` 6 bars. `GoldPickaxe` / `PlatinumPickaxe`: 10 bar + 4 Wood.

Totals for a full kit of one tier: Copper/Tin pick+axe+sword+bow+armor = 8+6+6+7+12+20+16 = 75 bars (225 ore). Iron/Lead/Silver/Tungsten = 10+8+8+7+15+25+20 = 93 bars (279 ore for Iron/Lead, 372 for Silver/Tungsten). Prioritize pickaxe > armor > sword.

### 1d. Wood gear, ammo, healing

| Result (qty) | Ingredients | Station |
|---|---|---|
| `WoodenSword` | 7 `Wood` | `WorkBenches` |
| `WoodenHammer` | 8 `Wood` | `WorkBenches` |
| `WoodenBow` | 10 `Wood` | `WorkBenches` |
| `WoodHelmet` | 20 `Wood` | `WorkBenches` |
| `WoodBreastplate` | 30 `Wood` | `WorkBenches` |
| `WoodGreaves` | 25 `Wood` | `WorkBenches` |
| `WoodenArrow` (25) | 1 `Wood` [group:Wood], 1 `StoneBlock` | `WorkBenches` |
| `FlamingArrow` (10) | 10 `WoodenArrow`, 1 `Torch` | hand |
| `LesserHealingPotion` (2) | 1 `Mushroom`, 2 `Gel`, 2 `Bottle` | `Bottles` (a placed `Bottle` item becomes tile `Bottles`) |
| `HealingPotion` (1) | 2 `LesserHealingPotion`, 1 `GlowingMushroom` | `Bottles` |
| `GrapplingHook` | 3 `Chain`, 1 `Hook` | `Anvils` |

### 1e. Reference stats (from item defaults)

| Item | Damage | Item | Defense |
|---|---|---|---|
| `CopperShortsword` | 5 | `WoodHelmet`/`WoodBreastplate`/`WoodGreaves` | 1/1/0 |
| `WoodenSword` | 7 | `CopperHelmet`/`Chainmail`/`Greaves` | 1/2/1 |
| `CopperBroadsword` / `TinBroadsword` | 9 / 10 | `TinHelmet`/`Chainmail`/`Greaves` | 2/2/1 |
| `IronBroadsword` / `LeadBroadsword` | 12 / 13 | `IronHelmet`/`Chainmail`/`Greaves` | 2/3/2 |
| `SilverBroadsword` / `TungstenBroadsword` | 14 / 14 | `LeadHelmet`/`Chainmail`/`Greaves` | 3/3/2 |
| `GoldBroadsword` / `PlatinumBroadsword` | 15 / 16 | `SilverHelmet`/`Chainmail`/`Greaves` | 3/4/3 |
| `WoodenBow` / `CopperBow` / `IronBow` / `TungstenBow` | 4 / 6 / 8 / 10 (+ arrow 5) | `TungstenHelmet`/`Chainmail`/`Greaves` | 4/5/3 |
| `LesserHealingPotion` | heals 50 | | |

## 2. Pickaxe power

Pick power of early pickaxes (`Item.pick`):

| Pickaxe | Power | Pickaxe | Power |
|---|---|---|---|
| `CopperPickaxe` | 35 | `SilverPickaxe` | 45 |
| `TinPickaxe` | 35 | `TungstenPickaxe` | 50 |
| `CactusPickaxe` | 35 | `GoldPickaxe` | 55 |
| `IronPickaxe` | 40 | `PlatinumPickaxe` | 59 |
| `LeadPickaxe` | 43 | `NightmarePickaxe` / `DeathbringerPickaxe` | 65 / 70 |
| | | `MoltenPickaxe` | 100 |

Minimum power per tile (from `Player.GetPickaxeDamage`; below the minimum the hit does 0):

| Tile | Min power | Note |
|---|---|---|
| `Dirt`, `Stone`, `Grass`, `Sand`, `ClayBlock`, `Mud`, `SnowBlock`, `Copper`, `Tin`, `Iron`, `Lead`, `Silver`, `Tungsten`, `Gold`, `Platinum`, `Heart` (life crystal), `Pots` | none | starting `CopperPickaxe` mines all of these |
| `Meteorite` | 50 | |
| `Demonite`, `Crimtane` | 55 | only enforced below `Main.worldSurface` |
| `Obsidian` | 55 | |
| `Ebonstone`, `Crimstone` | 65 | damage per hit halved |
| `Hellstone` | 65 | damage halved |
| Dungeon bricks | 100 | below surface, outside the middle 35%-65% of the map width |
| `Cobalt`/`Palladium`, `Mythril`/`Orichalcum`, `Adamantite`/`Titanium` | 100 / 110 / 150 | hardmode |

Hits to break: damage per hit = pick power (doubled for `Dirt`, `ClayBlock`, `Sand`, `Mud`, `SnowBlock`, ash/silt/slush/hardened sand; halved for evil stones, `Hellstone`, `Pearlstone`); a tile breaks at 100 accumulated. So `CopperPickaxe` (35): stone/ore 3 hits, dirt 2 hits. Trees need an axe (any).

## 3. Housing and town NPCs

### 3a. Valid room (`WorldGen.StartRoomCheck` + `RoomNeeds` + `ScoreRoom`)

| Rule | Exact value |
|---|---|
| Size | Flood fill from an interior air tile; counts every reached tile INCLUDING the boundary blocks/door/platform tiles. Need >= 60 and < 750. Minimum practical shapes: 10x6 or 6x10 outer frame (interior 8x4); a comfortable starter is 10 wide x 7 tall outer. |
| Enclosure | Flood fill stops at solid tiles and closed doors (`ClosedDoor`). Any opening leaks the fill and the room grows too big or reaches the world edge (fail). |
| Background walls | For every interior tile, some tile within +-2 horizontally AND some tile within +-2 vertically must have a player-placed (house) wall or a solid tile. Natural walls (`WallID.Dirt`, `WallID.DirtUnsafe`, cave walls) do not count. Simplest: cover the whole interior with `WoodWall` (WallID `Wood`) or `StoneWall` (WallID `Stone`). |
| Light source | One tile of `TileID.Sets.RoomNeeds.CountsAsTorch`: `Torches`, `Candles`, `Chandeliers`, `Jackolanterns`, `HangingLanterns`, `Lamps`, `Candelabras`, `Fireplace`, ... `Campfire` does NOT count. |
| Flat surface | One of `CountsAsTable`: `Tables`, `WorkBenches`, `Pianos`, `Dressers`, `Bathtubs`, `Bookcases`, `AlchemyTable`, `Tables2`, `PicnicTable`, ... |
| Comfort | One of `CountsAsChair`: `Chairs`, `Beds`, `Benches`, `Thrones`, `PicnicTable`, `Toilets`. |
| Entrance | One of `CountsAsDoor`: `ClosedDoor`/`OpenDoor`, `Platforms` (a `WoodPlatform` in the wall/ceiling counts), trapdoors, tall gates. |
| Home spot | Needs at least one floor tile that is solid and NOT a platform, with solid tiles directly left and right of it, and a 3-wide x 3-tall gap of non-solid space above it. Flat block floor with the furniture leaving open space solves this. |
| Evil | Corruption/Crimson tiles near the room (scan box around it, minus 5 per `Sunflower`) reduce the score; >= 50 net evil tiles flags it evil and typically makes it unusable. Build away from evil biomes. |
| Occupancy | One NPC per room. Shared walls between rooms are fine. |
| Edge | Room must be >= 10 tiles from any world edge. |

Bot recipe for one valid room: outer frame 10 wide x 7 tall of `Wood` (`WoodBlock` tile) or `StoneBlock`; `WoodWall` behind all 8x5 interior tiles; `WoodenDoor` in a side wall (needs a 1x3 gap); `WoodenTable` + `WoodenChair` + `Torch` placed inside. Cost: frame 30 perimeter tiles minus 3 door gap = 27 `Wood`, 40 `WoodWall` = 10 `Wood`, door 6, table 8, chair 4, so about 55 `Wood` + 1 `Torch` per room (adjacent rooms sharing a wall cost less). Verify in-game with `WorldGen.StartRoomCheck(x,y)` then `WorldGen.RoomNeeds(npcType)` from inside the room.

### 3b. Move-in conditions

Town NPC spawn checks run only during the day (`Main.dayTime`), roughly every 7200 ticks (2 minutes at normal speed), and need an empty valid room. One NPC moves in per check.

| NPC (`NPCID`) | Condition (exact) | Realistic day 1-3? |
|---|---|---|
| `Guide` | Always (if none alive) | Present at start |
| `Merchant` | Any player has >= 5000 copper value (50 `SilverCoin`) in the 58 inventory slots (coins/ammo slots included; banks and chests do NOT count) | Yes: pots + slimes + surface chests give 50 silver within day 1 |
| `Nurse` | Merchant alive AND a player has consumed >= 1 `LifeCrystal` | Yes, after first life crystal |
| `Demolitionist` | Merchant alive AND an explosive in inventory (`Bomb`, `Dynamite`, `Grenade`, `StickyBomb`, ...) | Likely: bombs drop from underground pots / chests |
| `ArmsDealer` | Any bullet or gun in inventory (`MusketBall`, `FlintlockPistol`, any item with `useAmmo == Bullet`) | Sometimes (chest guns, Merchant does not sell bullets) |
| `DyeTrader` | A dye or dye material in inventory AND >= 4 town NPCs | Possible day 2-3 (strange plants, dye items) |
| `Painter` | >= 8 town NPCs | Unlikely |
| `Angler` | Talk to `SleepingAngler` on an ocean beach (world edge) | Yes if the bot walks to the ocean |
| `Stylist` | Free `WebbedStylist` in a spider cave | Rare early |
| `Golfer` | Rescue `GolferRescue` in the underground desert | Rare early |
| `Dryad` | Any of the first three bosses defeated | No (needs a boss) |
| `BestiaryGirl` (Zoologist) | Bestiary >= 10% complete | Unlikely before day 3 |

Realistic target for day 1-3: Guide, Merchant, Nurse, Demolitionist (+ Angler if ocean visited). Build 3-4 rooms.

## 4. Early dangers and responses

| Threat | Facts | Bot response |
|---|---|---|
| Night | `!Main.dayTime`. `Zombie` (walks, hits ~14) and `DemonEye` (flies) spawn on the surface; more damage than slimes. | Before dusk (`Main.time > 48000` during day) return to house, close door, mine/craft inside, or go underground where surface night spawns do not apply. Zombies cannot open doors except during a Blood Moon. |
| Slimes | `BlueSlime`/`GreenSlime` (net IDs) by day; drop `Gel` (torches, potions) and copper coins. | Fight with sword; farm ~20 `Gel` on day 1. |
| Fall damage | Damage starts after falling > 25 tiles (`25 + extraFall`): damage = (tiles fallen - 25) * 10. Water landing and some blocks (`TileID.Sets.NegatesFallDamage`) cancel it. | Never drop more than 20 tiles. Dig staircases or place `WoodPlatform`/`Rope` when descending shafts. |
| Darkness | Underground is pitch black without light. | Place a `Torch` every ~8-10 tiles while mining; carry >= 50 torches before going below `Main.worldSurface`. |
| Lava | Liquid lava pools are generated deep in the cavern layer (lava line ~ (rockLayer + maxTilesY)/2 + 0..-100 + 50..80, so about y 750-900 on a small world). Contact = heavy burn damage. | Check `Main.tile[x,y].LiquidType == LiquidID.Lava` (liquid > 0) before digging into a tile; never dig straight down below `Main.rockLayer`. |
| Cave enemies | `CaveBat`, `GiantWormHead`, `Skeleton` (cavern), `MotherSlime`, `ToxicSludge`, `LavaSlime` deeper. | Keep HP > 50%; retreat up when HP < 40%; drink `LesserHealingPotion`. |
| Eye of Cthulhu | Rolled at dusk only if: not yet defeated, a player has consumed >= 5 `LifeCrystal` (200 max HP) AND defense > 10, AND >= 4 town NPCs alive; then 1/3 chance. Warning chat line: "You feel an evil presence watching you..." Spawns at night. | On that message: go inside a closed house, or prepare an arena with platforms; if gear is below iron tier + 200 HP, hide and wait for dawn (it leaves at day). |
| Blood Moon | At dusk, 1/9 chance (not on new moon) once any player has consumed >= 2 `LifeCrystal`. More/stronger zombies (`BloodZombie`, `Drippler`), zombies can open doors. Message "The Blood Moon is rising...". | Stay indoors behind a door you can block, or replace the door with solid blocks for the night; resume at dawn. |
| Drowning | Breath runs out after ~7 s submerged. | Avoid long underwater digs. |

Max-life items: `LifeCrystal` (tile `Heart`, generated between y = (2*worldSurface + rockLayer)/3 and maxTilesY-300, i.e. mid-underground to cavern). Use = +20 max HP and heals 20, cap 15 crystals (100 -> 400). `ManaCrystal` (5 `FallenStar`, crafted by hand; stars fall at night): +20 max mana, cap 9 (20 -> 200).

## 5. Ordered plan, days 1-3

Each step lists a checkable completion test. Skip a step if its test already passes.

| # | Goal | Done when |
|---|---|---|
| 1 | Chop trees near spawn (`CopperAxe`) | inventory `Wood` >= 100 (target 200 before dusk) |
| 2 | Place a crafting base | tile `WorkBenches` placed within 10 tiles of spawn |
| 3 | Craft starter weapon | own `WoodenSword` (or skip if broadsword later) |
| 4 | Kill slimes for gel | `Gel` >= 10 |
| 5 | Torches | `Torch` >= 30 (10 gel + 10 wood) |
| 6 | House 1 (for yourself / Guide) by dusk | room passes `StartRoomCheck` and `RoomNeeds`; tiles `ClosedDoor`, `Tables`, `Chairs`, `Torches` inside |
| 7 | Night 1: mine down from inside the house or a sheltered shaft | `StoneBlock` >= 50 |
| 8 | Furnace | tile `Furnaces` placed next to `WorkBenches` |
| 9 | Tier-1 ore (Copper or Tin) | `CopperOre`/`TinOre` >= 45 (15 bars) |
| 10 | Tier-2 ore (Iron or Lead) | `IronOre`/`LeadOre` >= 45 |
| 11 | Smelt | `IronBar`/`LeadBar` >= 15 |
| 12 | Anvil | tile `Anvils` placed (`IronAnvil` or `LeadAnvil`) |
| 13 | Better pickaxe + weapon | own `IronPickaxe`/`LeadPickaxe` (or tier-1 pick if no tier-2 yet) and a broadsword of the best tier; plus `WoodenBow` + 100 `WoodenArrow` for flyers |
| 14 | Armor | full helmet+chainmail+greaves of the best tier (total defense >= 6) |
| 15 | Coins | inventory coin value >= 50 silver (break `Pots`, open surface/underground `Containers`) |
| 16 | Houses 2-4 (Merchant, Nurse, Demolitionist) | 3 additional rooms pass the room check; Merchant present (`NPC.AnyNPCs(NPCID.Merchant)`) |
| 17 | Storage | 2 `Chest` placed in base |
| 18 | Life crystals underground (below `Main.worldSurface`, mostly near/below `Main.rockLayer`) | `ConsumedLifeCrystals` >= 3 (max HP >= 160); Nurse moves in after the first |
| 19 | Potions | `Bottle` from `Glass` at `Furnaces`, place `Bottle` on the table, craft `LesserHealingPotion` >= 5 |
| 20 | Tier-3/4 ore in the cavern (`Silver`/`Tungsten`, `Gold`/`Platinum`) | `SilverBar`/`TungstenBar` >= 20, upgrade pickaxe and armor |
| 21 | Later: `Sawmill` (needs `Chain`), `Loom`, `Bed` (set spawn), grappling hook | tiles placed |

Rules of thumb: keep >= 20 `Torch`, >= 3 `LesserHealingPotion` and 30 free inventory slots before every underground trip; return to base if HP < 40% or night falls while on the surface; deposit ore/blocks in chests, but keep coins in inventory until the Merchant has moved in.

## 6. Layers (small world 4200 x 1200)

Y grows downward. Values come from `TerrainPass` and are randomized per world; read the real ones from `Main.worldSurface`, `Main.rockLayer`, `Main.UnderworldLayer` at runtime.

| Layer | Y range (small world, typical) | Contents |
|---|---|---|
| Space / sky | y < ~200 | floating islands, sky silver/gold veins |
| Surface | ~204-312 (terrain clamped to 17%-26% of maxTilesY) down to `Main.worldSurface` | dirt, grass, trees, slimes, pots; shallow `Copper`/`Tin` and `Iron`/`Lead` veins |
| `Main.worldSurface` | = deepest surface height + 25, typically ~ 280-340 | above = surface spawns and sky light |
| Underground | `Main.worldSurface` .. `Main.rockLayer` (rockLayer typically ~ 380-480, always worldSurface + a multiple of 6) | dirt-to-stone transition, copper/tin, iron/lead, silver/tungsten veins; life crystals in its lower part |
| Cavern | `Main.rockLayer` .. `Main.UnderworldLayer` | stone, all four tiers, gold/platinum only here (plus sky), most life crystals, chests, water then lava pools below ~ y 750-900 |
| Underworld | `Main.UnderworldLayer` = maxTilesY - 200 = 1000 .. 1200 | ash, lava, hellstone (65 power), demons. Do not enter early. |

Ore depth bands (generation): tier 1 and tier 2 appear surface, underground and cavern; tier 3 underground, cavern and sky; tier 4 only cavern (`GenVars.rockLayerLow` and below) and sky. Life crystals: y from (2*worldSurface + rockLayer)/3 to maxTilesY - 300.
