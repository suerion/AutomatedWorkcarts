## Features

- Automates workcarts with NPC conductors
- Uses configurable triggers to instruct conductors how to navigate
- Provides default triggers for underground tunnels
- Optional map markers for automated workcarts

## How it works

A workcart can be automated one of two ways:
- By running the `aw.toggle` command while looking at the workcart.
- By placing a trigger on the workcart spawn position, so that it will receive a conductor when it spawns.

Automating a workcart dismounts the current driver if present, and adds a conductor NPC.
- The conductor and workcart are invincible, and the workcart does not require fuel.
- If the workcart was automated using the `aw.toggle` command, the conductor will start driving based on the `DefaultSpeed` and `DefaultTrackSelection` in the plugin configuration.
- If the workcart was automated using a trigger, it will use the trigger options, falling back to the plugin configuration for anything not specified by the trigger.

The main purpose of triggers, aside from determining which workcarts will be automated, is to instruct conductors how to navigate the tracks. Each trigger has several properties, including direction (e., `Fwd`, `Rev`, `Invert`), speed (e.g, `Hi`, `Med`, `Lo`, `Zero`), track selection (e.g., `Default`, `Left`, `Right`), stop duration (in seconds), and departure speed/direction. When a workcart passes through a trigger while a conductor is present, the workcart's instructions will change based on the trigger options. For example, if a workcart is currently following the instructions `Fwd` + `Hi` + `Left`, and then passes through a trigger that specifies only track selection `Right`, the workcart instructions will change to `Fwd` + `Hi` + `Right`, causing the workcart to turn right at every intersection it comes to, until it passes through a trigger that instructs otherwise.

## Getting started

### Use case #1: Automate all underground workcarts

1. Set `EnableTunnelTriggers` -> `TrainStation` to `true` in the plugin configuration.
2. Reload the plugin.

All workcarts parked at their spawn locations will receive a conductor and start moving, automatically stopping briefly at stations to pick up passengers. From the map perspective, each workcart will move in a counter-clockwise circle around an adjacent loop, or in a clockwise circle around the outer edges of the map, depending on its spawn orientation and available nearby loops. This combination will cover most maps, allowing players to go almost anywhere by switching directions at various stops.

To see the triggers visually, grant the `automatedworkcarts.managetriggers` permission and run the `aw.showtriggers` command. For 60 seconds, this will show triggers at nearby tunnels. From here, you can add, update, move, and remove triggers to your liking. See the Commands section for how to manage triggers.

### Use case #2: Automate aboveground workcarts

1. Carefully examine the tracks on your map to determine the route(s) you would like workcarts to take.
2. Make sure `EnableMapTriggers` is set to `true` in the plugin configuration. This option is enabled by default if installing the plugin from scratch.
3. Grant yourself the `automatedworkcarts.managetriggers` permission.
4. Find a workcart spawn location where you would like workcarts to automatically receive conductors.
5. Aim at the track and run the command `aw.addtrigger Conductor Fwd Hi`. Any workcart that spawns on this trigger will automatically receive a conductor, and start driving forward at max speed.
6. Find a portion of track where you want automated workarts to stop briefly.
7. Aim at the track and run the command `aw.addtrigger Brake Zero 15 Hi`. Any workcart that passes through this trigger will brake until stopping, wait for 15 seconds, then start moving the same direction as `Hi` speed.
8. Keep adding/editing triggers and spawning workcarts to refine the routes.

## Permission

- `automatedworkcarts.toggle` -- Allows usage of the `aw.toggle` command.
- `automatedworkcarts.managetriggers` -- Allows viewing, adding, updating and removing triggers.

## Commands

