# RFC-0009: In-Game Admin Tools & Visualization

Status: `Draft`
Owner: `Szabó Máté`
Created: `2026-05-15`
Target Milestone: `M9`

## 1. Goal

Provide admins with in-game and exported tools to:
- Monitor live anti-cheat status and flagged players in real-time
- Manually override damage reduction thresholds for specific players (auditable)
- Export historical data in multiple formats (CSV, Excel, PNG charts)
- Visualize trends and patterns to support decision-making
- Live-tune config parameters and receive ML-powered recommendations

## 2. Non-Goals

- Client-side UI (only server console / admin output)
- Automatic bans (admins decide)
- Real-time graph rendering in-game (chart images exported to files instead)
- Mobile app or web dashboard (can be built as separate service consuming our APIs)

## 3. User/Operator Experience

### 3.1 Live Dashboard

```
/ac-dashboard
=== MogyAntiCheat Live Dashboard ===
Server: MyServer | Players: 42 | Uptime: 18h 23m

FLAGGED PLAYERS (24h):
  PlayerX (rifle)           | Confidence: 0.82 | Nerf: 45% | Action: Monitor
  PlayerY (pistol)          | Confidence: 0.67 | Nerf: 0%  | Action: Apply Nerf
  PlayerZ (ak, rifle, lmg)  | Confidence: 0.91 | Nerf: 70% | Action: Under Review

RECENT EVENTS:
  14:32:45  PlayerX fired rifle, distance 78m, accuracy 94% → Flagged (confidence 0.78)
  14:28:12  PlayerY killed PlayerA, headshot, accuracy 96% → Applied 30% nerf
  14:15:03  LagSwitch detected: PlayerZ (spike +95ms, asymmetry 0.02)

GLOBAL STATS:
  Avg accuracy (24h): 65.3%
  Flagged incidents: 8
  Penalties applied: 5
  False positives (admin review): 0

Commands:
  /ac-override <player> <nerf%|off>  - Manually adjust damage
  /ac-chart <player> <metric>         - View player trend
  /ac-export <format>                 - Export data
  /ac-suggest                         - ML recommendations
```

### 3.2 Manual Override

```
/ac-override PlayerX 50
Setting PlayerX damage reduction to 50%
Reason (optional): suspected aimbot, high ping correlation
Audit recorded: admin_id=123, timestamp=2026-05-15T14:35:00Z, old_nerf=45%, new_nerf=50%
```

### 3.3 ASCII/Text Charts

```
/ac-chart PlayerX accuracy
=== Player Accuracy Trend (last 7 days, rifle) ===

100% |                           ██
 90% |                  ██      ███
 80% | ██████          ████    ████
 70% | ██████  ██     ████     ████  ██
 60% | ██████  ██     ████     ████  ██
     +─────────────────────────────────────
       Mon   Tue   Wed   Thu   Fri   Sat
       
Daily Average: 78.4% | Trend: ↑ +3.2%
Weapon: rifle | Samples: 445 | Headshot%: 28%
```

### 3.4 Data Export

```
/ac-export csv
Exporting player data to: oxide/data/MogyAntiCheat_Export_20260515.csv
Format: player_id, name, weapon, kills, deaths, assists, accuracy, kda_ratio, 
         ping_avg, ping_stddev, ping_spikes_24h, suspicion_events, current_nerf_pct

File written, 847 rows.
```

**Excel format** includes:
- Summary tab (global stats, model health)
- Per-player sheet (K/D/A, accuracy, ping baseline)
- Suspicious events (flagged players, incidents, confidence scores)
- Charts (accuracy distribution, ping baseline scatter plot)

**PNG/SVG chart exports:**
```
/ac-export chart PlayerX accuracy png
Generated: oxide/data/charts/PlayerX_accuracy_trend.png (1200x600)
Chart includes: 7-day trend, percentile bands, annotation for flagged events
```

### 3.5 Config Live-Tuning

```
/ac-config-tune MaxAccuracy 88
Current: 92%, New: 88%
This will make detection stricter. Are you sure? (y/n): y
Applied to config, reloaded. Next shots evaluated against new threshold.
Audit: admin_id=123, param=MaxAccuracy, old=92, new=88, timestamp=2026-05-15T14:40:00Z
```

### 3.6 ML Recommendations

