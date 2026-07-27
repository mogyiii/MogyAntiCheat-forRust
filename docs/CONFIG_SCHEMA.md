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
- Note: the weighted score is **squared** in the penalty term, so a value that ordinary
  engagements exceed multiplies the penalty for normal play. `ml-service/train.py` derives it
  from the p90 of observed hit distances, bounded so a maximum-range hit cannot amplify by
  more than 3x.

## Weapon key matching

The config is keyed by Rust item shortnames (`rifle.ak`, `smg.2`), but the server reports prefab
short names (`ak47u`, `smg`), and Carbon drops the category prefix entirely. A weapon is matched
in this order:

1. exact key,
2. case-insensitive exact key,
3. built-in alias (`smg` -> `smg.2`, `semi_auto_rifle` -> `rifle.semiauto`, ...),
4. the segment after the last dot (`m39` -> `rifle.m39`),
5. separator- and order-insensitive token signature (`bolt_rifle` -> `rifle.bolt`,
   `shotgun_pump` -> `shotgun.pump`).

If none match, `WeaponFallback` applies. A weapon that reaches neither is never flagged, and the
plugin logs a one-time warning naming it.

## `WeaponFallback`

- Type: `object`
- Purpose: detection settings for weapons the `Weapons` block does not name — modded, renamed, or
  newly added ones. Without it those weapons are exempt from checking entirely.

Fields:
- `Enabled` (`bool`, default `true`) — set `false` to restore the old "unknown weapon is never
  checked" behaviour.
- `Families` (`object`) — one entry per family, each with the same `MaxAccuracy` / `SampleCount` /
  `SafeDistance` fields as a weapon entry. Families: `auto_rifle`, `smg`, `lmg`, `semi_rifle`,
  `sniper`, `shotgun`, `pistol`, `projectile`, `explosive`.

A weapon's family is inferred from name fragments (`m249`/`hmlmg`/`minigun` -> `lmg`, `bolt`/`l96`
-> `sniper`, and so on). `explosive` ships with `MaxAccuracy = 1.0` on purpose: a rocket registers
a hit on virtually every shot, so hit ratio carries no signal there.

The shipped family thresholds are deliberately lenient cross-server guesses meant to catch blatant
outliers, not to fine-tune. Run `ml-service/train.py` on your own event logs to replace them with
measured values — see `docs/ML_TRAINING.md`.

## `DailyReport`

- Type: `object`
- Purpose: a recurring digest of the most suspicious players, delivered to **your own** Discord
  webhook. Distinct from `WeeklyReport` in every way that matters: that one is an opt-in,
  anonymized summary sent to the plugin developer; this is your server's own data about your own
  players, so it defaults to real names.

Fields:
- `Enabled` (`bool`, default `false`)
- `DiscordWebhookUrl` (`string`, default `""`) — your webhook. Nothing is sent without it.
- `IntervalHours` (`int`, default `24`, range `1..168`) — delivery cadence. The scheduler ticks
  every 15 minutes and sends when the interval has elapsed; enabling the feature seeds the clock
  rather than firing immediately.
- `TopCount` (`int`, default `10`, range `1..25`) — maximum players listed.
- `MinSuspicionScore` (`float`, default `0.35`) — players below this are omitted, so a quiet day
  sends "nothing to review" instead of a list of ordinary players.
- `IncludeNames` (`bool`, default `true`) — real display names. Turn off if the webhook lands in a
  channel wider than your staff; the report then falls back to the hashed identifier.
- `IncludeSteamIds` (`bool`, default `true`)
- `IncludeLagswitch` (`bool`, default `true`)
- `IncludeKDA` (`bool`, default `true`)

Command: `/ac-daily-now` sends immediately without waiting for the schedule and without advancing
it — for testing the webhook. It works even when `Enabled` is `false`, as long as a URL is set.

### What the score means

Each listed player gets a 0-1 suspicion score, used only to order the list:

- **60%** — how far the weapon's accuracy sits above *that weapon's own* threshold. The accuracy is
  first passed through a **Wilson score lower bound**, so a short window counts for less: eleven
  hits from eleven shots is mostly luck and ranks below forty-of-forty-five. Without this the list
  fills with the plugin's own metric artifact, since `RegisterHit` drops misses older than
  `MissExpirySeconds` and a slow-firing player reads as perfect.
- **25%** — the damage reduction the plugin already applied.
- **15%** — lagswitch incidents within the reporting period.

Note on the period: accuracy comes from each weapon's **rolling window** (the last `SampleCount`
shots), which is current state rather than a total for the period — the report labels it as such.
Lagswitch incidents are timestamped and genuinely limited to the interval.

## `AimTracking`

- Type: `object`
- Purpose: records how the player's view arrived on target before each shot. Hit ratio describes the
  result of aiming; this describes the approach, which is what separates assisted aim from a good
  player. Feeds `AimDeltaDeg` / `SnapDeg` / `SnapSettleMs` on every `shot` telemetry event.

Fields:
- `Enabled` (`bool`, default `true`) — set `false` to stop sampling entirely; shot events then
  report `-1` for all three fields.
- `SampleHz` (`float`, default `20`, range `5..50`) — view-direction sampling rate. A snap completes
  well inside 100 ms, so 20 Hz resolves it. Only players holding a ranged weapon are sampled.
- `WindowMs` (`float`, default `400`, range `100..2000`) — how much history each shot is analysed
  against.

The fields are collected for offline analysis only — the plugin does not act on them. See
`docs/ML_TRAINING.md`.

## `MaxHitDistance`

- Type: `float`
- Default: `500.0`
- Meaning: hits reporting a distance above this have the distance discarded (the hit still counts).
- Why: `Vector3.Distance(info.HitPositionWorld, info.PointStart)` degenerates into a distance from
  the world origin when `PointStart` is unset, producing 1000-2000 m readings on a 4k map. Since
  the weighted score is squared in the penalty, one such reading can null a player's damage.
- Constraint: `>= 0`. `0` disables the check.
- Note: the event log keeps the raw measurement either way, so the ML trainer can still report how
  often the reading breaks.

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
- `DiscordWebhookUrl` (`string`) — Discord webhook the weekly summary is delivered to. Empty by default
  in source/`.cs` builds (the source holds a `__WEEKLY_WEBHOOK__` sentinel); the official release DLL is
  built with the developer's webhook injected (see `docs/DLL_BUILD.md`). Override it with your own, or
  leave `Accepted = false` to send nothing.
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

