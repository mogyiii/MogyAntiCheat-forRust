# RFC-0005: Enhanced Logging & KDA + Ping Monitoring System

Status: `Accepted`
Owner: `Szabó Máté`
Created: `2026-05-15`
Target Milestone: `M6`

## 1. Goal

Extend plugin logging to capture K/D/A (Kills/Deaths/Assists) statistics and continuous per-player ping monitoring. This provides richer data for offline analysis and enables AI-driven penalty refinement by establishing per-player ping baselines and detecting anomalous network behavior during combat.

## 2. Non-Goals

- Real-time chart rendering in-game (that is M9)
- Automatic server-wide config tuning (that is M8)
- Historical player reputation scoring (future feature)

## 3. User/Operator Experience

**Server Admin:**
- New command `/ac-stats <player>` shows player's K/D/A, ping baseline, and recent suspicious activity
- Webhook payload enriched with KDA and ping telemetry
- Config option to enable/disable detailed ping logging (for privacy-sensitive servers)

**Monitoring:**
- Admin can see per-player baseline: avg ping, p95, min, max, stddev
- Ping spikes detected (> 2σ deviation from baseline) are logged automatically
- K/D/A data persists across server saves

**Example output:**
```
/ac-stats player_123
=== Player Stats ===
Name: PlayerX
K/D/A: 42 kills / 8 deaths / 12 assists
Weapon Accuracy (overall): 78.5%
Ping Baseline: avg=85ms, p95=110ms, stddev=12ms
Recent Ping Spikes: 3 (last 24h)
Suspicion Events: 1 (weapon: rifle)
```

## 4. Technical Design

### 4.1 Data Model

**Per-Player Ping Aggregator:**
```csharp
class PlayerPingStats {
    ulong PlayerId;
    double EMA;                      // Exponential moving average
    int Min;                         // Min in rolling 60-sec window
    int Max;                         // Max in rolling 60-sec window
    double StdDev;                   // Standard deviation
    long SampleCount;
    
    DateTime BaselineEstablishedAt;  // When baseline became stable
    int OutlierCount;                // Ping spikes detected
}
```

**Shot Event (for logging and AI ingestion):**
```csharp
class ShotEvent {
    long Timestamp;
    ulong PlayerId;
    string WeaponName;
    
    int PingAtShot;
    int DeltaPingMs;                 // Change since last shot
    int TimeSinceLastShotMs;
    
    float Distance;
    bool WasHit;
    float AccuracyInWindow;          // Accuracy since last significant event
}
```

**K/D/A Event:**
```csharp
class KDAEvent {
    long Timestamp;
    ulong PlayerId;
    ulong VictimPlayerId;           // For kills/assists
    string WeaponName;
    int Distance;
    bool WasHeadshot;
    int PingAtKill;
}
```

### 4.2 Ping Baseline Algorithm

1. **Warm-up**: First 100 shots collected without penalties, baseline computed
2. **EMA update** (per-packet): `EMA = 0.8 * EMA + 0.2 * CurrentPing`
3. **Rolling window** (60 sec): track min/max within that window
4. **StdDev**: sliding window standard deviation (updated every 10 shots)
5. **Anomaly threshold**: spike detected if `|CurrentPing - EMA| > 2.5 * StdDev`

```csharp
void UpdatePingBaseline(int ping) {
    playerStats.EMA = (playerStats.EMA * 0.8) + (ping * 0.2);
    playerStats.RollingWindow.Add(ping);
    
    if (playerStats.RollingWindow.Count > WINDOW_SIZE) {
        playerStats.RollingWindow.RemoveAt(0);
    }
    
    playerStats.Min = playerStats.RollingWindow.Min();
    playerStats.Max = playerStats.RollingWindow.Max();
    playerStats.StdDev = CalculateStdDev(playerStats.RollingWindow);
    
    if (IsAnomalouslyHighPing(ping)) {
        playerStats.OutlierCount++;
        LogAudit($"Ping spike: {ping}ms (baseline: {playerStats.EMA:F0}ms ±{playerStats.StdDev:F1}ms)");
    }
}

bool IsAnomalouslyHighPing(int ping) {
    if (playerStats.SampleCount < 100) return false; // Baseline not ready
    var deviation = Math.Abs(ping - playerStats.EMA);
    return deviation > (playerStats.StdDev * 2.5);
}
```

### 4.3 Shot Telemetry

Every weapon fire captures:
- Current ping (via engine hook)
- Delta since last shot (helps detect unnatural firing patterns)
- Time since last significant event (shot/hit/reload)

Shot events queued in-memory, flushed periodically to file + optional AI service.

### 4.4 K/D/A Persistence

**History structure extended:**
```csharp
class WeaponHistory {
    // ... existing fields ...
    
    int TotalKills;
    int TotalDeaths;
    int TotalAssists;
    
    List<KDAEvent> RecentKDAEvents;  // Last 1000 for export
}
```

Saved to `MogyAntiCheat_Stats.json` on `ServerSave` and `Unload`.

### 4.5 Event Queue & Flush Strategy

**Queue:**
- In-memory buffer: `List<ShotEvent>` + `List<KDAEvent>`
- Max buffer size: 5000 events

