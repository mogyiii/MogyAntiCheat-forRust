# MogyAntiCheat Source of Truth

This document defines the intended behavior of the current plugin implementation (`MogyAntiCheat.cs`, version 1.10.0).

## Purpose

MogyAntiCheat is a mitigation-first anti-cheat layer for Rust (Oxide/uMod and Carbon):

- Detect statistically suspicious shooting behavior.
- Apply dynamic damage reduction instead of immediate bans.
- Keep false-positive harm lower than hard punishment systems.

## Scope

In scope:

- Per-player, per-weapon shot history tracking.
- Pending shot correlation with confirmed PvP/NPC hits.
- Accuracy and long-range weighted suspicion scoring.
- Real-time outgoing damage scaling.
- Data persistence and admin commands.
- Public extension API (read-only query + event notifications).
- Optional outbound webhook/HTTP pipeline with queue, rate limiting, retry, and backoff.

Out of scope:

- Process/file-level cheat detection.
- Global behavioral heuristics outside combat events.
- Automated bans or external punishment actions.

## Runtime Data Model

- `_playerStats: Dictionary<ulong, Dictionary<string, WeaponData>>`
  - Key 1: attacker `userID`.
  - Key 2: weapon short prefab name (without `.entity`).
  - `WeaponData.History`: rolling list of `ShotResult { IsHit, Distance }`.
  - `WeaponData.PendingMisses`: pending shot timestamps + distances.
- `_lastHitTime: Dictionary<ulong, float>`
  - Debounce map to avoid duplicate rapid hit processing.
- `_activeSuspicionByWeapon: Dictionary<ulong, HashSet<string>>` 
  - Tracks currently suspicious weapons per player for transition-based event emission.
- `_webhookQueue: Queue<WebhookEnvelope>` + runtime send state
  - In-memory bounded queue for outbound webhook events (`suspicion`, `penalty_applied`).
- `_playerPingStats: Dictionary<ulong, PlayerPingStats>`
  - Per-player EMA ping baseline, running variance (Welford), min/max, anomaly count.
  - Populated every shot fired (both `OnWeaponFired` and `OnEntityTakeDamage`).
  - Runtime-only (not persisted).
- `_playerKDAStats: Dictionary<ulong, PlayerKDAStats>`
  - Per-player kills, deaths, assists counters.
  - Persisted to `MogyAntiCheat_KDA.json`.
- `_damageContributors: Dictionary<ulong, HashSet<ulong>>`
  - Tracks which players dealt damage to each live victim (for assist calculation).
  - Cleared per victim on `OnEntityDeath`. Runtime-only.
- `_lagswitchIncidents: Dictionary<ulong, List<LagSwitchIncident>>`
  - Per-attacker list of detected lagswitch incidents (confidence above threshold). Runtime-only (not persisted).
- `_lastDisconnectTime: Dictionary<ulong, float>`
  - `Time.realtimeSinceStartup` when player last disconnected. Used in reconnect scoring.
- `_connectionDropCount: Dictionary<ulong, int>`
  - Total disconnects per player since plugin load.
- `_telemetryQueue: List<ShotTelemetryEvent>`
  - In-memory buffer of shot, hit, kill, and death events.
  - Flushed on timer or when size reaches `TelemetryQueueMaxSize`.
  - Player identity is stored as `PlayerHash` (irreversible per-server HMAC-SHA256 of the SteamID), never the raw SteamID.
- `_telemetrySalt: string`
  - Random per-server salt used to hash SteamIDs. Loaded from / persisted to `MogyAntiCheat_Salt.json`; never transmitted.
- `_mlSuggestionCache: Dictionary<ulong, Dictionary<string, MLSuggestionCacheEntry>>`
  - Per-player, per-weapon cached ML confidence scores fetched from the ML service.
  - Each entry holds `FetchedAtMs`, `Confidence`, `SuggestedNerfPct`, `AnomalyType`, `Reason`.
  - Entries are considered expired after `MLService.CacheSuggestionsSeconds` seconds.
  - Runtime-only (not persisted).
- `_manualOverrides: Dictionary<ulong, float>`
  - Per-player admin-set damage multiplier [0.0–1.0].
  - When present, applied as a floor penalty (min of computed nerf and manual value).
  - Cleared on `/ac-reset`. Runtime-only (not persisted).
