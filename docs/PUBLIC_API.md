# MogyAntiCheat Public API

This document defines the public extension contract for external plugins.
Current state: `Implemented` (milestone M3 baseline).

## Versioning

- Config key: `PublicApi.ApiVersion`
- Current default: `1.0.0`
- Method: `GetApiVersion()`

Version policy:
- Patch: typo/docs/non-breaking metadata adjustments.
- Minor: additive fields/hooks/methods with backward compatibility.
- Major: breaking change (rename/remove field, change payload shape, or semantic contract break).
- Breaking changes must be documented in `CHANGELOG.md` and `docs/ROADMAP.md`.

## Hook Contract

All hooks are notification-only in `1.0.0` (return value is ignored).

## `OnMogyAcSuspicion`

When called:
- Triggered once when a player+weapon enters suspicious state.
- Trigger conditions are based on current weapon evaluation (`accuracy > maxAccuracy` with enough sample data).

Payload fields:
- `string apiVersion`
- `ulong playerId`
- `string weaponShortName`
- `float accuracy`
- `float maxAccuracy`
- `float weightedScore`
- `float suggestedNerf`
- `int sampleCount`
- `string timestampUtc` (ISO-8601 UTC)

## `OnMogyAcPenaltyApplied`

When called:
- Triggered when outgoing damage scaling is applied (`appliedMultiplier < 1.0`) for non-admin attackers.

Payload fields:
- `string apiVersion`
- `ulong attackerId`
- `ulong targetId`
- `string weaponShortName`
- `float appliedMultiplier`
- `float originalDamage`
- `float scaledDamage`
- `string timestampUtc` (ISO-8601 UTC)

## Query Methods

## `GetApiVersion()`

Returns:
- `string` configured API version (`PublicApi.ApiVersion`).

## `GetPlayerAcState(ulong playerId)`

Purpose:
- Retrieve read-only anti-cheat summary for one player.

Returns:
- `null` when no tracked data exists for the player.
- Otherwise an object with:
  - `apiVersion`
  - `playerId`
  - `globalNerf`
  - `weapons` array
  - `timestampUtc`

Weapon entry fields:
- `weaponShortName`
- `accuracy`
- `sampleCount`
- `weightedScore`
- `maxAccuracy`
- `safeDistance`
- `isSuspicious`
- `suggestedNerf`

## Config Controls

Under `PublicApi`:
- `Enabled` (`bool`) global API event gate.
- `ApiVersion` (`string`) declared contract version.
- `EmitSuspicionEvents` (`bool`) toggles `OnMogyAcSuspicion`.
- `EmitPenaltyEvents` (`bool`) toggles `OnMogyAcPenaltyApplied`.

## Compatibility Contract

- External plugins should check method existence and null responses.
- New fields may be added without notice in minor versions.
- Field removals/renames require major bump.
