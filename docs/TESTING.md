# MogyAntiCheat Testing Guide

This document provides a comprehensive testing strategy for validating MogyAntiCheat functionality across all features and edge cases.

**Plugin Version:** 1.10.0  
**API Version:** 1.3.0  
**Runtimes:** Oxide/uMod, Carbon

> Note: the test cases below were authored for 1.9.8 (Milestone M9). The 1.10.0 additions
> (SteamID hashing, opt-in weekly telemetry report) are **not yet covered** and need test cases.

---

## Test Environment Setup

### Prerequisites

- Rust game server (latest build recommended)
- Oxide/uMod v2.0.44+ OR Carbon
- Admin/moderator in-game account
- Test player accounts (2–5 for multi-player scenarios)

### Installation Verification

1. Copy `MogyAntiCheat.cs` to `oxide/plugins/` or `carbon/plugins/`
2. Reload: `/oxide.reload MogyAntiCheat` or Carbon equivalent
3. Verify startup log shows: `[MogyAntiCheat] <version> initialized`
4. Check config file created: `oxide/config/MogyAntiCheat.json`
5. Check data directory: `oxide/data/` for runtime stats file

### Baseline Config

Default config should have:
- `PublicApi.Enabled = true`
- `PublicApi.ApiVersion = 1.3.0`
- `KDATracking.Enabled = true`
- `PingMonitoring.Enabled = true`
- `LagswitchDetection.Enabled = true`
- `MLService.Enabled = false` (for M9 testing, or configure if available)

---

## Part 1: Shot Tracking & Accuracy Detection

### Test 1.1: Basic Shot Registration

**Objective:** Verify that fired shots are tracked and stored.

**Steps:**
1. Admin spawns test player A in a safe zone
2. Admin spawns test player B nearby (20m away)
3. Player A fires 10 shots at player B with rifle
4. Admin runs `/ac-check A`
5. Verify output shows "rifle" weapon with approximately 10 shots recorded

**Expected:**
- Shots count = 10
- Accuracy % shown (will be 0% if no hits yet)

---

### Test 1.2: Hit Correlation

**Objective:** Verify shot-to-hit matching works correctly.

**Steps:**
1. Player A fires 20 shots at player B (rifle, various ranges 20–50m)
2. Player B is hit by approximately 15 of those shots (rest are deliberate misses)
3. Admin runs `/ac-check A`
4. Verify accuracy calculation

**Expected:**
- Accuracy ≈ 75% (15/20)
- Shot history shows mix of hits and misses
- Weapon records correct distance for hits

---

### Test 1.3: Long-Range Weighting

**Objective:** Verify long-range shots are weighted more heavily in suspicion scoring.

**Steps:**
1. Player A fires 30 shots at player B from 80m away
2. All 30 shots hit (simulating aimbot-like accuracy at range)
3. Admin runs `/ac-check A` and checks accuracy % and weighted score
4. Compare with Player C who fired 30 shots from 15m, hit 30 (contact range)

**Expected:**
- Player A: accuracy ~100%, weighted score > 1.5
- Player C: accuracy ~100%, weighted score ≈ 1.0
- Player A's weighted score is higher due to distance contribution

---

### Test 1.4: Pending Shot Expiry

**Objective:** Verify shots expire after `MissExpirySeconds`.

**Steps:**
1. Player A fires 5 shots at player B (rifle)
2. Wait 25 seconds (default expiry is 20s)
3. Player B is hit (damage taken)
4. Admin runs `/ac-check A`
5. Verify the hit is NOT correlated to the expired pending shots

**Expected:**
- Hit recorded, but pending shots already expired
- New hit entry created without matching old pending shots
- Accuracy not inflated by the delayed hit

---

## Part 2: Suspicion & Damage Penalties

### Test 2.1: Suspicion Event Emission

**Objective:** Verify `OnMogyAcSuspicion` hook fires when threshold exceeded.

**Setup:**
- Configure rifle `MaxAccuracy = 0.70`
- Configure rifle `SampleCount = 10`

**Steps:**
1. Player A fires 20 shots at player B (rifle)
2. 18 hits (90% accuracy, exceeds 70% threshold)
3. Admin runs `/ac-check A`
4. External plugin listener (if configured) should have received suspicion event

