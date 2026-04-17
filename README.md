# Mogy's Anti-Cheat for Rust (Oxide/uMod + Carbon)

[Hungarian docs](README.hu.md) | [Source of truth](docs/SOURCE_OF_TRUTH.md)

![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.9.2-blue)
![Game](https://img.shields.io/badge/game-Rust-orange)

## Project Documentation

- Source of truth: `docs/SOURCE_OF_TRUTH.md`
- Roadmap: `docs/ROADMAP.md`
- Public API: `docs/PUBLIC_API.md`
- Main features guide (EN): `docs/MAIN_FEATURES_GUIDE.en.md`
- Admin recipes (EN): `docs/ADMIN_RECIPES.en.md`
- Plugin development guide (EN): `docs/PLUGIN_DEV_GUIDE.en.md`
- Config schema: `docs/CONFIG_SCHEMA.md`
- RFC template: `docs/RFCs/TEMPLATE.md`
- Change log: `CHANGELOG.md`

MogyAntiCheat is a statistical anti-cheat plugin for Rust servers running Oxide/uMod or Carbon.
Instead of instantly banning players, it dynamically reduces outgoing damage for suspicious combat behavior. This lowers the impact of false positives while still protecting fair gameplay.

## Core Idea

The plugin does not scan files or processes. It observes combat events and computes per-weapon accuracy trends.

1. Every shot is tracked as a pending attempt.
2. Valid player-vs-player hits are matched back to recent pending shots.
3. Per-weapon accuracy is calculated from a rolling history.
4. Long-range hits are weighted more heavily than short-range hits.
5. If thresholds are exceeded, outgoing damage is reduced (down to 0 in extreme cases).

## Key Features

- Time-aware shot/hit correlation using shot expiry windows.
- Per-weapon tuning through config (`MaxAccuracy`, `SampleCount`, `SafeDistance`).
- Persistent stats across restarts (runtime data directory).
- Ignores buildings; NPC targets are included when `DebugMode` is enabled.
- Players with bypass permission are exempt from damage nerfing.
- In-game admin chat commands for checks and resets.
- Public extension API for external plugins.
- Optional webhook/HTTP delivery with queueing, retry/backoff, and rate limiting.
- Automatic Discord webhook compatibility (`username` + `content` payload).

## Installation

1. Install Oxide/uMod or Carbon on your Rust server.
2. Copy `MogyAntiCheat.cs` into:
   - Oxide/uMod: `server/<identity>/oxide/plugins/`
   - Carbon: `server/<identity>/carbon/plugins/`
3. Reload the plugin or restart the server.
4. Configure thresholds in:
   - Oxide/uMod: `server/<identity>/oxide/config/MogyAntiCheat.json`
   - Carbon: `server/<identity>/carbon/configs/MogyAntiCheat.json` (or your Carbon config directory)

## Configuration

Default config contains per-weapon entries under `Weapons` and global settings:

- `MissExpirySeconds`: How long a fired shot can stay in pending state before it is considered stale.
- `DefaultLanguage`: Default language for plugin messages (`en` by default).
- `DebugMode`: Enables extra debug logs (`false` by default).
- `PublicApi`: Extension API controls (`Enabled`, `ApiVersion`, event toggles).
- `Webhook`: Optional outbound event delivery settings.

Each weapon entry supports:

- `MaxAccuracy`: Maximum allowed hit ratio (0.38 = 38%).
- `SampleCount`: Rolling history size for that weapon.
- `SafeDistance`: Distance baseline for long-range weighting.

Webhook key fields:

- `Enabled`: Enable/disable webhook sending (`false` by default).
- `Endpoint`: Target webhook URL.
- `AuthToken`, `AuthHeader`: Optional auth header settings.
- `MaxRetries`, `BaseBackoffSeconds`, `MaxBackoffSeconds`: Retry/backoff behavior.
- `RateLimitPerSecond`: Max sends per second.
- `QueueMaxSize`: In-memory queue size cap.
- `EmitSuspicionEvents`, `EmitPenaltyEvents`: Per-event toggles.

## Discord Webhook Quick Start

1. Set `Webhook.Enabled = true`
2. Set `Webhook.Endpoint = https://discord.com/api/webhooks/...`
3. Optional: `/ac-debug on` for easier troubleshooting
4. Reload plugin

Notes:
- For Discord endpoints, the plugin automatically sends Discord-compatible payloads (`username` + `content`).
- Webhook delivery is independent from `PublicApi.Enabled`.

## Runtime Notes (Oxide/Carbon)

- The plugin keeps one shared codebase for both runtimes.
- On startup, it detects runtime environment (`Oxide/uMod` or `Carbon`) and resolves data/debug file path accordingly.
- If Carbon runtime-specific path behavior differs on your host, prefer the server log startup line as source of truth for active data path.

## Language Customization

Default files:

- Oxide/uMod: `oxide/lang/en/MogyAntiCheat.json`, `oxide/lang/hu/MogyAntiCheat.json`
- Carbon: use your Carbon language directory if separated (commonly `carbon/lang/...`)

To customize text:

1. Edit your language JSON file.
2. Set `DefaultLanguage` in `MogyAntiCheat.json` config.
3. Reload plugin and verify `/ac-check` output.
4. Optional: use `/ac-lang <languageCode>` as admin for runtime default language switch.

## Permissions

- `mogyanticheat.admin`: Access to all chat commands.
- `mogyanticheat.bypass`: Exempt from damage nerf logic (unless `DebugMode` is enabled).

## Commands

- `/ac-check [playerName]` - Show detailed anti-cheat stats for one player.
- `/ac-list` - List online players with average accuracy and current damage multiplier.
- `/ac-reset [playerName]` - Clear a player's tracked stats.
- `/ac-lang <languageCode>` - Set plugin default language (e.g., `en`, `hu`).
- `/ac-debug <on|off>` - Toggle debug mode runtime (when enabled, bypass is ignored and non-building combat entities, including NPC/debug targets, are included in analysis).
- `/ac-weapon <weaponShortName|active> <MaxAccuracy|SampleCount|SafeDistance> <value>` - Update weapon thresholds in-game and save config.
- `/ac-debug-log [clear]` - Show/clear debug log file path.
- `/ac-why [weaponShortName|active]` - Explain why nerf is or is not applied for a weapon.
- `/ac-help` - Show available admin command list.

## FAQ (Common Concerns)

### Does this punish good players for having a great day?
No. The system uses a rolling sample window (`SampleCount`), so short hot streaks are absorbed by normal variance. A player would need sustained, statistically abnormal performance to trigger meaningful penalties.

### Is this just a consistency detector and not a human behavior model?
It measures consistency with context, not raw hit-rate alone. Distance weighting (`SafeDistance`) matters, so close-range success is treated very differently from highly accurate long-range sprays.

### Can one lucky spray cause a sudden nerf?
Not realistically. Penalty is not binary and not instant-ban logic; it scales gradually. Minor overperformance may cause a tiny temporary multiplier change, not a hard punishment event.

### Will high ping players get punished?
Network conditions can affect many systems, but this plugin evaluates long-run shot-to-hit patterns, not a single delayed event. In practice, short-lived latency spikes should not look like cheat-grade consistency.

### Is this a hidden/shadow ban?
No. This plugin does not ban by itself. It applies a configurable outgoing damage multiplier as a soft mitigation while suspicious patterns continue.

### What about god-tier legit players?
Default values are intentionally conservative and leave a large skill buffer. Server owners can raise/lower `MaxAccuracy`, `SampleCount`, and `SafeDistance` to match their population.

### Is this easy for cheat makers to bypass?
Any anti-cheat can be challenged, but behavior-based detection raises attacker cost because it targets sustained statistical patterns instead of one simple signature.

### Does it treat all weapons the same?
No. Tuning is per weapon under `Weapons`, so thresholds can be adjusted for each recoil profile and intended engagement range.

### Is this replacing EAC / server moderation?
No. Think of it as a mitigation layer, not a full replacement. It reduces damage impact from suspicious behavior and provides actionable signals for admins.

### How can admins verify why a player was affected?
Use `/ac-check` and `/ac-why` for direct insight, and enable debug mode when investigating edge cases in live conditions.

## License

MIT License.

---
Created by **Mogy**
