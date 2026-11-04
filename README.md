# Wards Plus
This mod aims to replicate a couple effects that I've enjoyed from other mods, but singled out and put in one place. And since I don't know how to model, everything is based off the vanilla Ward to keep things simple!

## What does it do?
Put simply, this mod adds new, specialized varieties of Ward to the game. Instead of working as PvP rule zones and the like, these will automate certain actions within your base, like repair, refueling, etc. The most basic of these will be unlocked and craftable early in the game, while others will require some amount of progression through biomes and materials. 

| Ward Name       	| Prefab Name          	| Description                                                                                                                                                                                                                                                                                                      	| Crafting Cost                                       	| Default Range                   	|
|-----------------	|----------------------	|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------	|-----------------------------------------------------	|---------------------------------	|
| Repair Ward     	| `WP_Ward_Repair`     	| Consumes "fuel" items in it's inventory to repair structures in its range. When idle, checks every ~30 seconds for needed repairs, then becomes active, repairing 1 structure per second. <br> Each fuel item will repair 10 structures, e.g. 50 resin = 500 repairs <br> Default fuel list: Resin, GreydwarfEye 	| 10 CoreWood, 10 Stone, 50 Greydwarf Eyes            	| 20m                             	|
| Refuel Ward     	| `WP_Ward_Refuel`     	| Checks nearby structures every ~30 seconds and attempts to place fuel from its inventory into them. Useful for keeping your Sconces and Hearths fed. <br> Includes a simple UI to set a denylist of prefabs, in case you don't want it touching Charcoal Kilns or Campfires.                                     	| 10 FineWood, 10 Stone, 1 Surtling Core, 25 Coal     	| 20m                             	|
| Gathering Ward (TBD) 	| `WP_Ward_Gathering`  	| Picks up loose items in it's range and puts in its own inventory. Has a much lower max range by default, for performance and reliability. <br> Configurable to insert into nearby containers within a secondary radius, default off.                                                                                           	| 10 FineWood, 10 Stone, 1 Surtling Core, 25 Feathers 	| 10m (pickup) <br> 10m (deposit) 	|
| Spawnproof Ward 	| `WP_Ward_Spawnproof` 	| Spawnproofs a large area. Has controls to tune the spawnproof radius.                                                                                                                                                                                                                                            	| 10 AncientBark, 50 Stone, 1 Surtling Core           	| 10-60m                          	|
| Defender Ward   	| `WP_Ward_Defender`   	| Insert a trophy, and when enemies are detected nearby this ward will summon a friendly* copy to help defend your base! <br> \**summoned copy may still damage your base structures*                                                                                                                              	| 10 YggdrasilWood, 10 BlackMarble, 1 Black Core, 5 DragonTear      	| 20m (detection range)           	|

