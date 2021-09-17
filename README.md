## Features

- Automates workcarts with NPC conductors
- Uses configurable triggers to instruct conductors how to navigate
- Provides default triggers for underground tunnels
- Optional map markers for automated workcarts

**Note: This plugin is early access. Features are subject to change. Please provide feedback in the plugin Help section to help guide development.**

## How it works

A workcart can be automated one of two ways:
- By running the `aw.toggle` command while looking at the workcart.
- By placing a trigger on the workcart spawn position, so that it will receive a conductor when it spawns.

Automating a workcart dismounts the current driver if present, and adds a conductor NPC.
- The conductor and workcart are invincible, and the workcart does not require fuel.
- If the workcart was automated using the `aw.toggle` command, the conductor will start driving based on the `DefaultSpeed` and `DefaultTrackSelection` in the plugin configuration.
- If the workcart was automated using a trigger, it will use the trigger options, falling back to the plugin configuration for anything not specified by the trigger.

The main purpose of triggers, aside from determining which workcarts will be automated, is to instruct conductors how to navigate the tracks. Each trigger has several properties, including direction (e., `Fwd`, `Rev`, `Invert`), speed (e.g, `Hi`, `Med`, `Lo`, `Zero`), track selection (e.g., `Default`, `Left`, `Right`), stop duration (in seconds), and departure speed/direction. When a workcart passes through a trigger while a conductor is present, the workcart's instructions will change based on the trigger properties. For example, if a workcart is currently following the instructions `Fwd` + `Hi` + `Left`, and then passes through a trigger that specifies only track selection `Right`, the workcart instructions will change to `Fwd` + `Hi` + `Right`, causing the workcart to turn right at every intersection it comes to, until it passes through a trigger that instructs otherwise.

### Trigger types

#### Map specific triggers

- Can be placed anywhere on train tracks, even in underground tunnels.
- Only apply to the map they were placed on.
- Enabled via the `EnableMapTriggers` configuration option.
- Added with the `aw.addtrigger` or `awt.add` command.
- Saved in data file: `oxide/data/AutomatedWorkcarts/MAP_NAME.json`.

#### Tunnel triggers

- Can be placed only in the vanilla train tunnels (usually underground).
- Automatically replicate at all tunnels of the same type.
- Enabled via the `EnableTunnelTriggers` -> `*` options.
- Added with the `aw.addtunneltrigger` or `awt.addt` command.
- Saved in data file `oxide/data/AutomatedWorkcarts/TunnelTriggers.json`.

### Routes (advanced)

Routes are an advanced feature intended for complex aboveground tracks. Using routes allow workcarts to respond to only select triggers by ignoring triggers designated for other routes. This is useful for situations where multiple workcarts need to pass through shared tracks but exit those tracks in different directions.

#### Do I need routes?

Probably not. The routes feature allows solving some uses cases that simply aren't possible using only global triggers. However, many use cases that you might think you need routes for are actually achievable using global triggers.

Here are some example ways you can avoid needing to use the routes feature.

- Design the map to reduce track sharing to a minimum, and simplify shared track sections to remove unnecessary branches where you don't want automated workcarts to go.
- Place track selection triggers before shared track sections. These triggers should assign different track selections to workcarts coming from different source tracks. As long as the shared track sections do not require the workcarts to change tracks, this allows the workcarts to exit the shared tracks in different directions.
- Place track selection triggers with the `Swap` instruction to designate that a workcart should flip its track selection between `Left` and `Right`.

#### How to assign routes

A workcart can only be assigned a route when it receives a conductor. This can be done one of two ways.

- By running the `aw.toggle @<route_name>` command while aiming at the workcart. When this command adds a conductor, the workcart will be assigned the specified route.
- When a workcart receives a conductor via a trigger, if the trigger also has an assigned route, the workcart will be assigned that route.

#### How workcarts respond to triggers when using routes

- Triggers **with** an assigned route will affect **only** workcarts assigned the same route.
- Triggers **without** an assigned route will affect **all** workcarts.

## Getting started

### How to: Automate all underground workcarts

1. Set `EnableTunnelTriggers` -> `TrainStation` to `true` in the plugin configuration.
2. Reload the plugin.

All workcarts parked at their spawn locations will receive a conductor and start moving, automatically stopping briefly at stations to pick up passengers. From the map perspective, each workcart will move in a counter-clockwise circle around an adjacent loop, or in a clockwise circle around the outer edges of the map, depending on its spawn orientation and available nearby loops. This combination will cover most maps, allowing players to go almost anywhere by switching directions at various stops.

