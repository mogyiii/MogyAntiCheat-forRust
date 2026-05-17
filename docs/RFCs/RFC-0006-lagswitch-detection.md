# RFC-0006: LagSwitch Detection

Status: `Accepted`
Owner: `Szabó Máté`
Created: `2026-05-15`
Target Milestone: `M7`

## 1. Goal

Detect intentional lagswitch attacks — rapid network manipulation where players exploit high-latency windows to gain unfair advantage (take damage while appearing offline, then reconnect and strike with delayed visibility). Identify patterns where connection state anomalies, asynchronous packet flow, and ping spikes align with high-accuracy kills.

## 2. Non-Goals

- Detect natural packet loss or jitter (that's handled by ping baseline in M6)
- Predict which players will lagswitch (detection only, no behavioral modeling)
- Automatic ban logic (admins decide action)

## 3. User/Operator Experience

**Admin Discovery:**
```
/ac-lagswitch-audit PlayerX
=== Lagswitch Audit for PlayerX (last 24h) ===

Incident 1 [2026-05-15 14:23:45 UTC]
  Victim: PlayerY
  Weapon: rifle, 45m
  Pre-kill window: ping 80→180ms (spike +100ms)
  Asymmetry: IN=12 pps, OUT=0.3 pps (ratio 0.025)
  Kill accuracy: 97% (headshot)
  Confidence: 0.87 (HIGH)
  
Incident 2 [2026-05-15 14:18:12 UTC]
  Victim: PlayerZ
  Weapon: pistol, 12m
  Pre-kill window: ping 85→160ms
  Asymmetry: IN=8 pps, OUT=0.2 pps
  Kill accuracy: 92%
  Confidence: 0.79 (HIGH)

Summary: 2 high-confidence incidents. Pattern: consistent pre-kill spike + data asymmetry.
Recommendation: Monitor closely, collect more data before action.
```

**Webhook Event:**
```json
{
  "event": "OnMogyAcLagswitchDetected",
  "player_id": "76561198000000000",
  "confidence": 0.87,
  "victim_id": "76561198000000001",
  "weapon": "rifle",
  "distance": 45,
  "kill_accuracy": 97,
  "ping_baseline_avg": 85,
  "ping_at_kill": 180,
  "pre_kill_asymmetry_ratio": 0.025,
  "time_window_seconds": 1
}
```

## 4. Technical Design

### 4.1 Lagswitch Signature

A lagswitch kill combines three indicators:

1. **Ping anomaly**: Baseline established (from M6), sudden spike before kill
2. **Packet asymmetry**: Incoming >> Outgoing (server catch-up)
3. **Kill characteristics**: High accuracy on moving target, follows immediately after anomaly

### 4.2 Data Capture

**Connection State Monitor (per player):**
```csharp
class ConnectionState {
    ulong PlayerId;
    DateTime LastConnectionChange;  // Join/rejoin timestamp
    int ConnectionDropCount;        // Rejoins
    long TotalPlaytimeMs;
    
    // Packet flow metrics
    long IncomingPacketsPerSecond;  // From server to player
    long OutgoingPacketsPerSecond;  // From player to server
    double AsymmetryRatio;          // OUT / IN (normal: 0.8-1.2, lagswitch: < 0.1)
}
```

**Pre-kill window (1 second before suspected kill):**
```csharp
class PreKillWindow {
    int PingAtWindowStart;
    int PingAtKill;
    int PingDelta;
    
    long IncomingPacketsWindow;     // IN pps in 1 sec before kill
    long OutgoingPacketsWindow;     // OUT pps in 1 sec before kill
    double AsymmetryRatio;
    
    bool HasOutstandingPositionUpdates;  // Server has unacked position deltas
    int PositionUpdateLag;          // Time since player's last position update
}
```

### 4.3 Detection Algorithm

```csharp
bool IsLagswitchKill(Kill kill) {
    var preKillWindow = AnalyzePreKillWindow(kill, windowSize: 1000); // 1 sec
    
    // Score each component
    double pingSpikeSc = ScorePingSpike(preKillWindow);      // 0-1
    double asymmetrySc = ScoreAsymmetry(preKillWindow);      // 0-1
    double killQualitySc = ScoreKillQuality(kill);           // 0-1
    
    // Weighted confidence
    double confidence = (pingSpikeSc * 0.35) 
                      + (asymmetrySc * 0.40) 
                      + (killQualitySc * 0.25);
    
    return confidence > config.LagswitchThreshold; // Default: 0.70
}

double ScorePingSpike(PreKillWindow window) {
    // Deviation from baseline (using M6 stats)
    var baseline = GetPingBaseline(window.PlayerId);
    var spike = window.PingAtKill - baseline.Avg;
    
    if (spike < 50) return 0.0;                  // < 50ms spike: normal
    if (spike > 150) return 1.0;                 // > 150ms spike: very suspicious
    
    // Linear scale 50-150ms
    return (spike - 50.0) / 100.0;
}

double ScoreAsymmetry(PreKillWindow window) {
    // Normal: OUT/IN ≈ 0.8-1.2 (bidirectional)
    // Lagswitch: OUT/IN << 0.2 (server catch-up)
    
    double ratio = window.OutgoingPacketsWindow / (double)(window.IncomingPacketsWindow + 1);
    
    if (ratio > 0.5) return 0.0;                 // Normal bidirectional flow
    if (ratio < 0.05) return 1.0;                // Extreme asymmetry
    
    // Inverse sigmoid-like curve for 0.05-0.5
    return 1.0 - (ratio / 0.5);
}

double ScoreKillQuality(Kill kill) {
    // High accuracy on moving target + headshot + unexpected angle
    
    double baseScore = kill.Accuracy / 100.0;   // 0.7-1.0 → 0-1
    
    // Bonus: victim was moving (harder shot)
    if (kill.VictimWasMoving) baseScore *= 1.1;
    
    // Bonus: headshot
    if (kill.WasHeadshot) baseScore *= 1.05;
    
    return Math.Min(baseScore, 1.0);
}
```

### 4.4 Pattern Detection

**Repeat lagswitch behavior:**
```csharp
void DetectLagswitchPattern(ulong playerId) {
    var recent24h = GetLagswitchIncidentsLast24h(playerId);
    
    if (recent24h.Count >= 3) {
        // Multiple incidents in short time window
        double avgConfidence = recent24h.Average(x => x.Confidence);
        
        if (avgConfidence > 0.75) {
            EmitWarning($"Player {playerId} shows lagswitch pattern (3+ incidents, avg conf {avgConfidence:F2})");
            
            // Notify admin + webhook
            foreach (var incident in recent24h) {
                EmitLagswitchPattern(playerId, incident);
            }
        }
    }
}
```

### 4.5 Forensic Timeline Export

Admin command generates detailed timeline:
```csharp
void ExportLagswitchTimeline(ulong playerId, string outputPath) {
    var incidents = GetAllLagswitchIncidents(playerId);
    
    var timeline = new StringBuilder();
    timeline.AppendLine($"Lagswitch Forensic Timeline: {playerId}");
    timeline.AppendLine($"Report generated: {DateTime.UtcNow:O}");
    timeline.AppendLine();
    
    foreach (var incident in incidents.OrderByDescending(x => x.Timestamp)) {
        timeline.AppendLine($"[{incident.Timestamp:O}] Kill: {incident.VictimId} ({incident.Weapon}, {incident.Distance}m)");
        timeline.AppendLine($"  Ping Baseline: avg={incident.PingBaselineAvg}ms, spike={incident.PingAtKill}ms (+{incident.PingDelta}ms)");
        timeline.AppendLine($"  Packet Asymmetry: IN={incident.IncomingPps} pps, OUT={incident.OutgoingPps} pps, ratio={incident.AsymmetryRatio:F3}");
        timeline.AppendLine($"  Kill Quality: accuracy={incident.KillAccuracy}%, headshot={incident.WasHeadshot}, moving_target={incident.VictimWasMoving}");
        timeline.AppendLine($"  Confidence Score: {incident.Confidence:F2}");
        timeline.AppendLine();
    }
    
    File.WriteAllText(outputPath, timeline.ToString());
}
```

## 5. Configuration Changes

**New config keys:**

```json
{
  "LagswitchDetection": {
    "Enabled": true,
    "Threshold": 0.70,              // Confidence threshold (0.0-1.0)
    "PatternThreshold": 0.75,       // For repeat behavior detection
    "MinIncidentsForPattern": 3,
    "TimeWindowForPatternHours": 24,
    "AsymmetryRatioThreshold": 0.2, // OUT/IN ratio above this is normal
    "PingSpikeMinimumMs": 50,       // Minimum spike to consider
    "PreKillWindowMs": 1000         // Analyze 1 sec before kill
  }
}
```

## 6. Public API / Hook Changes

**New hook:**
```csharp
// Fired when lagswitch kill is detected (confidence > threshold)
OnMogyAcLagswitchDetected(
    string playerId, 
    string victimId, 
    string weaponName, 
    float confidence,
    Dictionary<string, object> details)
// details = { 
//   ping_baseline_avg, ping_at_kill, ping_delta,
//   incoming_pps, outgoing_pps, asymmetry_ratio,
//   kill_accuracy, victim_was_moving, was_headshot,
//   distance, timestamp
// }
```

**New query method:**
```csharp
Dictionary<string, object> GetLagswitchStats(ulong playerId)
// Returns: { incident_count_24h, incident_count_7d, avg_confidence, pattern_detected }

List<Dictionary<string, object>> GetLagswitchIncidents(ulong playerId, int limit = 50)
// Returns: recent lagswitch incidents with all forensic details
```

**API Version:** Bump to `1.2.0` (minor version)

## 7. Compatibility and Migration

- **Backward compatible**: No breaking changes, new hooks are additive
- **Packet metrics**: Populated from existing game engine hooks, no new dependencies
- **Config**: Safe defaults for all new keys

## 8. Security / Abuse Considerations

- **False positives**: High ping players in distant regions might trigger spikes; use baseline deviation (2.5σ) to minimize
- **Admin abuse**: Audit trail logs who ran `/ac-lagswitch-audit` and when
- **Innocent kills**: A single incident doesn't mean cheating; pattern detection requires 3+ incidents
- **Weaponized reports**: Keep incident data server-local, don't share with untrusted clients

## 9. Test Plan

- **Unit tests:**
  - Ping spike scoring (boundary at 50ms, 150ms)
  - Asymmetry ratio scoring (normal vs extreme)
  - Kill quality scoring (accuracy, movement, headshot)
  - Pattern detection (3+ incidents within 24h)

- **In-game validation:**
  - Legitimate high-ping player (baseline 120ms): doesn't trigger on normal spikes
  - Simulated lagswitch kill: ping 80→200ms + asymmetry 0.02 + 95% accuracy = flags high confidence
  - Repeat lagswitch: 4 incidents in 6h → pattern detected
  - False positive: Good player, high accuracy, normal ping/packet flow → no flag

- **Regression:**
  - Existing lagswitch detection (if any) still works
  - Admin commands unchanged
  - Penalty application unaffected

## 10. Rollout Plan

1. **Phase 1** (Internal): Test on dev/staging servers with simulated data
2. **Phase 2** (Beta): Deploy with conservative threshold (0.80), collect baseline data
3. **Phase 3** (Tuning): Adjust threshold based on false-positive rate, community feedback
4. **Phase 4** (General): Release with recommended defaults, documentation for admins

**Monitoring:**
- Incident frequency (% of kills flagged)
- Confidence distribution (histogram)
- False-positive rate vs. confirmed cheater feedback

## 11. Acceptance Criteria

- [ ] Ping spike scoring works across baseline variance ranges
- [ ] Asymmetry scoring correctly identifies server catch-up patterns
- [ ] Kill quality scoring accounts for accuracy, headshots, victim movement
- [ ] Confidence threshold properly discriminates true lagswitch from legitimate play
- [ ] Pattern detection fires on 3+ incidents in 24h window
- [ ] `/ac-lagswitch-audit` generates complete forensic timeline
- [ ] Webhook `OnMogyAcLagswitchDetected` fires with correct payload
- [ ] Query API returns incident history with details
- [ ] Config threshold and min-incident parameters work as documented
- [ ] False-positive rate < 5% on legitimate high-skill players
