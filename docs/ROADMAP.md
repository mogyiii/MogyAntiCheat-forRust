# MogyAntiCheat Roadmap

This roadmap tracks planned feature milestones.
Status values: `Planned`, `In Progress`, `Done`, `Deferred`.

## M1 - Internationalization (i18n) Foundation

Status: `In Progress`
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
