## Features

- Allows automating trains with NPC conductors
- Allows placing triggers to instruct conductors how to navigate
- Allows placing spawn points, which spawn workcarts with optional train wagons
- Optional default triggers for underground tunnels
- Optional map markers for automated trains
- Extensible API for creating addon plugins
- Fully compatible with the new trains from the May 2022 Rust update

## Introduction

The primary feature of this plugin is to add "triggers" onto train tracks. A trigger is an invisible object which can detect when a train comes into contact with it, in order to perform various functions, including the following.

- Add a conductor to the train
- Change the speed, direction and track selection of the train
- Temporarily stop the train for a configurable period of time
- Run custom server commands
- Destroy the train

All of the above functions, with the exception of adding a conductor, only apply to trains that have a conductor, meaning that player-driven trains can pass through most triggers unaffected.

A trigger can also serve as a workcart spawn point, with an optional number of attached wagons and workcarts.

### How conductors work

Automating a train will add a conductor NPC to every workcart attached to that train, automatically dismounting any players currently driving them.

- Conductors are invincible
- All workcarts and wagons that are part of an automated train are invincible
- Automated workcarts do not require fuel
- Automated trains cannot be coupled to other trains, nor uncoupled

### How triggers work

Each trigger can have multiple properties, including direction (e.g., `Fwd`, `Rev`, `Invert`), speed (e.g, `Hi`, `Med`, `Lo`, `Zero`), track selection (e.g., `Default`, `Left`, `Right`), stop duration (in seconds), and departure speed/direction. When a conductor-driven train touches a trigger, its instructions may change according to the trigger properties. For example, if a train is currently following the instructions `Fwd` + `Hi` + `Left`, and then touches a trigger that specifies only track selection `Right`, the train instructions will change to `Fwd` + `Hi` + `Right`, causing the train to turn right at every intersection it comes to, until it touches a trigger that instructs otherwise.

### Types of triggers

#### Map specific triggers

- Can be placed anywhere on train tracks, above or below ground.
- Only apply to the map they were placed on, using world coordinates.
- Enabled via the `EnableMapTriggers` configuration option.
- Added with the `aw.addtrigger` or `awt.add` command.
- Saved in data file: `oxide/data/AutomatedWorkcarts/MAP_NAME.json`.
  - Note: The file name for non-procedural maps will exclude the wipe number so that you can re-use the triggers across force wipes.

#### Tunnel triggers

- Can be placed only in the vanilla train tunnels.
- Automatically replicate at all tunnels of the same type, using tunnel-relative coordinates.
- Enabled via the `EnableTunnelTriggers` -> `*` options.
- Added with the `aw.addtunneltrigger` or `awt.addt` command.
- Saved in data file `oxide/data/AutomatedWorkcarts/TunnelTriggers.json`.

In the future, when Facepunch adds procedurally generated rail connections to monuments, the concept of tunnel triggers will be adapted to "monument triggers", allowing you to place triggers above ground using monument-relative coordinates.

## Tutorials

### Tutorial: Automate all underground workcarts

The plugin provides default triggers which you can enable for underground tunnels. This setup should take only a few minutes.

1. Set `EnableTunnelTriggers` -> `TrainStation` to `true` in the plugin configuration.
2. Reload the plugin.

This will place `Conductor` triggers on the vanilla workcart spawn points in the train station maintenance tunnels, which will automatically add conductors to the workcarts when they spawn. The workcarts will drive forward at max speed and turn left at all intersections. This will also place `Brake` triggers at the train stations, which will cause trains to automatically stop briefly near the elevators.

From the map perspective, each workcart will move in a counter-clockwise circle around an adjacent loop, or in a clockwise circle around the outer edges of the map, depending on which maintenance tunnel the workcart spawned in and the available nearby loops. This combination will cover most maps, allowing players to go almost anywhere by switching directions at various stops.

To see the triggers visually, grant the `automatedworkcarts.managetriggers` permission and run the `awt.show` command. For 60 seconds, this will show triggers at nearby tunnels. From here, you can add, update, move, and remove triggers to your liking. See the Commands section for how to manage triggers.

### Tutorial: Automate aboveground workcarts

1. If you are a map developer, please see the "Tips for map developers" section below. Designing the tracks sensibly will simplify setting up automated routes.
2. Carefully examine the tracks on your map to determine the route(s) you would like workcarts to use.
3. Grant yourself the `automatedworkcarts.managetriggers` permission.
4. Find a train spawn location where you would like trains to automatically receive conductors.
5. Aim at the track and run the command `awt.add Conductor Fwd Hi`. Any train that spawns on this trigger will automatically receive a conductor, and start driving forward at max speed.
6. Find a portion of track where you want automated workarts to stop briefly.
7. Aim at the track and run the command `awt.add Brake Zero 15 Hi`. Any train that passes through this trigger will brake until stopping, wait for `15` seconds, then start moving the same direction at `Hi` speed.
8. Keep adding/editing triggers and spawning workcarts to refine the routes.

