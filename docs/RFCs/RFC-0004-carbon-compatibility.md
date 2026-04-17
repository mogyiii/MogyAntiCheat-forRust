# RFC-0004: Carbon Mod Compatibility

Status: `Draft`
Owner: `Mogy`
Created: `2026-04-09`
Target Milestone: `M5`

## 1. Goal

Introduce a compatibility layer and operational guidelines so `MogyAntiCheat` can run on Carbon-based Rust servers with behavior parity to Oxide/uMod.

## 2. Non-Goals

- Rewriting anti-cheat logic from scratch for Carbon-only APIs.
- Dropping Oxide/uMod support.
- Adding new detection heuristics unrelated to runtime compatibility.

## 3. User/Operator Experience

- Server operators can deploy the same plugin source on Oxide/uMod and Carbon with minimal runtime-specific adjustments.
- Admin command behavior (`/ac-check`, `/ac-list`, `/ac-reset`, `/ac-debug`, `/ac-weapon`, `/ac-why`, `/ac-help`) remains consistent.
- Any unavoidable differences are documented with clear setup notes and expected behavior.

## 4. Technical Design

- Introduce explicit abstraction boundaries for runtime-dependent services:
  - data/config/lang path resolution
  - permission and command registration behavior (if needed)
  - lifecycle differences impacting persistence or hooks
- Keep anti-cheat scoring and mitigation pipeline runtime-agnostic.
- Add runtime detection and guarded fallback handling where APIs differ.
- Document compatibility matrix in project docs.

## 5. Configuration Changes

- No breaking config schema changes expected.
- If runtime-specific keys become necessary, they must be optional with safe defaults and clear migration notes.

## 6. Public API / Hook Changes

- Existing public hook contract should remain stable.
- If runtime differences require payload adjustments, they must be versioned and documented in `docs/PUBLIC_API.md`.

## 7. Compatibility and Migration

- Existing Oxide/uMod configs and data files must remain valid.
- Define migration rules for persistence path differences (if Carbon uses a different base directory).
- Ensure fallback behavior when runtime-specific API calls are unavailable.

## 8. Security / Abuse Considerations

- Runtime detection must not disable mitigation logic silently.
- Any degraded mode must be explicit in logs so operators can act.

## 9. Test Plan

- Validate command registration and permission checks on both runtimes.
- Validate persistence read/write and reload behavior on both runtimes.
- Validate suspicion and penalty event flow parity on representative weapons/distances.
- Validate webhook/public API behavior is unchanged across runtimes.

## 10. Rollout Plan

- Phase 1: Compatibility audit + matrix documentation.
- Phase 2: Runtime abstraction refactor with feature parity validation.
- Phase 3: Beta release notes for Carbon support and operator feedback.
- Phase 4: Mark Carbon support as stable after regression checklist passes.

## 11. Acceptance Criteria

- Core anti-cheat logic and mitigation outcomes are equivalent on Oxide/uMod and Carbon for matched scenarios.
- No known high-severity runtime-specific regressions remain open.
- Docs include clear installation/troubleshooting guidance for both runtimes.
