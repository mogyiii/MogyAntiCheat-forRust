# RFC-0001: Internationalization Foundation (HU/EN)

Status: `Draft`
Owner: `Mogy`
Created: `2026-03-24`
Target Milestone: `M1`

## 1. Goal

Introduce a stable internationalization (i18n) foundation so all player/admin-facing plugin messages can be displayed in multiple languages (starting with Hungarian and English) without code edits.

## 2. Non-Goals

- No GUI localization in this phase (chat/report strings only).
- No per-player persistent language preference yet.
- No external translation service integration.

## 3. User/Operator Experience

Server operators should be able to:

- Set a default language in config (`DefaultLanguage`).
- Keep language files in `oxide/lang/` (e.g., `en`, `hu`).
- Get automatic fallback if a key is missing.

Expected fallback order:

1. Selected language key
2. `DefaultLanguage` key
3. English key
4. Hardcoded safety text (`[MogyAC] Missing lang key: <key>`)

## 4. Technical Design

### 4.1 Message Keying

Replace hardcoded user-facing strings with message keys.

Initial key set (minimum):

- `PlayerNotFound`
- `NoData`
- `StatsHeader`
- `GlobalDamage`
- `ActiveListHeader`
- `ActiveListColumns`
- `StatsResetSuccess`
- `NoPermission`

### 4.2 Language Registration

Use Oxide language API in plugin lifecycle:

- `LoadDefaultMessages()` registers `en` + `hu` dictionaries.
- Helper method `Msg(BasePlayer player, string key, params object[] args)`:
  - resolves language,
  - retrieves key,
  - applies fallback,
  - formats placeholders.

### 4.3 Language Resolution

Resolution rules:

- If player context is available, try player language first.
- Otherwise use `DefaultLanguage` from config.
- Apply fallback chain as defined in section 3.

### 4.4 Command Output Migration

Migrate these commands to key-based messages:

- `/ac-check`
- `/ac-list`
- `/ac-reset`

All `SendReply(...)` outputs should route through `Msg(...)` (except debug/internal logs).

## 5. Configuration Changes

Add new top-level key:

- `DefaultLanguage` (`string`, default: `"en"`)

Validation:

- If missing or empty -> use `"en"`.
- If language file not found -> warn and fallback to `"en"`.

## 6. Public API / Hook Changes

No new public API hooks in RFC-0001.

## 7. Compatibility and Migration

Backward compatibility:

- Existing anti-cheat logic unchanged.
- Existing numerical config keys remain untouched.

Migration behavior:

- On first load after update, `DefaultLanguage` is injected if absent.
- Existing servers continue operating with English fallback even if no custom lang files are present.

## 8. Security / Abuse Considerations

- Language files are data-only; no runtime code execution.
- Missing/invalid formatting placeholders should fail safely and return fallback text.
- Avoid exposing sensitive internals in translatable strings.

## 9. Test Plan

### 9.1 Functional

- `DefaultLanguage = en` and `DefaultLanguage = hu` both render expected text.
- Missing key in `hu` falls back to `en`.
- Missing key in both returns safety text.

### 9.2 Regression

- `/ac-check`, `/ac-list`, `/ac-reset` still functionally identical.
- Non-admin permission checks unchanged.
- Anti-cheat scoring/nerf path unchanged.

### 9.3 Edge Cases

- Null player context uses default language.
- Invalid `DefaultLanguage` value does not crash plugin.

## 10. Rollout Plan

1. Implement message dictionaries + helper method.
2. Migrate command strings to keys.
3. Add config key and validation.
4. Validate fallback chain in test server.
5. Document language customization in README + config schema.

## 11. Acceptance Criteria

- All current chat/admin outputs are key-based (no hardcoded user-facing HU strings in command handlers).
- Config supports `DefaultLanguage` with safe fallback.
- Missing key handling is deterministic and documented.
- README and config docs mention i18n usage.
