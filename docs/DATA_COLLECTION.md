# MogyAntiCheat — Data Collection Notice

This document describes the optional **weekly telemetry report** feature (added in `1.10.0`).
It exists so the developer can improve detection thresholds and the ML configuration using
real-world, **anonymized** data from consenting servers.

Reading and understanding this document is required before enabling the feature.

## Consent is required (opt-in)

The weekly report is **off by default**. Nothing is sent until the server operator explicitly
sets `WeeklyReport.Accepted = true` in the config. By setting it to `true` you confirm that you
have read this notice and agree to the described data being sent.

On every load the plugin prints a short notice to the server console stating whether the report
is currently ON or OFF and how to change it.

## What is sent

The weekly report is an **aggregated summary**, delivered to a Discord webhook. It contains:

- `server_id` — your server hostname (identifies your server, not any player).
- Aggregate counters: number of tracked players, total shots, total hits, overall accuracy.
- Lagswitch incident totals (count + number of players), if enabled.
- Total kills/deaths across tracked players, if enabled.
- A short list of the most statistically suspicious players, each shown as:
  `<player hash> | <weapon> | acc=<accuracy> | n=<sample count>`.

The same anonymization applies to the per-batch telemetry sent to the (separate, self-hosted)
ML service `/ingest` endpoint: the player identifier there is the **hash**, never the raw SteamID.

## What is NOT collected

- **No player names / display names.** They never leave the server; they only appear in the
  local server console and in-game admin commands.
- **No IP addresses.** They are never collected or transmitted.
- **No raw SteamIDs.** See anonymization below.
- No chat messages, positions, inventories, or any non-combat data.

## How player identity is anonymized

Each SteamID is replaced with an **irreversible, per-server hash**:

```
player_hash = HMAC-SHA256(steamId, per_server_salt)   // truncated to 16 bytes
```

- The salt is a random value generated **once per server** and stored locally in
  `MogyAntiCheat_Salt.json`.
- **The salt is never transmitted.** Without it, the hash cannot be reversed back to a SteamID —
  not by the developer, not by anyone who intercepts the data.
- Because the salt is stable per server, the same player produces the same hash **within one
  server** (so behaviour can be attributed for training), but the same player on a different
  server produces a different hash (so identities cannot be cross-linked).

The result is pseudonymized operational data with no realistic path back to a real person.

## Configuration

See `CONFIG_SCHEMA.md` → `WeeklyReport`. Key fields:

- `Accepted` (`bool`, default `false`) — must be `true` to send anything.
- `DiscordWebhookUrl` (`string`) — where the weekly summary is delivered.
- `IntervalDays` (`int`, default `7`) — minimum days between reports.
- `Enabled`, `IncludeKDA`, `IncludeLagswitch` — feature toggles.

## Opting out

Set `WeeklyReport.Accepted = false` (or `Enabled = false`) and reload the plugin. Nothing further
will be sent. You can delete `MogyAntiCheat_Salt.json` at any time; a new salt will be generated
on next load.

## Admin command

- `/ac-weekly-now` (admin only) — sends the report immediately, for testing delivery. Requires
  `Accepted = true` and a configured webhook URL.

## Contact

Questions or data-removal requests: contact the plugin developer (Mogy).