### Tutorial: Create spawn points

Workcarts can be spawned via spawn triggers. The following steps will walk you through an example to get you started.

1. Grant yourself the `automatedworkcarts.managetriggers` permission.
2. Aim somewhere on train tracks, and run the command `awt.add Spawn` (or `awt.addt Spawn` for a tunnel-relative trigger). A workcart should spawn there immediately (if there is sufficient space) but won't have a conductor.
3. If the workcart is facing the wrong direction, aim at the trigger while facing approximately the direction you want the workcart to face, then run `awt.rotate` and `awt.respawn`.
4. To move the spawn point, aim where you want to move it to, and run `awt.move <id>`, where `<id>` should be replaced with the trigger id which you can see floating above the trigger (it will have a `#`).
5. To add wagons to the spawn point, aim at the trigger and run the command `awt.wagons WagonA WagonB WagonC WagonD Workcart`. This example command will add all 4 types of wagons, with an additional workcart at the end. You can add as
many wagons and workcarts as you want, in any order, as long as there is enough space for them on the tracks.
6. If you want to add a conductor to the train, aim at the trigger and run `awt.update Conductor Fwd Hi` then `awt.respawn`.

## Permission

- `automatedworkcarts.toggle` -- Allows usage of the `aw.toggle` and `aw.resetall` commands.
- `automatedworkcarts.managetriggers` -- Allows viewing, adding, updating and removing triggers.

## Commands

### Toggle automation of individual workcarts

- `aw.toggle @<optional_route_name>` -- Toggles automation of the train you are looking at.
  - If the route name is specified, the train will respond to both global triggers (i.e., triggers that do not specify a route) and triggers assigned to that route. The train will ignore triggers assigned other routes.
  - If the route name is **not** specified, the train will respond only to global triggers.
  - The train will start driving according to `DefaultSpeed` and `DefaultTrackSelection` in the plugin configuration.
- `aw.resetall` -- Resets all automated trains to normal. This removes all conductors.

### Manage triggers

- `aw.showtriggers @<optional_route_name> <optional_duration_in_seconds>` (alias: `awt.show`) -- Shows all nearby triggers to the player for specified duration. Defaults to 60 seconds.
  - This displays each trigger's id, speed, direction, etc.
  - Triggers are also automatically shown for at least 60 seconds when using any of the other trigger commands or when manually automating a workcart.
  - When specifying a route name, global triggers and triggers matching that route will be shown with their default colors, but triggers for different routes will be colored gray.
- `aw.addtrigger <option1> <option2> ...` (alias: `awt.add`) -- Adds a trigger to the track position where you are aiming, with the specified options. Automated trains that pass through the trigger will be affected by the trigger's options.
  - Speed options: `Hi` | `Med` | `Lo` | `Zero`.
  - Direction options: `Fwd` | `Rev` | `Invert`.
  - Track selection options: `Default` | `Left` | `Right` | `Swap`.
  - Other options:
    - `Spawn` -- Spawns a train at this trigger (1+ workcart, and optional wagons). Each trigger can spawn at most one train. The train will despawn when the trigger is removed, when the trigger is disabled, when this property is removed from the trigger, or when the plugin reloads. If the train is destroyed (e.g., via `ent kill` or via a `Destroy` trigger), the train will attempt to respawn within 30 seconds. See the `awt.wagons` command for how to add additional workcarts and wagons to the spawn point.
    - `Conductor` -- Adds a conductor to the train if not already present. A good place to put `Conductor` triggers is on workcart spawn locations, such as the vanilla spawns in the underground maintenance tunnels. This option can also be combined with the `Spawn` option to automatically spawn an automated train.
      - Note: Player-owned trains cannot receive conductors. Vanilla trains don't have owners, but most plugins that spawn vehicles for players will assign ownership by setting the `OwnerID` property of the vehicle.
    - `Brake` -- Instructs the train to brake until it reaches the designated speed. For example, if the train is going `Fwd_Hi` and enters a `Brake Med` trigger, it will temporarily go `Rev_Lo` until it slows down enough, then it will go `Fwd_Med`.
    - `Destroy` -- Destroys the train. This is intended primarily for testing and demonstrations, but it's also useful if you don't want to be bothered to make a more thoughtful track design.
    - `@<route_name>` -- Instructs the trigger to ignore this trigger if it's not assigned this route (replace `<route_name>` with the name you want).
      - If the trigger has the `Conductor` property and the train does not already have a conductor, it will be assigned this route.
    - `Enabled` -- Enables the trigger.
    - `Disabled` -- Disables the trigger. Disabled triggers are ignored by trains and are colored gray.
