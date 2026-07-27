# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**MogyAntiCheat** is a statistical anti-cheat plugin for Rust game servers running Oxide/uMod or Carbon. It is a single C# file (`MogyAntiCheat.cs`) compiled at runtime by the modding framework — there is no local build step, no test suite, and no package manager.

This is now a **public, community-maintained** project (MIT). The original author is no longer actively developing new features; contributions via fork/PR are welcome. An optional opt-in weekly telemetry report exists; the webhook is not stored in source (`DefaultWeeklyReportWebhook` is a `__WEEKLY_WEBHOOK__` sentinel) and is injected only into the official release DLL via `build-release.ps1` — see `docs/DATA_COLLECTION.md` and `docs/DLL_BUILD.md`.

## Deployment

- Copy `MogyAntiCheat.cs` to `oxide/plugins/` (Oxide/uMod) or `carbon/plugins/` (Carbon)
- Reload in-game: `oxide.reload MogyAntiCheat` or the Carbon equivalent
- Data file saved by the server at `MogyAntiCheat_Stats.json` in the runtime data directory

## Architecture

### Single-file plugin (`MogyAntiCheat.cs`)

All logic lives in one file with these clearly separated concerns:

**Shot Tracking** (`OnWeaponFired` hook): Every fired shot is stored as a pending entry (timestamp + distance) in a per-player, per-weapon `PendingMisses` queue.

**Hit Correlation** (`OnEntityTakeDamage` hook): Incoming PvP hits are matched to the most recent pending shot. Earlier pending shots are backfilled as misses. A 0.05 s debounce prevents double-processing.

**Accuracy & Penalty Computation**: Per-weapon accuracy = hits / total history. A weighted score amplifies long-range hits (shots beyond `SafeDistance` scale linearly). Penalty only activates after ≥ 10 samples and when accuracy exceeds `MaxAccuracy`. Hard clamps apply: accuracy > 95% with weighted score > 1.2 zeroes damage; nerf below 30% also zeroes. Global nerf = lowest nerf across all tracked weapons.

**Public API** (version 1.0.0): Two Oxide hooks are emitted to subscribing plugins:
- `OnMogyAcSuspicion(string playerId, string weaponName, float accuracy, float weightedScore)` — fired once per weapon on state transition into suspicion
- `OnMogyAcPenaltyApplied(string playerId, string weaponName, float nerfFactor, float newDamage)` — fired whenever damage is scaled

Query methods: `GetApiVersion()` → `string`, `GetPlayerAcState(ulong playerId)` → `Dictionary<string, object>`.

**Webhook Pipeline**: Optional async HTTP delivery with rate limiting, exponential backoff, Discord auto-detection, and a configurable queue. Never blocks the main game thread.

**Admin Commands**: `/ac-check`, `/ac-list`, `/ac-reset`, `/ac-lang`, `/ac-debug`, `/ac-weapon`, `/ac-debug-log`, `/ac-why`, `/ac-daily-now`, `/ac-ui`, `/ac-help`

**Runtime Detection**: On `Init`, plugin scans loaded assemblies to detect Oxide vs Carbon and resolves the correct data directory path.

**Persistence**: Only `History` (shot/hit records per player per weapon) is persisted. Pending shots and suspicion state are runtime-only. Saved on `ServerSave` and `Unload`.

### Data flow

```
OnWeaponFired → PendingMisses queue
OnEntityTakeDamage → RegisterHit → EvaluateWeapon
  → ProcessSuspicionTransition (emit OnMogyAcSuspicion once per state change)
  → GetLowestNerf → apply damage scaling
  → emit OnMogyAcPenaltyApplied + webhook event
```

## Key Documents

- `docs/SOURCE_OF_TRUTH.md` — authoritative behavioral specification; consult before changing algorithm logic
- `docs/PUBLIC_API.md` — API contract and versioning policy (additive = minor bump, breaking = major bump)
- `docs/CONFIG_SCHEMA.md` — all configuration fields and defaults
- `docs/DATA_COLLECTION.md` — opt-in weekly telemetry notice (hashing, consent flag, default webhook)
- `docs/ML_TRAINING.md` — offline trainer that calibrates config thresholds from event logs; also
  records measured problems in the current detection metric. `ml-service/mogyac/replay.py` is a
  hand-maintained replica of the plugin's `WeaponData` logic — if you change how accuracy, the
  weighted score, or the penalty is computed, update it and rerun `ml-service/selftest.py`.
- `docs/DLL_BUILD.md` — building/distributing as a precompiled DLL
- `docs/examples/MogyAcExampleSubscriber.cs` — reference implementation for external plugins using the API
- `plugins/MogyAcZeroDamageAlert.cs` — companion plugin (ships separately)
- `docs/ROADMAP.md` — milestone planning
- `docs/RFCs/` — RFC proposals for major features

## Localization

Message strings live in `oxide/lang/en/MogyAntiCheat.json` and `oxide/lang/hu/MogyAntiCheat.json`. All player-facing output must go through the lang system; never hardcode strings in logic. When adding a new message, add the key to both language files.

## Constraints

- Target runtime: .NET 4.6+ via Oxide/Carbon. Do not use C# features unavailable in that target.
- No NuGet packages. Only Oxide framework APIs, `UnityEngine`, `System.*`, and `Newtonsoft.Json` (provided by the game runtime) are available.
- Algorithm changes must stay consistent with `docs/SOURCE_OF_TRUTH.md`. If the spec and code diverge, update both.
- API versioning policy in `docs/PUBLIC_API.md` must be followed when changing public hook signatures or query methods.
