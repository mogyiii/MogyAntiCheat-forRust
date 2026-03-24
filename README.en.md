# MogyAntiCheat for Rust (Oxide/uMod)

![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.6.8-blue)
![Game](https://img.shields.io/badge/game-Rust-orange)
## Project Documentation

- Source of truth: `docs/SOURCE_OF_TRUTH.md`
- Roadmap: `docs/ROADMAP.md`
- Public API (draft): `docs/PUBLIC_API.md`
- Config schema: `docs/CONFIG_SCHEMA.md`
- RFC template: `docs/RFCs/TEMPLATE.md`
- Change log: `CHANGELOG.md`

MogyAntiCheat is a statistical anti-cheat plugin for Rust servers running Oxide/uMod.
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
- Persistent stats across restarts (`oxide/data/MogyAntiCheat_Stats.json`).
- Ignores buildings and NPC targets.
- Admin players are exempt from damage nerfing.
- In-game admin chat commands for checks and resets.

## Installation

1. Install Oxide/uMod on your Rust server.
2. Copy `MogyAntiCheat.cs` into `server/<identity>/oxide/plugins/`.
3. Reload the plugin or restart the server.
4. Configure thresholds in `server/<identity>/oxide/config/MogyAntiCheat.json`.

## Configuration

Default config contains per-weapon entries under `Weapons` and one global setting:

- `MissExpirySeconds`: How long a fired shot can stay in pending state before it is considered stale.

Each weapon entry supports:

- `MaxAccuracy`: Maximum allowed hit ratio (0.38 = 38%).
- `SampleCount`: Rolling history size for that weapon.
- `SafeDistance`: Distance baseline for long-range weighting.

Example:

```json
{
  "Weapons": {
    "rifle.ak": {
      "MaxAccuracy": 0.38,
      "SampleCount": 40,
      "SafeDistance": 25.0
    }
  },
  "MissExpirySeconds": 20.0
}
```

## Commands (Admin Only)

- `/ac-check [playerName]` - Show detailed anti-cheat stats for one player.
- `/ac-list` - List online players with average accuracy and current damage multiplier.
- `/ac-reset [playerName]` - Clear a player's tracked stats.

## How Nerfing Works

Nerf is computed per weapon and the plugin applies the lowest (most restrictive) multiplier across tracked weapons.

- No nerf if too little data exists (`History.Count < 10`).
- If accuracy is above `MaxAccuracy`, penalty scales with:
  - how far above the threshold the player is,
  - and weighted long-range performance.
- Severe outliers can be set to `0` damage.

## Notes and Limits

- This is mitigation-focused, not a full anti-cheat replacement.
- Works best when weapon thresholds are tuned to your server's PvP style.
- If your server has unusual combat mods, recalibrate `MaxAccuracy`, `SampleCount`, and `SafeDistance`.

## License

MIT License.

---
Created by **Mogy**


