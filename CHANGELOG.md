# Changelog

All notable changes to this project should be documented in this file.

The format is based on Keep a Changelog.

## [1.9.8] - 2026-05-17

### Added (Milestone M9: In-game Admin Tools & Visualization)
- Six new admin commands:
  - `/ac-dashboard` — Live tabular view of all tracked players (nerf, ping, lagswitch incidents, K/D/A, override status).
  - `/ac-override <player> <0-100|off>` — Set manual damage reduction per player with full audit trail.
  - `/ac-chart <player> <accuracy|ping|kda>` — ASCII sparkline charts for accuracy trends, ping baseline ruler, and K/D/A bars.
  - `/ac-export csv` — Export all player stats to timestamped CSV file (player_id, weapon, accuracy, shots, K/D/A, ping stats, lagswitch incidents, override).
  - `/ac-config-tune <param> <value>` — Live config parameter adjustment (MissExpirySeconds, LagswitchDetection.Threshold, PingMonitoring.AnomalyThresholdStdDev).
  - `/ac-suggest` — Query ML service `/config-recommend` endpoint for auto-tuned config recommendations and display diff.
- Manual override audit trail: `_overrideAuditLog` records admin ID, target player, old/new override values, and timestamp for every override change.
- Comprehensive testing documentation: `docs/TESTING.md` with 14 test categories, 80+ test cases, and validation checklist.

### Changed
- Plugin version bumped to `1.9.8`.
- API version remains `1.3.0` (no breaking changes to public API, only new admin CLI commands).
- Damage application now checks `_manualOverrides` dictionary: manual override acts as a penalty floor (min of algorithm nerf and manual multiplier).
- `_playerStats` now always cleared in `/ac-reset` along with all related tracking dicts, including `_manualOverrides`.
- README.md features expanded to detail all nine milestones (M1–M9); version badge updated to 1.9.8.

### Documentation
- `docs/SOURCE_OF_TRUTH.md` updated: version 1.9.8, added manual override data model, all seven new admin commands documented.
- `docs/ROADMAP.md` and `docs/ROADMAP.hu.md`: M9 status set to `Done`.
- `docs/RFCs/RFC-0009-admin-dashboard-export.md`: status changed to `Accepted`.
- `docs/TESTING.md`: new comprehensive testing guide covering core functionality, commands, data persistence, hooks, performance, and edge cases.

## [1.9.7] - 2026-05-17

### Added (Milestone M8: ML/Neural Network Service Module)
- ML service integration (`MLService` config block): optional external confidence scoring via REST API.
- Config params: `MLService.Enabled`, `MLService.Endpoint`, `MLService.AuthToken`, `MLService.TimeoutSeconds`, `MLService.CacheSuggestionsSeconds`, `MLService.FallbackToLocalScoring`.
- ML penalty suggestion caching: `_mlSuggestionCache` stores per-player, per-weapon ML confidence scores (time-bounded).
- Admin command `/ac-ml-feedback <player> <confirmed_cheater|false_positive|uncertain>` to submit outcome feedback to ML service.
- `GetMLPenaltySuggestion(ulong playerId, string weapon)` public API method for external plugins to query cached ML scores.
- ML fields added to `OnMogyAcPenaltyApplied` hook payload: `mlConfidence`, `mlSuggestedNerfPct`, `mlAnomalyType`, `mlApplied`.
- Public API version bumped to `1.3.0`.
- ML service stub: `ml-service/server.py` (Flask implementation with `/ingest`, `/penalty-suggestion`, `/config-recommend`, `/feedback` endpoints).
- ML service documentation: `ml-service/README.md` with endpoint contracts, authentication, and production notes.

### Changed
- Plugin version bumped to `1.9.7`.
- Suspension/penalty event handling now asynchronously fetches ML suggestions (non-blocking `webrequest.Enqueue`).
- Telemetry flushing to ML service: batched shot/hit/kill/death events posted to `/ingest` with server_id and batch_id.

