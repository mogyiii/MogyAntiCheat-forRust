# MogyAntiCheat ML Service

Standalone REST service that augments the MogyAntiCheat plugin with anomaly scoring, and an offline
trainer that calibrates the plugin's config from real telemetry.

The plugin works without this service (it falls back to its built-in heuristics), so the service is
entirely optional. The **trainer** is useful even if you never run the service: it reads your event
logs and tells you where your thresholds should be.

## Layout

```
train.py          offline trainer: logs -> model.json + config-recommendation.json + report
report_charts.py  anonymized per-player statistics page (one dot per player, no names)
server.py         Flask service serving the trained model
selftest.py       124 checks over the whole pipeline, no server or network needed
mogyac/        shared library (stdlib only)
  logparse.py    streaming reader for both event-log formats
  replay.py      replica of the plugin's WeaponData logic + feature extraction
  calibrate.py   per-weapon threshold calibration
  scoring.py     robust anomaly scorer, fit offline / applied online
  statsutil.py   percentile, median, MAD helpers
reports/       generated training reports
data/          runtime state: ingested events, admin verdicts (gitignored)
```

## Quick start

```bash
python train.py                 # reads ../logs, writes model.json
python report_charts.py         # writes reports/player-statistics.html
pip install flask
ML_AUTH_TOKEN=your_secret python server.py
```

Default port `8080` (`PORT` to change). Python 3.8+; the trainer needs no third-party packages at
all, `server.py` needs only Flask.

Full training documentation, including how each threshold is derived and what the first run found:
[`docs/ML_TRAINING.md`](../docs/ML_TRAINING.md).

## Environment

| Variable | Default | Purpose |
|---|---|---|
| `ML_AUTH_TOKEN` | *(empty)* | Bearer token required on every request. Empty disables auth (dev only). |
| `PORT` | `8080` | Listen port. |
| `ML_MODEL_PATH` | `./model.json` | Trained model to serve. |
| `ML_DATA_DIR` | `./data` | Where feedback and ingested events are written. |
| `ML_PERSIST_EVENTS` | `1` | Append incoming events to `data/ingested-events.jsonl` for the next retrain. |
| `ML_SCORE_TTL_SECONDS` | `3600` | How long a quiet player's scores and window state are kept. |

## Plugin configuration

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
| `POST` | `/ingest` | Receive batched telemetry events from the plugin and score them |
| `GET` | `/penalty-suggestion` | Per-weapon anomaly confidence for a player |
| `GET` | `/config-recommend` | Calibrated config values from the last training run |
| `POST` | `/feedback` | Record an admin verdict (labels for the next retrain) |
| `GET` | `/model-info` | Which model is loaded, its features and weights |
| `POST` | `/reload-model` | Re-read `model.json` without restarting |
| `GET` | `/health` | Liveness and counters |

Without a `model.json` the service still answers every endpoint, but reports
`model_loaded: false`, suggests no nerfs, and returns no config changes. It does not guess.

## POST /ingest

Since plugin `1.10.0` the body is a **bare JSON array** of event objects (no `server_id` /
`batch_id` / `timestamp` / `events` wrapper — the wrapped form is still accepted). Field names are
the serialized C# property names, and the player identifier is an **irreversible per-server hash**
(`PlayerHash`), never a raw SteamID.

**Request body:**
```json
[
  {
    "TimestampMs": 1715790220000,
    "PlayerHash": "a1b2c3d4e5f60718",
    "WeaponName": "rifle.ak",
    "Distance": 45.2,
    "Hit": true,
    "PingMs": 87,
    "DeltaPingMs": 3,
    "AccuracyInWindow": 0.78,
    "EventType": "shot",
    "HitArea": "chest",
    "GameTimeHour": 13.5,
    "AimDeltaDeg": 12.4,
    "SnapDeg": 8.1,
    "SnapSettleMs": 145.0
  }
]
```

- `EventType` is one of `shot`, `hit`, `kill`, `death`.
- `HitArea` and `GameTimeHour` may be absent/`null`/`-1` for some event types.
- `PlayerHash` is stable within a server (so behaviour can be attributed) but not linkable to a
  real person — see `docs/DATA_COLLECTION.md`.
