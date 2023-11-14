# DangerousTreasures

Oxide plugin for Rust. Dangerous event with treasure chests.

**Dangerous Treasures** is an event that occurs once every one to two hours. The event spawns a box at a random position on the map away from all player obstructions and water.

The event opens with a barrage of rockets that blast the chest's location. A sphere surrounds the treasure chest, and a fire aura is activated which sets players on fire who enter it. The chest is locked for a random period of time to give players an opportunity to travel to it.

A server broadcast is sent informing players of the event, it's location, and to use '/dtd' to draw on their screen where the chest is located (usable once every 15 seconds).

Players cannot build inside of the sphere, damage the chest, nor loot it until the event starts.

When the event starts the fire aura and sphere are obliterated and the chest becomes lootable. Once the contents are stolen the thief is announced and the event ends.

Treasure chests contain **6** random items of high-quality loot found in supply drops by default.

If **Zone Manager** is installed, this plugin will not open events inside of zones created by ZoneManager. This plugin creates its own zones and does not use ZoneManager for any other reason.

## Permissions

- `dangeroustreasures.use` -- Allows a user to open a new event. Admins do not need this permission. It's not advised to give it to players unless you trust they won't abuse it.
- `dangeroustreasures.th` -- Awarded when the map wipes. The plugin gives this out automatically so you don't need to.
- `dangeroustreasures.notitle` permission to exempt users from ranked titles

## Chat Commands

- `/dtevent` -- Open an event at a random position on the map
- `/dtevent tp` -- Open an event at a random position on the map and teleport to it *(admin only)*
- `/dtevent me` -- Spawn an event at the position you are looking at
- `/dtevent custom` -- Remove or set a custom spawn location for all events to spawn at
- `/dtevent help` -- Basic help information, and display a list of monuments for use with blacklisting
- `/dtd` -- View status of the current or next event
- `/dtd tp` -- Teleport an admin to the nearest treasure chest
- `/dtd ladder` -- View treasure hunter ladder for the current wipe
- `/dtd ladder resetme` -- Reset your score for the current wipe
- `/dtd lifetime` -- View treasure hunter lifetime scores
- `/dtd additem` -- Modify Treasure -> Loot config in-game. This will overwrite entries in the config with the same item name, but will not update any active treasure chests. e.g. `/dtd additem rifle.ak 1 10135`

## Console Commands

- `dtevent` -- Open an event at a random position on the map

## Configuration

**Event Messages** - Allow or block messages sent to players by the plugin

- Show Barrage Message (default: enabled)
- Show Opened Message (default: enabled)
- Show Prefix (default: enabled)
- Show Started Message (default: enabled)

**Events** - Control how events function

- Amount Of Items To Spawn (default: 6)
- Amount Of Spheres (default: 5) (Increase this number to make the sphere darker)
- Auto Draw Minimum Distance (default: 300)
- Auto Draw On New Event For Nearby Players (default: disabled)
- Automated (default: enabled)
- Every Max Seconds (default: 7200 seconds)
- Every Min Seconds (default: 3600 seconds)
- Fire Aura Radius (Advanced Users Only) (min: 10.0, max: 150.0, default: 25.0)
- Grant DDRAW temporarily to players (default: enabled)
- Grant Draw Time (default: 15 seconds)
- Max Manual Events (default: 1)
- Player Limit For Event (default: 0)
- Time To Loot (default: 900 seconds)
- Use Spheres (default: enabled)

**Fireballs** - Control settings for all fireball functionality. Increase these settings to make the fire aura more deadly.

- Damage Per Second (Tick) (default: 10 health)
- Enabled (default: true)
- Generation (default: 5)
- Lifetime Max (default: 10 seconds)
- Lifetime Min (default: 7.5 seconds)
- Radius (default: 1 meter)
- Spawn Every X Seconds: (default: 5)
- Tick Rate (default: every 1 second)
- Water To Extinguish (default: 25)

**Lusty Map** support:

- Enabled (default: true)
- Icon File (default: <http://i.imgur.com/XoEMTJj.png>) - If you use a custom icon file name then it must be in the oxide/data/LustyMap/custom/ folder, or be a link to a valid file/website address.
- Icon Name (default: dtchest) - A unique identifier is appended to this name to separate one event from another.
- Icon Rotation (default: 0.0)

**Economics** and **Server Rewards** support under Rewards:

- Use ServerRewards (default: disabled)
- ServerReward Points: 20
- Use Economics (default: disabled)
- Economics Money: $20

**Missile Launcher** - Controls functionality for launching missiles at random players too close to the event as a warning mechanism and roleplay aspect of the plugin. Missiles do not do any damage.

- Acquire Time In Seconds (default: 10)
- Enabled (default: true)
- Ignore Flying Players (default: false)
- Life Time In Seconds (default: 60)
- Radar Distance (default: 15 meters)
- Spawn Every X Seconds (default: 15)
- Target Chest If No Player Target (default: disabled)

**Newman Mode** - Provides a roleplay aspect to the event where players without harmful items are protected by the fire's aura.

- Protect Nakeds From Fire Aura (default: false)
- Protect Nakeds From Other Harm (default: false)

**Ranked Ladder** - Track the number of treasure chests that each player has stolen. Award the top 3 thieves with the given treasure hunter group and permission names. This adds support for plugins which give titles based on groups or permissions. Awards are reset each server wipe.

- Award Top X Players On Wipe (default: 3)
- Enabled (default: true)
- Group Name: treasurehunter
- Permission Name: dangeroustreasures.th

**Rocket Opener** - Provides functionality for the number of rockets used when an event is opened, their speed, and whether or not to use fire rockets.

- Enabled (default: true)
- Rockets (default: 10)
- Speed (default: 25)
- Use Fire Rockets (default: disabled)

**Settings** - provides support for renaming event commands and the permission used for players to manually open events.

- Distance Chat Command: dtd
- Event Chat Command: dtevent
- Event Console Command: dtevent
- Permission Name: dangeroustreasures.use
- Allow PVP with TruePVE - Allow PVP on TruePVE servers inside of event zones

**Monuments** - allows events to spawn at monuments

- Auto Spawn At Monuments Only (default: diabled): Events automatically started will only spawn at monuments
- Chance To Spawn At Monuments Instead (default: 0, range 0.0 - 1.0): Events have a chance to spawn at monuments instead
- Allow Treasure Loot Underground (default: false): Allow events start underground (needs improved)
- Blacklisted (default: Outpost) - A list of monuments that events may not start at.

**NPCs** - allows npcs to spawn at events

- Enabled (default: false)
- Amount To Spawn (default: 2)
- Spawn Murderers And Scientists (default: false) - randomly spawn murderers and scientists
- Spawn Murderers (default: false) - only spawn murderers (does not override the above setting)
- Despawn Inventory On Death (default: true)
- Spawn Random Amount (default: false) - spawn a random amount of NPCs based on Amount To Spawn setting
- Blacklisted (default: Bandit Camp, Outpost) - A list of monuments that NPCs may not spawn at.

**Skins** - Provides support to skin the treasure chest. Randomly select skins, including workshop skins, or use a preset skin instead.

- Include Workship Skins (default: enabled)
- Preset Skin (default: 0 (disabled))
- Use Random Skin (default: enabled)

**Unlock Time** - The time in seconds that an event should start after it has been opened. This allows players time to travel to the event.

- Max Seconds (default: 480)
- Min Seconds (default: 300)
- Require All Npcs Die Before Unlocking : false
- Unlock When Npcs Die :  true

**Countdown** - Provides a countdown notification to players at the configured seconds which announces the time before the treasure chest becomes unlocked.

- Default Values: 120, 60, 30, and 15 seconds
- Disabled by default

**Unlooted Announcements** - Provides an announcement every X minutes to warn players that the treasure chest will be destroyed if not looted within the 'Time To Loot' setting.

- Enabled (default: false)
- Notify Every X Minutes (Minimum 1) (default: every 3 minutes)

**Treasure** - Includes a list of the possible items to spawn inside of the chest (see config). Includes settings to increase the amount of loot given by a percentage amount on specific days. Includes a setting to randomize skins (including workship skins) on spawned items. Items which SKIN value is not 0 will not be randomized.

- Include Workshop Skins (default: disabled)
- Use Random Skins (default: disabled)
- Percent Increase When Using Day Of Week Loot (default: disabled)
- Percent Increase On Monday (default: 0.0) - Increase the amount of loot by the given percentage on this specific day only. Use 25.0 for a 25% increase.
- Percent Increase On Tuesday (default: 0.0)
- Percent Increase On Wednesday (default: 0.0)
- Percent Increase On Thursday (default: 0.0)
- Percent Increase On Friday (default: 0.0)
- Percent Increase On Saturday (default: 0.0)
- Percent Increase On Sunday (default: 0.0)
- Minimum Percent Loss (default: 0.0) - Set above 0% to randomize the amount given from items. e.g. Use 25.0 to give between 75 and 100 where 100 is the maximum amount. (This is a typo and should read Maximum Percent Loss)

**Treasure Loot** - Includes tables for loot on specific days, and a table for the default loot which will be used if said specific day is not configured.

- Day Of Week Loot Monday (default: not configured)
- Day Of Week Loot Tuesday (default: not configured)
- Day Of Week Loot Wednesday (default: not configured)
- Day Of Week Loot Thursday (default: not configured)
- Day Of Week Loot Friday (default: not configured)
- Day Of Week Loot Saturday (default: not configured)
- Day Of Week Loot Sunday (default: not configured)
- Loot (default: see loot table below)

### Default Configuration

```json
"Loot": [
      {
        "shortname": "ammo.pistol",
        "amount": 40,
        "skin": 0,
        "amountMin": 40
      },
      {
        "shortname": "ammo.pistol.fire",
        "amount": 40,
        "skin": 0,
        "amountMin": 40
      },
      {
        "shortname": "ammo.pistol.hv",
        "amount": 40,
        "skin": 0,
        "amountMin": 40
      },
      {
        "shortname": "ammo.rifle",
        "amount": 60,
        "skin": 0,
        "amountMin": 60
      },
      {
        "shortname": "ammo.rifle.explosive",
        "amount": 60,
        "skin": 0,
        "amountMin": 60
      },
      {
        "shortname": "ammo.rifle.hv",
        "amount": 60,
        "skin": 0,
        "amountMin": 60
      },
      {
        "shortname": "ammo.rifle.incendiary",
        "amount": 60,
        "skin": 0,
        "amountMin": 60
      },
      {
        "shortname": "ammo.shotgun",
        "amount": 24,
        "skin": 0,
        "amountMin": 24
      },
      {
        "shortname": "ammo.shotgun.slug",
        "amount": 40,
        "skin": 0,
        "amountMin": 40
      },
      {
        "shortname": "survey.charge",
        "amount": 20,
        "skin": 0,
        "amountMin": 20
      },
      {
        "shortname": "metal.refined",
        "amount": 150,
        "skin": 0,
        "amountMin": 150
      },
      {
        "shortname": "bucket.helmet",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "cctv.camera",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "coffeecan.helmet",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "explosive.timed",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "metal.facemask",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "metal.plate.torso",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "mining.quarry",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "pistol.m92",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "rifle.ak",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "rifle.bolt",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "rifle.lr300",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "smg.2",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "smg.mp5",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "smg.thomspon",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "supply.signal",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      },
      {
        "shortname": "targeting.computer",
        "amount": 1,
        "skin": 0,
        "amountMin": 1
      }
    ],

```

## Localization

## Plugin Developers

### OnDangerousOpen (temporarily removed)

```csharp
OnDangerousOpen(Vector3 eventPos) : returning a non-null value will cause the plugin to select another event position
```