### Documentation
- `docs/RFCs/RFC-0008-ml-service-module.md`: status changed to `Accepted`.
- `docs/ROADMAP.md` and `docs/ROADMAP.hu.md`: M8 status set to `Done`.
- `docs/SOURCE_OF_TRUTH.md`: updated to version 1.9.7, added ML service contract and data model.
- `docs/PUBLIC_API.md`: updated to version 1.3.0, documented `GetMLPenaltySuggestion` and ML fields in penalty payload.

## [1.9.6] - 2026-05-16

### Added (Milestone M7: Lagswitch Detection)
- Lagswitch incident detection: composite confidence scoring from ping spikes, kill quality, and reconnect patterns.
- Config: `LagswitchDetection` block with `Enabled`, `Threshold`, `PatternThreshold`, `MinIncidentsForPattern`, `PingSpikeMinimumMs`, `PreKillWindowMs`.
- Admin command `/ac-lagswitch-audit [player]` to display forensic timeline of lagswitch incidents with ping spike details, accuracy, headshot status, and reconnect score.
- Pattern warning: flagged when ≥3 incidents within 24h with avg confidence ≥0.75.
- Hook `OnMogyAcLagswitchDetected` emitted when incident confidence exceeds threshold; payload includes player, victim, weapon, confidence, ping stats, kill quality, reconnect score.
- Tracking: `_lagswitchIncidents`, `_lastDisconnectTime`, `_connectionDropCount` dicts for incident recording and reconnect scoring.
- Player disconnect hook `OnPlayerDisconnected` to update last disconnect time and track total disconnects.

### Changed
- Plugin version bumped to `1.9.6`.
- `OnEntityDeath` hook extended: evaluates kill for lagswitch confidence and emits hook if threshold exceeded.
- Admin command `/ac-reset` now also clears lagswitch incidents and disconnect tracking.
- Public API version bumped to `1.2.0`.

### Documentation
- `docs/RFCs/RFC-0006-lagswitch-detection.md`: status changed to `Accepted`.
- `docs/ROADMAP.md` and `docs/ROADMAP.hu.md`: M7 status set to `Done`.
- `docs/SOURCE_OF_TRUTH.md`: updated to version 1.9.6, added lagswitch data model and event flow.
- `docs/PUBLIC_API.md`: updated to version 1.2.0, documented `OnMogyAcLagswitchDetected` hook and `GetLagswitchStats` query method.

## [1.9.5] - 2026-05-15

### Added (Milestone M6: Enhanced Logging & KDA + Ping Monitoring)
- K/D/A (Kills/Deaths/Assists) tracking: `_playerKDAStats` per player, persisted to `MogyAntiCheat_KDA.json`, survives restarts.
- Per-player ping baseline: EMA + Welford online variance computation; baseline established after 50 samples.
- Ping anomaly detection: spikes > 2.5σ above baseline logged; `AnomalyCount` incremented per player.
- Admin command `/ac-stats [player]` to display K/D/A, KDR, ping baseline (avg/min/max/stddev), and per-weapon accuracy.
- Assist credit system: damage contributors tracked in `_damageContributors` during `OnEntityTakeDamage`, credited on kill in `OnEntityDeath`.
- Hook `OnMogyAcPingBaselineUpdate` emitted once per player when baseline established; includes ping stats snapshot.
- Telemetry event logging: shot/hit/kill/death events buffered in `_telemetryQueue`, flushed to `MogyAntiCheat_Events_<YYYYMMDD>.log` (JSON Lines format).
- Telemetry config: `EventLogging` block with `Enabled`, `FlushIntervalSeconds`, `QueueMaxSize`.
- Config: `KDATracking.Enabled` and `PingMonitoring` blocks with subkeys.

### Changed
- Plugin version bumped to `1.9.5`.
- `OnEntityTakeDamage` now tracks damage contributors and records telemetry; ping baseline updated on every shot in `OnWeaponFired`.
- `GetPlayerAcState` query extended: payload now includes `pingAvg`, `pingStdDev`, `pingAnomalyCount`, `kills`, `deaths`, `assists` fields.
- Public API version bumped to `1.1.0`.

