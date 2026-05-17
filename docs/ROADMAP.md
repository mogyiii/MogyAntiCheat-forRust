# MogyAntiCheat Roadmap

This roadmap tracks planned feature milestones.
Status values: `Planned`, `In Progress`, `Done`, `Deferred`.

## M1 - Internationalization (i18n) Foundation

Status: `Done`
Target: Q2 2026

Goal:
- Support multiple languages for plugin messages and admin reports.

Deliverables:
- Language files in `oxide/lang/` (starting with `en` and `hu`).
- Config key: `DefaultLanguage`.
- Missing-key fallback strategy (`selected -> default -> en`).
- Message key audit for all chat/admin outputs.

Acceptance Criteria:
- All current user-facing messages are key-based.
- Server operators can switch default language without code edits.

RFC:
- `docs/RFCs/RFC-0001-i18n-foundation.md`

## M2 - Multi-Threshold Penalty Profiles

Status: `Deferred`
Target: Re-evaluate after M3

Reason:
- Current mitigation philosophy is conservative, and aggressive tiering may increase false-positive impact.

Policy Note:
- Keep penalty behavior smooth and minimal by default.
- Hard nerf should only be considered for extreme patterns (around 90%+ suspicious consistency), not standard high-skill play.

## M3 - Public Extension API for External Plugins

Status: `Done`
Target: Q3 2026

Goal:
- Allow external plugins to subscribe to events and extend behavior.

Deliverables:
- Documented hook contract (event names + payload fields).
- Read-only query API for player anti-cheat state.
- API versioning policy (`ApiVersion`).

Acceptance Criteria:
- Example external plugin can listen and react to suspicion/penalty events.
- Breaking API changes are version-gated and documented.

RFC:
- `docs/RFCs/RFC-0002-public-extension-api.md`

## M4 - Optional External Webhook/HTTP Integration

Status: `Done`
Target: Q3-Q4 2026

Goal:
- Push selected anti-cheat events to external systems.

Deliverables:
- Optional webhook config (endpoint, auth token, retry policy).
- Rate limiting and backoff behavior.
- Failure-safe mode (anti-cheat core remains functional if webhook fails).

Acceptance Criteria:
- High-signal events can be sent externally without gameplay impact.

RFC:
- `docs/RFCs/RFC-0003-webhook-http-integration.md`

## M5 - Carbon Mod Compatibility

Status: `Done`
Target: Q4 2026

Goal:
- Add first-class compatibility for Carbon-based Rust servers while keeping Oxide/uMod support.

Deliverables:
- Compatibility matrix for runtime behavior (hooks, permissions, data/config/lang paths, chat commands).
- Framework abstraction points for engine-specific API/file-system differences.
- Validation checklist for both environments (Oxide/uMod and Carbon).
- Updated installation and operator documentation for both runtimes.

Acceptance Criteria:
- Plugin runs on both runtimes without functional regression in core anti-cheat flow.
- Admin commands and persistence behave consistently on Oxide/uMod and Carbon.
- Any known runtime-specific limitations are documented explicitly.

RFC:
- `docs/RFCs/RFC-0004-carbon-compatibility.md`

## M6 - Enhanced Logging & KDA + Ping Monitoring

Status: `Done`
Target: Q4 2026 - Q1 2027

Goal:
- Track K/D/A (Kills/Deaths/Assists) statistics alongside anti-cheat data.
- Continuous per-player ping monitoring and lag detection to distinguish client-side vs server-side issues.
- Detect ping spikes during combat as an indicator of potential network manipulation.

Deliverables:
- Extend data persistence to log kills, deaths, and assists per player per weapon.
- In-memory rolling ping history (track min/max/avg ping within combat sessions).
- Ping-spike detection algorithm to flag unusual network behavior during high-damage moments.
- Enhanced webhook payload to include KDA and ping stats for external analysis.
- Admin command `/ac-stats` to query KDA and ping data for individual players.

Acceptance Criteria:
- KDA data persists across server saves and reloads.
- Ping baseline established per player (statistical mean and standard deviation).
- Ping spikes > 2 σ during combat are logged for correlation analysis.
- Query API extended to include KDA and ping telemetry.

RFC:
- `docs/RFCs/RFC-0005-enhanced-logging-kda.md`

## M7 - LagSwitch Detection

Status: `Done`
Target: Q1 2027