## Configuration
Settings live in `BepInEx/config/visteus.wardsplus.cfg`, and can also be changed in-game with [ConfigurationManager](https://thunderstore.io/c/valheim/p/Azumatt/Official_BepInEx_ConfigurationManager/) (F1).

Settings are **admin-only and synced from the server**, except when marked *(client)*.

### General
| Setting | Default | Description |
|---|---|---|
| Show Glow Orb | `false` | *(client)* Show the vanilla ward's spherical glow above active wards. When off, only the colored magic lines glow. Requires a reload. |
| Remember Panel Position | `false` | *(client)* Keep the ward settings panel where you last dragged it. When off, it always opens at its default spot, and turning this off clears the remembered position. Takes effect immediately. |

### Ward settings
Each ward has its own config section (`[Repair Ward]`, `[Refuel Ward]`, `[Spawnproof Ward]`, `[Defender Ward]`). The first rows of each table below are the settings every ward has; ranges are in meters.

### Repair Ward
| Setting | Default | Allowed | Description |
|---|---|---|---|
| Enabled | `true` | `true` / `false` | Add this ward to the hammer. Requires a restart. |
| Recipe | `RoundLog:10,Stone:10,GreydwarfEye:50` | `Item:Amount` pairs | Build cost (item prefab names), comma separated. Requires a restart. |
| Crafting Station | `piece_workbench` | Prefab name | Station required nearby to build it (e.g. `piece_workbench`, `forge`, `blackforge`). Empty for none. Requires a restart. |
| Default Range | `20` | 1–200 | Range a newly placed ward starts with. |
| Min Range | `5` | 1–200 | Smallest range players can set in the ward's panel. |
| Max Range | `40` | 1–200 | Largest range players can set in the ward's panel. |
| Glow Color | `40FF59FF` (green) | RGBA hex | *(client)* Color of the ward's magic lines, glow and icon. Requires a restart. |
| Fuel | `Resin:10,GreydwarfEye:10` | `Item:Repairs` pairs | Items the ward burns as fuel (item prefab names), in the order they are used. Each item gives that many repairs, and one repair fully heals one piece. |
| Scan Interval | `30` | 5–600 | Seconds between checks for damaged pieces while nothing needs repairing. Varies by ±20% so wards don't sync up. |
| Repair Interval | `1` | 0.25–30 | Seconds between repairs while damaged pieces are in range. |
| Default Repair Below | `100` | 10–100 | Health % a newly placed ward starts with for "Repair below". Pieces are only repaired when their health is below it. 100 repairs any damage; lower values save fuel on wear from rain. |
| Always Repair | `piece_sharpstakes,piece_dvergr_sharpstakes` | Prefab names | Pieces repaired at any damage, ignoring each ward's "Repair below" setting. Comma separated. |

### Refuel Ward
| Setting | Default | Allowed | Description |
|---|---|---|---|
| Enabled | `true` | `true` / `false` | Add this ward to the hammer. Requires a restart. |
| Recipe | `FineWood:10,Stone:10,Coal:25,SurtlingCore:1` | `Item:Amount` pairs | Build cost (item prefab names), comma separated. Requires a restart. |
| Crafting Station | `piece_workbench` | Prefab name | Station required nearby to build it (e.g. `piece_workbench`, `forge`, `blackforge`). Empty for none. Requires a restart. |
| Default Range | `20` | 1–200 | Range a newly placed ward starts with. |
| Min Range | `5` | 1–200 | Smallest range players can set in the ward's panel. |
| Max Range | `40` | 1–200 | Largest range players can set in the ward's panel. |
| Glow Color | `FF801AFF` (orange) | RGBA hex | *(client)* Color of the ward's magic lines, glow and icon. Requires a restart. |
| Scan Interval | `30` | 5–600 | Seconds between top-ups of the fires and smelters in range. Varies by ±20% so wards don't sync up. |
| Default Denied | *(empty)* | Prefab names | Pieces a newly placed ward starts with turned off (e.g. `piece_bathtub,smelter`). Players can change this per ward in its panel. Comma separated. |
| Input Stations | `charcoal_kiln` | Prefab names | Stations with no fuel slot whose input the ward loads instead, like the charcoal kiln's wood. Fires and smelter fuel are always handled; smelter ore never is. Comma separated. |

### Spawnproof Ward
| Setting | Default | Allowed | Description |
|---|---|---|---|
| Enabled | `true` | `true` / `false` | Add this ward to the hammer. Requires a restart. |
| Recipe | `ElderBark:10,Stone:50,SurtlingCore:1` | `Item:Amount` pairs | Build cost (item prefab names), comma separated. Requires a restart. |
| Crafting Station | `piece_workbench` | Prefab name | Station required nearby to build it (e.g. `piece_workbench`, `forge`, `blackforge`). Empty for none. Requires a restart. |
| Default Range | `30` | 1–200 | Range a newly placed ward starts with. |
| Min Range | `10` | 1–200 | Smallest range players can set in the ward's panel. |
| Max Range | `60` | 1–200 | Largest range players can set in the ward's panel. |
| Glow Color | `3399FFFF` (blue) | RGBA hex | *(client)* Color of the ward's magic lines, glow and icon. Requires a restart. |

### Defender Ward
| Setting | Default | Allowed | Description |
|---|---|---|---|
| Enabled | `true` | `true` / `false` | Add this ward to the hammer. Requires a restart. |
| Recipe | `YggdrasilWood:10,BlackMarble:10,BlackCore:1,DragonTear:5` | `Item:Amount` pairs | Build cost (item prefab names), comma separated. Requires a restart. |
| Crafting Station | `forge` | Prefab name | Station required nearby to build it (e.g. `piece_workbench`, `forge`, `blackforge`). Empty for none. Requires a restart. |
| Default Range | `20` | 1–200 | Detection range a newly placed ward starts with. |
| Min Range | `5` | 1–200 | Smallest range players can set in the ward's panel. |
| Max Range | `20` | 1–200 | Largest range players can set in the ward's panel. |
| Glow Color | `B24CFFFF` (purple) | RGBA hex | *(client)* Color of the ward's magic lines, glow and icon. Requires a restart. |
| Scan Interval | `2` | 1–30 | Seconds between checks for enemies in range. Varies by ±20% so wards don't sync up. |
| Summon Level | `1` | 1–3 | Level of the summoned defender: 1 is a normal creature, 2 is one star, 3 is two stars. |
| Summon Cooldown | `30` | 0–600 | Minimum seconds between two summons from the same ward, so a defender that dies isn't replaced instantly. |
| Idle Despawn | `10` | 3–120 | Seconds a defender stays once it has nothing left to fight. |
| Denied Creatures | *(empty)* | Prefab names | Creatures that can never be summoned (e.g. `Wolf,Lox`). Bosses and water-only creatures never are. Comma separated. |
| Trophy Overrides | *(empty)* | `Trophy:Creature` pairs | Makes a trophy summon that creature by default (e.g. `TrophyDraugr:Draugr_Ranged`). Also lets a trophy summon a creature that doesn't drop it. Comma separated. |
