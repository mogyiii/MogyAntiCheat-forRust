# MogyAntiCheat Roadmap

This roadmap tracks planned feature milestones.
Status values: `Planned`, `In Progress`, `Done`, `Deferred`.

## M1 - Internationalization (i18n) Foundation

Status: `Planned`
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

## M2 - Multi-Threshold Penalty Profiles

Status: `Planned`
Target: Q2-Q3 2026

Goal:
- Replace single-threshold logic with configurable penalty tiers.

Deliverables:
- New config structure for threshold tiers (warn, soft-nerf, hard-nerf).
- Optional per-weapon override and global defaults.
- Config validation with warnings on invalid tiers.

Acceptance Criteria:
- Operators can define multiple `%` levels with distinct outcomes.
- Existing single-threshold config can be migrated safely.

## M3 - Public Extension API for External Plugins

Status: `Planned`
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

## M4 - Optional External Webhook/HTTP Integration

Status: `Planned`
Target: Q3-Q4 2026

Goal:
- Push selected anti-cheat events to external systems.

Deliverables:
- Optional webhook config (endpoint, auth token, retry policy).
- Rate limiting and backoff behavior.
- Failure-safe mode (anti-cheat core remains functional if webhook fails).

Acceptance Criteria:
- High-signal events can be sent externally without gameplay impact.

## Notes

- Each milestone should be detailed in its own RFC under `docs/RFCs/`.
- Implementation is done only after RFC acceptance.
