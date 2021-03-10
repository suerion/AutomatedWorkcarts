## Features

- Automates workcarts with NPC conductors

## Configuration

```json
{
  "TimeAtStation": 30.0
}
```

## Developer Hooks

#### OnWorkcartAutomate

- Called when a workcart is about to be automated
- Returning `false` will prevent the workcart from being automated
- Returning `null` will result in the default behavior

```csharp
object OnWorkcartAutomate(TrainEngine workcart)
```