- `_overrideAuditLog: List<OverrideAuditEntry>`
  - Ordered log of all `/ac-override` changes: `TimestampUtc`, `AdminId`, `AdminName`, `TargetId`, `TargetName`, `OldValue`, `NewValue`.
  - Runtime-only (not persisted).

Persistence:

- Saved on `OnServerSave` and `Unload`.
- `MogyAntiCheat_Stats.json` — weapon history (shots/hits) per player.
- `MogyAntiCheat_KDA.json` — K/D/A counters per player.
- `MogyAntiCheat_Events_<YYYYMMDD>.log` — JSON Lines telemetry: one event object per line (shot/hit/kill/death, hashed player IDs), no batch wrapper. The ML `/ingest` POST body is a bare JSON array of the same event objects (no `server_id`/`timestamp`/`batch_id`/`count` envelope).
- `MogyAntiCheat_Salt.json` — per-server random salt for SteamID hashing (never transmitted).
- `MogyAntiCheat_WeeklyReport.json` — last weekly-report send timestamp.
- Pending shots, suspicion cache, ping stats, and damage contributors are runtime-only.

Runtime compatibility:

- Plugin keeps a single shared codebase for Oxide/uMod and Carbon.
- Runtime is detected at startup by loaded assembly name scan.
- Debug/data path uses runtime-aware resolution with Oxide data path fallback.

## Event Flow

### 1) `OnWeaponFired(BaseProjectile weapon, BasePlayer player)`

- Ignore null and NPC attackers.
- Resolve weapon short name.
- Update per-player ping baseline (EMA + Welford variance); delta-ping computed vs last recorded ping.
- On first baseline establishment (`SampleCount == PingBaselineSamples`), emit `OnMogyAcPingBaselineUpdate`.
- Anomaly detection (if `PingMonitoring.Enabled`): spike logged when `|ping - EMA| > threshold * StdDev`; `AnomalyCount` incremented.
- Ensure attacker/weapon tracking state exists.
- Add a pending shot entry (`AddMiss`) with ping and delta-ping.
- Enqueue `ShotTelemetryEvent` (type `shot`).

### 2) `OnEntityTakeDamage(BaseEntity entity, HitInfo info)`

- Ignore invalid events.
- Ignore `BuildingBlock`.
- Continue for real player targets (`BasePlayer`, non-NPC, valid Steam ID).
- In `DebugMode`, include all `BaseCombatEntity` targets (except buildings), including NPC/debug-spawned entities, for hit analysis.
- Continue only for real player attackers (non-NPC, valid Steam ID).
- If target is a real player and `KDATracking.Enabled`: record attacker in `_damageContributors[victim]` for assist calculation.
- Debounce repeated hit events within 0.05 seconds.
- Resolve active weapon and hit distance.
- Sanitize hit distance: a distance above `MaxHitDistance` is treated as unknown (`0`) for
  detection purposes. The hit still counts; only the distance is discarded. The raw measurement is
  what goes into the telemetry event.
- Resolve per-weapon settings (`GetWeaponTuning`) and load global `MissExpirySeconds`.
- Register hit into rolling history (`RegisterHit`).
- Evaluate suspicion for active weapon and emit transition event payload.
- Compute attacker nerf (`GetLowestNerf`) and apply manual override floor (`_manualOverrides`) if present; scale outgoing damage if needed.
- Emit penalty-applied hook after scaling (if enabled).
- Enqueue `ShotTelemetryEvent` (type `hit`).

### 3) `OnEntityDeath(BaseCombatEntity entity, HitInfo info)`

- Only processed when `KDATracking.Enabled`.
- Ignore non-player and NPC victims.
- Increment victim's death counter.
- Enqueue `ShotTelemetryEvent` (type `death`).
- If no valid killer (environment/suicide/NPC): clean up `_damageContributors[victim]` and return.
- Increment killer's kill counter.
- Credit assists: all players in `_damageContributors[victim]` except the killer receive +1 assist.
- Clean up `_damageContributors[victim]`.
- Enqueue `ShotTelemetryEvent` (type `kill`).

## Shot Correlation Rules