**Expected:**
- `/ac-check` shows "SUSPICIOUS" status for rifle
- External plugin logs event with accuracy, maxAccuracy, weightedScore, etc.

---

### Test 2.2: Damage Penalty Application

**Objective:** Verify damage is scaled down when player is suspicious.

**Steps:**
1. Configure rifle `MaxAccuracy = 0.70`
2. Player A reaches suspicion state: 20 shots, 18 hits (90% acc)
3. Player A fires at player B with rifle; player B takes damage
4. Record damage dealt: `originalDamage`
5. Admin runs debug logs or webhook to see `scaledDamage`
6. Calculate multiplier = `scaledDamage / originalDamage`

**Expected:**
- Multiplier < 1.0 (e.g., 0.60–0.80 for 90% accuracy)
- Player B survives attacks that would normally be lethal
- Each shot shows reduced damage in kill logs

---

### Test 2.3: Admin Exemption

**Objective:** Verify admins are exempt from damage nerfing.

**Steps:**
1. Set rifle `MaxAccuracy = 0.50`
2. Admin account reaches suspicion: 20 shots, 18 hits (90% acc)
3. Admin fires at test player B; check damage dealt
4. Non-admin player C reaches same suspicion state
5. Player C fires at test player B; check damage dealt

**Expected:**
- Admin's damage: full (not scaled)
- Player C's damage: scaled down
- Admin can be nerfed only in `DebugMode = true`

---

### Test 2.4: Hard Clamps

**Objective:** Verify penalty does not apply under extreme conditions (false-positive protection).

**Steps:**
1. Player A: 20 shots, 19 hits (95% accuracy) at 200m range
2. Weighted score = 2.5 (very long range)
3. Admin runs `/ac-check A`
4. Verify penalty is 0% (no nerf applied due to hard clamp)

**Expected:**
- Despite high accuracy + long range, nerf = 0%
- Reason: accuracy > 95% AND weighted score > 1.2 triggers hard clamp

---

## Part 3: K/D/A Tracking

### Test 3.1: Kill Counter

**Objective:** Verify kills are counted per player.

**Steps:**
1. Player A kills player B (rifle, headshot)
2. Player A kills player C (rifle)
3. Player A kills player D (different weapon)
4. Admin runs `/ac-stats A`

**Expected:**
- Kills = 3
- Each weapon shows up in stats

---

### Test 3.2: Death Counter

**Objective:** Verify deaths are counted.

**Steps:**
1. Player B dies to player A
2. Player B dies to player C
3. Admin runs `/ac-stats B`

**Expected:**
- Deaths = 2
- KDR shown correctly

---

### Test 3.3: Assist Tracking

**Objective:** Verify assists are credited to damage contributors.

**Steps:**
1. Player A fires at player B, deals 40 damage
2. Player C fires at player B, deals 60 damage
3. Player D fires final shot, kills player B
4. Admin runs `/ac-stats A`, `/ac-stats C`, `/ac-stats D`

**Expected:**
- Player D: Kills +1
- Player A: Assists +1
- Player C: Assists +1
- Player B: Deaths +1

---

## Part 4: Ping Monitoring & Anomaly Detection

### Test 4.1: Ping Baseline Establishment

**Objective:** Verify ping baseline is computed after threshold samples.

**Config:** `PingBaselineSamples = 50` (from code constant)

**Steps:**
1. Player A starts on server (ping baseline not yet established)
2. Player A fires 50 shots (updates baseline each shot)
3. Admin runs `/ac-stats A`
4. Verify "Ping: Ping baseline established" (or similar)

**Expected:**
- After shot 50: baseline EMA, stddev shown
- Min/max ping recorded
- Sample count = 50+

---

### Test 4.2: Ping Anomaly Detection

**Objective:** Verify spike detection works.

**Config:** `PingMonitoring.AnomalyThresholdStdDev = 2.5`

