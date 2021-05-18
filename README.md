## Features

- Automates workcarts with NPC conductors
- Conductors automatically stop at vanilla train stations
- Allows creating custom stops for other maps

## Permission

- `automatedworkcarts.toggle` -- Allows usage of the `aw.toggle` or `aw.toggleall` commands.
- `automatedworkcarts.managetriggers` -- Allows adding and removing custom triggers.

## Commands

- `aw.toggle` -- Toggles automation for the workcart you are standing on or looking at.
  - This command is disabled while the `AutomateAllWorkcarts` configuration option is `true`.
- `aw.addtrigger <option1> <option2> ...` -- Adds a trigger to the track position where the player is aiming, with the specified options. Workcarts that pass through the trigger will be affected by the trigger's options.
  - Speed options: `Rev_Hi` | `Rev_Med` | `Rev_Lo` | `Zero` | `Fwd_Lo` | `Fwd_Med` | `Fwd_Hi`.
  - Track selection options:  `Default` | `Left` | `Right`.
  - Passing the `Start` option will cause any workcart that passes through (or spawns on) the trigger to become an automated workcart. Useful if you have configured the plugin with `AutomateAllWorkcarts` set to `false`.
  - Examples:
    - `aw.addtrigger` -- Creates a trigger with speed `Zero`. This causes the workcart to turn its engine off for the duration of the `TimeAtStation` configuration option.
    - `aw.addtrigger Rev_Hi` -- Creates a trigger that will cause workcarts to select max speed.
    - `aw.addtrigger Rev_Hi` -- Creates a trigger that will cause workcarts to turn left at every intersection until another trigger changes the selection.
    - `aw.addtrigger Start Rev_Hi Left` -- Creates a trigger with max forward speed and left track selection.
- `aw.updatetrigger <id> <option1> <option2> ...` -- Updates the options of the specified trigger. Options are the same as for `aw.addtrigger`.
- `aw.movetrigger <id>` -- Moves the specified trigger to the track position where the player is aiming.
- `aw.removetrigger <id>` -- Removes the specified trigger.
- `aw.showtriggers` -- Shows all triggers, including id, speed and track selection. This lasts 60 seconds, but is extended any time you add, update, move or remove a trigger.
  - You must be an admin to see them.

The following command aliases are also available:
- `aw.addtrigger` -> `awt.add`
- `aw.movetrigger` -> `awt.move`
- `aw.updatetrigger` -> `awt.update`
- `aw.removetrigger` -> `awt.remove`

## Configuration

```json
{
  "AutomateAllWorkcarts": false,
  "AutoDetectStations": true,
  "TimeAtStation": 30.0,
  "DefaultSpeed": "Fwd_Hi",
  "DepartureSpeed": "Fwd_Lo",
  "DefaultTrackSelection": "Left"
}
```

- `AutomateAllWorkcarts` (`true` or `false`) -- While `true`, all workcarts will be automated, except those blocked by other plugins; the `aw.toggle` command will be disabled. While false, you can either automate individual workcarts with `aw.toggle` or use custom triggers to automate workcarts that pass through them (or spawn on them).
- `TimeAtStation` -- Number of seconds that trains should wait after stopping.
  - For custom triggers, the timer starts as soon as the workcart enters a trigger with speed `Zero`, not when the workcart actually stops.
- `AutoDetectStations` (`true` or `false`) -- While `true`, the plugin will auto detect vanilla train stations and add triggers to them, causing automated workcarts to stop at them for the configured `TimeAtStation`.
  - Note: These triggers cannot be customized.
- `DefaultSpeed` -- Default speed to use when a workcart starts being automated.
  - Basically this applies when the plugin loads, when toggling on with `aw.toggle`, or when a workcart spawns.
- `DepartureSpeed` -- Speed to use when departing from a stop.
- `DefaultTrackSelection` (`Default`, `Left` or `Right`) -- Default track selection to use when a workcart starts being automated.
  - Basically this applies when the plugin loads, when toggling on with `aw.toggle`, or when a workcart spawns.

### Possible speeds
- `"Rev_Hi"`
- `"Rev_Med"`
- `"Rev_Lo"`
- `"Zero"`
- `"Fwd_Lo"`
- `"Fwd_Med"`
- `"Fwd_Hi"`

### Possible track selections
- `"Default"`
- `"Left"`
- `"Right"`

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoTriggers": "There are no workcart triggers on this map.",
  "Error.TriggerNotFound": "Error: Trigger id #{0} not found.",
  "Error.ErrorNoTrackFound": "Error: No track found nearby.",
  "Error.NoWorkcartFound": "Error: No workcart found.",
  "Error.FullyAutomated": "Error: You cannot do that while full automation is on.",
  "Error.AutomateBlocked": "Error: Another plugin blocked automating that workcart.",
  "Toggle.Success.On": "That workcart is now automated.",
  "Toggle.Success.Off": "That workcart is no longer automated.",
  "AddTrigger.Syntax": "Syntax: <color=#fd4>{0} <speed> <track selection></color>\nSpeeds: {1}\nTrack selections: {2}",
  "AddTrigger.Success": "Successfully added trigger #{0}.",
  "UpdateTrigger.Syntax": "Syntax: <color=#fd4>{0} <id> <speed> <track selection></color>\nSpeeds: {1}\nTrack selections: {2}",
  "UpdateTrigger.Success": "Successfully updated trigger #{0}",
  "MoveTrigger.Success": "Successfully moved trigger #{0}",
  "RemoveTrigger.Syntax": "Syntax: <color=#fd4>{0} <id></color>",
  "RemoveTrigger.Success": "Trigger #{0} successfully removed.",
  "Info.Trigger": "Workcart Trigger #{0}",
  "Info.Trigger.Start": "Starts automation",
  "Info.Trigger.Speed": "Speed: {0}",
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
