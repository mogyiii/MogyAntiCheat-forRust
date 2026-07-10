# MogyAntiCheat Config Schema (Current + Planned)

This document defines config keys, types, defaults, and constraints.

## Current Keys

## `MissExpirySeconds`

- Type: `float`
- Default: `20.0`
- Meaning: Maximum age of pending shot entries used for hit matching.
- Constraint: `> 0`

## `Weapons.<weapon>.MaxAccuracy`

- Type: `float`
- Typical range: `0.25 - 0.75`
- Meaning: Max allowed hit ratio before penalty logic starts.

## `Weapons.<weapon>.SampleCount`

- Type: `int`
- Typical range: `10 - 100`
- Meaning: Rolling history size used for accuracy calculations.

## `Weapons.<weapon>.SafeDistance`

- Type: `float`
- Typical range: `10 - 100`
- Meaning: Distance baseline used by weighted scoring.

## `DefaultLanguage`

- Type: `string`
- Default: `"en"`
- Meaning: Default message language (implemented).
- Constraint: should match an available language code (`en`, `hu`, ...).

## `DebugMode`

- Type: `bool`
- Default: `false`
- Meaning: Enables additional debug logging in server console for suspicion transitions, penalties, and runtime config changes.

## `PublicApi`

- Type: `object`
- Purpose: controls extension API behavior and declared version.

Fields:
- `Enabled` (`bool`, default `true`)
- `ApiVersion` (`string`, default `"1.0.0"`)
- `EmitSuspicionEvents` (`bool`, default `true`)
- `EmitPenaltyEvents` (`bool`, default `true`)


## `Webhook`

- Type: `object`
- Purpose: optional external HTTP push for high-signal anti-cheat events.

Fields:
- `Enabled` (`bool`, default `false`)
- `Endpoint` (`string`, default `""`)
- `AuthToken` (`string`, default `""`)
- `AuthHeader` (`string`, default `"Authorization"`)
- `MaxRetries` (`int`, default `3`, range `0..10`)
- `BaseBackoffSeconds` (`float`, default `1.5`, range `0.25..60`)
- `MaxBackoffSeconds` (`float`, default `20.0`, range `1..300`)
- `RateLimitPerSecond` (`int`, default `2`, range `1..100`)
- `QueueMaxSize` (`int`, default `500`, range `10..5000`)
- `EmitSuspicionEvents` (`bool`, default `true`)
- `EmitPenaltyEvents` (`bool`, default `true`)

## `WeeklyReport`

- Type: `object`
- Purpose: optional, opt-in **anonymized** weekly telemetry summary sent to the plugin developer.
- See `DATA_COLLECTION.md` for the full data-collection notice. Nothing is sent unless `Accepted` is `true`.

Fields:
- `Enabled` (`bool`, default `true`) — feature toggle.
- `Accepted` (`bool`, default `false`) — **consent gate**. No data leaves the server until this is `true`.
- `DiscordWebhookUrl` (`string`, default `""`) — Discord webhook the weekly summary is delivered to.
- `IntervalDays` (`int`, default `7`, min `1`) — minimum days between reports.
- `IncludeKDA` (`bool`, default `true`) — include aggregate kill/death totals.
- `IncludeLagswitch` (`bool`, default `true`) — include aggregate lagswitch incident totals.

Notes:
- Player identity in the report (and in the ML `/ingest` telemetry) is an irreversible per-server
  HMAC-SHA256 hash of the SteamID. The salt lives in `MogyAntiCheat_Salt.json` and is never sent.
- No names, IP addresses, or raw SteamIDs are ever transmitted.
- Last-send timestamp is persisted in `MogyAntiCheat_WeeklyReport.json`.

## Planned Keys (M2+)

## `PenaltyTiers`

- Type: `array<object>`
- Meaning: Multi-threshold penalty definitions.

Planned tier object fields:
- `MinAccuracy` (`float`)
- `MinWeightedScore` (`float`, optional)
- `DamageMultiplier` (`float`, range `0..1`)
- `Action` (`string`, e.g., `warn`, `nerf`, `hard_nerf`)

## Validation Rules

- Invalid numeric values should be clamped or rejected with clear warning logs.
- Unknown keys should be ignored but reported in debug logs.
- On invalid config, plugin should use safe defaults instead of crashing.






## Webhook Behavior Notes

- Webhook delivery is independent from PublicApi.Enabled.
- PublicApi toggles only in-process hooks (OnMogyAcSuspicion, OnMogyAcPenaltyApplied).
- Discord webhook endpoint (discord.com/api/webhooks) automatically receives Discord-compatible payload (username + content).
- Non-Discord endpoints receive the raw anti-cheat event JSON payload.