To see the triggers visually, grant the `automatedworkcarts.managetriggers` permission and run the `aw.showtriggers` command. For 60 seconds, this will show triggers at nearby tunnels. From here, you can add, update, move, and remove triggers to your liking. See the Commands section for how to manage triggers.

### How to: Automate aboveground workcarts

1. If you are a map developer, please see the "Tips for map developers" section below. Designing the tracks sensibly will simplify setting up automated routes.
2. Carefully examine the tracks on your map to determine the route(s) you would like workcarts to use.
3. Make sure `EnableMapTriggers` is set to `true` in the plugin configuration. This option is enabled by default if installing the plugin from scratch.
4. Grant yourself the `automatedworkcarts.managetriggers` permission.
5. Find a workcart spawn location where you would like workcarts to automatically receive conductors.
6. Aim at the track and run the command `aw.addtrigger Conductor Fwd Hi`. Any workcart that spawns on this trigger will automatically receive a conductor, and start driving forward at max speed.
7. Find a portion of track where you want automated workarts to stop briefly.
8. Aim at the track and run the command `aw.addtrigger Brake Zero 15 Hi`. Any workcart that passes through this trigger will brake until stopping, wait for 15 seconds, then start moving the same direction at `Hi` speed.
9. Keep adding/editing triggers and spawning workcarts to refine the routes.

## Permission

- `automatedworkcarts.toggle` -- Allows usage of the `aw.toggle` and `aw.resetall` commands.
- `automatedworkcarts.managetriggers` -- Allows viewing, adding, updating and removing triggers.

## Commands

### Toggle automation of individual workcarts

- `aw.toggle @<optional_route_name>` -- Toggles automation of the workcart you are looking at.
  - If the route name is specified, the workcart will respond to both global triggers (i.e., triggers that do not specify a route) and triggers assigned to that route. The workcart will ignore triggers assigned other routes.
  - If the route name is **not** specified, the workcart will respond only to global triggers.
- `aw.resetall` -- Resets all automated workcarts to normal. This removes all conductors.

### Manage triggers

- `aw.showtriggers @<optional_route_name> <optional_duration_in_seconds>` -- Shows all nearby triggers to the player for specified duration. Defaults to 60 seconds.
  - This displays each trigger's id, speed, direction, etc.
  - Triggers are also automatically shown for at least 60 seconds when using any of the other trigger commands.
  - When specifying a route name, global triggers and triggers matching that route will be shown with their normal colors, but triggers for different routes will be colored gray.
- `aw.addtrigger <option1> <option2> ...` -- Adds a trigger to the track position where you are aiming, with the specified options. Automated workcarts that pass through the trigger will be affected by the trigger's options.
  - Speed options: `Hi` | `Med` | `Lo` | `Zero`.
  - Direction options: `Fwd` | `Rev` | `Invert`.
  - Track selection options: `Default` | `Left` | `Right` | `Swap`.
  - Other options:
    - `Conductor` -- Adds a conductor to the workcart if not already present. Recommended to place on some or all workcart spawn locations, depending on how many workcarts you want to automate.
      - Note: Owned workcarts cannot receive conductors. Vanilla workcarts don't have owners, but most plugins that spawn vehicles for players will assign ownership.
    - `Brake` -- Instructs the workcart to brake until it reaches the designated speed. For example, if the workcart is going `Fwd_Hi` and enters a `Brake Med` trigger, it will temporarily go `Rev_Lo` until it slows down enough, then it will go `Fwd Med`.
    - `Destroy` -- Destroys the workcart. This is intended for lazy track designs. You should not need this if you design your routes thoughtfully.
    - `@<route_name>` -- Instructs the workcart to ignore this trigger if it's not assigned this route (replace `<route_name>` with the name you want).
      - If the trigger has the `Conductor` property and the workcart does not already have a conductor, it will be assigned this route.
    - `Enabled` -- Enables the trigger if currently disabled.
    - `Disabled` -- Disables the trigger if currently enabled. Disabled triggers are ignored by workcarts and are colored gray.
- `aw.addtrunneltrigger <option1> <option2>` -- Adds a trigger to the track position where you are aiming.
  - Must be in a supported train tunnel (one enabled in plugin configuration).
  - This trigger will be replicated at all train tunnels of the same type. Editing or removing one of those triggers will affect them all.
