# Changelog

All notable changes to this project should be documented in this file.

The format is based on Keep a Changelog.

## [Unreleased]

### Added
- Documentation foundation for milestone-based planning:
  - `docs/ROADMAP.md`
  - `docs/RFCs/TEMPLATE.md`
  - `docs/PUBLIC_API.md`
  - `docs/CONFIG_SCHEMA.md`
- English documentation file: `README.en.md`.
- Source of truth documentation: `docs/SOURCE_OF_TRUTH.md`.
- First concrete RFC for M1 i18n: docs/RFCs/RFC-0001-i18n-foundation.md.
- Added language pack files: oxide/lang/en/MogyAntiCheat.json, oxide/lang/hu/MogyAntiCheat.json.

### Changed
- Reworked Hungarian README.md structure and aligned version badge to 1.6.8.
- i18n foundation implemented in plugin code (MogyAntiCheat.cs): language keys, DefaultLanguage, and localized admin command messages.
- Added admin command `/ac-lang <languageCode>` for runtime default language switching.

## [1.6.8] - 2026-03-24

### Changed
- Plugin version in code is `1.6.8` (`MogyAntiCheat.cs`).







