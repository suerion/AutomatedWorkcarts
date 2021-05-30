## Features

- Automates workcarts with NPC conductors
- Conductors automatically stop at vanilla train stations
- Allows creating custom stops for other maps

## Permission

- `automatedworkcarts.toggle` -- Allows usage of the `aw.toggle` command.
- `automatedworkcarts.managetriggers` -- Allows adding and removing triggers.

## Commands

- `aw.toggle` -- Toggles automation for the workcart you are standing on or looking at.
  - This command is disabled while the `AutomateAllWorkcarts` configuration option is `true`.
- `aw.addtrigger <option1> <option2> ...` -- Adds a trigger to the track position where the player is aiming, with the specified options. Automated workcarts that pass through the trigger will be affected by the trigger's options.
  - Speed options: `Hi` | `Med` | `Lo` | `Zero`.
  - Direction options: `Fwd` | `Rev` | `Invert`.
  - Track selection options: `Default` | `Left` | `Right` | `Swap`.
  - Passing the `Start` option will enable automation for any workcart that enters the trigger. This is useful if you have set `AutomateAllWorkcarts` set to `false` but don't want to manually automate individual workcarts. The recommendation is to place this on specific workcart spawn points.
  - Examples:
    - `aw.addtrigger` -- Creates a trigger with speed `Zero`. This causes the workcart to turn its engine off for the duration of the `EngineOffDuration` configuration option.
    - `aw.addtrigger Rev Hi` -- Creates a trigger that will cause workcarts to select max speed.
    - `aw.addtrigger Rev Hi` -- Creates a trigger that will cause workcarts to turn left at every intersection until another trigger changes the selection.
    - `aw.addtrigger Start Rev Hi Left` -- Creates a trigger with max forward speed and left track selection, which automatically enables automation for any workcart that enters it.
- `aw.addtrunneltrigger <option1> <option2>` -- Adds a trigger to the track position where the player is aiming. Must be in a supported train tunnel. This trigger will be replicated at all train tunnels of the same type.
- `aw.updatetrigger <id> <option1> <option2> ...` -- Updates the options of the specified trigger. Options are the same as for `aw.addtrigger`.
- `aw.replacetrigger <id> <option1> <option2> ...` -- Replaces all options on the specified trigger with the options specified, without moving the trigger. Options are the same as for `aw.addtrigger`. This is useful for removing options from a trigger which `aw.updatetrigger` does not allow.
- `aw.movetrigger <id>` -- Moves the specified trigger to the track position where the player is aiming.
- `aw.removetrigger <id>` -- Removes the specified trigger.
- `aw.showtriggers` -- Shows all triggers, including id, speed and track selection. This lasts 60 seconds, but is extended any time you add, update, move or remove a trigger.
  - You must be an admin to see the triggers.

The following command aliases are also available:
- `aw.addtrigger` -> `awt.add`
- `aw.updatetrigger` -> `awt.update`
- `aw.replacetrigger` -> `awt.replace`
- `aw.movetrigger` -> `awt.move`
- `aw.removetrigger` -> `awt.remove`
- `aw.addtunneltrigger` -> `awt.addt`

## Configuration

```json
{
  "AutomateAllWorkcarts": false,
  "EngineOffDuration": 30.0,
  "DefaultSpeed": "Fwd_Hi",
  "DepartureSpeed": "Fwd_Lo",
  "DefaultTrackSelection": "Left"
}
```

- `AutomateAllWorkcarts` (`true` or `false`) -- While `true`, all workcarts will be automated, except those blocked by other plugins; the `aw.toggle` command will be disabled. While false, you can either automate individual workcarts with `aw.toggle` or use custom triggers to automate workcarts that enter them.
- `EngineOffDuration` -- Number of seconds that trains should wait after stopping.
  - For custom triggers, the timer starts as soon as the workcart enters a trigger with speed `Zero`, not when the workcart actually stops.
- `DefaultSpeed` -- Default speed to use when a workcart starts being automated.
  - Allowed values: `"Rev_Hi"` | `"Rev_Med"` | `"Rev_Lo"` | `"Zero"` | `"Fwd_Lo"` | `"Fwd_Med"` | `"Fwd_Hi"`.
  - Basically this applies when the plugin loads, when toggling on with `aw.toggle`, or when a workcart spawns.
- `DepartureSpeed` -- Speed to use when departing from a stop.
- `DefaultTrackSelection` -- Default track selection to use when a workcart starts being automated.
  - Allowed values: `"Left"` | `"Default"` | `"Right"`.
  - Basically this applies when the plugin loads, when toggling on with `aw.toggle`, or when a workcart spawns.

## Localization

```json
{
  "Error.NoPermission": "You don't have permission to do that.",
  "Error.NoTriggers": "There are no workcart triggers on this map.",
  "Error.TriggerNotFound": "Error: Trigger id #{0}{1} not found.",
  "Error.ErrorNoTrackFound": "Error: No track found nearby.",
  "Error.NoWorkcartFound": "Error: No workcart found.",
  "Error.FullyAutomated": "Error: You cannot do that while full automation is on.",
  "Error.AutomateBlocked": "Error: Another plugin blocked automating that workcart.",
  "Error.UnsupportedTunnel": "Error: Not a supported train tunnel.",
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