- `aw.updatetrigger <id> <option1> <option2> ...` -- Adds or updates properties of the specified trigger, without removing any properties.
  - Options are the same as for `aw.addtrigger`.
- `aw.replacetrigger <id> <option1> <option2> ...` -- Replaces all properties of the specified trigger with the properties specified.
  - Options are the same as for `aw.addtrigger`.
  - This is useful for removing properties from a trigger (without having to remove and add it back) since `aw.updatetrigger` does not remove properties.
- `aw.movetrigger <id>` -- Moves the specified trigger to the track position where the player is aiming.
- `aw.removetrigger <id>` -- Removes the specified trigger.

Tip: For the commands that update, move or remove triggers, you can skip the `<id>` argument if you are aiming at a nearby trigger.

### Command aliases

The following command aliases are also available:
- `aw.showtriggers` -> `awt.show`
- `aw.addtrigger` -> `awt.add`
- `aw.addtunneltrigger` -> `awt.addt`
- `aw.updatetrigger` -> `awt.update`
- `aw.replacetrigger` -> `awt.replace`
- `aw.movetrigger` -> `awt.move`
- `aw.removetrigger` -> `awt.remove`

### Command examples

Simple examples:

- `aw.addtrigger Lo` -- Causes the workcart to move in its **current direction** at `Lo` speed. Exmaple: `Fwd_Hi` -> `Fwd_Lo`.
- `aw.addtrigger Fwd Lo` -- Causes the workcart to move **forward** at `Lo` speed, regardless of its current direction. Example: `Rev_Hi` -> `Fwd_Lo`.
- `aw.addtrigger Invert` -- Causes the workcart to **reverse direction** at its **current speed**. Example: `Fwd_Med` -> `Rev_Med`.
- `aw.addtrigger Invert Med` -- Causes the workcart to **reverse direction** at `Med` speed. Example: `Fwd_Hi` -> `Rev_Med`.
- `aw.addtrigger Brake Med` -- Causes the workcart to brake until it reaches the `Med` speed. Examle: `Fwd_Hi` -> `Rev_Lo` -> `Fwd_Med`.
- `aw.addtrigger Left` -- Causes the workcart to turn left at all future intersections.

Advanced examples:

- `aw.addtrigger Conductor Fwd Hi Left` -- Causes the workcart to automatically receive a conductor, to go forward at full speed, and to always turn left.
- `aw.addtrigger Brake Zero 10 Hi` -- Causes the workcart to brake to a stop, then wait 10 seconds, then go full speed in the same direction.
- `aw.addtrigger Zero 20 Med` -- Causes the workcart to turn off its engine for 20 seconds (rolling to a stop), then go the **same direction** at `Med` speed.

Route examples:

- `aw.addtrigger @Route1 Conductor Fwd Hi Left` -- Causes the workcart to automatically receive a conductor, to go forward at full speed, to always turn left, and to ignore triggers assigned to other routes (i.e., routes not named `Route1`).
- `aw.addtrigger @Route1 Left` -- Causes the workcart to turn left at all future intersections, **only** if the workcart is assigned the `Route1` route.

Update examples:

- `aw.updatetrigger 60` -- Updates the trigger's stop duration to `60` seconds.
- `aw.updatetrigger Left` -- Updates the trigger's track selection to `Left`.
- `aw.updatetrigger Fwd Hi` -- Updates the trigger's direction to `Fwd`, and speed to `Hi`.
- `aw.updatetrigger @Route2` -- Updates the trigger's route to `Route2`.

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
  },
  "MapMarkerUpdateInveralSeconds": 5.0,
  "TriggerDisplayDistance": 150.0
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
- `MapMarkerUpdateInveralSeconds` -- The number of seconds between map marker updates. Updating the map markers periodically for many workcarts can impact performance, so you may adjust this value to trade off between accuracy and performance.
- `TriggerDisplayDistance ` -- Determines how close you must be to a trigger to see it when viewing triggers.

## FAQ

#### How can I get workcarts to spawn on custom maps?

Use a free plugin called Workcart Spawner from another website (not allowed to post a link here).

#### Will this plugin cause lag?

This plugin's logic is optimized for performance and should not cause lag. However, workcarts moving along the tracks does incur some overhead, regardless of whether a player or NPC is driving them. Therefore, having many automated workcarts may reduce server FPS. One way to address this is to limit the number of automated workcarts with the `MaxConductors` configuration option.

