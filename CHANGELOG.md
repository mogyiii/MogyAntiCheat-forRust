# Changelog

All notable changes to this project should be documented in this file.

The format is based on Keep a Changelog.

## [Unreleased]

### Added
- **ML training pipeline** (`ml-service/train.py`) that calibrates the plugin config from real event
  logs. Replays telemetry through a Python replica of the plugin's own `WeaponData` window logic, then
  fits `MaxAccuracy` / `SampleCount` / `SafeDistance` / `MissExpirySeconds` to the observed
  distribution instead of hand-picked constants. Outputs `model.json`, a paste-ready
  `config-recommendation.json`, and `reports/training-report.md`. Stdlib only — no numpy/sklearn.
- `ml-service/server.py` now serves that trained model: real per-weapon anomaly baselines on
  `/ingest`, calibrated values on `/config-recommend`, persisted admin verdicts on `/feedback`, plus
  new `/model-info` and `/reload-model` endpoints. Scores carry per-feature contributions so a
  suggestion can be explained. Falls back to reporting `model_loaded: false` rather than guessing.
- `ml-service/selftest.py` — 124 assertions over the replay replica, calibration, scorer and
  endpoints. The replay cases are pinned to values worked out by hand from `MogyAntiCheat.cs`, so
  they fail if the two implementations drift apart.
- `docs/ML_TRAINING.md` — how each threshold is derived, and the findings from the first run.

- ML feature `head_streak` (longest run of consecutive headshots) — measured at 26 robust sigmas
  between the population median and the most extreme player, and largely independent of accuracy
  (r = +0.42), making it the strongest single signal the existing telemetry contains. Features
  `aim_snap_speed` and `aim_settle_ms` are declared for the new aim telemetry but stay **inert**
  until logs carry it: a feature with no fitted baseline scores zero, so nothing changes until a
  training run sees real values. Two other candidates were measured and rejected — first-shot hit
  rate (r = +0.88 with accuracy, i.e. redundant) and trigger-interval quantisation (independent but
  only 2.9 sigma at the extreme).
- `ml-service/report_charts.py` — anonymized per-player statistics page (`reports/player-statistics.html`):
  one dot per player per weapon for accuracy vs volume, hit distance, headshot rate and anomaly
  percentile, with the current and calibrated `MaxAccuracy` drawn as reference lines. Players appear
  as opaque `P-nnn` labels with no relationship to their SteamID, so the page is shareable.

### Fixed
- **Weapon config coverage.** `ResolveWeaponConfigKey` only matched the segment after the last dot,
  so prefab names the server actually reports (`smg`, `bolt_rifle`, `shotgun_pump`, `bow_hunting`)
  matched nothing and `EvaluateWeapon` set `MaxAccuracy = 1.0` — no checking at all. On the reference
  server that silently exempted **33.6% of all shots** (`smg` alone was 24%). Matching now also tries
  case-insensitive keys, a built-in alias table (`smg` → `smg.2`), and a separator/order-insensitive
  token signature (`bolt_rifle` ↔ `rifle.bolt`).
- **Implausible hit distances.** New `MaxHitDistance` key (default `500`) discards distances that
  `Vector3.Distance(info.HitPositionWorld, info.PointStart)` produces when `PointStart` is unset —
  1000-2000 m readings on a 4k map, 3.7% of hits on the reference server. The hit still counts; only
  the distance is dropped. Since the weighted score is squared in the penalty term, one such reading
  was enough to null a player's damage (`mp5` weighted-score p95: 175 with them, 1.1 without). The
  raw measurement is still written to the event log for diagnosis.

### Added (plugin)
- **In-game admin panel (`/ac-ui`).** A CUI panel listing tracked players ranked by the same
  suspicion score the daily report uses, paginated, with accuracy coloured against that weapon's own
  threshold. Read-only by design: acting on a player still goes through the existing commands, which
  keep their audit trail. The panel is destroyed before every redraw, on disconnect, and on unload
  for every viewer — a leaked `CursorEnabled` panel would trap the player's mouse.
- **Per-period counters** (`MogyAntiCheat_PeriodCounters.json`): shots, hits, kills, deaths,
  suspicion flags raised, damage-reduced hits and nulled hits per player, accumulated since the last
  report and reset when a scheduled one is sent. Persisted, so a mid-period restart keeps the day.
  This makes the daily digest genuinely about the period rather than about the state of the rolling
  windows; players with no activity in the period are dropped from it entirely, however suspicious
  their stale window looks. `/ac-daily-now` does not reset them, so testing the webhook is free.
