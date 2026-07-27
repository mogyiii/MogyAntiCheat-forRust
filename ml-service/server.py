"""
MogyAntiCheat ML Service.

Serves the model produced by `train.py`. The endpoint contracts are unchanged from the original
stub — only the scoring behind them is real now.

    pip install flask
    python train.py                        # produces model.json
    ML_AUTH_TOKEN=secret python server.py

Live events arriving on /ingest are replayed through the same `mogyac.replay` window logic the
trainer used, so a player's features are computed identically online and offline. Scores are
percentile ranks against the trained population baselines.

Without a model.json the service still answers, but it says so: `model_loaded: false`, no nerf
suggestions, and /config-recommend returns nothing to change. It never invents a verdict.
"""

import json
import logging
import os
import threading
import time
import uuid

from flask import Flask, abort, jsonify, request

from mogyac import logparse, replay, scoring

app = Flask(__name__)
logging.basicConfig(level=logging.INFO)
log = logging.getLogger(__name__)

HERE = os.path.dirname(os.path.abspath(__file__))
AUTH_TOKEN = os.environ.get("ML_AUTH_TOKEN", "")
MODEL_PATH = os.environ.get("ML_MODEL_PATH", os.path.join(HERE, "model.json"))
DATA_DIR = os.environ.get("ML_DATA_DIR", os.path.join(HERE, "data"))
FEEDBACK_PATH = os.path.join(DATA_DIR, "feedback.jsonl")
EVENT_LOG_PATH = os.path.join(DATA_DIR, "ingested-events.jsonl")

# How long a player's scores stay served after their last event. The plugin caches suggestions
# itself; this only bounds memory on a long-running service.
SCORE_TTL_SECONDS = int(os.environ.get("ML_SCORE_TTL_SECONDS", 3600))
PERSIST_EVENTS = os.environ.get("ML_PERSIST_EVENTS", "1") not in ("0", "false", "False")

_lock = threading.Lock()
_model = {}
_engine = None
# player -> {weapon -> score dict}, plus a last-seen stamp for expiry
_scores = {}
_last_seen = {}
_counters = {"ingested_events": 0, "scored_evaluations": 0, "feedback_records": 0}


# ---------------------------------------------------------------------------------------
# model loading
# ---------------------------------------------------------------------------------------
def load_model():
    """(Re)load model.json and reset the live replay state to match its config."""
    global _model, _engine
    model = {}
    if os.path.exists(MODEL_PATH):
        with open(MODEL_PATH, "r", encoding="utf-8") as handle:
            model = json.load(handle)
        log.info("Loaded model trained %s on %d events (%d weapon baselines)",
                 model.get("trained_at", "?"),
                 model.get("trained_on", {}).get("events", 0),
                 len([k for k in model.get("baselines", {}) if k != scoring.GLOBAL_BASELINE_KEY]))
    else:
        log.warning("No model at %s — run train.py. Serving without scoring.", MODEL_PATH)

    with _lock:
        _model = model
        _engine = replay.ReplayEngine(
            model.get("weapons_config") or {},
            miss_expiry_ms=float(model.get("miss_expiry_seconds", 20.0)) * 1000.0,
            fallback_cfg=model.get("weapon_fallback"),
            max_hit_distance=float(model.get("max_hit_distance", replay.MAX_PLAUSIBLE_DISTANCE)),
        )
        _scores.clear()
        _last_seen.clear()
    return model


def model_loaded():
    return bool(_model.get("baselines"))


def check_auth():
    if not AUTH_TOKEN:
        return
    if request.headers.get("Authorization", "") != "Bearer %s" % AUTH_TOKEN:
        abort(401, "Unauthorized")


def ensure_data_dir():
    if not os.path.isdir(DATA_DIR):
        os.makedirs(DATA_DIR)


def expire_stale(now=None):
    """Drop players (and their replay windows) that have gone quiet."""
    now = now or time.time()
    stale = [player for player, seen in _last_seen.items() if now - seen > SCORE_TTL_SECONDS]
    for player in stale:
        _last_seen.pop(player, None)
        _scores.pop(player, None)
        if _engine is not None:
            for key in [k for k in _engine.state if k[0] == player]:
                del _engine.state[key]
    return len(stale)


