# MogyAntiCheat Source of Truth

This document defines the intended behavior of the current plugin implementation (`MogyAntiCheat.cs`, version 1.8.0).

## Purpose

MogyAntiCheat is a mitigation-first anti-cheat layer for Rust (Oxide/uMod):

- Detect statistically suspicious shooting behavior.
- Apply dynamic damage reduction instead of immediate bans.
- Keep false-positive harm lower than hard punishment systems.

## Scope

In scope:

- Per-player, per-weapon shot history tracking.
- Pending shot correlation with confirmed PvP/NPC hits.
- Accuracy and long-range weighted suspicion scoring.
- Real-time outgoing damage scaling.
- Data persistence and admin commands.
- Public extension API (read-only query + event notifications).

Out of scope:

- Process/file-level cheat detection.
- Global behavioral heuristics outside combat events.
- Automated bans or external punishment actions.

## Runtime Data Model

- `_playerStats: Dictionary<ulong, Dictionary<string, WeaponData>>`
  - Key 1: attacker `userID`.
  - Key 2: weapon short prefab name (without `.entity`).
  - `WeaponData.History`: rolling list of `ShotResult { IsHit, Distance }`.
  - `WeaponData.PendingMisses`: pending shot timestamps + distances.
- `_lastHitTime: Dictionary<ulong, float>`
  - Debounce map to avoid duplicate rapid hit processing.
- `_activeSuspicionByWeapon: Dictionary<ulong, HashSet<string>>`
  - Tracks currently suspicious weapons per player for transition-based event emission.

Persistence:

- Saved on `OnServerSave` and `Unload`.
- Data file: `oxide/data/MogyAntiCheat_Stats.json`.
- Only `History` is persisted; pending misses and active suspicion cache are runtime-only.

## Event Flow

### 1) `OnWeaponFired(BaseProjectile weapon, BasePlayer player)`

- Ignore null and NPC attackers.
- Resolve weapon short name.
- Ensure attacker/weapon tracking state exists.
- Add a pending shot entry (`AddMiss`).

### 2) `OnEntityTakeDamage(BaseEntity entity, HitInfo info)`

- Ignore invalid events.
- Ignore `BuildingBlock`.
- Continue for real player targets (`BasePlayer`, non-NPC, valid Steam ID).
- In `DebugMode`, include all `BaseCombatEntity` targets (except buildings), including NPC/debug-spawned entities, for hit analysis.
- Continue only for real player attackers (non-NPC, valid Steam ID).
- Debounce repeated hit events within 0.05 seconds.
- Resolve active weapon and hit distance.
- Load per-weapon `SampleCount` and global `MissExpirySeconds`.
- Register hit into rolling history (`RegisterHit`).
- Evaluate suspicion for active weapon and emit transition hook (if enabled).
- Compute attacker nerf (`GetLowestNerf`) and scale outgoing damage if needed.
- Emit penalty-applied hook after scaling (if enabled).

## Shot Correlation Rules

`RegisterHit(distance, limit, expiryTime)` behavior:

- Search pending shots from newest to oldest for first non-expired shot.
- If found:
  - Non-expired pending shots before that index become misses.
  - Matched shot becomes hit with real hit distance.
  - Remove processed pending shots up to the matched index.
- If not found:
  - Record hit directly (lag tolerance fallback).
- Enforce rolling cap: trim oldest entries until `History.Count <= limit`.

## Scoring and Penalty Rules

### Accuracy

- `Accuracy = hits / totalHistory` for each weapon.

### Weighted Distance Score

- Uses hit-only history.
- Hit contributes:
  - `1.0` when `Distance <= SafeDistance`
  - `Distance / SafeDistance` when beyond safe distance
- Weighted score = average hit contribution.

### Nerf Computation

For each weapon with enough data (`History.Count >= 10`):

1. Read config: `MaxAccuracy`, `SafeDistance`.
2. If `Accuracy <= MaxAccuracy`, no penalty.
3. Else:
   - `Excess = (Accuracy - MaxAccuracy) / (1 - MaxAccuracy)`
   - `PenaltyFactor = Excess * (WeightedScore > 1 ? WeightedScore^2 : 1)`
   - `CurrentNerf = 1 - PenaltyFactor`
4. Hard clamps:
   - If `Accuracy > 0.95` and `WeightedScore > 1.2`: nerf = `0`
   - If `CurrentNerf < 0.30`: nerf = `0`
5. Global nerf for attacker = lowest nerf among tracked weapons.
6. Final global nerf clamped to `[0, 1]`.

Admin exemption:

- Nerf is not applied when attacker is admin in normal mode.
- In `DebugMode`, nerf is applied to admin attackers too.

## Public API Contract

Config under `PublicApi`:

- `Enabled` (`bool`, default `true`)
- `ApiVersion` (`string`, default `1.0.0`)
- `EmitSuspicionEvents` (`bool`, default `true`)
- `EmitPenaltyEvents` (`bool`, default `true`)

Query methods:

- `GetApiVersion()` -> configured API version string.
- `GetPlayerAcState(ulong playerId)` -> read-only player anti-cheat snapshot or `null`.

Hooks:

- `OnMogyAcSuspicion(Dictionary<string, object> payload)`
  - Emitted once when a player+weapon enters suspicious state.
- `OnMogyAcPenaltyApplied(Dictionary<string, object> payload)`
  - Emitted when outgoing damage scaling is applied.

## Configuration Contract

Top-level keys:

- `Weapons` (dictionary by weapon short name)
- `MissExpirySeconds` (float)
- `DefaultLanguage` (string, default `en`)
- `DebugMode` (bool, default `false`)
- `PublicApi` (object)

Each weapon requires:

- `MaxAccuracy` (float)
- `SampleCount` (int)
- `SafeDistance` (float)

If a weapon has no entry, history limit falls back to `40` during hit registration.

## Admin Command Contract

- `/ac-check [name]`
  - Admin-only.
  - Without argument: self-report.
  - With argument: report target player.
- `/ac-list`
  - Admin-only.
  - Displays online players with average tracked accuracy and global damage multiplier.
- `/ac-reset [name]`
  - Admin-only.
  - Removes target player's tracked state from memory.
- `/ac-lang <code>`
  - Admin-only.
  - Sets and persists `DefaultLanguage` in plugin config.
- `/ac-debug <on|off>`
  - Admin-only.
  - Runtime toggle for `DebugMode` with config persistence.
  - In debug mode, admin nerf is enabled and NPC targets are included.
- `/ac-weapon <weapon|active> <MaxAccuracy|SampleCount|SafeDistance> <value>`
  - Admin-only.
  - Updates `Weapons` thresholds in-game and persists config.

## Known Constraints

- Distance for fired shots is currently stored as `0` in pending queue; only confirmed hits store real distance.
- Debounce window (`0.05s`) may hide edge-case rapid multi-hit events.
- Suspicion is strictly combat-statistical and does not infer cheat type.

## Operational Guidance

- Treat this as a balancing tool, not final enforcement.
- Tune values per server style and weapon meta.
- Revisit thresholds after major Rust updates or recoil/combat changes.

## Change Management

When plugin behavior changes, update all of:

1. `MogyAntiCheat.cs` version string.
2. `README.en.md`.
3. This file (`docs/SOURCE_OF_TRUTH.md`).