`RegisterHit(distance, limit, expiryTime)` behavior:

- Search pending shots from newest to oldest for first non-expired shot.
- If found:
  - Non-expired pending shots before that index become misses.
  - Matched shot becomes hit with real hit distance.
  - Remove processed pending shots up to the matched index.
- If not found:
  - Record hit directly (lag tolerance fallback).
- Enforce rolling cap: trim oldest entries until `History.Count <= limit`.

## Scoring and Penalty Rules

### Accuracy

- `Accuracy = hits / totalHistory` for each weapon.

### Weighted Distance Score

- Uses hit-only history.
- Hit contributes:
  - `1.0` when `Distance <= SafeDistance`
  - `Distance / SafeDistance` when beyond safe distance
- Weighted score = average hit contribution.

### Nerf Computation

For each weapon with enough data (`History.Count >= 10`):

1. Resolve settings via `GetWeaponTuning` (see Weapon Settings Resolution). If nothing resolves,
   or the resolved `MaxAccuracy` is `1.0`, no penalty.
2. If `Accuracy <= MaxAccuracy`, no penalty.
3. Else:
   - `Excess = (Accuracy - MaxAccuracy) / (1 - MaxAccuracy)`
   - `PenaltyFactor = Excess * (WeightedScore > 1 ? WeightedScore^2 : 1)`
   - `CurrentNerf = 1 - PenaltyFactor`
4. Hard clamps:
   - If `Accuracy > 0.95` and `WeightedScore > 1.2`: nerf = `0`
   - If `CurrentNerf < 0.30`: nerf = `0`
5. Global nerf for attacker = lowest nerf among tracked weapons.
6. Final global nerf clamped to `[0, 1]`.

Admin exemption:

- Nerf is not applied when attacker is admin in normal mode.
- In `DebugMode`, nerf is applied to admin attackers too.

## Public API Contract

Config under `PublicApi`:

- `Enabled` (`bool`, default `true`)
- `ApiVersion` (`string`, default `1.1.0`)
- `EmitSuspicionEvents` (`bool`, default `true`)
- `EmitPenaltyEvents` (`bool`, default `true`)

Query methods:

- `GetApiVersion()` → configured API version string.
- `GetPlayerAcState(ulong playerId)` → read-only player anti-cheat snapshot or `null` (includes ping and KDA fields since 1.1.0).
- `GetPlayerPingStats(ulong playerId)` → ping baseline snapshot or `null`.
- `GetPlayerKDAStats(ulong playerId)` → K/D/A counters or `null`.

Hooks:

- `OnMogyAcSuspicion(Dictionary<string, object> payload)`
  - Emitted once when a player+weapon enters suspicious state.
  - Payload includes `pingBaselineAvg` and `pingBaselineStdDev` (since 1.1.0).
- `OnMogyAcPenaltyApplied(Dictionary<string, object> payload)`
  - Emitted when outgoing damage scaling is applied.
  - Payload includes `pingAtEvent`, `pingBaselineAvg`, `pingAnomaly` (since 1.1.0).
- `OnMogyAcPingBaselineUpdate(Dictionary<string, object> payload)`
  - Emitted once per player when ping baseline is first established (since 1.1.0).
  - Gated by `PublicApi.Enabled`.
- `OnMogyAcLagswitchDetected(Dictionary<string, object> payload)`
  - Emitted when a kill's lagswitch confidence score exceeds `LagswitchDetection.Threshold` (since 1.2.0).
  - Gated by `PublicApi.Enabled`.
  - Payload: `playerId`, `victimId`, `weaponShortName`, `confidence`, `pingAtKill`, `pingBaselineAvg`, `pingSpike`, `killAccuracy`, `wasHeadshot`, `distance`, `reconnectScore`, `timestampUtc`.

Query method (since 1.3.0):
- `GetMLPenaltySuggestion(ulong playerId, string weapon)` → cached ML suggestion or `null`.
  - Returns `Dictionary<string, object>` with `mlConfidence`, `mlSuggestedNerfPct`, `mlAnomalyType`, `mlReason` when a cached entry exists for the player+weapon.
  - Returns `null` if the ML service is disabled, no data is cached, or the cache entry has expired.

## Configuration Contract