- `aw.toggle` -- Toggles automation for the workcart you are looking at.
- `aw.addtrigger <option1> <option2> ...` -- Adds a trigger to the track position where you are aiming, with the specified options. Automated workcarts that pass through the trigger will be affected by the trigger's options.
  - Speed options: `Hi` | `Med` | `Lo` | `Zero`.
  - Direction options: `Fwd` | `Rev` | `Invert`.
  - Track selection options: `Default` | `Left` | `Right` | `Swap`.
  - Other options:
    - `Conductor` -- Adds a conductor to the workcart if not already present. Recommended to place on some or all workcart spawn locations, depending on how many workcarts you want to automate.
      - Note: Owned workcarts cannot receive conductors.
    - `Brake` -- Instructs the workcart to brake until it reaches the designated speed. For example, if the workcart is going `Fwd_Hi` and enters a `Brake Med` trigger, it will temporarily go `Rev_Lo` until it slows down enough, then it will go `Fwd Med`.
  - Simple examples:
    - `aw.addtrigger Lo` -- Causes the workcart to move at `Lo` speed in its current direction. Exmaple: `Fwd_Hi` -> `Fwd_Lo`.
    - `aw.addtrigger Fwd Lo` -- Causes the workcart to move forward at `Lo` speed, regardless of its current direction. Example: `Rev_Hi` -> `Fwd_Lo`.
    - `aw.addtrigger Invert` -- Causes the workcart to reverse direction at its current speed. Example: `Fwd_Med` -> `Rev_Med`.
    - `aw.addtrigger Invert Med` -- Causes the workcart to reverse direction at `Med` speed. Example: `Fwd_Hi` -> `Rev_Med`.
    - `aw.addtrigger Brake Med` -- Causes the workcart to brake until it reaches the `Med` speed. Examle: `Fwd_Hi` -> `Rev_Lo` -> `Fwd_Med`.
    - `aw.addtrigger Left` -- Causes the workcart to turn left at all future intersections.
  - Advanced examples:
    - `aw.addtrigger Conductor Fwd Hi Left` -- Causes the workcart to automatically receive a conductor, and to go forward at full speed, always turning left.
    - `aw.addtrigger Brake Zero 10 Hi` -- Causes the workcart to brake to a stop, wait 10 seconds, then go full speed in the same direction.
    - `aw.addtrigger Zero 20 Med` -- Causes the workcart to turn off its engine for 20 seconds, then go `Med` speed in the same direction.
- `aw.addtrunneltrigger <option1> <option2>` -- Adds a trigger to the track position where you are aiming.
  - Must be in a supported train tunnel (one enabled in plugin configuration).
  - This trigger will be replicated at all train tunnels of the same type. Editing or removing one of those triggers will affect them all.
- `aw.updatetrigger <id> <option1> <option2> ...` -- Updates the options of the specified trigger. Options are the same as for `aw.addtrigger`.
- `aw.replacetrigger <id> <option1> <option2> ...` -- Replaces all options on the specified trigger with the options specified. Options are the same as for `aw.addtrigger`. This is useful for removing options from a trigger since `aw.updatetrigger` does not allow that.
- `aw.movetrigger <id>` -- Moves the specified trigger to the track position where the player is aiming.
- `aw.removetrigger <id>` -- Removes the specified trigger.
- `aw.showtriggers <seconds>` -- Shows all nearby triggers to the player for specified duration. Defaults to 60 seconds.
  - This displays the trigger id, speed, direction, etc.
  - Triggers are also automatically shown for at least 60 seconds when using any of the other trigger commands.

Tip: For the commands that update, move or remove triggers, you can skip the `<id>` argument if you are aiming at a nearby trigger.

The following command aliases are also available:
- `aw.addtrigger` -> `awt.add`
- `aw.addtunneltrigger` -> `awt.addt`
- `aw.updatetrigger` -> `awt.update`
- `aw.replacetrigger` -> `awt.replace`
- `aw.movetrigger` -> `awt.move`
- `aw.removetrigger` -> `awt.remove`

## Configuration

Default configuration:

```json
{
  "DefaultSpeed": "Fwd_Hi",
  "DefaultTrackSelection": "Left",
  "BulldozeOffendingWorkcarts": false,
  "EnableMapTriggers": true,
  "EnableTunnelTriggers": {
    "TrainStation": false,
    "BarricadeTunnel": false,
    "LootTunnel": false,
    "Intersection": false,
    "LargeIntersection": false,
  },
  "MaxConductors": -1,
  "ConductorOutfit": [
    {
      "ShortName": "jumpsuit.suit",
      "Skin": 0
    },
    {
      "ShortName": "sunglasses03chrome",
      "Skin": 0
    },
    {
      "ShortName": "hat.boonie",
      "Skin": 0
    }
  ],
  "ColoredMapMarker": {
    "Enabled": false,
    "Color": "#0099ff",
    "Alpha": 1.0,
    "Radius": 0.05
  },
  "VendingMapMarker": {
    "Enabled": false,
    "Name": "Automated Workcart"
  }
}
```

- `DefaultSpeed` -- Default speed to use when a workcart starts being automated.
  - Allowed values: `"Rev_Hi"` | `"Rev_Med"` | `"Rev_Lo"` | `"Zero"` | `"Fwd_Lo"` | `"Fwd_Med"` | `"Fwd_Hi"`.
  - This value is ignored if the workcart is on a trigger that specifies speed.
