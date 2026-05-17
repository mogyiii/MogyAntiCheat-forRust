# MogyAntiCheat ML Service

Standalone REST service that augments the MogyAntiCheat plugin with machine-learning-based confidence scoring.

The plugin works without this service (falls back to built-in heuristics). This service is entirely optional.

## Quick Start

```bash
pip install flask
ML_AUTH_TOKEN=your_secret python server.py
```

Default port: `8080`. Override with `PORT` env var.

## Plugin Configuration

```json
"MLService": {
  "Enabled": true,
  "Endpoint": "http://localhost:8080",
  "AuthToken": "your_secret",
  "TimeoutSeconds": 5,
  "CacheSuggestionsSeconds": 60,
  "FallbackToLocalScoring": true
}
```

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/ingest` | Receive batched telemetry events from the plugin |
| `GET` | `/penalty-suggestion` | Return per-weapon ML confidence score for a player |
| `GET` | `/config-recommend` | Return tuning recommendations based on ingested data |
| `POST` | `/feedback` | Record admin outcome feedback (confirmed_cheater / false_positive / uncertain) |
| `GET` | `/health` | Liveness check |

## POST /ingest

**Request body:**
```json
{
  "server_id": "server_123",
  "batch_id": "batch_abcd1234",
  "timestamp": 1715790225000,
  "events": [
    {
      "event_type": "shot",
      "player_id": "76561198000000000",
      "weapon": "rifle.ak",
      "distance": 45.2,
      "ping_at_shot": 87,
      "delta_ping": 3,
      "accuracy_in_window": 0.78,
      "hit": true,
      "timestamp": 1715790220000
    }
  ]
}
```

**Response:** `{ "status": "accepted", "batch_id": "..." }`

## GET /penalty-suggestion

**Query params:** `player_id` (required)

**Response:**
```json
{
  "player_id": "76561198000000000",
  "weapons": {
    "rifle.ak": {
      "ml_confidence": 0.72,
      "suggested_nerf_pct": 35,
      "anomaly_type": "high_accuracy_stability",
      "reason": "Pattern matches confirmed cheater profile",
      "recommended_action": "apply_nerf"
    }
  },
  "global_assessment": { "confidence": 0.72, "summary": "..." },
  "timestamp": 1715790225000
}
```

## POST /feedback

**Request body:**
```json
{
  "player_id": "76561198000000000",
  "outcome": "confirmed_cheater",
  "admin_comment": "Obvious aimbot, reported by multiple players"
}
```

`outcome` must be one of: `confirmed_cheater`, `false_positive`, `uncertain`.

**Response:** `{ "status": "recorded", "feedback_id": "fbk_abc123" }`

## Authentication

Pass `Authorization: Bearer <token>` on every request when `ML_AUTH_TOKEN` is set. Leave the env var empty to disable auth (dev/testing only).

## Production Notes

`server.py` uses in-memory storage for simplicity — events and scores are lost on restart. Replace the storage layer with a database (SQLite, PostgreSQL, etc.) and a real trained model before deploying to production. The endpoint contracts above are stable; only the scoring logic is a stub.