**Steps:**
1. Player A establishes baseline: 70ms avg, 5ms stddev
2. Player A fires normally (ping stays ~70ms) for 20 shots
3. Network spike: Player A fires with ping 140ms (spike of +70ms)
4. Spike threshold = 70 + (2.5 * 5) = 82.5ms
5. Actual ping (140ms) > threshold → anomaly detected
6. Admin runs `/ac-stats A`

**Expected:**
- Ping anomalies count incremented
- Event logged if event logging enabled

---

### Test 4.3: Ping Baseline Update Hook

**Objective:** Verify `OnMogyAcPingBaselineUpdate` fires once baseline is established.

**Steps:**
1. External plugin listener subscribes to hook
2. New player joins, fires 50+ shots
3. Verify hook received with payload: `playerId`, `avg`, `min`, `max`, `stddev`, `sampleCount`

**Expected:**
- Hook fires exactly once per player
- Payload fields populated correctly

---

## Part 5: Lagswitch Detection

### Test 5.1: Lagswitch Incident Recording

**Objective:** Verify lagswitch incidents are detected and logged.

**Config:** `LagswitchDetection.Threshold = 0.70`

**Steps:**
1. Player A establishes baseline: 80ms, stddev 5ms
2. Player A's ping spikes to 150ms during a kill event
3. Kill quality: accuracy 95%, headshot true
4. Reconnect window: no recent disconnect
5. Kill recorded; composite confidence calculated
6. If confidence >= 0.70 → incident recorded
7. Admin runs `/ac-lagswitch-audit A`

**Expected:**
- Incident listed with timestamp, victim, weapon, distance
- Confidence score shown (0.0–1.0)
- Ping spike component shown
- Kill accuracy component shown

---

### Test 5.2: Lagswitch Pattern Detection

**Objective:** Verify pattern warning fires on repeated incidents.

**Config:** `MinIncidentsForPattern = 3`, `PatternThreshold = 0.75`

**Steps:**
1. Player A has 3 lagswitch incidents in 24h, all with confidence >= 0.75
2. Admin runs `/ac-lagswitch-audit A`

**Expected:**
- Summary shows "Pattern detected" warning
- 24h incident count >= 3
- Average confidence >= 0.75

---

## Part 6: Manual Override & Audit Trail

### Test 6.1: Setting Override

**Objective:** Verify manual override is applied and logged.

**Steps:**
1. Player A fires, reaches suspicion (80% accuracy, should be nerfed ~50%)
2. Admin runs `/ac-override A 30`
3. Player A fires at player B; measure damage
4. Calculate multiplier = scaledDamage / originalDamage

**Expected:**
- Damage multiplier = 0.70 (30% reduction)
- Audit log entry created with: admin ID, admin name, target ID, target name, old value (auto), new value (30%)

---

### Test 6.2: Clearing Override

**Objective:** Verify override can be cleared.

**Steps:**
1. Player A has override set to 30%
2. Admin runs `/ac-override A off`
3. Player A fires again; measure damage

**Expected:**
- Damage reverts to algorithm-computed nerf (or no nerf if not suspicious)
- Audit log entry: new value = "auto"

---

### Test 6.3: Override Takes Priority

**Objective:** Verify manual override overrides algorithm.

**Steps:**
1. Player A: algorithm computes 70% nerf (very suspicious)
2. Admin sets `/ac-override A 20` (only 20% nerf)
3. Player A fires; measure damage
4. Calculate multiplier

**Expected:**
- Multiplier = 0.80 (20% reduction, not 30% from algorithm)
- Admin choice takes precedence

---

## Part 7: Admin Commands

### Test 7.1: `/ac-dashboard`

**Objective:** Verify live player overview displays correctly.

**Steps:**
1. Multiple players online, some tracked
2. Admin runs `/ac-dashboard`
3. Verify table shows: names, nerf %, ping, LS count, K/D/A, override status

**Expected:**
- All tracked players listed
- Correct statistics per player
- Override status shows % if set, "-" if not

---

### Test 7.2: `/ac-chart <player> accuracy`

**Objective:** Verify ASCII accuracy chart renders.

**Steps:**
1. Player A has shot history across multiple weapons
2. Admin runs `/ac-chart A accuracy`
3. Chart shows per-weapon accuracy sparklines

