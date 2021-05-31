## Features

- Automates workcarts with NPC conductors
- Uses configurable triggers to instruct conductors how to navigate
- Provides default triggers for underground tunnels

## How it works

A workcart can be automated one of two ways:
- By running the `aw.toggle` command while looking at the workcart.
- By placing a trigger on the workcart spawn position, so that it will be automated when it spawns.

Automating a workcart dismounts the current driver if present, and adds a conductor NPC.
- The conductor and workcart are invincible.
- If the workcart was automated using the `aw.toggle` command, the conductor will start driving based on the `DefaultSpeed` and `DefaultTrackSelection` in the plugin configuration.
- If the workcart was automated using a trigger, it will use the trigger options, falling back to the plugin configuration for anything not specified by the trigger.

The main purpose of triggers, aside from determining which workcarts will be automated, is to instruct conductors how to navigate the tracks. Each trigger has several properties, including direction (e., `Fwd`, `Rev`), speed (e.g, `Hi`, `Med`, `Lo`, `Zero`), track selection (e.g., `Default`, `Left`, `Right`), and stop duration (in seconds). When a workcart passes through a trigger while a conductor is present, the workcart's instructions will change based on the trigger options. For example, if a workcart is currently following the instructions `Fwd` + `Hi` + `Left`, and then passes through a trigger that specifies only track selection `Right`, the workcart instructions will change to `Fwd` + `Hi` + `Right`, causing the workcart to turn right at every intersection it comes to, until it passes through a trigger that instructs otherwise.

## Getting started

### Use case #1: Automate all underground workcarts

1. Set `EnableTunnelTriggers` -> `TrainStation` to `true` in the plugin configuration.
2. Reload the plugin.

All workcarts parked at their spawn locations will receive a conductor and start moving, automatically stopping briefly at stations to pick up passengers before moving one. From the map perspective, each workcart will move in a counter-clockwise circle around an adjacent loop, or in a clockwise circle around the outer edges of the map, depending on its spawn orientation and available nearby loops. This combination will cover most maps, allowing players to go almost anywhere by switching directions at various stops.

To see the triggers visually, grant the `automatedworkcarts.managetriggers` permission and run the `aw.showtriggers` command. For 60 seconds, this will show triggers at nearby tunnels. From here, you can add, update, move, and remove triggers to your liking. See the Commands section for how details on managing triggers.

### Use case #2: Automate aboveground workcarts

1. Carefully examine the tracks on your map to determine the route(s) you would like workcarts to take.
2. Make sure `EnableMapTriggers` is set to `true` in the plugin configuration. This option is enabled by default if installing the plugin from scratch.
3. Grant yourself the `automatedworkcarts.managetriggers` permission.
4. Find a workcart spawn location where you would like workcarts to automatically receive conductors.
5. Aim at the track and run the command `aw.addtrigger Start Fwd Hi`. Any workcart that spawns on this trigger will automatically receive a conductor, and start driving forward at max speed.
6. Find a portion of track where you want automated workarts to stop briefly.
7. Aim at the track and run the command `aw.addtrigger Zero 30`. Any workcart that passes through this trigger will shut off its engine for 30 seconds, before changing to the `DepartureSpeed` from the plugin configuration. You may want to move this trigger back, considering that the workcart may cover a significant distance while rolling to a stop.
8. To make the workcart slow down more quickly, aim at a point farther back on the track (somewhere before the workcart would reach the `Zero` trigger) and run the command `aw.addtrigger Invert Lo`. This trigger will cause the workcart to reverse its speed (brake) until it reaches the `Zero` trigger. Be careful where you place this, or the workcart may come to a stop and start going backward before reaching the next trigger. One reason for using the `Invert` direction instead of `Rev` is, just in case the workcart does go backward, when it reaches the original `Invert` trigger, it will start moving forward again.
9. Keep adding/editing triggers and spawning workcarts to refine the routes.

## Permission

- `automatedworkcarts.toggle` -- Allows usage of the `aw.toggle` command.
- `automatedworkcarts.managetriggers` -- Allows adding and removing triggers.

## Commands

