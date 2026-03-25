# RFC-0002: Public Extension API for External Plugins

ID: `RFC-0002`
Status: `Accepted`
Target Milestone: `M3`
Author: `Mogy`
Date: `2026-03-25`

## Summary

Introduce a stable, read-only extension API so external Oxide plugins can subscribe to high-signal anti-cheat events and query current anti-cheat state for a player.

## Motivation

Server operators often need ecosystem integrations (moderation dashboards, Discord alerts, review queues) without forking the core anti-cheat plugin.

## Goals

- Expose a documented event contract for suspicion and penalty signals.
- Expose read-only query methods for player state.
- Add explicit API versioning (`PublicApi.ApiVersion`) for compatibility control.

## Non-Goals

- No override/cancel return contract in this version.
- No external HTTP/webhook push in this milestone (handled by M4).

## API Surface

Hooks:
- `OnMogyAcSuspicion(Dictionary<string, object> payload)`
- `OnMogyAcPenaltyApplied(Dictionary<string, object> payload)`

Methods:
- `GetApiVersion()`
- `GetPlayerAcState(ulong playerId)`

Config:
- `PublicApi.Enabled`
- `PublicApi.ApiVersion`
- `PublicApi.EmitSuspicionEvents`
- `PublicApi.EmitPenaltyEvents`

## Compatibility

- Default API version: `1.0.0`.
- Additive changes require minor version increments.
- Breaking shape/semantic changes require major version increments.

## Rollout

- Implement in plugin code.
- Publish contract in `docs/PUBLIC_API.md`.
- Include example consumer plugin under `docs/examples/`.

## Risks

- Event spam if integrations perform heavy operations in hooks.
- Integrators depending on undocumented payload fields.

Mitigation:
- Keep payload fields documented and versioned.
- Keep hooks read-only and lightweight.

## Acceptance Criteria

- External plugin can react to suspicion events.
- External plugin can react to penalty events.
- External plugin can query a player's anti-cheat state.
- Contract version is exposed and documented.