#### Is this compatible with the Cargo Train Event plugin?

Generally, yes. However, if all workcarts are automated, the Cargo Train Event will never start since it needs to select an idle workcart, so it's recommended to limit the number of automated workcarts using the `MaxConductors` configuration option, and/or to automate only specific workcarts based on their spawn location using triggers.

#### Is it safe to allow player workcarts and automated workcarts on the same tracks?

The best practice is to have separate, independent tracks for player vs automated workcarts. However, automated workcarts do have collision handling logic that makes them somewhat compatible with player tracks.

- When an automated workcart is rear-ended, if it's currently stopping or waiting at a stop, it will depart early.
- When an automated workcart collides with another workcart in front of it, its engine stops for a few seconds to allow the forward workcart to assert its will.
- When two automated workcarts collide head-on, the slower one (or a random one if they are going the same speed) will explode.
- When an automated workcart collides with a non-automated workcart in a manner other than a rear-end, and the workcart is not going the same direction or fast enough, having the `BulldozeOffendingWorkcarts` configuration option set to `true` will cause the non-automated workcart to be destroyed.

## Tips for map developers

- Design the map with automated routes in mind.
- Avoid dead-ends on the main routes.
  - While these can work, they may require additional effort to design automated routes, due to increased collision potential.
- Create parallel tracks throughout most of the map.
  - This allows workcarts to move in both directions, similar to the vanilla underground tracks.
- Create frequent alternate tracks.
  - Creating alternate tracks at intended stop points allows player-driven workcarts to easily pass automated workcarts at designated stops.
  - Creating alternate tracks throughout the map provides opportunities for player-driven workcarts to avoid automated workcarts for various reasons.
  - Be intentional about which track you set as the "default", since that is likely the one that players will use.
- Create completely independent tracks.
  - For example, an outer circle and an inner circle.
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
  "Toggle.Success.On.WithRoute": "That workcart is now automated with route <color=#fd4>@{0}</color>.",
  "Toggle.Success.Off": "That workcart is no longer automated.",
  "ResetAll.Success": "All {0} conductors have been removed.",
  "ShowTriggers.Success": "Showing all triggers for <color=#fd4>{0}</color>.",
  "ShowTriggers.WithRoute.Success": "Showing all triggers for route <color=#fd4>@{0}</color> for <color=#fd4>{1}</color>",
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
  "Help.OtherOptions": "Other options: <color=#fd4>Conductor</color> | <color=#fd4>Brake</color> | <color=#fd4>Destroy</color> | <color=#fd4>@ROUTE_NAME</color> | <color=#fd4>Enabled</color> | <color=#fd4>Disabled</color>",
  "Info.Trigger": "Workcart Trigger #{0}{1}",
  "Info.Trigger.Prefix.Map": "M",
  "Info.Trigger.Prefix.Tunnel": "T",
  "Info.Trigger.Disabled": "DISABLED",
  "Info.Trigger.Map": "Map-specific",
  "Info.Trigger.Route": "Route: @{0}",
  "Info.Trigger.Tunnel": "Tunnel type: {0} (x{1})",
  "Info.Trigger.Conductor": "Adds Conductor",
  "Info.Trigger.Destroy": "Destroys workcart",
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

#### API_AutomateWorkcart

```csharp
bool API_AutomateWorkcart(TrainEngine workcart)
```

Automates the specified workcart. Returns `true` if successful, or if already automated. Returns `false` if it was blocked by another plugin.

#### API_StopAutomatingWorkcart

```csharp
void API_StopAutomatingWorkcart(TrainEngine workcart, bool immediate = false)
```

Stops automating the specified workcart. Destroys in the same frame if `immediate` is `true`.

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

#### OnWorkcartAutomationStart

```csharp
bool? OnWorkcartAutomationStart(TrainEngine workcart)
```

- Called when a workcart is about to become automated
- Returning `false` will prevent the workcart from becoming automated
- Returning `null` will result in the default behavior

#### OnWorkcartAutomationStarted

```csharp
void OnWorkcartAutomationStarted(TrainEngine workcart)
```

- Called after a workcart has become automated
- No return behavior

#### OnWorkcartAutomationStopped

```csharp
void OnWorkcartAutomationStopped(TrainEngine workcart)
```

- Called after a workcart has stopped being automated
- No return behavior