```
/ac-suggest
=== ML Service Recommendations (model confidence: HIGH) ===

Based on 125,000 shots from 847 players:

SafeDistance: 30m → recommended 28m (Δ -2m, confidence 91%)
MaxAccuracy: 92% → recommended 90% (Δ -2%, confidence 87%)
Weapon "rifle" threshold: 88% → recommended 85% (Δ -3%, confidence 89%)

Apply recommendations? (y/n): y
Updated config. Changes take effect immediately.
Audit logged for review.
```

## 4. Technical Design

### 4.1 Command Structure

**Dashboard commands:**
```csharp
[Command("ac-dashboard")]
void CmdDashboard(BasePlayer player) {
    if (!player.IsAdmin) return;
    
    var dashboard = BuildDashboard();
    SendChatMessage(player, dashboard);
}

string BuildDashboard() {
    var flagged = GetFlaggedPlayers(last24h: true);
    var events = GetRecentEvents(limit: 5);
    var stats = GetGlobalStats(last24h: true);
    
    return $@"
=== MogyAntiCheat Live Dashboard ===
Server: {GetServerName()} | Players: {GetPlayerCount()} | Uptime: {GetUptime()}

FLAGGED PLAYERS (24h):
{RenderFlaggedTable(flagged)}

RECENT EVENTS:
{RenderEventsTable(events)}

GLOBAL STATS:
{RenderGlobalStatsTable(stats)}
";
}
```

### 4.2 Override & Audit Trail

```csharp
[Command("ac-override")]
void CmdOverride(BasePlayer admin, string[] args) {
    if (!admin.IsAdmin) return;
    
    var targetId = ulong.Parse(args[0]);
    var nerfPct = args.Length > 1 && int.TryParse(args[1], out var n) ? n : -1;
    
    if (nerfPct < 0 || nerfPct > 100) {
        SendChatMessage(admin, "Invalid nerf %. Use 0-100 or 'off'");
        return;
    }
    
    var oldNerf = GetPlayerNerf(targetId);
    ApplyManualNerf(targetId, nerfPct);
    
    // Audit trail
    LogAudit(new AuditEntry {
        Timestamp = DateTime.UtcNow,
        AdminId = admin.userID,
        Action = "manual_override",
        TargetPlayerId = targetId,
        OldValue = oldNerf,
        NewValue = nerfPct,
        Notes = nerfPct == 0 ? "Disabled" : $"Manual {nerfPct}% nerf"
    });
    
    SendChatMessage(admin, $"Set {GetPlayerName(targetId)} damage nerf to {nerfPct}%");
}
```

### 4.3 Chart Generation

**ASCII chart (in-memory):**
```csharp
string GenerateAsciiChart(List<float> values, string title, int width = 50, int height = 10) {
    if (values.Count == 0) return "No data";
    
    var min = values.Min();
    var max = values.Max();
    var range = max - min;
    if (range == 0) range = 1;
    
    var chart = new StringBuilder();
    chart.AppendLine($"=== {title} ===");
    
    for (int y = height; y > 0; y--) {
        var threshold = min + (range * y / height);
        chart.Append($"{threshold:F0}% |");
        
        for (int x = 0; x < values.Count; x++) {
            if (values[x] >= threshold) {
                chart.Append("██");
            } else {
                chart.Append("  ");
            }
        }
        chart.AppendLine();
    }
    
    chart.Append("    +");
    chart.AppendLine(new string('─', values.Count * 2));
    
    return chart.ToString();
}
```

**Image chart (export to file):**
```csharp
async Task ExportChartAsImage(ulong playerId, string metric, string format) {
    var data = GetPlayerMetricHistory(playerId, metric);
    var imageBuffer = GenerateChartImage(data, metric, format: format);
    
    var filename = $"oxide/data/charts/{playerId}_{metric}_{DateTime.Now:yyyyMMdd}.{format}";
    await File.WriteAllBytesAsync(filename, imageBuffer);
    
    SendChatMessage(admin, $"Chart exported to: {filename}");
}
```

### 4.4 CSV/Excel Export

