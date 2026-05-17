# MogyAntiCheat Public API

This document defines the public extension contract for external plugins.
Current state: `Implemented` (milestone M7 baseline).

## Versioning

- Config key: `PublicApi.ApiVersion`
- Current default: `1.2.0`
- Method: `GetApiVersion()`

Version policy:
- Patch: typo/docs/non-breaking metadata adjustments.
- Minor: additive fields/hooks/methods with backward compatibility.
- Major: breaking change (rename/remove field, change payload shape, or semantic contract break).
- Breaking changes must be documented in `CHANGELOG.md` and `docs/ROADMAP.md`.

## Hook Contract

All hooks are notification-only (return value is ignored).

---

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
- `double pingBaselineAvg` — player's EMA ping baseline at the time of event (0 if no baseline yet)
- `double pingBaselineStdDev` — standard deviation of player's ping baseline (0 if no baseline yet)
- `string timestampUtc` (ISO-8601 UTC)

---

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
- `int pingAtEvent` — attacker's ping at the moment damage was processed
- `double pingBaselineAvg` — attacker's EMA ping baseline (0 if no baseline yet)
- `bool pingAnomaly` — true if `pingAtEvent` is a statistically anomalous spike relative to baseline
- `string timestampUtc` (ISO-8601 UTC)

---

## `OnMogyAcPingBaselineUpdate`

When called:
- Fired once per player when their ping baseline is first established (after reaching the minimum sample threshold).
- Gated by `PublicApi.Enabled`.

Payload fields:
- `string apiVersion`
- `ulong playerId`
- `double avg` — EMA ping average
- `int min` — minimum observed ping
- `int max` — maximum observed ping
- `double stddev` — standard deviation
- `long sampleCount`
- `string timestampUtc` (ISO-8601 UTC)

---

## `OnMogyAcLagswitchDetected`

When called:
- Triggered when a kill's computed lagswitch confidence exceeds `LagswitchDetection.Threshold`.
- Gated by `PublicApi.Enabled`.

Payload fields:
- `string apiVersion`
- `ulong playerId` — suspected attacker
- `ulong victimId`
- `string weaponShortName`
- `float confidence` — 0.0–1.0 composite score
- `int pingAtKill`
- `double pingBaselineAvg`
- `int pingSpike` — `pingAtKill - pingBaselineAvg`
- `float killAccuracy` — attacker weapon accuracy at time of kill
- `bool wasHeadshot`
- `float distance`
- `float reconnectScore` — 0–1; >0 if attacker reconnected within the pre-kill window
- `string timestampUtc` (ISO-8601 UTC)

---

## Query Methods

### `GetApiVersion()`

Returns:
- `string` configured API version (`PublicApi.ApiVersion`).

---

### `GetPlayerAcState(ulong playerId)`

Purpose:
- Retrieve read-only anti-cheat summary for one player.

Returns:
- `null` when no tracked data exists for the player.
- Otherwise an object with:
  - `apiVersion`
  - `playerId`
  - `globalNerf`
  - `weapons` array (see weapon entry fields below)
  - `pingAvg` — EMA ping average (0 if no data)
  - `pingStdDev` — ping standard deviation (0 if no data)
  - `pingAnomalyCount` — total ping anomalies detected
  - `kills`
  - `deaths`
  - `assists`
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

---

### `GetPlayerPingStats(ulong playerId)`

Returns:
- `null` if no ping data recorded for the player.
- Otherwise:
  - `double avg` — EMA ping average
  - `int min`
  - `int max`
  - `double stddev`
  - `long sampleCount`
  - `bool hasBaseline`
  - `int anomalyCount`

---

### `GetPlayerKDAStats(ulong playerId)`

Returns:
- `null` if no KDA data recorded for the player.
- Otherwise:
  - `int kills`
  - `int deaths`
  - `int assists`
  - `float kdaRatio` — kills / deaths (deaths=0 → returns kills as-is)

---

### `GetLagswitchStats(ulong playerId)`

Returns:
- `null` if no lagswitch incidents recorded.
- Otherwise:
  - `int incidentCount24h`
  - `int incidentCount7d`
  - `int incidentCountTotal`
  - `float avgConfidence`
  - `bool patternDetected` — true when `incidentCount24h ≥ MinIncidentsForPattern` and `avgConfidence ≥ PatternThreshold`

---

## Config Controls

Under `PublicApi`:
- `Enabled` (`bool`) global API event gate (also gates `OnMogyAcPingBaselineUpdate`).
- `ApiVersion` (`string`) declared contract version.
- `EmitSuspicionEvents` (`bool`) toggles `OnMogyAcSuspicion`.
- `EmitPenaltyEvents` (`bool`) toggles `OnMogyAcPenaltyApplied`.

Under `PingMonitoring`:
- `Enabled` (`bool`) — controls whether anomaly detection runs; ping sampling always occurs.
- `AnomalyThresholdStdDev` (`float`) — multiplier for spike detection threshold.

Under `KDATracking`:
- `Enabled` (`bool`) — controls whether K/D/A and assist tracking run.

---

## Compatibility Contract

- External plugins should check method existence and null responses.
- New fields may be added without notice in minor versions.
- Field removals/renames require major bump.

---

## Webhook Integration Note

- Webhook HTTP delivery is configured under `Webhook` and is separate from this Public API contract.
- `PublicApi.Enabled` and related `PublicApi.Emit*` flags control only in-process hook emission.
- Webhook event sending for suspicion/penalty remains available when `Webhook.Enabled=true`, even if public API hooks are disabled.