### Documentation
- `docs/RFCs/RFC-0005-enhanced-logging-kda.md`: status changed to `Accepted`.
- `docs/ROADMAP.md` and `docs/ROADMAP.hu.md`: M6 status set to `Done`.
- `docs/SOURCE_OF_TRUTH.md`: updated to version 1.9.5, added K/D/A and ping data model sections.
- `docs/PUBLIC_API.md`: updated to version 1.1.0, added ping and KDA query methods and hook fields.

## [1.9.4] - 2026-05-14

### Added (Milestone M5: Carbon Mod Compatibility)
- Runtime detection: on `Init`, plugin scans loaded assemblies to identify Oxide vs Carbon.
- Runtime-aware data/config/debug path resolution: Carbon uses its own directories; Oxide uses standard oxide/ paths.
- Compatibility matrix: core anti-cheat logic unchanged; framework-specific APIs abstracted (hooks, data paths, permissions).
- Installation/deployment docs for both runtimes.
- `CARBON_COMPATIBILITY.md` detailing framework differences, path resolution, and validation checklist.

### Changed
- Plugin version bumped to `1.9.4`.
- Shared codebase: single `MogyAntiCheat.cs` now supports both Oxide/uMod and Carbon without branches.
- Data path resolution unified: `_runtimeDataDirectory` computed once at `Init`, used for all file I/O.
- Admin/moderator permission checks adapted to both frameworks.

### Documentation
- `docs/RFCs/RFC-0004-carbon-compatibility.md`: status changed to `Accepted`.
- `docs/ROADMAP.md` and `docs/ROADMAP.hu.md`: M5 status set to `Done`.
- `docs/CARBON_COMPATIBILITY.md`: new detailed compatibility guide.
- `docs/SOURCE_OF_TRUTH.md`: updated to version 1.9.4, added runtime compatibility notes.

## [1.7.0] - 2026-03-25

### Added
- M3 public extension API implementation in `MogyAntiCheat.cs`:
  - Public API config block: `PublicApi.Enabled`, `PublicApi.ApiVersion`, `PublicApi.EmitSuspicionEvents`, `PublicApi.EmitPenaltyEvents`.
  - Hook events: `OnMogyAcSuspicion`, `OnMogyAcPenaltyApplied`.
  - Query methods: `GetApiVersion()`, `GetPlayerAcState(ulong playerId)`.
- Example external consumer plugin: `docs/examples/MogyAcExampleSubscriber.cs`.

### Changed
- Plugin version updated to `1.7.0`.
- `docs/PUBLIC_API.md` moved from draft to implemented contract.
- Roadmap status updated: M3 marked `Done` in `docs/ROADMAP.md` and `docs/ROADMAP.hu.md`.
- Config and behavior documentation updated:
  - `docs/CONFIG_SCHEMA.md`
  - `docs/SOURCE_OF_TRUTH.md`
  - `README.en.md`
  - `README.md`

## [1.6.8] - 2026-03-24

### Added
- Documentation foundation for milestone-based planning:
  - `docs/ROADMAP.md`
  - `docs/RFCs/TEMPLATE.md`
  - `docs/PUBLIC_API.md`
  - `docs/CONFIG_SCHEMA.md`
- English documentation file introduced (`README.en.md`, later consolidated into `README.md`).
- Source of truth documentation: `docs/SOURCE_OF_TRUTH.md`.
- First concrete RFC for M1 i18n: `docs/RFCs/RFC-0001-i18n-foundation.md`.
- Language pack files: `oxide/lang/en/MogyAntiCheat.json`, `oxide/lang/hu/MogyAntiCheat.json`.

### Changed
- Reworked Hungarian `README.md` structure and aligned version badge to `1.6.8`.
- i18n foundation implemented in plugin code (`MogyAntiCheat.cs`): language keys, `DefaultLanguage`, and localized admin command messages.
- Added admin command `/ac-lang <languageCode>` for runtime default language switching.


