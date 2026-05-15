# RFC-0008: ML/Neural Network Service Module

Status: `Draft`
Owner: `Szabó Máté`
Created: `2026-05-15`
Target Milestone: `M8`

## 1. Goal

Build a separate machine-learning service (not embedded in the plugin) that learns from historical anti-cheat data and gameplay telemetry to:
- Improve detection accuracy and reduce false positives
- Evaluate individual shots and full combat sessions using neural networks
- Automatically tune detection thresholds based on server-specific data
- Provide confidence scores to augment existing heuristics

The service runs independently (Python/C#/.NET), communicates with the plugin via REST API, and gracefully degrades if unavailable.

## 2. Non-Goals

- Real-time neural network training on live game data (too expensive)
- Server-specific model fine-tuning without admin approval
- Automatic player bans (service provides scores, admins decide)
- Replace core plugin anti-cheat logic (only augment)

## 3. User/Operator Experience

**Server Admin:**
```
/ac-suggest
=== ML Service Recommendations (updated 2026-05-15 14:00 UTC) ===

Current Config vs. Recommendations:
  SafeDistance: 30m (recommended: 28m) - Adjustment: -2m
  MaxAccuracy: 92% (recommended: 90%) - Adjustment: -2%
  Weapon "rifle" threshold: 88% (recommended: 85%) - Adjustment: -3%

Model Stats:
  Trained on: 125,000 shots from 847 players
  Accuracy (precision/recall): 94% / 87%
  Last retrain: 2026-05-14 18:00 UTC
  Confidence: HIGH

Apply recommendations? (y/n):
```

**Live Inference:**
```
High-accuracy player detected → ML service scores shot pattern
ML returns: { confidence: 0.82, anomaly_type: "high_accuracy_cluster", 
              recommended_action: "apply_30%_nerf", reason: "pattern matches 92% of confirmed cheaters" }
Plugin applies 30% nerf, emits webhook with AI confidence attached
```

**Feedback Loop:**
Admin reviews suspicious player after 1 week, confirms cheater → Admin marks as "confirmed_cheater"
Plugin reports back to ML service, model retrains, next week ML is smarter

## 4. Technical Design

### 4.1 Architecture

```
Rust Game Server (Plugin)
    ↓ (HTTP POST batch events)
    ├─→ Local Queue (MogyAntiCheat_Events_*.log)
    └─→ ML Service REST API
         ↓
    ML Service (separate process/container)
    ├─ Ingestion API (/ingest)
    │   └─ Validates, queues batch
    ├─ Processing Pipeline
    │   ├─ Feature Extraction (shot patterns, ping stats, KDA context)
    │   ├─ Model Inference (neural net scores)
    │   └─ Aggregate Scores (per-player, per-weapon, per-timeframe)
    ├─ Training Pipeline (offline, daily/weekly)
    │   ├─ Labeled examples (confirmed cheaters vs legitimate)
    │   └─ Hyperparameter tuning
    └─ Query API
        ├─ /penalty-suggestion (real-time scoring)
        ├─ /config-recommend (auto-tuning suggestions)
        └─ /player-report (historical analysis)
         ↓ (HTTP GET responses)
    Plugin receives scores, applies locally
```

### 4.2 Service Endpoints

**POST /ingest**
```json
{
  "server_id": "server_123",
  "timestamp": 1715790225000,
  "batch_id": "batch_abcd1234",
  "events": [
    {
      "timestamp": 1715790220000,
      "player_id": "76561198000000000",
      "event_type": "shot",
      "weapon": "rifle",
      "distance": 45.2,
      "ping_at_shot": 87,
      "delta_ping": 3,
      "time_since_last_shot_ms": 420,
      "accuracy_in_window": 0.78,
      "hit": true
    },
    {
      "timestamp": 1715790223000,
      "player_id": "76561198000000000",
      "event_type": "kill",
      "victim_id": "76561198000000001",
      "weapon": "rifle",
      "distance": 45.2,
      "ping_at_kill": 91,
      "accuracy": 0.97,
      "headshot": true,
      "victim_was_moving": true
    }
  ]
}
```

Response: `{ "status": "accepted", "batch_id": "batch_abcd1234" }`

**GET /penalty-suggestion?player_id=76561198000000000&timeframe=last_hour**
```json
{
  "player_id": "76561198000000000",
  "timeframe": "last_hour",
  "weapons": {
    "rifle": {
      "sample_count": 87,
      "base_accuracy": 0.89,
      "ml_confidence": 0.72,
      "suggested_nerf_pct": 35,
      "anomaly_type": "high_accuracy_stability",
      "explanation": "Shot pattern matches 88% of confirmed cheaters in 40-60m range",
      "recommended_action": "apply_nerf"
    },
    "pistol": {
      "sample_count": 12,
      "base_accuracy": 0.65,
      "ml_confidence": 0.15,
      "suggested_nerf_pct": 0,
      "anomaly_type": null,
      "explanation": "Normal skill variation",
      "recommended_action": "monitor"
    }
  },
  "global_assessment": {
    "confidence": 0.68,
    "summary": "Suspicious activity detected on rifle; monitor overall pattern"
  }
}
```

**GET /config-recommend?server_id=server_123&training_days=30**
```json
{
  "server_id": "server_123",
  "trained_on_samples": 125000,
  "trained_on_players": 847,
  "current_config": {
    "SafeDistance": 30,
    "MaxAccuracy": 92,
    "weapons": { "rifle": { "threshold": 88 } }
  },
  "recommendations": {
    "SafeDistance": { "current": 30, "recommended": 28, "delta": -2, "confidence": 0.91 },
    "MaxAccuracy": { "current": 92, "recommended": 90, "delta": -2, "confidence": 0.87 },
    "weapons": {
      "rifle": {
        "threshold": { "current": 88, "recommended": 85, "delta": -3, "confidence": 0.89 }
      }
    }
  },
  "model_stats": {
    "precision": 0.94,
    "recall": 0.87,
    "f1_score": 0.90,
    "last_retrain": "2026-05-14T18:00:00Z"
  }
}
```

**POST /feedback**
```json
{
  "player_id": "76561198000000000",
  "outcome": "confirmed_cheater",  // or "false_positive" or "uncertain"
  "feedback_timestamp": 1715790300000,
  "admin_comment": "Obvious aimbot, reported by multiple players"
}
```

Response: `{ "status": "recorded", "feedback_id": "fbk_xyz789" }`

### 4.3 Feature Engineering

**Shot-level features:**
- Distance (meters)
- Ping at shot (ms)
- Delta ping since last shot (ms)
- Time since last shot (ms)
- Accuracy in rolling window (%)
- Weapon type
- Hit/miss outcome
- Distance category (0-25m, 25-50m, 50-100m, 100m+)

**Session-level features:**
- KDA ratio
- Average accuracy per weapon
- Ping baseline (mean, stddev)
- Ping spike frequency (spikes/hour)
- Average accuracy by distance
- Headshot % 
- Movement patterns (unpredictable vs. static)
- Weapon rotation pattern (versatile vs. one-trick)

**Player-level features:**
- Total playtime (hours)
- Skill progression (accuracy trend over 7 days)
- Pattern consistency (stddev of daily accuracy)
- Multi-server presence (if federated)

### 4.4 Neural Network Architecture (Conceptual)

```
Input Layer
├─ Shot features (8 dims): distance, ping, delta_ping, time_since_last, accuracy, weapon_type, hit, distance_cat
├─ Session context (6 dims): avg_accuracy, kda, headshot%, ping_mean, ping_stddev, spike_freq
└─ Player context (3 dims): playtime, skill_trend, consistency

Dense Layers
├─ Layer 1: 32 neurons, ReLU
├─ Layer 2: 16 neurons, ReLU
├─ Dropout: 0.3
├─ Layer 3: 8 neurons, ReLU
└─ Output: 1 neuron, Sigmoid → [0.0, 1.0] confidence score

Loss: Binary Crossentropy (cheater vs. legitimate)
Optimizer: Adam
Training: Labeled data (confirmed cheaters, legitimate players, false positives)
```

### 4.5 Training Pipeline

**Data preparation:**
1. Collect events from M6 (shots, hits, kills, ping data)
2. Label examples: "confirmed_cheater" (admin override), "legitimate" (1+ weeks clean), "uncertain" (skip)
3. Augment: Shift accuracy by ±5%, vary ping by ±10% (data scarcity mitigation)

**Training schedule:**
- Daily: Lightweight inference optimization (batch predictions)
- Weekly: Full model retrain (Sunday 02:00 UTC)
- Monthly: Hyperparameter sweep, threshold optimization

**Feedback integration:**
- Admin marks player as "confirmed_cheater" or "false_positive"
- Plugin sends feedback to ML service
- Feedback labeled automatically from timestamp (can be added to next training run)

## 5. Configuration Changes

**Plugin config additions:**
```json
{
  "MLService": {
    "Enabled": false,
    "Endpoint": null,               // e.g., "http://ml-service:8080"
    "AuthToken": null,
    "TimeoutSeconds": 5,
    "RetryAttempts": 2,
    "BatchTimeoutSeconds": 30,
    "CacheSuggestionsSeconds": 60,  // Cache penalty suggestions for 60 sec
    "FallbackToLocalScoring": true  // If service unavailable, use built-in heuristics
  }
}
```

**ML Service config (separate):**
```json
{
  "service": {
    "port": 8080,
    "auth_token": "ml_service_secret_token",
    "log_level": "info"
  },
  "ingestion": {
    "max_batch_size": 10000,
    "queue_flush_interval_sec": 30
  },
  "model": {
    "model_path": "models/cheater_detector.h5",
    "feature_config_path": "config/features.json",
    "min_samples_for_inference": 20,
    "confidence_threshold": 0.60
  },
  "training": {
    "schedule": "0 2 * * 0",        // Weekly Sunday 02:00 UTC
    "min_labeled_samples": 500,
    "test_split": 0.2,
    "validation_split": 0.1
  },
  "persistence": {
    "data_dir": "/var/lib/mogyantiml",
    "logs_dir": "/var/log/mogyantiml",
    "retention_days": 365
  }
}
```

## 6. Public API / Hook Changes

**Plugin-side query (new):**
```csharp
Dictionary<string, object> GetMLPenaltySuggestion(ulong playerId, string weapon = null)
// Returns:
// {
//   confidence: 0.75,
//   suggested_nerf_pct: 35,
//   anomaly_type: "high_accuracy_cluster",
//   reason: "Pattern matches X% of confirmed cheaters"
// }
```

**Plugin-side feedback method (new):**
```csharp
void ReportFeedback(ulong playerId, string outcome, string adminComment = null)
// outcome = "confirmed_cheater" | "false_positive" | "uncertain"
// Sends feedback asynchronously to ML service
```

**Webhook expansion:**
All existing webhooks now include optional ML fields:
```json
{
  "event": "OnMogyAcPenaltyApplied",
  "player_id": "...",
  "ml_confidence": 0.72,
  "ml_anomaly_type": "high_accuracy_cluster",
  "ml_suggested_nerf_pct": 35,
  "ml_applied": true,  // Did plugin use ML suggestion?
  "...": "..."
}
```

**API Version:** Bump to `1.3.0` (minor version)

## 7. Compatibility and Migration

- **Standalone plugin**: Works perfectly without ML service (falls back to built-in heuristics)
- **Gradual adoption**: Admins can enable ML service without disrupting existing configs
- **Service unavailable**: Plugin queues events locally, resumes sync when service returns
- **Model versioning**: Service includes model version in responses; plugin logs for audit

## 8. Security / Abuse Considerations

- **Data privacy**: Events don't include player names, only IDs + gameplay metrics
- **Service auth**: All requests signed with AuthToken (HTTP header: `Authorization: Bearer <token>`)
- **Data retention**: ML service can be air-gapped (no external internet) or federated
- **Feedback abuse**: Admin marks player as cheater, but needs evidence (server logs correlate)
- **Model poisoning**: Ignore feedback from single source; require consensus before retraining

## 9. Test Plan

- **Unit tests:**
  - Feature extraction (ping stats, accuracy, KDA)
  - Model inference (input normalization, output bounds)
  - Config recommendation logic (confidence thresholds)
  - Feedback ingestion (deduplication, labeling)

- **Integration tests:**
  - Plugin → ML service batch ingestion (success + failure cases)
  - Penalty suggestion retrieval and application
  - Config recommendation and comparison
  - Feedback loop (confirm cheater → model retrain → better scoring)

- **Offline validation:**
  - Train model on known cheater data, test precision/recall > 85%
  - False-positive rate < 5% on legitimate high-skill players
  - Config recommendations improve server balance (A/B test)

- **In-game validation:**
  - ML service scoring applied correctly in real-time
  - Fallback to local scoring if service unavailable
  - Webhook payload includes ML fields
  - Admin feedback recorded and used for retraining

## 10. Rollout Plan

1. **Phase 0** (Preparation): Collect baseline data with M6/M7 enabled, build training dataset
2. **Phase 1** (Beta): Deploy ML service on isolated staging server, test with synthetic data
3. **Phase 2** (Canary): Enable on 2-3 production servers, monitor penalty suggestions vs. admin feedback
4. **Phase 3** (Tuning): Adjust thresholds based on false-positive rates, retrain on server-specific data
5. **Phase 4** (General): Roll out to all servers with documentation

**Monitoring:**
- ML service uptime (alert if < 99%)
- Inference latency (p95 < 500ms)
- Model staleness (alert if retrain failed > 1 week)
- Feedback loop health (confirm/false positive ratio)

## 11. Acceptance Criteria

- [ ] ML service accepts batched events and queues for processing
- [ ] Feature extraction correctly computes all dimensions
- [ ] Neural network model trains on labeled data (precision/recall > 85%)
- [ ] Inference API returns confidence scores in expected range [0.0, 1.0]
- [ ] Config recommendation logic suggests tuned thresholds
- [ ] Plugin gracefully handles service unavailability (fallback mode)
- [ ] Feedback loop records admin marks and integrates into retraining
- [ ] Penalty suggestions applied correctly (nerfing %, audit trail)
- [ ] Model versioning and update strategy documented
- [ ] False-positive rate < 5% on legitimate players
- [ ] Webhook payloads include ML confidence and anomaly type
- [ ] Service and plugin can operate independently (no hard dependency)