# ---------------------------------------------------------------------------------------
# POST /ingest
# ---------------------------------------------------------------------------------------
@app.route("/ingest", methods=["POST"])
def ingest():
    check_auth()
    body = request.get_json(force=True, silent=True)
    if body is None:
        abort(400, "Expected a JSON array of events")

    # Plugin >= 1.10.0 posts a bare array; older builds wrapped it in {"events": [...]}.
    if isinstance(body, dict):
        raw_events = body.get("events")
        batch_id = body.get("batch_id", str(uuid.uuid4()))
    else:
        raw_events = body
        batch_id = str(uuid.uuid4())
    if not isinstance(raw_events, list):
        abort(400, "Expected a JSON array of events")

    now = time.time()
    scored = 0
    with _lock:
        for raw in raw_events:
            if not isinstance(raw, dict):
                continue
            event = logparse.normalize(raw)
            if event is None:
                continue
            _counters["ingested_events"] += 1
            _last_seen[event.player] = now

            evaluation = _engine.feed(event) if _engine is not None else None
            if evaluation is None or not model_loaded():
                continue

            result = scoring.score(_model, evaluation.weapon, evaluation.features,
                                   sample_count=evaluation.sample_count)
            result["reason"] = scoring.explain(result, evaluation.features)
            result["accuracy"] = round(evaluation.accuracy, 3)
            result["sample_count"] = evaluation.sample_count
            result["weighted_score"] = round(evaluation.weighted_score, 3)
            result["updated_at"] = int(now * 1000)
            _scores.setdefault(event.player, {})[evaluation.weapon] = result
            scored += 1
            _counters["scored_evaluations"] += 1
        expire_stale(now)

    if PERSIST_EVENTS:
        try:
            ensure_data_dir()
            with open(EVENT_LOG_PATH, "a", encoding="utf-8") as handle:
                for raw in raw_events:
                    if isinstance(raw, dict):
                        handle.write(json.dumps(raw) + "\n")
        except OSError as exc:
            # Losing the archive must never cost us a live score.
            log.warning("Could not append to %s: %s", EVENT_LOG_PATH, exc)

    log.info("Ingested %d events (%d scored) batch=%s", len(raw_events), scored, batch_id)
    return jsonify({"status": "accepted", "batch_id": batch_id, "scored": scored,
                    "model_loaded": model_loaded()})


# ---------------------------------------------------------------------------------------
# GET /penalty-suggestion
# ---------------------------------------------------------------------------------------
@app.route("/penalty-suggestion", methods=["GET"])
def penalty_suggestion():
    check_auth()
    player_id = request.args.get("player_id")
    if not player_id:
        abort(400, "Missing player_id")

    # The plugin queries with the raw player id while /ingest keys on the hashed one, so accept
    # either. See the contributor note in README.md.
    with _lock:
        scores = _scores.get(str(player_id)) or {}
        weapons_out = {}
        for weapon, data in scores.items():
            weapons_out[weapon] = {
                "ml_confidence": data["ml_confidence"],
                "suggested_nerf_pct": data["suggested_nerf_pct"],
                "anomaly_type": data["anomaly_type"],
                "reason": data["reason"],
                "recommended_action": "apply_nerf" if data["suggested_nerf_pct"] > 0 else "monitor",
                "accuracy": data["accuracy"],
                "sample_count": data["sample_count"],
                "top_factors": data["top_factors"],
            }
        confidences = [d["ml_confidence"] for d in scores.values()]

    if not model_loaded():
        summary = "No trained model loaded — run train.py."
    elif not weapons_out:
        summary = "No recent telemetry for this player."
    else:
        summary = "Anomaly percentile against the trained population for this weapon."

    return jsonify({
        "player_id": str(player_id),
        "weapons": weapons_out,
        "global_assessment": {
            "confidence": round(max(confidences), 3) if confidences else 0.0,
            "summary": summary,
            "model_loaded": model_loaded(),
        },
        "timestamp": int(time.time() * 1000),
    })