Top-level keys:

- `Weapons` (dictionary by weapon short name)
- `MissExpirySeconds` (float)
- `DefaultLanguage` (string, default `en`)
- `DebugMode` (bool, default `false`)
- `PublicApi` (object)
- `Webhook` (object)
- `MLService` (object)
  - `Enabled` (bool, default `false`)
  - `Endpoint` (string, default `""`) — base URL of ML service, e.g. `http://ml-service:8080`
  - `AuthToken` (string, default `""`) — sent as `Authorization: Bearer <token>`
  - `TimeoutSeconds` (int, default `5`)
  - `CacheSuggestionsSeconds` (int, default `60`)
  - `FallbackToLocalScoring` (bool, default `true`) — if `true`, plugin scoring runs normally when ML is unavailable

Each weapon requires:

- `MaxAccuracy` (float)
- `SampleCount` (int)
- `SafeDistance` (float)

Also top-level:

- `MaxHitDistance` (float, default `500`) — distance sanity bound; `0` disables it
- `WeaponFallback` (object) — `Enabled` (bool, default `true`) plus `Families`, one entry per
  weapon family with the same three fields

### Weapon Settings Resolution

`GetWeaponTuning(weaponName)` resolves in this order, and the result is cached per prefab name
until the weapon config changes:

1. **A `Weapons` entry**, matched by: exact key; case-insensitive key; built-in alias
   (`smg` → `smg.2`, `semi_auto_rifle` → `rifle.semiauto`, `semi_auto_pistol` →
   `pistol.semiauto`, `hunting_bow` → `bow.hunting`); the segment after the last dot
   (`m39` → `rifle.m39`); separator- and order-insensitive token signature
   (`bolt_rifle` → `rifle.bolt`, `shotgun_pump` → `shotgun.pump`).
2. **`WeaponFallback.Families[<family>]`** when enabled. Family is inferred from name fragments:
   `explosive`, `lmg`, `sniper`, `semi_rifle`, `auto_rifle`, `smg`, `shotgun`, `pistol`,
   `projectile`, first match wins.
3. **Unresolved** — `MaxAccuracy = 1.0`, `SampleCount = 40`, `SafeDistance = 1.0`. The weapon is
   never flagged, and the plugin emits a one-time console warning naming it.

A resolved `MaxAccuracy` of `1.0` (the `explosive` family) is not the same as unresolved: it is
covered on purpose, and no warning is emitted. `/ac-why` reports which of the three applied.

## Admin Command Contract

- `/ac-check [name]`
  - Admin-only.
  - Without argument: self-report.
  - With argument: report target player.
- `/ac-list`
  - Admin-only.
  - Displays online players with average tracked accuracy and global damage multiplier.
- `/ac-reset [name]`
  - Admin-only.
  - Removes target player's tracked state from memory (clears stats, ping, KDA, damage contributors).
- `/ac-stats [name]`
  - Admin-only.
  - Shows K/D/A, ping baseline, and per-weapon accuracy for a player.
- `/ac-lagswitch-audit [name]`
  - Admin-only.
  - Shows lagswitch incident timeline with ping spike, kill quality, and reconnect scores.
  - Flags pattern when ≥ `MinIncidentsForPattern` incidents within 24h exceed `PatternThreshold` confidence.
- `/ac-lang <code>`
  - Admin-only.
  - Sets and persists `DefaultLanguage` in plugin config.
- `/ac-debug <on|off>`
  - Admin-only.
  - Runtime toggle for `DebugMode` with config persistence.
  - In debug mode, admin nerf is enabled and NPC targets are included.
- `/ac-weapon <weapon|active> <MaxAccuracy|SampleCount|SafeDistance> <value>`
  - Admin-only.
  - Updates `Weapons` thresholds in-game and persists config.
- `/ac-ml-feedback <playerName> <confirmed_cheater|false_positive|uncertain>`
  - Admin-only.
  - Sends outcome feedback to the configured ML service endpoint (`POST /feedback`).
  - No-op (with error message) when `MLService.Enabled` is `false` or endpoint is not configured.
- `/ac-dashboard`
  - Admin-only.
  - Prints a live tabular view of all tracked players: name, current nerf, ping average, lagswitch incident count, K/D/A, and manual override status.