- `aw.toggle` -- Toggles automation for the workcart you are standing on or looking at.
- `aw.addtrigger <option1> <option2> ...` -- Adds a trigger to the track position where the player is aiming, with the specified options. Automated workcarts that pass through the trigger will be affected by the trigger's options.
  - Speed options: `Hi` | `Med` | `Lo` | `Zero`.
  - Direction options: `Fwd` | `Rev` | `Invert`.
  - Track selection options: `Default` | `Left` | `Right` | `Swap`.
  - Passing the `Start` option will enable automation for any workcart that enters the trigger. The recommendation is to place this on specific workcart spawn points.
  - Examples:
    - `aw.addtrigger` -- Creates a trigger with speed `Zero`. This causes the workcart to turn its engine off 30 seconds, or the specified duration.
    - `aw.addtrigger Rev Hi` -- Creates a trigger that will cause workcarts to select max speed.
    - `aw.addtrigger Rev Hi` -- Creates a trigger that will cause workcarts to turn left at every intersection until another trigger changes the selection.
    - `aw.addtrigger Start Rev Hi Left` -- Creates a trigger with max forward speed and left track selection, which automatically enables automation for any workcart that enters it.
- `aw.addtrunneltrigger <option1> <option2>` -- Adds a trigger to the track position where the player is aiming.
  - Must be in a supported train tunnel (one enabled in plugin configuration).
  - This trigger will be replicated at all train tunnels of the same type.
  - Moving, updating or removing the trigger will be reflected at all train tunnels of the same type.
- `aw.updatetrigger <id> <option1> <option2> ...` -- Updates the options of the specified trigger. Options are the same as for `aw.addtrigger`.
- `aw.replacetrigger <id> <option1> <option2> ...` -- Replaces all options on the specified trigger with the options specified, without moving the trigger. Options are the same as for `aw.addtrigger`. This is useful for removing options from a trigger which `aw.updatetrigger` does not allow.
- `aw.movetrigger <id>` -- Moves the specified trigger to the track position where the player is aiming.
- `aw.removetrigger <id>` -- Removes the specified trigger.
- `aw.showtriggers` -- Shows all triggers, including id, speed and track selection. This lasts 60 seconds, but is extended any time you add, update, move or remove a trigger.
  - You must be an admin to see the triggers.

The following command aliases are also available:
- `aw.addtrigger` -> `awt.add`
- `aw.addtunneltrigger` -> `awt.addt`
- `aw.updatetrigger` -> `awt.update`
- `aw.replacetrigger` -> `awt.replace`
- `aw.movetrigger` -> `awt.move`
- `aw.removetrigger` -> `awt.remove`

## Configuration

```json
{
  "DefaultSpeed": "Fwd_Hi",
  "DepartureSpeed": "Fwd_Med",
  "DefaultTrackSelection": "Left",
  "DestroyOffendingWorkcarts": false,
  "EnableMapTriggers": true,
  "EnableTunnelTriggers": {
    "TrainStation": false,
    "BarricadeTunnel": false,
    "LootTunnel": false
  }
}
```

- `DefaultSpeed` -- Default speed to use when a workcart starts being automated.
  - Allowed values: `"Rev_Hi"` | `"Rev_Med"` | `"Rev_Lo"` | `"Zero"` | `"Fwd_Lo"` | `"Fwd_Med"` | `"Fwd_Hi"`.
  - This value is ignored if the workcart is on a trigger that specifies speed.
- `DepartureSpeed` -- Speed to use when departing from a stop.
- `DefaultTrackSelection` -- Default track selection to use when a workcart starts being automated.
  - Allowed values: `"Left"` | `"Default"` | `"Right"`.
  - This value is ignored if the workcart is on a trigger that specifies track selection.
- `BulldozeOffendingWorkcarts` (`true` or `false`) -- While `true`, automated workcarts will destroy other non-automated workcarts in their path that are not heading the same speed and direction.
  - Regardless of this setting, automated workcarts may destroy each other in head-on or perpendicular collisions.