# ---------------------------------------------------------------------------------------
# GET /config-recommend
# ---------------------------------------------------------------------------------------
@app.route("/config-recommend", methods=["GET"])
def config_recommend():
    check_auth()
    recommendation = _model.get("config_recommendation") or {}
    behaviour = recommendation.get("behaviour") or {}
    current = behaviour.get("current") or {}
    calibrated = behaviour.get("calibrated") or {}
    return jsonify({
        "trained_on_samples": recommendation.get("trained_on_samples", 0),
        "recommendations": recommendation.get("recommendations", {}),
        "model_stats": {
            # No cheater labels exist, so precision/recall are not measurable. What *is* measurable
            # is how the calibrated thresholds change the plugin's behaviour on replayed events.
            "labels": _model.get("label_source", "unsupervised"),
            "flag_rate_current": current.get("flag_rate", 0.0),
            "flag_rate_calibrated": calibrated.get("flag_rate", 0.0),
            "zero_damage_rate_current": current.get("zero_damage_rate", 0.0),
            "zero_damage_rate_calibrated": calibrated.get("zero_damage_rate", 0.0),
            "note": ("Thresholds calibrated from replayed telemetry; no labelled cheaters, so "
                     "precision/recall are not defined. Feed admin verdicts to /feedback and "
                     "retrain with --feedback to learn feature weights."),
        },
        "trained_at": _model.get("trained_at"),
        "timestamp": int(time.time() * 1000),
    })


# ---------------------------------------------------------------------------------------
# POST /feedback
# ---------------------------------------------------------------------------------------
@app.route("/feedback", methods=["POST"])
def feedback():
    check_auth()
    body = request.get_json(force=True, silent=True)
    if not body or "player_id" not in body or "outcome" not in body:
        abort(400, "Missing player_id or outcome")

    valid_outcomes = {"confirmed_cheater", "false_positive", "uncertain"}
    if body["outcome"] not in valid_outcomes:
        abort(400, "outcome must be one of: %s" % ", ".join(sorted(valid_outcomes)))

    feedback_id = "fbk_%s" % uuid.uuid4().hex[:8]
    record = {
        "feedback_id": feedback_id,
        "player_id": str(body["player_id"]),
        "outcome": body["outcome"],
        "admin_comment": body.get("admin_comment", ""),
        "received_at": int(time.time() * 1000),
    }
    # Verdicts are the only labelled data this system will ever get, so they go to disk before
    # anything else can go wrong. train.py --feedback reads this file.
    try:
        ensure_data_dir()
        with open(FEEDBACK_PATH, "a", encoding="utf-8") as handle:
            handle.write(json.dumps(record) + "\n")
    except OSError as exc:
        log.error("Could not persist feedback: %s", exc)
        abort(500, "Could not persist feedback")

    with _lock:
        _counters["feedback_records"] += 1
    log.info("Feedback recorded: %s -> %s (%s)", record["player_id"], record["outcome"], feedback_id)
    return jsonify({"status": "recorded", "feedback_id": feedback_id})


# ---------------------------------------------------------------------------------------
# GET /model-info, POST /reload-model, GET /health
# ---------------------------------------------------------------------------------------
@app.route("/model-info", methods=["GET"])
def model_info():
    check_auth()
    baselines = _model.get("baselines", {})
    return jsonify({
        "model_loaded": model_loaded(),
        "model_format_version": _model.get("model_format_version"),
        "trained_at": _model.get("trained_at"),
        "trained_on": _model.get("trained_on", {}),
        "features": _model.get("features", []),
        "weights": _model.get("weights", {}),
        "label_source": _model.get("label_source", "unsupervised"),
        "refit": _model.get("refit"),
        "decision_percentile": _model.get("decision_percentile"),
        "miss_expiry_seconds": _model.get("miss_expiry_seconds"),
        "weapon_baselines": sorted(k for k in baselines if k != scoring.GLOBAL_BASELINE_KEY),
    })


@app.route("/reload-model", methods=["POST"])
def reload_model():
    """Pick up a freshly trained model.json without restarting the service."""
    check_auth()
    load_model()
    return jsonify({"status": "reloaded", "model_loaded": model_loaded(),
                    "trained_at": _model.get("trained_at")})


@app.route("/health", methods=["GET"])
def health():
    with _lock:
        tracked = len(_scores)
        windows = len(_engine.state) if _engine is not None else 0
        counters = dict(_counters)
    return jsonify({
        "status": "ok",
        "model_loaded": model_loaded(),
        "trained_at": _model.get("trained_at"),
        "players_tracked": tracked,
        "active_windows": windows,
        **counters,
    })


load_model()

if __name__ == "__main__":
    port = int(os.environ.get("PORT", 8080))
    log.info("MogyAntiCheat ML Service starting on port %d", port)
    app.run(host="0.0.0.0", port=port)