**CSV:**
```csharp
void ExportCSV(BasePlayer admin) {
    var players = GetAllPlayers();
    var csv = new StringBuilder();
    
    csv.AppendLine("player_id,name,weapon,kills,deaths,assists,accuracy,kda,ping_avg,ping_stddev,spikes_24h,suspicion_events,nerf_pct");
    
    foreach (var p in players) {
        var stats = GetPlayerStats(p.Id);
        var line = $"{p.Id},\"{p.Name}\",{stats.PrimaryWeapon},{stats.Kills},{stats.Deaths}," +
                   $"{stats.Assists},{stats.Accuracy:F2},{stats.KDA:F2},{stats.PingAvg}," +
                   $"{stats.PingStdDev:F2},{stats.PingSpikes24h},{stats.SuspicionEvents},{stats.NerfPct}";
        csv.AppendLine(line);
    }
    
    var filename = $"oxide/data/MogyAntiCheat_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
    File.WriteAllText(filename, csv.ToString());
    
    SendChatMessage(admin, $"CSV exported: {filename} ({players.Count} players)");
}
```

**Excel (using a library like EPPlus or NPOI):**
```csharp
async Task ExportExcel(BasePlayer admin) {
    var workbook = new ExcelPackage();
    
    // Summary sheet
    var summary = workbook.Workbook.Worksheets.Add("Summary");
    summary.Cells["A1"].Value = "MogyAntiCheat Report";
    summary.Cells["A2"].Value = $"Generated: {DateTime.UtcNow:O}";
    summary.Cells["A3"].Value = "Global Stats (24h)";
    summary.Cells["A4"].Value = $"Flagged players: {GetFlaggedPlayerCount()}";
    summary.Cells["A5"].Value = $"Penalties applied: {GetPenaltyCount24h()}";
    summary.Cells["A6"].Value = $"Average accuracy: {GetAverageAccuracy():F2}%";
    
    // Player detail sheet
    var players = workbook.Workbook.Worksheets.Add("Players");
    players.Cells["A1"].Value = "Player ID";
    players.Cells["B1"].Value = "Name";
    players.Cells["C1"].Value = "Kills";
    players.Cells["D1"].Value = "Deaths";
    // ... etc
    
    var playerList = GetAllPlayers();
    for (int i = 0; i < playerList.Count; i++) {
        var p = playerList[i];
        players.Cells[$"A{i+2}"].Value = p.Id;
        players.Cells[$"B{i+2}"].Value = p.Name;
        // ... etc
    }
    
    var filename = $"oxide/data/MogyAntiCheat_Report_{DateTime.Now:yyyyMMdd}.xlsx";
    await workbook.SaveAsAsync(new FileInfo(filename));
    
    SendChatMessage(admin, $"Excel report exported: {filename}");
}
```

### 4.5 Config Tuning

```csharp
[Command("ac-config-tune")]
void CmdConfigTune(BasePlayer admin, string[] args) {
    if (!admin.IsAdmin) return;
    
    var paramName = args[0];
    var newValue = args[1];
    
    var oldValue = config.GetValue(paramName);
    
    // Validation: some params have constraints
    if (!ValidateConfigParam(paramName, newValue)) {
        SendChatMessage(admin, $"Invalid value for {paramName}");
        return;
    }
    
    // Apply
    config.SetValue(paramName, newValue);
    SaveConfig();
    
    // Audit
    LogAudit(new AuditEntry {
        Timestamp = DateTime.UtcNow,
        AdminId = admin.userID,
        Action = "config_tune",
        Parameter = paramName,
        OldValue = oldValue,
        NewValue = newValue
    });
    
    SendChatMessage(admin, $"Applied: {paramName} = {newValue} (was {oldValue})");
    Puts($"[AC] Config tuned by {admin.displayName}: {paramName} {oldValue} → {newValue}");
}
```

### 4.6 ML Recommendations

```csharp
[Command("ac-suggest")]
async Task CmdSuggest(BasePlayer admin) {
    if (!admin.IsAdmin) return;
    
    SendChatMessage(admin, "Fetching ML recommendations...");
    
    var recommendations = await _mlClient.GetConfigRecommendations();
    
    var message = "=== ML Service Recommendations ===\n";
    message += $"Model confidence: {recommendations.ModelConfidence}\n\n";
    
    foreach (var rec in recommendations.Changes) {
        message += $"{rec.Parameter}: {rec.Current} → {rec.Recommended} " +
                   $"(Δ {rec.Delta:+0;-0}, confidence {rec.Confidence:P0})\n";
    }
    
    message += "\nApply all recommendations? Use: /ac-config-apply-ml";
    SendChatMessage(admin, message);
}
```