- **`DailyReport` config block — operator-facing suspicion digest.** Sends a ranked list of the
  most worth-checking players to the server owner's *own* Discord webhook on a configurable
  interval (default 24h, off until a URL is set). Distinct from `WeeklyReport`, which is the opt-in
  anonymized summary sent to the developer: this is your server's own data about your own players,
  so it defaults to real names and SteamIDs, with `IncludeNames`/`IncludeSteamIds` to fall back to
  the hashed identifier. `/ac-daily-now` sends immediately for webhook testing without advancing
  the schedule.
  - The ranking score is 60% accuracy above that weapon's *own* threshold, 25% applied damage
    reduction, 15% lagswitch incidents in the period. The accuracy term uses a **Wilson score lower
    bound**, so eleven-of-eleven ranks below forty-of-forty-five — without it the list fills with
    the plugin's own metric artifact (RegisterHit drops misses older than `MissExpirySeconds`, so a
    slow-firing player reads as 100%). Verified against the reference dataset: the naive version
    flagged 9 players, 6 of them tied at the maximum score on 11-19 sample windows; the corrected
    version flags 2 with a spread.
  - Honest labelling: accuracy figures come from each weapon's rolling window and are reported as
    current state, not as a total for the period. Lagswitch incidents are timestamped and genuinely
    limited to the interval.
- **`AimTracking` config block — aim kinematics telemetry.** Samples the view direction at 20 Hz for
  players holding a ranged weapon and records, on every `shot` event, `AimDeltaDeg` (angle since the
  previous shot), `SnapDeg` (largest angular step in the preceding 400 ms) and `SnapSettleMs` (delay
  between that step and the trigger). Hit ratio describes the *result* of aiming; these describe the
  *approach*, which is where assisted aim differs from a good player — an aimbot crosses a large
  angle, stops dead and fires tens of milliseconds later, repeatably. Collected for offline analysis
  only; the plugin does not act on them. `Enabled: false` turns sampling off.
- `WeaponFallback` config block: per-family detection settings (`auto_rifle`, `smg`, `lmg`,
  `semi_rifle`, `sniper`, `shotgun`, `pistol`, `projectile`, `explosive`) applied to any weapon the
  `Weapons` block does not name, so modded or newly added weapons are checked instead of exempt.
  `Enabled: false` restores the previous behaviour. `explosive` ships at `MaxAccuracy = 1.0` on
  purpose — a rocket registers a hit on nearly every shot, so hit ratio carries no signal.
- One-time console warning naming any weapon that resolves to neither a config entry nor a family.
- `/ac-why` now reports where a weapon's thresholds came from (config key, `family:<name>`, or
  unconfigured); `/ac-config-tune MaxHitDistance <value>` tunes the new bound live.

### Notes
Behaviour change worth reading before upgrading: closing the coverage gap applies the **existing**
thresholds to a third more shots, which on the reference server took the flag rate from 23.3% to
39.1%. Those thresholds were already mis-set — the accuracy they are compared against is the
plugin's own window metric (median ~33%), not raw hits/shots (~7%), so `MaxAccuracy = 0.35` sits
slightly *above* average rather than far above it. Calibrating with `ml-service/train.py` brings the
same events to 2.3% (damage nulled outright: 4.7% → 1.4%). Coverage and calibration only pay off
together — run the trainer on your own logs after upgrading, or set `WeaponFallback.Enabled = false`
to keep the old coverage while you do.

### Changed
- Project is now **public and community-maintained**; the original author is no longer actively
  developing new features. Added Contributing sections to `README.md` / `README.hu.md`.
- Weekly-report webhook is no longer stored in the public source: `DefaultWeeklyReportWebhook` holds a
  `__WEEKLY_WEBHOOK__` sentinel (resolves to empty → `.cs`/source builds send nothing). The official
  release DLL injects the real webhook at build time via `build-release.ps1` (with `.gitignore` for
  `webhook.secret` / `build/`). Still opt-in (`Accepted = false`) and fully overridable. Documented in
  `docs/DATA_COLLECTION.md` and `docs/DLL_BUILD.md`.

## [1.10.0] - 2026-07-10

### Added
- Opt-in **anonymized weekly telemetry report** (`WeeklyReport` config block) that delivers an
  aggregated summary to a Discord webhook to help improve detection thresholds and ML tuning.
  - Off by default; requires `WeeklyReport.Accepted = true` (informed-consent gate).
  - `/ac-weekly-now` admin command to send a report immediately for testing.
  - On-load server-console disclosure of the current on/off state.
- `docs/DATA_COLLECTION.md` — full data-collection notice.

### Changed
- Telemetry now stores player identity as an irreversible per-server HMAC-SHA256 hash
  (`ShotTelemetryEvent.PlayerHash`) instead of the raw SteamID. Applies to event logs and the
  ML `/ingest` payload. No names, IP addresses, or raw SteamIDs are transmitted.
- New data files: `MogyAntiCheat_Salt.json` (per-server hash salt, never transmitted) and
  `MogyAntiCheat_WeeklyReport.json` (last-send timestamp).

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