**Flush triggers:**
1. **Time-based**: Every 5 minutes
2. **Size-based**: Buffer > 5000 events
3. **Critical event**: If high-suspicion pattern detected (e.g., multi-weapon sync anomaly)

**Flush destination:**
- Local file: `oxide/data/MogyAntiCheat_Events_<YYYYMMDD>.log` (JSON Lines format)
- Optional AI service: HTTP POST to configured endpoint

```csharp
async Task FlushEventQueue() {
    if (eventQueue.Count == 0) return;
    
    var batch = new {
        server_id = GetServerId(),
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        events = eventQueue.ToList()
    };
    
    // Write to local log
    File.AppendAllText(logPath, JsonConvert.SerializeObject(batch) + "\n");
    
    // Optional: send to AI service
    if (config.AIServiceEnabled && config.AIServiceEndpoint != null) {
        try {
            await _aiClient.PostAsync($"{config.AIServiceEndpoint}/ingest", batch);
        } catch {
            // Fail-safe: log locally and continue
            LogError($"AI service unreachable, events queued locally");
        }
    }
    
    eventQueue.Clear();
}
```

## 5. Configuration Changes

**New config keys:**

```json
{
  "PingMonitoring": {
    "Enabled": true,
    "LogDetailed": false,           // Include all ping samples (verbose)
    "AnomalyThreshold": 2.5,        // StdDev multiplier for spike detection
    "BaselineWarmupSamples": 100
  },
  "EventLogging": {
    "Enabled": true,
    "BufferSize": 5000,
    "FlushIntervalSeconds": 300,    // 5 minutes
    "LocalLogDirectory": "oxide/data"
  },
  "KDATracking": {
    "Enabled": true
  },
  "AIService": {
    "Enabled": false,
    "Endpoint": null,               // e.g., "http://ml-service:8080"
    "AuthToken": null,
    "BatchTimeoutSeconds": 30
  }
}
```

## 6. Public API / Hook Changes

**New hook:**
```csharp
// Fired when ping baseline is established or refreshed
OnMogyAcPingBaselineUpdate(string playerId, Dictionary<string, object> baseline)
// baseline = { avg, p95, min, max, stddev, sample_count }
```

**Extended hook:**
```csharp
// Existing OnMogyAcPenaltyApplied now includes:
// payload.ping_at_event = current ping
// payload.ping_baseline_avg = player baseline
// payload.ping_anomaly = boolean
```

**New query methods:**
```csharp
Dictionary<string, object> GetPlayerKDAStats(ulong playerId)
// Returns: { kills, deaths, assists, kda_ratio, per_weapon: {...} }

Dictionary<string, object> GetPlayerPingStats(ulong playerId)
// Returns: { avg, p95, min, max, stddev, outlier_count, baseline_samples }

List<Dictionary<string, object>> GetPlayerShotHistory(ulong playerId, int limit = 100)
// Returns: recent shot events with ping and accuracy context
```

**API Version:** Bump to `1.1.0` (minor version, additive, backward-compatible)

## 7. Compatibility and Migration

- **Backward compatible**: Existing configs work unchanged (new settings have safe defaults)
- **Data format**: `MogyAntiCheat_Stats.json` extended with KDA fields; old saves load fine
- **Migration**: On first load, missing KDA fields initialized to 0

## 8. Security / Abuse Considerations

- **Privacy**: Detailed ping logs can be disabled via config for GDPR/privacy-sensitive servers
- **DOS via logs**: Event queue bounded at 5000; overflow events discarded with warning
- **AI service**: Endpoint should be HTTPS + auth token required if configured
- **Data retention**: Admins should implement log rotation for `MogyAntiCheat_Events_*.log` files

## 9. Test Plan

- **Unit tests:**
  - EMA + StdDev calculation correctness
  - Anomaly threshold boundary cases
  - Queue flush logic (size/time triggers)

- **In-game validation:**
  - Normal ping variation (80-120ms range): no anomalies flagged
  - Synthetic ping spike (80ms → 250ms): correctly detected
  - Stable high-ping player (150-170ms range): no false positives
  - K/D/A tracking: kills, deaths, assists counted correctly
  - Event queue: verify JSON format + flush completion

- **Regression:**
  - Existing accuracy detection unchanged
  - Penalty application unchanged
  - Admin commands still work

## 10. Rollout Plan

1. **Phase 1** (Beta): Enable on small set of test servers, validate data quality
2. **Phase 2** (Canary): Roll out to 5-10 production servers, monitor event queue behavior
3. **Phase 3** (General): Default-enabled, documentation updated
4. **Monitoring:**
   - Event queue size (alert if consistently > 3000)
   - Flush latency (should be < 100ms)
   - AI service availability (if enabled)

## 11. Acceptance Criteria

- [ ] Ping baseline computed correctly (EMA, StdDev)
- [ ] Anomaly detection fires on real ping spikes, doesn't fire on normal variance
- [ ] K/D/A tracked and persisted correctly
- [ ] Shot events queued and flushed per schedule
- [ ] `/ac-stats` command returns complete data
- [ ] Webhook payload includes ping/KDA fields
- [ ] Query API methods return expected format
- [ ] Event log files are valid JSON Lines format
- [ ] Config migration (old → new) is seamless
- [ ] Ping logging can be disabled for privacy
