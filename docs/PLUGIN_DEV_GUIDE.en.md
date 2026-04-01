# MogyAntiCheat Plugin Development Quick Guide (EN)

This guide explains how to build your own Oxide/uMod plugin that reacts to `MogyAntiCheat` public events.

Featured example:
- Show a server-wide notification when a player's damage reaches 0% (`appliedMultiplier == 0`).

## 1) Prerequisites

- Rust server with Oxide/uMod.
- `MogyAntiCheat` plugin loaded.
- In `MogyAntiCheat` config:
  - `PublicApi.Enabled = true`
  - `PublicApi.EmitPenaltyEvents = true`

Detailed API fields: `docs/PUBLIC_API.md`

## 2) Where to place your plugin file

Typical location on a running server:
- `server/<identity>/oxide/plugins/`

In this repository, development example folder:
- `plugins/`

Example file:
- `plugins/MogyAcZeroDamageAlert.cs`

## 3) Which hook to subscribe to

For this feature, use:
- `OnMogyAcPenaltyApplied(Dictionary<string, object> payload)`

This hook is called when MogyAntiCheat actually scales outgoing damage.

Useful payload fields:
- `attackerId` (`ulong`)
- `weaponShortName` (`string`)
- `appliedMultiplier` (`float`)
- `originalDamage` (`float`)
- `scaledDamage` (`float`)
- `timestampUtc` (`string`)

## 4) Minimal plugin template

```csharp
using Oxide.Plugins;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("MyAcSubscriber", "YourName", "1.0.0")]
    [Description("MogyAntiCheat penalty event subscriber.")]
    public class MyAcSubscriber : RustPlugin
    {
        private void OnMogyAcPenaltyApplied(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            // Parse fields and run your custom logic here.
        }
    }
}
```

## 5) 0% damage detection logic

Recommended rule:
- Treat it as 0% when `appliedMultiplier <= 0.0001` or `scaledDamage <= 0.0001`.

Why:
- Exact `== 0` checks can be unreliable with floating-point values.

Recommended protection:
- Per-player cooldown (for example 60 seconds) to avoid chat spam.

## 6) Sending server notifications

Global chat message:
- `PrintToChat("...")`

Include these values when possible:
- player name or SteamID
- weapon name
- short cheating-suspicion warning

## 7) Testing steps

1. Load/reload `MogyAntiCheat` and your plugin.
2. Verify `PublicApi` and `EmitPenaltyEvents` are enabled.
3. Enable debug mode if you want faster event visibility (`/ac-debug on`).
4. Reproduce a situation where AC drives outgoing damage to 0%.
5. Confirm the alert appears once during the cooldown window.

## 8) Extension ideas

- Send Discord webhook from your external plugin.
- Admin-only alerts (instead of global chat).
- Write structured evidence logs to file.
- Add multilingual messages (`lang.RegisterMessages`).

## 9) Compatibility tips

- Always handle missing payload keys.
- Wrap `Convert.ToSingle` / `Convert.ToUInt64` in `try/catch`.
- New fields can be added in minor API versions, so rely on stable base fields.

---

Related files:
- `docs/PUBLIC_API.md`
- `docs/examples/MogyAcExampleSubscriber.cs`
- `plugins/MogyAcZeroDamageAlert.cs`