**Expected:**
- Per-weapon bar chart with █▓▒░ characters
- Accuracy % shown per weapon
- Shot count shown

---

### Test 7.3: `/ac-chart <player> ping`

**Objective:** Verify ping visualization.

**Steps:**
1. Player A has established ping baseline
2. Admin runs `/ac-chart A ping`
3. Chart shows ruler with min/avg/max

**Expected:**
- Min, avg (EMA), max, stddev values
- ASCII ruler with ▲ marker at average
- Sample count shown

---

### Test 7.4: `/ac-chart <player> kda`

**Objective:** Verify K/D/A bar chart.

**Steps:**
1. Player A has K=5, D=3, A=2
2. Admin runs `/ac-chart A kda`

**Expected:**
- Proportional bar chart for K, D, A
- KDR calculated correctly

---

### Test 7.5: `/ac-export csv`

**Objective:** Verify CSV export works.

**Steps:**
1. Multiple players tracked
2. Admin runs `/ac-export csv`
3. Check file created in `oxide/data/MogyAntiCheat_Export_*.csv`
4. Open CSV and verify columns

**Expected:**
- File created with timestamp in filename
- Columns: player_id, weapon, accuracy, shots, hits, global_nerf, manual_override, kills, deaths, assists, ping_avg, ping_stddev, ping_anomalies, ls_incidents
- One row per player-weapon combination
- Data matches `/ac-check` output

---

### Test 7.6: `/ac-config-tune`

**Objective:** Verify live config tuning.

**Steps:**
1. Admin runs `/ac-config-tune MissExpirySeconds 25`
2. Verify config file updated (check `oxide/config/MogyAntiCheat.json`)
3. Fire shots at new expiry window; verify behavior changed

**Expected:**
- Config updated in memory and persisted
- Message shows old and new values
- Plugin uses new value immediately (no reload needed)

---

### Test 7.7: `/ac-suggest`

**Objective:** Verify ML recommendation fetch (if ML enabled).

**Steps:**
1. Configure `MLService.Enabled = true` with ML service endpoint
2. Admin runs `/ac-suggest`
3. Wait for response

**Expected:**
- Recommendations displayed (if ML service available)
- Format: parameter, current value, recommended value, confidence %
- If service unavailable: error message

---

## Part 8: Public API & Hooks

### Test 8.1: External Plugin Hook Subscription

**Objective:** Verify external plugins can subscribe to hooks.

**Setup:** Create a small test plugin that logs hook events.

**Steps:**
1. Test plugin subscribes to `OnMogyAcSuspicion`
2. Player A reaches suspicion state
3. Check test plugin logs

**Expected:**
- Hook fires with correct payload
- Payload includes: apiVersion, playerId, weaponShortName, accuracy, maxAccuracy, weightedScore, suggestedNerf, sampleCount, pingBaselineAvg, pingBaselineStdDev, timestampUtc

---

### Test 8.2: Query Method `GetPlayerAcState`

**Objective:** Verify read-only query returns correct data.

**Steps:**
1. Call `GetPlayerAcState(playerID)` from test plugin
2. Verify returned data structure

**Expected:**
- Returns dict with: apiVersion, playerId, globalNerf, weapons[], pingAvg, pingStdDev, pingAnomalyCount, kills, deaths, assists, timestampUtc
- Weapons[] has: weaponShortName, accuracy, sampleCount, weightedScore, maxAccuracy, safeDistance, isSuspicious, suggestedNerf

---

### Test 8.3: Query Method `GetPlayerKDAStats`

**Objective:** Verify K/D/A query works.

**Steps:**
1. Call `GetPlayerKDAStats(playerID)` from test plugin
2. Verify returned values match `/ac-stats`

**Expected:**
- Returns: kills, deaths, assists, kdaRatio
- kdaRatio = kills / deaths (or kills if deaths=0)

---

### Test 8.4: Query Method `GetMLPenaltySuggestion`

**Objective:** Verify ML penalty suggestion caching (M9 feature).

**Steps:**
1. Configure ML service
2. Trigger suspicion event → fetches ML suggestion
3. Call `GetMLPenaltySuggestion(playerId, weapon)` before cache expires
4. Call again after cache expiry (`CacheSuggestionsSeconds`)