- `DefaultTrackSelection` -- Default track selection to use when a workcart starts being automated.
  - Allowed values: `"Left"` | `"Default"` | `"Right"`.
  - This value is ignored if the workcart is on a trigger that specifies track selection.
- `BulldozeOffendingWorkcarts` (`true` or `false`) -- While `true`, automated workcarts will destroy other non-automated workcarts in their path that are not heading the same speed and direction.
  - Regardless of this setting, automated workcarts may destroy each other in head-on or perpendicular collisions.
- `EnableMapTriggers` (`true` or `false`) -- While `false`, existing map-specific triggers will be disabled, and no new map-specific triggers can be added.
- `EnableTunnelTriggers` -- While `false` for a particular tunnel type, existing triggers in those tunnels will be disabled, and no new triggers can be added to tunnels of that type.
  - `TrainStation` (`true` or `false`) -- Self-explanatory.
  - `BarricadeTunnel` (`true` or `false`) -- This affects straight tunnels that spawn NPCs, loot, as well as barricades on the tracks.
  - `LootTunnel` (`true` or `false`) -- This affects straight tunnels that spawn NPCs and loot.
  - `Intersection` (`true` or `false`) -- This affects 3-way intersections.
  - `LargeIntersection` (`true` or `false`) -- This affects 4-way intersections.
- `MaxConductors` -- The maximum number of conductors allowed on the map at once. Set to `-1` for no limit.
- `ConductorOutfit` -- Items to use for the outfit of each conductor.
- `ColoredMapMarker`
  - `Enabled` (`true` or `false`) -- Whether to enable colored map markers.
  - `Color` -- The marker color, using the hexadecimal format popularized by HTML.
  - `Alpha` (`0.0` - `1.0`) -- The marker transparency (`0.0` is invisible, `1.0` is fully opaque).
  - `Radius` -- The marker radius.
- `VendingMapMarker`
  - `Enabled` (`true` or `false`) -- Whether to enable vending machine map markers.
  - `Name` -- The name to display when hoving the mouse over the marker.

## FAQ

#### Will this plugin cause lag?

This plugin's logic is optimized for performance and should not cause lag. However, workcarts moving along the tracks, regardless of whether a player or NPC is driving them, does incur some overhead. Therefore, having many automated workcarts may reduce server FPS. You can limit the number of automated workcarts with the `MaxConductors` configuration option.

#### Is this compatible with the Cargo Train Event plugin?

Generally, yes. However, if all workcarts are automated, the Cargo Train Event will never start since it needs to select an idle workcart, so it's recommended to limit the number of automated workcarts using the `MaxConductors` configuration option, and/or by automating only specific workcarts based on their spawn location.

#### Is it safe to allow player workcarts and automated workcarts on the same tracks?

The best practice is to have separate, independent tracks for player vs automated workcarts. However, automated workcarts do have collision handling logic that makes them somewhat compatible with player tracks.

- When an automated workcart is rear-ended, if it's currently stopping or waiting at a stop, it will depart early.
- When an automated workcart collides with another workcart in front of it, its engine stops for a few seconds to allow the forward workcart to assert its will.
- When two automated workcarts collide head-on, the slower one (or a random one if they are going the same speed) will explode.
- When an automated workcart collides with a non-automated workcart other than a rear-end, and the workcart is not going the same direction or fast enough, having the `BulldozeOffendingWorkcarts` configuration option set to `true` will cause the non-automated workcart to be destroyed.

## Tips for map makers

- Design the map with automated routes in mind.
- Avoid dead-ends on the main routes.
  - While these can work, they may require additional effort to design automated routes, due to increased collision potential.
- Create parallel tracks throughout most of the map.
  - This allows workcarts to move in both directions, similar to the vanilla underground tracks.
- Create frequent alternate tracks.
  - Creating alternate tracks at intended stop points allows player-driven workcarts to easily pass automated workcarts at designated stops.
  - Creating alternate tracks elsewhere provides opportunities for player-driven workcarts to avoid automated workcarts for various reasons.
  - Be intentional about which track you set as the "default", since that is likely the one that players will use.
- Create completely independent tracks.
  - For example, an outer cricle and an inner circle.
  - This allows users of this plugin to selectively automate only specific areas, while allowing other areas to have player-driven workcarts, therefore avoiding interactions between the two.
