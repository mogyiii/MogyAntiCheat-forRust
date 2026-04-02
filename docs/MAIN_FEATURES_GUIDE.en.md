# MogyAntiCheat Main Features Guide (EN)

This guide explains the main features of `MogyAntiCheat` in practical, server-admin terms.

Quick scope note:
- `README.en.md` is broad and startup-focused.
- this file explains feature behavior and intent.
- `docs/ADMIN_RECIPES.en.md` is step-by-step operations playbook.

## 1. Damage Nerf Instead of Instant Ban

`MogyAntiCheat` is a statistical anti-cheat for Rust (Oxide/uMod) that reduces suspicious players' outgoing damage instead of banning immediately.

Why this matters:
- lowers false-positive impact
- keeps fair players safer during live combat
- gives admins time to review behavior

## 2. How Detection Works (Core Flow)

The plugin tracks combat per player and per weapon:

1. On shot fire, a pending attempt is stored.
2. On valid damage event, the hit is matched to recent pending attempts.
3. A rolling history is maintained (`SampleCount` per weapon).
4. Accuracy is calculated from that history.
5. Long-range hits increase weighted suspicion (`SafeDistance` based score).
6. If thresholds are exceeded, damage multiplier is reduced.

Important behavior:
- buildings are ignored
- normal mode focuses on real player-vs-player targets
- in debug mode, non-building combat entities can also be analyzed

## 3. Per-Weapon Tuning

Each weapon can be tuned independently in config:

- `MaxAccuracy`: max allowed hit ratio before suspicious state
- `SampleCount`: rolling history size for calculations
- `SafeDistance`: distance baseline for weighted scoring

Practical approach:
- keep stricter values for easy/rapid-fire weapons
- allow higher accuracy for sniper-style weapons
- increase `SampleCount` for more stability, decrease for faster reaction

## 4. Dynamic Penalty Model

Penalty is not binary. The plugin calculates a multiplier between `0.0` and `1.0`.

- `1.0` means no penalty
- below `1.0` means scaled-down outgoing damage
- `0.0` can happen in extreme suspicious cases

Global player multiplier is the lowest suggested nerf across tracked weapons.

## 5. Admin Controls In-Game

Main admin commands:

- `/ac-check [playerName]` - detailed stats
- `/ac-list` - online overview (avg accuracy + current damage multiplier)
- `/ac-reset [playerName]` - clear tracked stats
- `/ac-weapon <weapon|active> <MaxAccuracy|SampleCount|SafeDistance> <value>` - live tuning + save
- `/ac-why [weapon|active]` - explain current nerf decision
- `/ac-debug <on|off>` - toggle debug mode
- `/ac-debug-log [clear]` - inspect or clear debug log path
- `/ac-lang <languageCode>` - change default language
- `/ac-help` - command summary

Permissions:
- `mogyanticheat.admin`
- `mogyanticheat.bypass` (ignored when debug mode is enabled)

## 6. Public API for Extensions

External plugins can subscribe to anti-cheat events:

- `OnMogyAcSuspicion(Dictionary<string, object> payload)`
- `OnMogyAcPenaltyApplied(Dictionary<string, object> payload)`

Query methods:
- `GetApiVersion()`
- `GetPlayerAcState(ulong playerId)`

Use this to build:
- admin alert plugins
- custom moderation workflows
- extra logging/analytics modules

Reference: `docs/PUBLIC_API.md`

## 7. Webhook/HTTP Event Delivery

Optional outbound event sending supports:

- queueing
- retry with exponential backoff
- rate limiting
- queue size cap
- per-event enable/disable (`suspicion`, `penalty_applied`)

Discord compatibility is built in:
- if endpoint is a Discord webhook URL, payload is sent as `username` + `content`
- otherwise raw JSON event payload is sent

## 8. Persistence and Reliability

Tracked weapon history is persisted in:
- `oxide/data/MogyAntiCheat_Stats.json`

Data is saved on server save and plugin unload, so anti-cheat learning survives restarts.

## 9. Recommended Rollout

1. Start with defaults and observe `/ac-list`.
2. Use `/ac-why` on suspicious cases before tightening values.
3. Tune per weapon in small steps.
4. Enable webhook or API integrations after baseline is stable.
5. Keep debug mode for testing windows, not permanent production use.

---

Related docs:
- `README.en.md`
- `docs/CONFIG_SCHEMA.md`
- `docs/PUBLIC_API.md`
- `docs/PLUGIN_DEV_GUIDE.en.md`