**Expected:**
- First call returns cached value immediately
- Second call returns updated value (or null if cache expired)

---

## Part 9: Data Persistence

### Test 9.1: Stats Persistence on Restart

**Objective:** Verify player stats survive server restart.

**Steps:**
1. Player A fires 50 shots, achieves 80% accuracy (rifle)
2. Server restart (plugin reload)
3. Player A fires again; runs `/ac-check A`

**Expected:**
- Previous 50 shots are restored from `MogyAntiCheat_Stats.json`
- New shots added to existing history
- Accuracy recalculated correctly

---

### Test 9.2: KDA Persistence

**Objective:** Verify K/D/A stats persist.

**Steps:**
1. Player A has 5 kills, 3 deaths, 2 assists
2. Server restart
3. Run `/ac-stats A`

**Expected:**
- K/D/A values restored
- KDR unchanged

---

### Test 9.3: Manual Override Persistence Expectation

**Objective:** Verify override does NOT persist (runtime-only by design).

**Steps:**
1. Set `/ac-override A 50`
2. Server restart
3. Player A fires

**Expected:**
- Override cleared after restart
- Damage follows algorithm (not manual override)

---

## Part 10: Webhook Integration (Optional)

### Test 10.1: Webhook Event Queueing

**Objective:** Verify events are queued for delivery.

**Setup:** Configure `Webhook.Enabled = true` with mock endpoint.

**Steps:**
1. Player A triggers suspicion event
2. Player B triggers penalty event
3. Check webhook queue via debug logs

**Expected:**
- Events enqueued with correct payload structure
- Events include: event type, player_id, weapon, confidence, timestamp, etc.

---

### Test 10.2: Discord Webhook Compatibility

**Objective:** Verify Discord-specific payload formatting.

**Setup:** Configure Discord webhook endpoint.

**Steps:**
1. Trigger suspicion event
2. Check Discord channel for formatted message

**Expected:**
- Message includes username field (admin or plugin)
- Content field has readable summary
- No raw JSON dump

---

## Part 11: Internationalization (i18n)

### Test 11.1: Language Switching

**Objective:** Verify messages are localized.

**Steps:**
1. Default language: English
2. Admin runs `/ac-lang hu`
3. Admin runs `/ac-help`
4. Verify output is in Hungarian

**Expected:**
- All messages translated
- Supported languages listed on invalid lang

---

### Test 11.2: Fallback Chain

**Objective:** Verify fallback strategy works.

**Setup:** Delete one lang key from Hungarian file to test fallback.

**Steps:**
1. Set language to Hungarian
2. Trigger the missing key
3. Verify fallback to English (or default)

**Expected:**
- Missing key falls back correctly
- No errors

---

## Part 12: Performance & Load Testing

### Test 12.1: High Player Count

**Objective:** Verify plugin handles 50+ players without lag.

**Setup:** Spawn 50+ test players or simulate high player count.

**Steps:**
1. All players fire weapons rapidly
2. Monitor server FPS/TPS
3. Check RAM usage
4. Run `/ac-list` periodically

**Expected:**
- No TPS drop below 10
- RAM usage stays < 500MB
- Commands respond normally

---

### Test 12.2: Large Shot History

**Objective:** Verify plugin handles players with thousands of recorded shots.

**Setup:** Player A fires 5000+ shots (e.g., via rapid spam).

**Steps:**
1. Player A fires continuously (uses `SampleCount` to cap history)
2. Run `/ac-check A`
3. Export CSV
4. Check performance remains normal

**Expected:**
- History capped at `SampleCount` (e.g., 100)
- No memory leak
- Accuracy calculation instant

---

## Part 13: Edge Cases

### Test 13.1: NPC/AI Enemies

**Objective:** Verify NPC hits are NOT tracked as player hits.

**Steps:**
1. Spawn NPC (scientist, zombie)
2. Player A fires at NPC
3. NPC is hit; admin runs `/ac-check A`

**Expected:**
- Shots to NPC not recorded in player stats
- Only real player-vs-player matches tracked

---

### Test 13.2: Structure Damage