- `/ac-override <playerName> <0-100|off>`
  - Admin-only.
  - Sets a manual damage reduction percentage (0 = no reduction, 100 = full block) for a specific player.
  - `off` clears the override and returns the player to algorithm-computed nerf.
  - Every change is recorded in `_overrideAuditLog` with admin identity and timestamps.
  - Cleared on `/ac-reset`.
- `/ac-chart <playerName> <accuracy|ping|kda>`
  - Admin-only.
  - Renders an ASCII visualization in chat: sparkline bar chart for accuracy (per-weapon), min/avg/max ruler for ping, and proportional K/D/A bars.
- `/ac-export csv`
  - Admin-only.
  - Writes all tracked player data (per-weapon accuracy, KDA, ping baseline, lagswitch incidents, override) to a timestamped CSV file in `_runtimeDataDirectory`.
- `/ac-config-tune <param> <value>`
  - Admin-only.
  - Supported parameters: `MissExpirySeconds`, `LagswitchDetection.Threshold`, `PingMonitoring.AnomalyThresholdStdDev`.
  - Changes are validated, applied immediately, and persisted to config.
- `/ac-suggest`
  - Admin-only.
  - Queries the ML service `/config-recommend` endpoint and displays a diff of current vs. recommended config values.
  - No-op when ML service is disabled or unreachable.

## Known Constraints

- Distance for fired shots is currently stored as `0` in pending queue; only confirmed hits store real distance.
- Debounce window (`0.05s`) may hide edge-case rapid multi-hit events.
- Suspicion is strictly combat-statistical and does not infer cheat type.

## Operational Guidance

- Treat this as a balancing tool, not final enforcement.
- Tune values per server style and weapon meta.
- Revisit thresholds after major Rust updates or recoil/combat changes.

## Change Management

When plugin behavior changes, update all of:

1. `MogyAntiCheat.cs` version string.
2. `README.md`.
3. This file (`docs/SOURCE_OF_TRUTH.md`).


## Weekly Report Contract (opt-in telemetry)

Config under `WeeklyReport` (see `DATA_COLLECTION.md` and `CONFIG_SCHEMA.md`):

- `Enabled`, `Accepted`, `DiscordWebhookUrl`, `IntervalDays`, `IncludeKDA`, `IncludeLagswitch`.
- `DiscordWebhookUrl` default comes from `DefaultWeeklyReportWebhook` in `MogyAntiCheat.cs`, which is a
  `__WEEKLY_WEBHOOK__` sentinel in the public source (resolves to empty → sends nothing). The official
  release DLL injects the real webhook at build time (`build-release.ps1`). Overridable per server.

Behavior:

- A timer runs hourly (`WeeklyReportTick`). Nothing is sent unless `Enabled` and `Accepted` are both `true`
  and a webhook URL is configured.
- On first activation the last-send timestamp is seeded; the first report is sent only after `IntervalDays` elapse.
- The report is an aggregated, anonymized summary (server hostname, tracked-player count, shot/hit totals and
  overall accuracy, optional lagswitch and K/D totals, and a top-N list of suspicious players by hashed ID).
- Delivered as a Discord-compatible payload (`username` + `content`), capped to Discord's content length.
- All player identifiers in the report are per-server HMAC hashes; no names, IPs, or raw SteamIDs are included.
- On load, `LogDataCollectionDisclosure` prints the current on/off state to the server console.
- `/ac-weekly-now` (admin) sends a report immediately for testing; requires `Accepted = true` and a webhook URL.

## Webhook Delivery Contract

Config under Webhook:

- Enabled, Endpoint, optional AuthToken + AuthHeader.
- RateLimitPerSecond, QueueMaxSize, MaxRetries, BaseBackoffSeconds, MaxBackoffSeconds.
- EmitSuspicionEvents, EmitPenaltyEvents.

Behavior:

- Suspicion and penalty events are enqueued and sent asynchronously.
- Rate limiting is enforced per second.
- Failed sends are retried with exponential backoff, then dropped after max retries.
- Anti-cheat core flow is fail-safe and does not block gameplay on webhook errors.
- Discord webhook endpoints receive Discord-compatible request body (username + content).



