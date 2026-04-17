# Changelog

All notable changes to this project should be documented in this file.

The format is based on Keep a Changelog.

## [Unreleased]

### Added
- Runtime admin command `/ac-debug <on|off>` to toggle `DebugMode` and persist it in config.
- Runtime admin command `/ac-weapon <weapon|active> <MaxAccuracy|SampleCount|SafeDistance> <value>` to edit weapon thresholds in-game.
- Runtime admin commands `/ac-debug-log [clear]` and `/ac-why [weapon|active]` for file-based diagnostics and nerf reason inspection.
- Runtime admin command `/ac-help` to list all available MogyAC commands in chat.
- Roadmap milestone `M5 - Carbon Mod Compatibility` added (`docs/ROADMAP.md`, `docs/ROADMAP.hu.md`).
- New RFC draft for Carbon support planning: `docs/RFCs/RFC-0004-carbon-compatibility.md`.
- Runtime compatibility layer for Carbon: startup runtime detection and runtime-aware data/debug path resolution.

### Changed
- Plugin version is currently `1.9.2` in source metadata.
- Language selection now follows plugin `DefaultLanguage` deterministically for command/report messages.
- Hungarian language pack and Hungarian README text updated with proper UTF-8 characters.
- In `DebugMode`, admin nerf is applied too and NPC targets are included in hit analysis.
- `DebugMode` target filtering broadened to all non-building combat entities (covers `entity.spawn player` debug targets too).
- Installation/runtime docs now include both Oxide/uMod and Carbon deployment notes.

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