## 5. Configuration Changes

**New config keys:**

```json
{
  "AdminTools": {
    "Enabled": true,
    "DashboardRefreshSeconds": 5,
    "ExportDirectory": "oxide/data",
    "ChartImageFormat": "png",           // or "svg"
    "ChartWidth": 1200,
    "ChartHeight": 600,
    "AllowLiveTuning": true,
    "RequireConfirmation": true,        // Confirm before applying changes
    "AuditTrailEnabled": true
  },
  "Export": {
    "CSVEnabled": true,
    "ExcelEnabled": false,              // Requires external library
    "ChartExportEnabled": true,
    "IncludePersonalData": false,       // Names, IDs obfuscated
    "RetentionDays": 90                 // Auto-delete old exports
  }
}
```

## 6. Public API / Hook Changes

**No new hooks** (dashboard is admin-only, no plugin extension needed)

**Query methods extended:**
```csharp
List<Dictionary<string, object>> GetFlaggedPlayers(int hoursBack = 24)
// Returns: { player_id, name, weapon, confidence, current_nerf_pct, reason }

List<Dictionary<string, object>> GetRecentEvents(int limit = 50)
// Returns: { timestamp, event_type, player_id, victim_id, weapon, details }

Dictionary<string, object> GetGlobalStats(int hoursBack = 24)
// Returns: { avg_accuracy, flagged_count, penalties_applied, false_positives }

List<float> GetPlayerMetricHistory(ulong playerId, string metric, int days = 7)
// metric = "accuracy" | "kda" | "ping_avg" | "headshot_rate"
// Returns: daily or hourly values
```

**API Version:** Bump to `1.4.0` (minor version)

## 7. Compatibility and Migration

- **Backward compatible**: No breaking changes to core plugin
- **Gradual feature adoption**: Admins can enable charts/export separately
- **Library dependencies**: Excel export requires external NuGet (optional, graceful fallback to CSV)

## 8. Security / Abuse Considerations

- **Admin-only access**: Commands require `IsAdmin` check
- **Audit trail**: Every override and config change logged with admin ID, timestamp, reason
- **Data export privacy**: Can optionally obfuscate player names
- **Config constraints**: Live tuning limited to safe ranges (e.g., accuracy 0-100%)
- **Chart exports**: Generated server-side, no client-side processing

## 9. Test Plan

- **Unit tests:**
  - ASCII chart generation (boundary cases, empty data)
  - CSV formatting (escaping, special characters)
  - Config validation (range checks, type validation)
  - Audit log formatting

- **In-game validation:**
  - `/ac-dashboard` displays flagged players correctly
  - `/ac-override` applies and logs audit trail
  - `/ac-chart` generates readable output
  - `/ac-export csv|excel|chart` produces valid files
  - `/ac-config-tune` applies changes and persists
  - `/ac-suggest` fetches ML recommendations

- **Regression:**
  - Existing admin commands unchanged
  - Penalty application unaffected
  - Plugin stability under high admin activity

## 10. Rollout Plan

1. **Phase 1** (Beta): Enable dashboard and override on staging servers
2. **Phase 2** (Canary): Export features on 5-10 production servers
3. **Phase 3** (General): Roll out all features with documentation
4. **Monitoring:**
   - Admin command usage (audit trail completeness)
   - Export file sizes and frequency
   - Config changes correlation with penalty effectiveness

## 11. Acceptance Criteria

- [ ] `/ac-dashboard` shows live flagged players, recent events, global stats
- [ ] `/ac-override` applies damage nerf and logs audit trail correctly
- [ ] `/ac-chart` generates ASCII charts with accurate data
- [ ] `/ac-export csv` produces valid, importable CSV
- [ ] `/ac-export excel` produces formatted workbook with charts (if library available)
- [ ] `/ac-export chart` generates PNG/SVG image files
- [ ] `/ac-config-tune` applies config changes and persists
- [ ] `/ac-suggest` fetches and displays ML recommendations
- [ ] All admin actions logged in audit trail (admin ID, timestamp, change)
- [ ] Config constraints prevent invalid values
- [ ] Export file retention policy enforced (old files deleted)
- [ ] Admin-only access verified for all commands
