## Features

- Automates workcarts with NPC conductors
- Conductors automatically stop at vanilla train stations
- Allows creating custom stops for other maps

## Permission

- `automatedworkcarts.managetriggers` -- Allows adding and removing custom triggers.

## Commands

- `aw.toggle` -- Toggles automated workcarts on or off.
  - Workcarts are already automated by default on plugin load, so this command is mainly for users who want to toggle it off while they set up triggers.
  - Note: Reloading the plugin will toggle them back on.
- `aw.addtrigger <speed> <track selection>` -- Adds a trigger at the nearest train track, with the specified speed and track selection.
  - When a workcart collides with this trigger, its speed and track selection will be adjusted accordingly.
  - See the configuration section for a list of possible speeds and track selections.
  - Both `<speed>` and `<track selection>` are optional.
    - `aw.addtrigger` -- Adds a trigger with speed `Zero`.
    - `aw.addtrigger <speed>` -- Adds a trigger that only affects speed.
    - `aw.addtrigger <track selection>` -- Adds a trigger that only affects track selection.
- `aw.updatetrigger <id> <speed> <track selection>` -- Updates the speed and track selection of the specified trigger.
  - Both `<speed>` and `<track selection>` are optional, but at least one must be specified.
    - `aw.updatetrigger <id> <speed>` -- Update only the speed of the trigger.
    - `aw.updatetrigger <id> <track selection>` -- Update only the track selection of the trigger.
- `aw.removetrigger <id>` -- Removes a trigger by id.
- `aw.showtriggers` -- Shows all triggers, including id, speed and track selection.
  - You must be an admin to see them.

The following command aliases are also available:
- `aw.addtrigger` -> `awt.add`
- `aw.updatetrigger` -> `awt.update`
- `aw.removetrigger` -> `awt.remove`

## Configuration

```json
{
  "TimeAtStation": 30.0,
  "AutoDetectStations": true,
  "DefaultSpeed": "Fwd_Hi",
  "DepartureSpeed": "Fwd_Lo",
  "DefaultTrackSelection": "Left"
}
```

- `TimeAtStation` -- Number of seconds that trains should wait after stopping.
  - For custom triggers, the timer starts as soon as the speed changes to zero.
- `AutoDetectStations` (`true` or `false`) -- While `true`, the plugin will auto detect vanilla train stations and add triggers for them, causing automated workcarts to stop at them for the configured `TimeAtStation`.
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
  "Error.NoWorkcartFound": "Error: You must be on a workcart to do that.",
  "Error.NoTriggers": "There are no workcart triggers on this map.",
  "Error.TriggerNotFound": "Error: Trigger id #{0} not found.",
  "Error.ErrorNoTrackFound": "Error: No track found nearby.",
  "Add.Syntax": "Syntax: <color=#fd4>{0} <speed> <track selection></color>\nSpeeds: {1}\nTrack selections: {2}",
  "Add.Success": "Successfully added trigger #{0}.",
  "Update.Syntax": "Syntax: <color=#fd4>{0} <id> <speed> <track selection></color>\nSpeeds: {1}\nTrack selections: {2}",
  "Update.Success": "Successfully updated trigger #{0}",
  "Remove.Syntax": "Syntax: <color=#fd4>{0} <id></color>",
  "Success.TriggerRemoved": "Trigger #{0} successfully removed.",
  "Info.Trigger": "Workcart Trigger #{0}",
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
