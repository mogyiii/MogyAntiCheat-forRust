# MogyAntiCheat Public API (Draft)

This document defines the planned extension contract for external plugins.
Current state: `Draft` (API not fully implemented yet).

## Versioning

- `ApiVersion`: `0.1-draft`
- Breaking changes must increment major version and be documented.

## Planned Hooks

## `OnMogyAcSuspicion`

When called:
- Triggered when a player exceeds a configured suspicion threshold.

Payload (planned):
- `ulong playerId`
- `string weaponShortName`
- `float accuracy`
- `float maxAccuracy`
- `float weightedScore`
- `float suggestedNerf`
- `double timestampUtc`

Return behavior (planned):
- Read-only notification for now.
- Future option: allow override/cancel by explicit return contract.

## `OnMogyAcPenaltyApplied`

When called:
- Triggered when outgoing damage scaling is applied.

Payload (planned):
- `ulong attackerId`
- `ulong targetId`
- `string weaponShortName`
- `float appliedMultiplier`
- `float originalDamage`
- `float scaledDamage`
- `double timestampUtc`

Return behavior (planned):
- Read-only notification.

## Planned Query Methods

## `GetPlayerAcState(ulong playerId)`

Purpose:
- Retrieve read-only anti-cheat summary for integration plugins.

Planned response fields:
- `globalNerf`
- `weapons[]` containing `accuracy`, `sampleCount`, `weightedScore`

## Compatibility Contract

- External plugins should guard for missing hooks/methods.
- Deprecated fields remain for at least one minor version before removal.

## Example Integration Ideas

- Discord moderator alert plugin.
- Admin dashboard plugin.
- Escalation plugin (manual review queue, evidence snapshots).