**Objective:** Verify structure/building damage is ignored.

**Steps:**
1. Player A fires at a stone wall
2. Wall takes damage
3. Run `/ac-check A`

**Expected:**
- Building block damage ignored
- No shots recorded for building hits

---

### Test 13.3: Player Disconnect During Shot History

**Objective:** Verify proper cleanup on disconnect.

**Steps:**
1. Player A fires, builds history
2. Player A disconnects
3. Player A reconnects as new character (different ID)
4. Check old data is not reused

**Expected:**
- Old player ID stats cleared from active memory (or isolated)
- Reconnected character starts fresh
- No cross-contamination

---

### Test 13.4: Extremely Long Range Shots

**Objective:** Verify long-range weighting doesn't break at extreme distances.

**Steps:**
1. Player A fires at player B from 500m+ away
2. Admin runs `/ac-check A`

**Expected:**
- Distance recorded correctly
- Weighted score computed without errors
- No division-by-zero or overflow

---

## Part 14: Carbon vs. Oxide Runtime Compatibility

### Test 14.1: Data Path Resolution (Carbon)

**Objective:** Verify plugin finds correct data directory on Carbon.

**Setup:** Deploy on Carbon server.

**Steps:**
1. Check server logs on startup
2. Verify data directory logged
3. Confirm config and data files created in correct location

**Expected:**
- Logs show: `[MogyAC] Data path: ...`
- Files created in Carbon's data directory
- Config loads correctly

---

### Test 14.2: Hooks Work on Both Runtimes

**Objective:** Verify `OnWeaponFired`, `OnEntityTakeDamage`, etc. fire on both.

**Setup:** Deploy same plugin to both Oxide and Carbon servers.

**Steps:**
1. Player fires on Oxide server → plugin logs shot
2. Player fires on Carbon server → plugin logs shot
3. Both should track similarly

**Expected:**
- Both runtimes log events
- Stats collected identically

---

## Test Execution Checklist

```
Core Functionality:
[ ] Shot tracking registers fired shots
[ ] Hit correlation matches shots to hits
[ ] Suspicion detected above threshold
[ ] Damage penalty applied correctly

Admin Commands:
[ ] /ac-dashboard displays all players
[ ] /ac-override sets and clears overrides
[ ] /ac-chart renders accuracy, ping, KDA
[ ] /ac-export csv writes file
[ ] /ac-config-tune updates config live
[ ] /ac-suggest queries ML service

Data:
[ ] Player stats persist across restart
[ ] KDA persists across restart
[ ] Override does NOT persist (runtime-only)
[ ] CSV export contains correct data

Advanced:
[ ] K/D/A tracking works
[ ] Ping baseline established
[ ] Lagswitch detection fires
[ ] Public API hooks work
[ ] External plugins can query state

Compatibility:
[ ] Oxide/uMod runtime works
[ ] Carbon runtime works
[ ] Webhook delivery works (if enabled)
[ ] Language switching works

Load:
[ ] 50+ players no lag
[ ] Large shot history handled
[ ] Performance acceptable
```

---

## Known Limitations & Gotchas

1. **Pending shots have distance = 0** — Only confirmed hits store real distance. This is a design trade-off for performance.

2. **Debounce window (0.05s)** — Rapid multi-hit events within this window may be hidden. Not applicable to most gameplay scenarios.

3. **No file/process scanning** — Plugin is purely combat-statistical; it does not detect external cheats.

4. **Accuracy weighting** — Long-range kills weighted more, but short-range can still trigger suspicion if patterns are consistent enough.

5. **Assist credit** — Only credited to players who dealt damage before the kill. Builders, medics, etc. do not auto-get assists.

6. **ML service optional** — Plugin works standalone; ML is augmentation only.

---

## Success Criteria

- [x] All commands execute without errors
- [x] Data persists and recovers correctly
- [x] Damage penalties applied reliably
- [x] Admin dashboard accurate
- [x] Performance acceptable at 50+ players
- [x] Hooks fire and external plugins can integrate
- [x] Both Oxide and Carbon supported

---

**Last Updated:** 2026-05-17  
**Status:** Milestone M9 Complete (1.9.8, API 1.3.0)