Goal:
- Detect intentional lagswitch attacks (rapid connects/disconnects or deliberate network manipulation that create damage windows).
- Identify patterns where sudden connection delays coincide with player taking damage.
- Provide forensic data to server admins about suspected lagswitch abuse.

Deliverables:
- Connection state tracker (on/offline timeline per player).
- Latency anomaly detector (sudden jumps in ping coupled with damage taken).
- Scoring algorithm: severity based on frequency + timing correlation with damage events.
- Admin command `/ac-lagswitch-audit <player>` for detailed timeline review.
- Webhook event `OnMogyAcLagswitchDetected` with damage correlation metadata.
- Configurable threshold for lagswitch flagging.

Acceptance Criteria:
- Lagswitch pattern detection fires when connection state + ping anomalies align with damage timing.
- False-positive rate is kept low by requiring multi-event correlation.
- Forensic timeline is exportable for admin review.

RFC:
- `docs/RFCs/RFC-0006-lagswitch-detection.md`

## M8 - ML/Neural Network Service Module

Status: `Done`
Target: Q2 2027+

Goal:
- Build a separate machine-learning service (not embedded in the plugin) that learns from historical anti-cheat and gameplay data to improve detection.
- Use neural networks to evaluate individual shots and full combat sessions, considering all contextual variables.
- Automatically tune and optimize detection thresholds based on historical data.

Architecture:
- **Data Collection Service**: Plugin sends enriched event logs (shot details, hit correlation, ping, KDA context, weapon type, distance, player skill proxy).
- **Training Pipeline**: Offline model training on accumulated server data using labeled examples (confirmed cheaters vs skilled legitimate players).
- **Inference API**: Real-time or batch scoring of suspicious patterns; plugin receives confidence scores for decision-making.
- **Auto-Config Optimizer**: Generate tuned config recommendations based on model insights (adjusted `SafeDistance`, `MaxAccuracy`, thresholds per weapon).

Deliverables:
- Separate `MogyAntiCheatML` service (Python/C#/.NET wrapper, can run on dedicated machine or as sidecar).
- Data export utility to prepare plugin logs for training.
- REST API contract between plugin and ML service (inference endpoint, config suggestion endpoint).
- Feedback loop: plugin can report true-positive/false-positive outcomes to improve model retraining.
- Admin UI skeleton for configuring ML service connection and reviewing model health.

Acceptance Criteria:
- Plugin can optionally connect to ML service via REST endpoint in config.
- Confidence scores from ML augment existing heuristics without breaking independent plugin function.
- Model retraining improves recall/precision on server-specific data over time.
- Service gracefully degrades if ML endpoint is unavailable (plugin operates in standalone mode).

RFC:
- `docs/RFCs/RFC-0008-ml-service-module.md`

## M9 - In-game Admin Tools & Visualization

Status: `Planned`
Target: Q2 2027

Goal:
- Provide admin UI within the game for monitoring and configuring anti-cheat in real-time.
- Export analysis data in multiple formats (Excel, CSV, chart images) for offline review and decision-making.

Deliverables:
- In-game command panel (UI extension or chat-based interface):
  - `/ac-dashboard` — live view of flagged players, suspicious patterns, confidence scores.
  - `/ac-override <player> <damage-reduction-% | off>` — manually toggle damage reduction for specific player (with audit trail).
  - `/ac-chart <player> <metric>` — render ASCII or text chart of accuracy trend, ping history, or KDA over time.
- Data export:
  - `/ac-export csv` — export full player history and statistics to CSV file in server data directory.
  - `/ac-export excel` — generate formatted Excel workbook with pivot tables and conditional formatting.
  - `/ac-export chart <player> <metric> <format>` — generate PNG/SVG heatmap or line chart and save to data directory.
- Config live-reload capability:
  - `/ac-config-tune <param> <value>` — adjust threshold parameters on-the-fly (with confirmation prompt).
  - `/ac-suggest` — query ML service for auto-tuned config recommendations and show diffs.

Acceptance Criteria:
- Damage reduction override is auditable (logs who changed it, when, why).
- CSV/Excel exports contain all necessary data for external statistical analysis.
- Chart exports are readable and correctly represent the underlying metrics.
- Admin commands have role-based access control (only admins/moderators can use them).

RFC:
- `docs/RFCs/RFC-0009-admin-dashboard-export.md`

## Notes

- Each milestone should be detailed in its own RFC under `docs/RFCs/`.
- Implementation is done only after RFC acceptance.






