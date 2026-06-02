"""
MogyAntiCheat ML Service — minimal Flask stub
Implements: /ingest, /penalty-suggestion, /config-recommend, /feedback
Run: pip install flask && python server.py
"""

import json
import logging
import os
import time
import uuid
from collections import defaultdict
from flask import Flask, jsonify, request, abort

app = Flask(__name__)
logging.basicConfig(level=logging.INFO)
log = logging.getLogger(__name__)

AUTH_TOKEN = os.environ.get("ML_AUTH_TOKEN", "")

# In-memory stores (replace with a real DB for production)
ingested_events = []
feedback_records = []
# player_id -> {weapon -> {ml_confidence, suggested_nerf_pct, anomaly_type, reason}}
player_scores = defaultdict(dict)


def check_auth():
    if not AUTH_TOKEN:
        return
    token = request.headers.get("Authorization", "")
    if token != f"Bearer {AUTH_TOKEN}":
        abort(401, "Unauthorized")


# ---------------------------------------------------------------------------
# POST /ingest
# ---------------------------------------------------------------------------
@app.route("/ingest", methods=["POST"])
def ingest():
    check_auth()
    body = request.get_json(force=True, silent=True)
    if not body or "events" not in body:
        abort(400, "Missing 'events' field")

    batch_id = body.get("batch_id", str(uuid.uuid4()))
    events = body["events"]
    ingested_events.extend(events)
    log.info("Ingested batch %s with %d events (total stored: %d)", batch_id, len(events), len(ingested_events))

    # Stub: simple heuristic scoring — replace with real model inference
    for ev in events:
        pid = str(ev.get("PlayerId", ev.get("player_id", "")))
        weapon = ev.get("WeaponName", ev.get("weapon", "unknown"))
        if ev.get("EventType", ev.get("event_type", "")) == "hit" and pid:
            accuracy = float(ev.get("AccuracyInWindow", ev.get("accuracy_in_window", 0.0)))
            distance = float(ev.get("Distance", ev.get("distance", 0.0)))
            ping = int(ev.get("PingMs", ev.get("ping_at_shot", 0)))
            # Naive scoring stub
            confidence = min(1.0, max(0.0, (accuracy - 0.70) * 2.0 + (distance / 200.0) - (ping / 500.0)))
            nerf_pct = int(confidence * 50) if confidence > 0.60 else 0
            player_scores[pid][weapon] = {
                "ml_confidence": round(confidence, 3),
                "suggested_nerf_pct": nerf_pct,
                "anomaly_type": "high_accuracy_stability" if confidence > 0.60 else None,
                "reason": f"Stub heuristic: acc={accuracy:.2f} dist={distance:.1f}m",
            }

    return jsonify({"status": "accepted", "batch_id": batch_id})


# ---------------------------------------------------------------------------
# GET /penalty-suggestion
# ---------------------------------------------------------------------------
@app.route("/penalty-suggestion", methods=["GET"])
def penalty_suggestion():
    check_auth()
    player_id = request.args.get("player_id")
    if not player_id:
        abort(400, "Missing player_id")

    scores = player_scores.get(player_id, {})
    weapons_out = {}
    for weapon, data in scores.items():
        weapons_out[weapon] = {
            "ml_confidence": data["ml_confidence"],
            "suggested_nerf_pct": data["suggested_nerf_pct"],
            "anomaly_type": data["anomaly_type"],
            "reason": data["reason"],
            "recommended_action": "apply_nerf" if data["suggested_nerf_pct"] > 0 else "monitor",
        }

    global_confidence = max((v["ml_confidence"] for v in scores.values()), default=0.0)
    return jsonify({
        "player_id": player_id,
        "weapons": weapons_out,
        "global_assessment": {
            "confidence": round(global_confidence, 3),
            "summary": "Stub response — replace with real model" if not scores else "Scores computed from ingested events",
        },
        "timestamp": int(time.time() * 1000),
    })


# ---------------------------------------------------------------------------
# GET /config-recommend
# ---------------------------------------------------------------------------
@app.route("/config-recommend", methods=["GET"])
def config_recommend():
    check_auth()
    sample_count = len(ingested_events)
    return jsonify({
        "trained_on_samples": sample_count,
        "recommendations": {
            "SafeDistance": {"current": 30, "recommended": 30, "delta": 0, "confidence": 0.0},
            "MaxAccuracy": {"current": 0.92, "recommended": 0.92, "delta": 0.0, "confidence": 0.0},
        },
        "model_stats": {
            "precision": 0.0,
            "recall": 0.0,
            "f1_score": 0.0,
            "note": "Stub — model not trained yet",
        },
        "timestamp": int(time.time() * 1000),
    })


# ---------------------------------------------------------------------------
# POST /feedback
# ---------------------------------------------------------------------------
@app.route("/feedback", methods=["POST"])
def feedback():
    check_auth()
    body = request.get_json(force=True, silent=True)
    if not body or "player_id" not in body or "outcome" not in body:
        abort(400, "Missing player_id or outcome")

    valid_outcomes = {"confirmed_cheater", "false_positive", "uncertain"}
    if body["outcome"] not in valid_outcomes:
        abort(400, f"outcome must be one of: {', '.join(valid_outcomes)}")

    feedback_id = f"fbk_{uuid.uuid4().hex[:8]}"
    record = {
        "feedback_id": feedback_id,
        "player_id": str(body["player_id"]),
        "outcome": body["outcome"],
        "admin_comment": body.get("admin_comment", ""),
        "received_at": int(time.time() * 1000),
    }
    feedback_records.append(record)
    log.info("Feedback recorded: %s → %s (%s)", record["player_id"], record["outcome"], feedback_id)

    return jsonify({"status": "recorded", "feedback_id": feedback_id})


# ---------------------------------------------------------------------------
# GET /health
# ---------------------------------------------------------------------------
@app.route("/health", methods=["GET"])
def health():
    return jsonify({
        "status": "ok",
        "ingested_events": len(ingested_events),
        "feedback_records": len(feedback_records),
        "players_scored": len(player_scores),
    })


if __name__ == "__main__":
    port = int(os.environ.get("PORT", 8080))
    log.info("MogyAntiCheat ML Service starting on port %d", port)
    app.run(host="0.0.0.0", port=port)