- For underground tunnels, ensure each "loop" has at least two train stations (or other stops). This works best with the plugin's default triggers since it allows players to travel anywhere with automated workcarts by switching directions at various stops.
- If distributing your map, use this plugin to make default triggers for your customers, and distribute the json file with your map.
  - The file can be found in `oxide/data/AutomatedWorkcarts/MAP_NAME.json`.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoTriggers": "There are no workcart triggers on this map.",
  "Error.TriggerNotFound": "Error: Trigger id #<color=#fd4>{0}{1}</color> not found.",
  "Error.ErrorNoTrackFound": "Error: No track found nearby.",
  "Error.NoWorkcartFound": "Error: No workcart found.",
  "Error.AutomateBlocked": "Error: Another plugin blocked automating that workcart.",
  "Error.UnsupportedTunnel": "Error: Not a supported train tunnel.",
  "Error.TunnelTypeDisabled": "Error: Tunnel type <color=#fd4>{0}</color> is currently disabled.",
  "Error.MapTriggersDisabled": "Error: Map triggers are disabled.",
  "Error.MaxConductors": "Error: There are already <color=#fd4>{0}</color> out of <color=#fd4>{1}</color> conductors.",
  "Error.WorkcartOwned": "Error: That workcart has an owner.",
  "Error.NoAutomatedWorkcarts": "Error: There are no automated workcarts.",
  "Toggle.Success.On": "That workcart is now automated.",
  "Toggle.Success.Off": "That workcart is no longer automated.",
  "ShowTriggers.Success": "Showing all triggers for <color=#fd4>{0}</color>.",
  "AddTrigger.Syntax": "Syntax: <color=#fd4>{0} <option1> <option2> ...</color>\n{1}",
  "AddTrigger.Success": "Successfully added trigger #<color=#fd4>{0}{1}</color>.",
  "UpdateTrigger.Syntax": "Syntax: <color=#fd4>{0} <id> <option1> <option2> ...</color>\n{1}",
  "UpdateTrigger.Success": "Successfully updated trigger #<color=#fd4>{0}{1}</color>",
  "MoveTrigger.Success": "Successfully moved trigger #<color=#fd4>{0}{1}</color>",
  "RemoveTrigger.Syntax": "Syntax: <color=#fd4>{0} <id></color>",
  "RemoveTrigger.Success": "Trigger #<color=#fd4>{0}{1}</color> successfully removed.",
  "Info.ConductorCount.Limited": "Total conductors: <color=#fd4>{0}/{1}</color>.",
  "Info.ConductorCount.Unlimited": "Total conductors: <color=#fd4>{0}</color>.",
  "Help.SpeedOptions": "Speeds: {0}",
  "Help.DirectionOptions": "Directions: {0}",
  "Help.TrackSelectionOptions": "Track selection: {0}",
  "Help.OtherOptions": "Other options: <color=#fd4>Conductor</color> | <color=#fd4>Brake</color>",
  "Info.Trigger": "Workcart Trigger #{0}{1}",
  "Info.Trigger.Prefix.Map": "M",
  "Info.Trigger.Prefix.Tunnel": "T",
  "Info.Trigger.Map": "Map-specific",
  "Info.Trigger.Tunnel": "Tunnel type: {0} (x{1})",
  "Info.Trigger.Conductor": "Adds Conductor",
  "Info.Trigger.StopDuration": "Stop duration: {0}s",
  "Info.Trigger.Speed": "Speed: {0}",
  "Info.Trigger.BrakeToSpeed": "Brake to speed: {0}",
  "Info.Trigger.DepartureSpeed": "Departure speed: {0}",
  "Info.Trigger.Direction": "Direction: {0}",
  "Info.Trigger.DepartureDirection": "Departure direction: {0}",
  "Info.Trigger.TrackSelection": "Track selection: {0}"
}
```

## Developer API

#### API_IsWorkcartAutomated

```csharp
bool API_IsWorkcartAutomated(TrainEngine workcart)
```

Returns `true` if the given workcart is automated, else `false`.

#### API_GetAutomatedWorkcarts

```csharp
TrainEngine[] API_GetAutomatedWorkcarts()
```

Returns an array of all workcarts that are currently automated.

## Developer Hooks

#### OnWorkcartAutomate

```csharp
bool? OnWorkcartAutomate(TrainEngine workcart)
```

- Called when a workcart is about to be automated
- Returning `false` will prevent the workcart from being automated
- Returning `null` will result in the default behavior

#### OnWorkcartAutomated

```csharp
void OnWorkcartAutomated(TrainEngine workcart)
```

- Called after a workcart has been automated
- No return behavior