- `EnableMapTriggers` (`true` or `false`) -- Whether map-specific triggers are enabled. While `false`, existing map-specific triggers will be disabled, and no new map-specific triggers can be added.
- `EnableTunnelTriggers` -- Whether triggers are enabled for the corresponding type of train tunnel. While `false` for a particular tunnel type, existing triggers will be disabled, and no new triggers can be added to tunnels of that type.
  - `TrainStation` (`true` or `false`) -- Self-explanatory.
  - `BarricadeTunnel` (`true` or `false`) -- Straight tunnels that spawn NPCs, loot, as well as barricades on the tracks.
  - `LootTunnel` (`true` or `false`) -- Straight tunnels that spawn NPCs and loot.

## FAQ

#### Will this plugin cause lag?

This plugin's logic is optimized for performance and should not cause lag. However, workcarts moving along the tracks, regardless of whether a player or NPC is driving them, does incur some overhead. Therefore, having many automated workcarts may reduce server FPS.

#### Is this compatible with the Cargo Train Event plugin?

Generally, yes. However, if all workcarts are automated, the Cargo Train Event will never start since it needs to select an idle workcart.

#### Is it safe to allow player workcarts and automated workcarts on the same tracks?

The best practice is to have separate, independent tracks for player vs automated workcarts. However, automated workcarts do have collision handling logic that makes them somewhat compatible with tracks that are not designed to completely avoid mixed interactions.

- When an automated workcart is rear-ended, if it's currently at a stop, it will depart early.
- When an automated workcart collides with another workcart in front of it, its engine stops for a few seconds to allow the forward workcart to assert its will.
- When two automated workcarts head-on collide, the slower one (or a random one if they are going the same speed) will explode.
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
- If distributing your map, use this plugin to make default triggers for your consumers, and distribute the json file with your map.
  - The file can be found in `oxide/data/AutomatedWorkcarts/MAP_NAME.json`.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoTriggers": "There are no workcart triggers on this map.",
  "Error.TriggerNotFound": "Error: Trigger id #{0}{1} not found.",
  "Error.ErrorNoTrackFound": "Error: No track found nearby.",
  "Error.NoWorkcartFound": "Error: No workcart found.",
  "Error.AutomateBlocked": "Error: Another plugin blocked automating that workcart.",
  "Error.UnsupportedTunnel": "Error: Not a supported train tunnel.",
  "Error.TunnelTypeDisabled": "Error: Tunnel type <color=#fd4>{0}</color> is currently disabled.",
  "Toggle.Success.On": "That workcart is now automated.",
  "Toggle.Success.Off": "That workcart is no longer automated.",
  "AddTrigger.Syntax": "Syntax: <color=#fd4>{0} <option1> <option2> ...</color>\n{1}",
  "AddTrigger.Success": "Successfully added trigger #{0}{1}.",
  "UpdateTrigger.Syntax": "Syntax: <color=#fd4>{0} <id> <option1> <option2> ...</color>\n{1}",
  "UpdateTrigger.Success": "Successfully updated trigger #{0}{1}",
  "MoveTrigger.Success": "Successfully moved trigger #{0}{1}",
  "RemoveTrigger.Syntax": "Syntax: <color=#fd4>{0} <id></color>",
  "RemoveTrigger.Success": "Trigger #{0}{1} successfully removed.",
  "Help.SpeedOptions": "Speeds: {0}",
  "Help.DirectionOptions": "Directions: {0}",
  "Help.TrackSelectionOptions": "Track selection: {0}",
  "Help.OtherOptions": "Other options: <color=#fd4>Start</color>",
  "Info.Trigger": "Workcart Trigger #{0}{1}",
  "Info.Trigger.Prefix.Map": "M",
  "Info.Trigger.Prefix.Tunnel": "T",
  "Info.Trigger.Map": "Map-specific",
  "Info.Trigger.Tunnel": "Tunnel type: {0} (x{1})",
  "Info.Trigger.Start": "Starts automation",
  "Info.Trigger.StopDuration": "Stop duration: {0}s",
  "Info.Trigger.Speed": "Speed: {0}",
  "Info.Trigger.Direction": "Direction: {0}",
  "Info.Trigger.TrackSelection": "Track selection: {0}"
}
```

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