- `aw.addtrunneltrigger <option1> <option2>` (alias: `awt.addt`) -- Adds a trigger to the track position where you are aiming.
  - Must be in a supported train tunnel (one enabled in plugin configuration).
  - This trigger will be replicated at all train tunnels of the same type. Editing or removing one of those triggers will affect them all.
- `aw.updatetrigger <id> <option1> <option2> ...` (alias: `awt.update`) -- Adds or updates properties of the specified trigger, without removing any properties.
  - Options are the same as for `aw.addtrigger`.
- `aw.replacetrigger <id> <option1> <option2> ...` (alias: `awt.replace`) -- Replaces all properties of the specified trigger with the properties specified.
  - Options are the same as for `aw.addtrigger`.
  - This is useful for removing properties from a trigger (without having to remove and add it back) since `aw.updatetrigger` does not remove properties.
- `aw.movetrigger <id>` (alias: `awt.move`) -- Moves the specified trigger to the track position where the player is aiming.
- `aw.rotatetrigger <id>` (alias: `awt.rotate`) -- Rotates the specified trigger to where the player is facing. This is only useful for `Spawn` triggers since it determines which way the train will be facing when spawned.
- `aw.removetrigger <id>` (alias: `awt.remove`) -- Removes the specified trigger.
- `aw.enabletrigger <id>` (alias: `awt.enable`) -- Enables the specified trigger. This is identical to `aw.updatetrigger <id> enabled`.
- `aw.disabletrigger <id>` (alias: `awt.disable`) -- Disables the specified trigger. This is identical to `aw.updatetrigger <id> disabled`. Disabled triggers are ignored by trains and are colored gray.
- `aw.settriggerwagons <id> <wagon1> <wagon2> ...` (alias: `awt.wagons`) -- Assigns zero or more train wagons to the trigger, replacing the current list of wagons. Whenever this trigger spawns a workcart, it will attempt to spawn all of the specified wagons behind it in the specified order. The wagons will be automatically coupled to the workcart. Allowed values: `WagonA`, `WagonB`, `WagonC`, `WagonD`, `Workcart`. To remove all wagons, run the command without specifying any wagons.
- `aw.respawntrigger <id>` (alias: `awt.respawn`) -- Despawns and respawns the train for the specified trigger. When used at a tunnel trigger, trains will be respawned at all instances of the tunnel trigger.
- `aw.addtriggercommand <id> <command>` (alias: `awt.addcmd`) -- Adds the specified command to the trigger. which will be executed whenever a train passes through the trigger. You can use the magic variable `$id` in the command which will be replaced by the primary workcart's Net ID.
- `aw.removetriggercommand <id> <number>` (alias: `awt.removecmd`) -- Removes the specified command from the trigger. The command number will be 1, 2, 3, etc. and will be visible on the trigger info when using `aw.showtriggers`.

Tip: For the commands that update, move or remove triggers, you can skip the `<id>` argument if you are aiming at a nearby trigger.

### Command examples

Simple examples:

- `awt.add Lo` -- Causes the train to move in its **current direction** at `Lo` speed. Example: `Fwd_Hi` -> `Fwd_Lo`.
- `awt.add Fwd Lo` -- Causes the train to move **forward** at `Lo` speed, regardless of its current direction. Example: `Rev_Hi` -> `Fwd_Lo`.
- `awt.add Invert` -- Causes the train to **reverse direction** at its **current speed**. Example: `Fwd_Med` -> `Rev_Med`.
- `awt.add Invert Med` -- Causes the train to **reverse direction** at `Med` speed. Example: `Fwd_Hi` -> `Rev_Med`.
- `awt.add Brake Med` -- Causes the train to **brake** until it reaches the `Med` speed. Examle: `Fwd_Hi` -> `Rev_Lo` -> `Fwd_Med`.
- `awt.add Left` -- Causes the train to turn **left** at all future intersections.

Advanced examples:

- `awt.add Conductor Fwd Hi Left` -- Causes the train to automatically receive a **conductor**, to move **forward** at `Hi` speed, and to always turn **left**.
- `awt.add Brake Zero 10 Hi` -- Causes the train to **brake** to a **stop**, wait 10 seconds, then move `Hi` speed in the **same direction**.
- `awt.add Zero 20 Med` -- Causes the train to **turn off** its engine for `20` seconds (slowly rolling to a stop), then move the **same direction** at `Med` speed.

