# Changelog

All notable changes to this project should be documented in this file.

The format is based on Keep a Changelog.

## [Unreleased]

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
- English documentation file: `README.en.md`.
- Source of truth documentation: `docs/SOURCE_OF_TRUTH.md`.
- First concrete RFC for M1 i18n: `docs/RFCs/RFC-0001-i18n-foundation.md`.
- Language pack files: `oxide/lang/en/MogyAntiCheat.json`, `oxide/lang/hu/MogyAntiCheat.json`.

### Changed
- Reworked Hungarian `README.md` structure and aligned version badge to `1.6.8`.
- i18n foundation implemented in plugin code (`MogyAntiCheat.cs`): language keys, `DefaultLanguage`, and localized admin command messages.
- Added admin command `/ac-lang <languageCode>` for runtime default language switching.