- `AimDeltaDeg` / `SnapDeg` / `SnapSettleMs` describe how the view arrived on target before the
  shot, and are `-1` when unmeasured (on `hit` events, when `AimTracking` is off, or in logs
  written before the field existed).

Events are replayed through the same window logic the trainer used, so a player's features are
computed identically online and offline. `hit` events produce a score; `shot` events accumulate
window state.

**Response:** `{ "status": "accepted", "batch_id": "...", "scored": 12, "model_loaded": true }`

> **Contributor note — key mismatch:** `/ingest` keys players by `PlayerHash`, but
> `/penalty-suggestion` and `/feedback` are still called by the plugin with the **raw**
> `player_id`. Until the plugin hashes the query side too, a lookup for a raw SteamID will not
> find the data ingested under its hash. Hash consistently on one side to correlate them.

## GET /penalty-suggestion

**Query params:** `player_id` (required)

```json
{
  "player_id": "76561198000000000",
  "weapons": {
    "rifle.ak": {
      "ml_confidence": 0.993,
      "suggested_nerf_pct": 15,
      "anomaly_type": "headshot_clustering",
      "reason": "Outlier on headshot_ratio=0.71 (z-weighted +4.02); accuracy=0.88 (z-weighted +2.90)",
      "recommended_action": "apply_nerf",
      "accuracy": 0.88,
      "sample_count": 40,
      "top_factors": ["headshot_ratio", "accuracy", "longrange_share"]
    }
  },
  "global_assessment": { "confidence": 0.993, "summary": "...", "model_loaded": true },
  "timestamp": 1715790225000
}
```

`ml_confidence` is a **percentile rank against the trained population**, i.e. "more unusual than
99.3% of the observed player-weapon population on this weapon" — not a probability of cheating.
There are no labelled cheaters in the training data; see `docs/ML_TRAINING.md`.

## GET /config-recommend

Returns the calibrated values from the last training run, in the flat
`Weapons.<weapon>.<Field>` shape `/ac-suggest` renders in game:

```json
{
  "trained_on_samples": 34745,
  "recommendations": {
    "Weapons.ak47u.MaxAccuracy": {
      "current": 0.35, "recommended": 0.95, "delta": 0.6,
      "confidence": 0.938, "source": "calibrated"
    },
    "Weapons.ak47u_diver.MaxAccuracy": {
      "current": "unset", "recommended": 0.843, "delta": null,
      "confidence": 0.3, "source": "family_fallback:auto_rifle"
    }
  },
  "model_stats": {
    "labels": "unsupervised",
    "flag_rate_current": 0.3907,
    "flag_rate_calibrated": 0.0229,
    "note": "..."
  }
}
```

`precision`/`recall` are deliberately absent: with no labelled cheaters they are not measurable.
What *is* measurable — how the calibrated thresholds change the plugin's behaviour on replayed
events — is reported instead.

## POST /feedback

```json
{
  "player_id": "76561198000000000",
  "outcome": "confirmed_cheater",
  "admin_comment": "Obvious aimbot, reported by multiple players"
}
```

`outcome` must be one of `confirmed_cheater`, `false_positive`, `uncertain`.
**Response:** `{ "status": "recorded", "feedback_id": "fbk_abc123" }`

Verdicts are appended to `data/feedback.jsonl`. They are the only labelled data this system gets:
`python train.py --feedback data/feedback.jsonl` uses them to learn the scorer's feature weights
instead of the built-in priors.

## Authentication

Pass `Authorization: Bearer <token>` on every request when `ML_AUTH_TOKEN` is set. Leave the env
var empty to disable auth (dev/testing only).

## Production notes

Scores and live window state are in memory and reset on restart; `data/feedback.jsonl` and
`data/ingested-events.jsonl` are on disk. For a busy server, replace the in-memory stores with a
database and put the app behind a real WSGI server (`gunicorn`, `waitress`) — Flask's development
server is single-threaded and the shared state is guarded by one lock.

Retraining is a batch job: run `train.py` over the accumulated logs, then `POST /reload-model`.
No restart, no downtime.