Route examples:

- `awt.add @Route1 Conductor Fwd Hi Left` -- Causes the train to automatically receive a **conductor**, to move **forward** at `Hi` speed, to always turn **left**, and to **ignore triggers assigned to other routes** (i.e., routes not named `Route1`).
- `awt.add @Route1 Left` -- Causes the train to turn **left** at all future intersections, **only** if the train is assigned the `Route1` route.

Update examples:

- `awt.update 60` -- Updates the trigger's stop duration to `60` seconds.
- `awt.update Left` -- Updates the trigger's track selection to `Left`.
- `awt.update Fwd Hi` -- Updates the trigger's direction to `Fwd`, and speed to `Hi`.
- `awt.update @Route2` -- Updates the trigger's route to `Route2`.

Spawn exmaples:

- `awt.add Spawn` -- Designates the trigger as a workcart spawn point.
- `awt.wagons WagonA WagonB` -- Designates that the workcart should spawn with one `WagonA` and one `WagonB` wagon coupled behind it.
- `awt.wagons WagonC WagonC` -- Designates that the workcart should spawn with two `WagonC` wagons coupled behind it.
- `awt.wagons Workcart` -- Designates that the workcart should spawn with an additional workcart coupled behind it.
- `awt.wagons WagonA WagonB WagonC WagonD Workcart` -- Designates that the workcart should spawn with all possible wagon types behind it, with an additional workcart.

## Configuration

Default configuration:

