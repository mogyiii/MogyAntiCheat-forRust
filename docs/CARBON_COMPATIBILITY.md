# Carbon Compatibility Guide

**Milestone:** M5  
**Last updated:** 2026-05-17

This document describes how MogyAntiCheat behaves across Oxide/uMod and Carbon runtimes, lists known differences, and provides a validation checklist for operators.

---

## Compatibility Matrix

| Area | Oxide/uMod | Carbon | Notes |
|---|---|---|---|
| Plugin file location | `oxide/plugins/MogyAntiCheat.cs` | `carbon/plugins/MogyAntiCheat.cs` | Same filename, different directory |
| Config file | `oxide/config/MogyAntiCheat.json` | `carbon/configs/MogyAntiCheat.json` | Managed by framework; no code difference |
| Language files | `oxide/lang/<code>/MogyAntiCheat.json` | `carbon/lang/<code>/MogyAntiCheat.json` | Managed by framework via `lang.RegisterMessages` |
| Stats data file | `oxide/data/MogyAntiCheat_Stats.json` | `carbon/data/MogyAntiCheat_Stats.json` | Resolved via `Interface.Oxide.DataFileSystem` |
| KDA data file | `oxide/data/MogyAntiCheat_KDA.json` | `carbon/data/MogyAntiCheat_KDA.json` | Same as stats |
| Debug log file | `oxide/data/MogyAntiCheat_Debug.log` | `carbon/data/MogyAntiCheat_Debug.log` | Runtime-aware path via `TryResolveCarbonDataDirectory` with Oxide path fallback |
| Runtime detection | `Oxide/uMod` logged at startup | `Carbon` logged at startup | Assembly name scan; fallback: `Oxide/uMod` |
| Hook registration | Standard Oxide hooks | Carbon Oxide-compat layer | Hooks `OnWeaponFired`, `OnEntityTakeDamage` behave identically |
| Permission system | `permission.RegisterPermission` | Carbon Oxide-compat layer | `mogyanticheat.admin`, `mogyanticheat.bypass` work identically |
| Chat commands | `[ChatCommand]` attribute | Carbon Oxide-compat layer | All `/ac-*` commands registered identically |
| Weapon short name | Includes category prefix, e.g. `rifle.m39` | May omit prefix, e.g. `m39` | Handled: exact match first, then suffix fallback in `ResolveWeaponConfigKey` |
| Public API hooks | `Interface.CallHook(...)` | Carbon Oxide-compat layer | `OnMogyAcSuspicion`, `OnMogyAcPenaltyApplied` emitted identically |
| Webhook pipeline | Fully functional | Fully functional | No runtime-specific differences |

---

## Data Path Resolution Details

MogyAntiCheat uses two separate mechanisms for file I/O:

**Framework DataFileSystem** (`Interface.Oxide.DataFileSystem`): Used for `MogyAntiCheat_Stats.json` and `MogyAntiCheat_KDA.json`. On Carbon, this resolves to Carbon's data directory via its Oxide compatibility layer.

**Manual path resolution** (`_runtimeDataDirectory`): Used for the debug log file (`MogyAntiCheat_Debug.log`), which is written via raw `File.AppendAllText`. The plugin resolves this path at startup:

1. Read `Interface.Oxide.DataDirectory` as the base.
2. If runtime is Carbon, traverse up two levels from the Oxide data dir and look for `carbon/data`.
3. If found, use `carbon/data`; otherwise fall back to the Oxide data path.

The startup log line `Runtime detected: <name> | Data directory: <path>` shows the resolved path. Use this as the authoritative source for the debug log location.

---

## Known Limitations

- **Debug log fallback**: If `carbon/data` does not exist at the expected path, the debug log writes to the Oxide data directory instead. The startup log always shows the active path.
- **Assembly scan**: Runtime detection scans loaded assembly names for the substring `"Carbon"`. A false positive is theoretically possible if another assembly carries that substring, but this is considered unlikely in standard deployments.
- **Weapon name prefix**: Carbon shortens prefab names differently per Rust version. The suffix-match fallback in `ResolveWeaponConfigKey` handles the common case, but if a weapon key is missing, add it explicitly via `/ac-weapon`.

---

## Validation Checklist

Use this checklist to verify correct operation on both runtimes before marking a deployment stable.

### Startup

- [ ] Server log shows `Runtime detected: Oxide/uMod` (or `Carbon`) with a valid data directory path.
- [ ] Plugin loads without errors on reload (`oxide.reload MogyAntiCheat` / Carbon equivalent).
- [ ] Config file appears in the correct framework directory.
- [ ] Language files are present and load without missing-key warnings.

### Persistence

- [ ] Stats data file (`MogyAntiCheat_Stats.json`) is created in the framework data directory on first save.
- [ ] KDA data file (`MogyAntiCheat_KDA.json`) is created on first save.
- [ ] Data survives a server save and plugin reload without corruption.
- [ ] `/ac-check` shows history for a player after combat.

### Admin Commands

- [ ] `/ac-check` — works for self and named player.
- [ ] `/ac-list` — lists online players with accuracy and nerf factor.
- [ ] `/ac-reset <player>` — clears tracked state; confirmed by subsequent `/ac-check`.
- [ ] `/ac-debug on` / `/ac-debug off` — toggles debug mode; persists across reload.
- [ ] `/ac-debug-log` — shows the correct path; `clear` variant empties the file.
- [ ] `/ac-weapon <weapon> MaxAccuracy <value>` — updates and persists config.
- [ ] `/ac-why` — explains nerf state for the current weapon.
- [ ] `/ac-lang hu` / `/ac-lang en` — switches language; persists across reload.

### Anti-Cheat Core

- [ ] Shot fired by a player is tracked (visible in `/ac-check` after hits).
- [ ] Weapon short name resolves correctly for weapons using Carbon's shortened prefab format.
- [ ] Penalty (damage reduction) applies after ≥ 10 samples with accuracy above threshold.
- [ ] Admin bypass works in normal mode; penalty applies to admins in debug mode.

### Public API

- [ ] `GetApiVersion()` returns a non-empty version string.
- [ ] `GetPlayerAcState(<steamId>)` returns a non-null dict for a tracked player.
- [ ] Subscriber plugin receives `OnMogyAcSuspicion` and `OnMogyAcPenaltyApplied` events.

### Webhook (if configured)

- [ ] Webhook events are sent to the endpoint without blocking gameplay.
- [ ] Retry and backoff behavior works on transient errors.

---

## Installation Reference

**Oxide/uMod:**
1. Copy `MogyAntiCheat.cs` to `server/<identity>/oxide/plugins/`.
2. Reload: `oxide.reload MogyAntiCheat`.
3. Config: `server/<identity>/oxide/config/MogyAntiCheat.json`.
4. Data: `server/<identity>/oxide/data/`.
5. Lang: `server/<identity>/oxide/lang/<code>/MogyAntiCheat.json`.

**Carbon:**
1. Copy `MogyAntiCheat.cs` to `server/<identity>/carbon/plugins/`.
2. Reload: use the Carbon plugin reload command.
3. Config: `server/<identity>/carbon/configs/MogyAntiCheat.json`.
4. Data: `server/<identity>/carbon/data/`.
5. Lang: `server/<identity>/carbon/lang/<code>/MogyAntiCheat.json`.

No source changes or config migration are needed when switching between runtimes. Existing data files are not automatically migrated if paths differ.