```json
{
  "EnableTerrainCollision": true,
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

- `EnableTerrainCollision` (`true` or `false`) -- While `true` (default), trains will be unable to travel through terrain or terrain triggers. This doesn't matter for procedural maps because vanilla train tracks never pass through terrain, but if you have a custom map which has train tunnels that pass through terrain, you should set this to `false` to allow trains to travel through those tunnels. This option affects all trains, regardless of whether they were spawned or controlled by this plugin.
- `DefaultSpeed` -- Default speed to use when a train starts being automated.
  - Allowed values: `"Rev_Hi"` | `"Rev_Med"` | `"Rev_Lo"` | `"Zero"` | `"Fwd_Lo"` | `"Fwd_Med"` | `"Fwd_Hi"`.
  - This value is ignored if the train is on a trigger that specifies speed.
- `DefaultTrackSelection` -- Default track selection to use when a train starts being automated.
  - Allowed values: `"Left"` | `"Default"` | `"Right"`.
  - This value is ignored if the train is on a trigger that specifies track selection.
- `BulldozeOffendingWorkcarts` (`true` or `false`) -- While `true`, automated trains will destroy other non-automated trains in their path.
  - Regardless of this setting, automated trains may destroy each other in head-on or perpendicular collisions.
- `EnableMapTriggers` (`true` or `false`) -- While `false`, existing map-specific triggers will be disabled, and no new map-specific triggers can be added.
- `EnableTunnelTriggers` -- While `false` for a particular tunnel type, existing triggers in those tunnels will be disabled, and no new triggers can be added to tunnels of that type.
  - `TrainStation` (`true` or `false`) -- Self-explanatory.
  - `BarricadeTunnel` (`true` or `false`) -- This affects straight tunnels that spawn NPCs, loot, as well as barricades on the tracks.
  - `LootTunnel` (`true` or `false`) -- This affects straight tunnels that spawn NPCs and loot.
  - `Intersection` (`true` or `false`) -- This affects 3-way intersections.
  - `LargeIntersection` (`true` or `false`) -- This affects 4-way intersections.
- `MaxConductors` -- The maximum number of automated trains allowed on the map at once. Set to `-1` for no limit. Note that having multiple automated workcarts on a single train will count as only one conductor.
- `ConductorOutfit` -- Items to use for the outfit of each conductor.
- `ColoredMapMarker`
  - `Enabled` (`true` or `false`) -- Whether to enable colored map markers. Enabling this has a performance cost.
  - `Color` -- The marker color, using the hexadecimal format popularized by HTML.
  - `Alpha` (`0.0` - `1.0`) -- The marker transparency (`0.0` is invisible, `1.0` is fully opaque).
  - `Radius` -- The marker radius.
- `VendingMapMarker`
  - `Enabled` (`true` or `false`) -- Whether to enable vending machine map markers. Enabling this has a performance cost.
  - `Name` -- The name to display when hoving the mouse over the marker.
- `MapMarkerUpdateInveralSeconds` -- The number of seconds between map marker updates. Updating the map markers periodically for many trains can impact performance, so you may adjust this value to trade off between accuracy and performance.
- `TriggerDisplayDistance ` -- Determines how close you must be to a trigger to see it when viewing triggers (e.g., after running `awt.show`).

## Routes (advanced)

Routes are an advanced feature intended for complex aboveground tracks. Using routes allow trains to respond to only select triggers by ignoring triggers designated for other routes. This is useful for situations where multiple trains need to pass through shared tracks but exit those tracks in different directions.

#### Do I need routes?

Probably not. The routes feature allows solving some uses cases that simply aren't possible using only global triggers. However, many use cases that you might think you need routes for are actually achievable using global triggers.

Here are some example ways you can avoid needing to use the routes feature.

- Design the map to reduce track sharing to a minimum, and simplify shared track sections to remove unnecessary branches where you don't want automated trains to go.
- Place track selection triggers before shared track sections. These triggers should assign different track selections to trains coming from different source tracks. As long as the shared track sections do not require the trains to change tracks, this allows the trains to exit the shared tracks in different directions.
- Place track selection triggers with the `Swap` instruction to designate that a train should flip its track selection between `Left` and `Right`.

#### How to assign routes

A train can only be assigned a route when it receives a conductor. This can be done one of two ways.

- By running the `aw.toggle @<route_name>` command while aiming at the train. When this command adds a conductor, the train will be assigned the specified route.
- When a train receives a conductor via a trigger, if the trigger also has an assigned route, the train will be assigned that route.

#### How trains respond to triggers when using routes

- Triggers **with** an assigned route will affect **only** trains assigned the same route.
- Triggers **without** an assigned route will affect **all** trains.

## FAQ

#### Will this plugin cause lag?

This plugin's logic is optimized for performance and should not cause lag. However, trains moving along the tracks does incur some overhead, regardless of whether a player or NPC is driving them. Therefore, having many automated trains may reduce server FPS. One way to address this is to limit the number of automated trains with the `MaxConductors` configuration option.

#### Is this compatible with the Cargo Train Event plugin?

Generally, yes. However, if all trains are automated, the Cargo Train Event will never start since it needs to select an idle workcart, so it's recommended to limit the number of automated trains using the `MaxConductors` configuration option, and/or to automate only specific trains based on their spawn location using triggers.

#### Is it safe to allow player trains and automated trains on the same tracks?

The **best practice** is to have separate, independent tracks for player vs automated trains. However, automated trains do have collision handling logic that makes them somewhat compatible with player tracks.

- When an automated train is rear-ended, if it's currently stopping or waiting at a stop, it will depart early.
- When an automated train collides with another train in front of it, its engine stops for a few seconds to allow the forward train to attempt passage.
- When two automated trains collide head-on, the slower one (or a random one if they are going the same speed) will explode.
- When an automated train collides with a non-automated train in a manner other than a rear-end, having the `BulldozeOffendingWorkcarts` configuration option set to `true` will cause the non-automated train to be destroyed.

## Tips for map developers

- Design the map with automated routes in mind.
- Avoid dead-ends on the main routes.
  - While these can work, they may require additional effort to design automated routes, due to increased collision potential.
- Create parallel tracks throughout most of the map.
  - This allows trains to move in both directions, similar to the vanilla underground tracks.
- Create frequent alternate tracks.
  - Create alternate tracks at intended stop points to allow player-driven trains to easily pass automated trains at designated stops.
  - Create alternate tracks throughout the map to provide opportunities for player-driven trains to avoid automated trains.
- Create completely independent tracks.
  - For example, an outer circle and an inner circle.
  - This allows users of this plugin to selectively automate only specific areas, while allowing other areas to have player-driven trains, therefore avoiding interactions between the two.
- For underground tunnels, ensure each "loop" has at least two train stations (or other stops). This works best with the plugin's default triggers since it allows players to travel anywhere with automated trains by switching directions at various stops.
- If distributing your map, use this plugin to make default triggers for your users, and distribute the json file with your map.
  - The file can be found in `oxide/data/AutomatedWorkcarts/MAP_NAME.json`.

## Localization

## Developer API

#### API_AutomateWorkcart

```csharp
bool API_AutomateWorkcart(TrainEngine workcart)
```

Automates the specified workcart, along with all workcarts attached to the same train. Returns `true` if successful, or if already automated. Returns `false` if it was blocked by another plugin.

#### API_StopAutomatingWorkcart

```csharp
void API_StopAutomatingWorkcart(TrainEngine workcart)
```

Stops automating the specified workcart, along with all workcarts attached to the same train.

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
