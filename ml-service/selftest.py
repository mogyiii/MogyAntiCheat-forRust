#!/usr/bin/env python3
"""
Self-test for the ML service. No framework, no dependencies beyond Flask (skipped if absent).

    python selftest.py

The important cases are the replay ones: `mogyac/replay.py` is a hand-maintained copy of the
plugin's WeaponData logic, and every trained threshold is only meaningful while the copy matches.
The expected values below were worked out by hand from MogyAntiCheat.cs — if a change to the
plugin's algorithm breaks them, that is the point.
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mogyac import calibrate, logparse, replay, scoring
from mogyac import statsutil as su

FAILURES = []


def check(name, actual, expected, tolerance=1e-9):
    ok = (abs(actual - expected) <= tolerance
          if isinstance(expected, (int, float)) and not isinstance(expected, bool)
          else actual == expected)
    print("%-58s %s (got %r)" % (name, "ok" if ok else "FAIL", actual))
    if not ok:
        FAILURES.append("%s: expected %r, got %r" % (name, expected, actual))


def event(ts, player, weapon, kind, distance=0.0, ping=50, dping=0, area=None):
    return logparse.Event(ts, player, weapon, kind, distance, ping, dping, area, 0.0)


# ---------------------------------------------------------------------------------------
print("\n-- replay: WeaponData state machine --")

# Four shots then a hit: the three earlier pending shots backfill as misses, so 1 hit / 4 shots.
win = replay.WeaponWindow()
for i in range(4):
    win.add_miss(1000 + i * 100)
win.register_hit(1500, 20.0, limit=40, expiry_ms=20000.0)
check("4 shots + 1 hit -> history length", len(win.history), 4)
check("4 shots + 1 hit -> accuracy", win.accuracy(), 0.25)

# Shots older than MissExpirySeconds are never counted as misses — the bias the trainer measures.
# Three shots were fired and one landed, but the two the player missed 29-30 s earlier are
# discarded, so the window reads 100% accuracy off a genuine 33%.
win = replay.WeaponWindow()
win.add_miss(0)          # 30 s before the hit
win.add_miss(1000)       # 29 s before
win.add_miss(29000)      # 1 s before, and this is the one the hit consumes
win.register_hit(30000, 15.0, limit=40, expiry_ms=20000.0)
check("expired pending shots dropped -> history length", len(win.history), 1)
check("expired pending shots dropped -> accuracy saturates", win.accuracy(), 1.0)

# A hit with no pending shot at all still records as a hit: this is the saturation source.
win = replay.WeaponWindow()
for i in range(12):
    win.register_hit(1000 + i * 60000, 10.0, limit=40, expiry_ms=20000.0)
check("hits with no pending shots -> accuracy", win.accuracy(), 1.0)
check("hits with no pending shots -> history length", len(win.history), 12)

# History is trimmed to SampleCount, keeping the most recent entries.
win = replay.WeaponWindow()
for i in range(30):
    win.add_miss(i * 100)
win.register_hit(3000, 10.0, limit=10, expiry_ms=20000.0)
check("history trimmed to SampleCount", len(win.history), 10)
check("trimmed history keeps newest (1 hit of 10)", win.accuracy(), 0.1)

# PendingMisses is capped at 100; the oldest is discarded.
win = replay.WeaponWindow()
for i in range(150):
    win.add_miss(i)
check("pending misses capped", len(win.pending), replay.PENDING_CAP)

print("\n-- replay: weighted score --")
win = replay.WeaponWindow()
win.history = [(True, 10.0, 10.0), (True, 50.0, 50.0), (False, 0.0, 0.0)]
# 10 m is inside SafeDistance so it scores 1.0; 50 m scores 50/25 = 2.0; mean over hits = 1.5.
check("weighted score at SafeDistance 25", win.weighted_score(25.0), 1.5)
# A rejected distance is stored as 0 (used) alongside the raw measurement, so the clamped score
# stays at 1.0 while the unclamped one shows what the bug used to produce.
win.history = [(True, 0.0, 1500.0), (True, 10.0, 10.0)]
check("clamped weighted score ignores the bad reading", win.weighted_score(25.0), 1.0)
check("unclamped weighted score shows the old value",
      win.weighted_score(25.0, raw=True), (1500.0 / 25.0 + 1.0) / 2)

print("\n-- replay: penalty math --")
check("below MaxAccuracy -> no penalty", replay.compute_nerf(0.30, 40, 1.0, 0.35)[0], 1.0)
check("too little history -> no penalty", replay.compute_nerf(0.99, 9, 1.0, 0.35)[0], 1.0)
# excess = (0.5 - 0.35) / 0.65 = 0.2308 -> nerf 0.769
check("above MaxAccuracy -> scaled nerf", replay.compute_nerf(0.50, 40, 1.0, 0.35)[0], 0.769, 0.001)
check("nerf below 0.30 collapses to zero", replay.compute_nerf(0.90, 40, 1.0, 0.35)[0], 0.0)
check("accuracy > 0.95 with long range -> zero", replay.compute_nerf(0.96, 40, 1.3, 0.35)[0], 0.0)
check("MaxAccuracy 1.0 disables the weapon", replay.compute_nerf(1.0, 40, 5.0, 1.0)[1], False)
# The squared weighted score is what makes a bogus distance dangerous.
check("weighted score squares the penalty", replay.compute_nerf(0.40, 40, 4.0, 0.35)[0], 0.0)

print("\n-- replay: config key resolution --")
cfg = {"rifle.ak": {}, "ak47u": {}, "shotgun.pump": {}, "smg.2": {}, "rifle.semiauto": {},
       "rifle.bolt": {}, "bow.hunting": {}}
check("exact match wins", replay.resolve_config_key(cfg, "ak47u"), "ak47u")
check("case-insensitive exact match", replay.resolve_config_key(cfg, "AK47U"), "ak47u")
check("suffix after last dot matches", replay.resolve_config_key(cfg, "ak"), "rifle.ak")
# The coverage gap this closes: prefab names use underscores where the config uses dots, and the
# Custom SMG's prefab name shares nothing with its item shortname.
check("underscore name matches dotted key",
      replay.resolve_config_key(cfg, "shotgun_pump"), "shotgun.pump")
check("reversed token order matches", replay.resolve_config_key(cfg, "bolt_rifle"), "rifle.bolt")
check("bow_hunting matches bow.hunting",
      replay.resolve_config_key(cfg, "bow_hunting"), "bow.hunting")
check("alias bridges smg -> smg.2", replay.resolve_config_key(cfg, "smg"), "smg.2")
check("alias bridges semi_auto_rifle -> rifle.semiauto",
      replay.resolve_config_key(cfg, "semi_auto_rifle"), "rifle.semiauto")
check("genuinely unknown weapon stays unresolved",
      replay.resolve_config_key(cfg, "plasma_cannon"), None)
check("token signature is order-insensitive",
      replay.weapon_token_signature("shotgun_pump"), replay.weapon_token_signature("pump.shotgun"))

print("\n-- replay: family fallback --")
check("family: m249 is lmg", replay.classify_family("m249"), "lmg")
check("family: rocket_launcher is explosive",
      replay.classify_family("rocket_launcher"), "explosive")
unresolved = replay.weapon_settings(cfg, "plasma_cannon")
check("no config and no fallback -> never flagged", unresolved.max_accuracy, 1.0)
check("no config and no fallback -> not resolved", unresolved.resolved, False)
fallback = {"Enabled": True, "Families": {
    "lmg": {"MaxAccuracy": 0.85, "SampleCount": 50, "SafeDistance": 30.0},
    "explosive": {"MaxAccuracy": 1.0, "SampleCount": 20, "SafeDistance": 25.0}}}
modded = replay.weapon_settings(cfg, "t3_minigun", fallback)
check("unconfigured weapon picks up its family", modded.max_accuracy, 0.85)
check("family source is recorded", modded.source, "family:lmg")
check("family fallback applies a penalty", modded.applies_penalty, True)
explosive = replay.weapon_settings(cfg, "rocket_launcher_dragon", fallback)
check("explosive family resolves but never penalises", explosive.applies_penalty, False)
check("explosive family still counts as resolved", explosive.resolved, True)
check("disabling the fallback restores the old behaviour",
      replay.weapon_settings(cfg, "t3_minigun", {"Enabled": False, "Families": fallback["Families"]}).resolved,
      False)

print("\n-- logparse: both on-disk formats --")
wrapped = logparse.normalize({"TimestampMs": 5, "PlayerId": 76561190000000000, "WeaponName": "ak47u",
                              "EventType": "hit", "Distance": 12.5, "Hit": True, "PingMs": 40,
                              "DeltaPingMs": 2, "HitArea": "head", "AccuracyInWindow": 0.4})
check("legacy PlayerId parsed", wrapped.player, "76561190000000000")
check("distance parsed", wrapped.distance, 12.5)
hashed = logparse.normalize({"TimestampMs": 5, "PlayerHash": "abc123", "WeaponName": "ak47u",
                             "EventType": "shot", "HitArea": "-1"})
check("PlayerHash preferred", hashed.player, "abc123")
check("HitArea -1 normalised to None", hashed.hit_area, None)
check("non-shot/hit event types kept", logparse.normalize(
    {"TimestampMs": 1, "PlayerId": 1, "EventType": "death"}).kind, "death")
check("unknown event type rejected", logparse.normalize(
    {"TimestampMs": 1, "PlayerId": 1, "EventType": "banana"}), None)

print("\n-- statsutil --")
check("percentile interpolates", su.percentile([0.0, 1.0, 2.0, 3.0], 50.0), 1.5)
check("percentile at 0", su.percentile([1.0, 2.0, 3.0], 0.0), 1.0)
check("percentile at 100", su.percentile([1.0, 2.0, 3.0], 100.0), 3.0)
check("mad of constant series is zero", su.mad([2.0] * 10), 0.0)
check("robust_scale falls back off MAD", su.robust_scale([2.0] * 9 + [3.0]) > 0, True)
table = su.make_percentile_table([float(i) for i in range(101)])
check("percentile rank at median", su.rank_in_table(table, 50.0), 0.5, 0.01)
check("percentile rank below range", su.rank_in_table(table, -5.0), 0.0)
check("percentile rank above range", su.rank_in_table(table, 500.0), 1.0)

print("\n-- replay: distance sanitization --")
engine = replay.ReplayEngine({"ak47u": {"MaxAccuracy": 0.5, "SampleCount": 40, "SafeDistance": 25.0}},
                             max_hit_distance=500.0)
check("plausible distance passes through", engine.sanitize_distance(120.0), 120.0)
check("world-origin artifact discarded", engine.sanitize_distance(1500.0), 0.0)
check("bound of 0 disables the check",
      replay.ReplayEngine({}, max_hit_distance=0.0).sanitize_distance(1500.0), 1500.0)
engine.feed(event(1000, "p", "ak47u", "shot"))
bogus_eval = engine.feed(event(1100, "p", "ak47u", "hit", distance=1500.0))
check("evaluation flags the bad reading", bogus_eval.distance_is_bogus, True)
check("evaluation keeps the raw distance for reporting", bogus_eval.distance, 1500.0)
check("bad reading cannot inflate the weighted score", bogus_eval.weighted_score, 1.0)
check("unclamped score records what it would have been",
      bogus_eval.weighted_score_unclamped, 1500.0 / 25.0)

print("\n-- replay: head streak and aim kinematics --")
win = replay.WeaponWindow()
engine = replay.ReplayEngine({"ak47u": {"MaxAccuracy": 0.5, "SampleCount": 40, "SafeDistance": 25.0}})
for i in range(6):
    engine.feed(event(1000 + i * 200, "hs", "ak47u", "shot"))
    engine.feed(event(1100 + i * 200, "hs", "ak47u", "hit", distance=20.0, area="head"))
state = engine.window("hs", "ak47u")
check("consecutive head hits build a streak", state.head_streak, 6)
engine.feed(event(3000, "hs", "ak47u", "hit", distance=20.0, area="leg"))
check("a labelled body hit breaks the streak", state.head_streak, 0)
engine.feed(event(3200, "hs", "ak47u", "hit", distance=20.0, area=None))
check("an unlabelled hit leaves the streak alone", state.head_streak, 0)

# Aim kinematics are absent from logs written before AimTracking existed.
names = list(replay.FEATURE_NAMES)
features = state.features(25.0)
check("aim features report UNKNOWN without telemetry",
      features[names.index("aim_snap_speed")], replay.UNKNOWN)

snap_event = logparse.Event(4000, "hs", "ak47u", "shot", 0.0, 50, 0, None, 0.0,
                            aim_delta_deg=48.0, snap_deg=42.0, snap_settle_ms=30.0)
engine.feed(snap_event)
features = engine.window("hs", "ak47u").features(25.0)
# 42 degrees traversed, fired 30 ms later -> 1400 deg/s
check("snap speed computed from telemetry",
      features[names.index("aim_snap_speed")], 1400.0, 1.0)
check("settle time carried through", features[names.index("aim_settle_ms")], 30.0)

print("\n-- scoring: unavailable features stay inert --")
rows_missing = [tuple([0.3] + [1.0] * (len(names) - 3) + [replay.UNKNOWN, replay.UNKNOWN])
                for _ in range(60)]
gated = scoring.fit_baselines({"testgun": rows_missing})
check("feature with no data gets no baseline",
      "aim_snap_speed" in gated["testgun"], False)
check("features with data do get one", "accuracy" in gated["testgun"], True)
check("available_features reports only the fitted ones",
      "aim_snap_speed" in scoring.available_features({"baselines": gated}), False)
gated_model = {"baselines": gated, "weights": scoring.DEFAULT_WEIGHTS,
               "decision_percentile": 0.99, "max_suggested_nerf_pct": 50}
check("an UNKNOWN value contributes nothing to the score",
      scoring.score(gated_model, "testgun",
                    tuple([0.3] + [1.0] * (len(names) - 3) + [9999.0, 0.0]))["contributions"]["aim_snap_speed"],
      0.0)
# A model trained by an older build has a shorter vector; it must not silently drop features.
short = scoring.directed_z(gated["testgun"], (0.3, 1.0))
check("short feature vector still yields every feature", len(short), len(names))

print("\n-- calibrate --")
check("family: ak47u_jungle is auto_rifle", calibrate.classify_family("ak47u_jungle"), "auto_rifle")
check("family: bolt_rifle is sniper", calibrate.classify_family("bolt_rifle"), "sniper")
check("calibrate re-exports the plugin's classifier",
      calibrate.classify_family is replay.classify_family, True)

stats = calibrate.WeaponStats("testgun")
for player in range(20):
    for i in range(12):
        stats.add_evaluation(replay.Evaluation(
            ts=i, player="p%d" % player, weapon="testgun", accuracy=0.30 + 0.01 * (i % 5),
            sample_count=40, weighted_score=1.0, weighted_score_unclamped=1.0, max_accuracy=0.35,
            safe_distance=25.0, nerf=1.0, suspicious=False, features=(0,) * len(replay.FEATURE_NAMES), distance=10.0,
            distance_is_bogus=False), replay.MIN_HISTORY_FOR_PENALTY)
rec = calibrate.calibrate_weapon(stats, {"MaxAccuracy": 0.35, "SampleCount": 40,
                                         "SafeDistance": 25.0}, calibrate.DEFAULT_PARAMS)
check("calibration produces a recommendation", rec is not None, True)
check("MaxAccuracy clears the population median",
      rec["recommended"]["MaxAccuracy"] > rec["observed"]["window_accuracy_pct"]["p50"], True)
check("saturated windows stay out of the percentiles",
      rec["observed"]["window_accuracy_pct"]["p99"] < 1.0, True)

saturated = calibrate.WeaponStats("saturated")
for player in range(10):
    for i in range(20):
        saturated.add_evaluation(replay.Evaluation(
            ts=i, player="p%d" % player, weapon="saturated", accuracy=1.0, sample_count=40,
            weighted_score=1.0, weighted_score_unclamped=1.0, max_accuracy=0.35, safe_distance=25.0,
            nerf=0.0, suspicious=True, features=(0,) * len(replay.FEATURE_NAMES), distance=10.0, distance_is_bogus=False),
            replay.MIN_HISTORY_FOR_PENALTY)
check("fully saturated weapon detected", saturated.saturation_share, 1.0)
sat_rec = calibrate.calibrate_weapon(saturated, None, calibrate.DEFAULT_PARAMS)
check("saturated weapon left unpenalised", sat_rec["recommended"]["MaxAccuracy"], 1.0)
check("saturated weapon records a reason", bool(sat_rec["disabled_reason"]), True)
check("saturated weapon still counts its players", sat_rec["observed"]["players"], 10)

print("\n-- scoring --")
NORMAL = (0.30, 1.0, 0.1, 0.1, 0.0, 1.0, 0.5, 0.0, 0.1, replay.UNKNOWN, replay.UNKNOWN)
EXTREME = (0.95, 4.0, 0.9, 0.8, 9.0, 12.0, 0.02, 0.4, 0.9, replay.UNKNOWN, replay.UNKNOWN)
samples = {"testgun": [tuple([0.3 + 0.001 * i] + list(NORMAL[1:])) for i in range(200)]}
baselines = scoring.fit_baselines(samples)
check("weapon baseline fitted", "testgun" in baselines, True)
check("global fallback baseline present", scoring.GLOBAL_BASELINE_KEY in baselines, True)
model = {"baselines": baselines, "weights": scoring.DEFAULT_WEIGHTS, "decision_percentile": 0.99,
         "max_suggested_nerf_pct": 50}
model["score_tables"] = scoring.build_score_tables(model, samples)

typical = scoring.score(model, "testgun", NORMAL)
extreme = scoring.score(model, "testgun", EXTREME)
check("typical sample scores low", typical["ml_confidence"] < 0.6, True)
check("extreme sample scores at the top", extreme["ml_confidence"] >= 0.99, True)
check("extreme sample suggests a nerf", extreme["suggested_nerf_pct"] > 0, True)
check("typical sample suggests nothing", typical["suggested_nerf_pct"], 0)
check("nerf capped at max_suggested_nerf_pct", extreme["suggested_nerf_pct"] <= 50, True)
check("anomaly type reported", extreme["anomaly_type"] is not None, True)
check("short history blocks a nerf",
      scoring.score(model, "testgun", EXTREME, sample_count=5)["suggested_nerf_pct"], 0)
check("unknown weapon falls back to global baseline",
      scoring.score(model, "never_seen", NORMAL)["baseline"],
      "global")
check("explanation mentions a feature",
      "Outlier on" in scoring.explain(extreme, EXTREME), True)

# Low cadence CV means scripted fire, so it has to push the score up, not down.
regular = scoring.score(model, "testgun", tuple(list(NORMAL[:6]) + [0.01] + list(NORMAL[7:])))
check("low cadence variance raises the score",
      regular["contributions"]["cadence_cv"] > typical["contributions"]["cadence_cv"], True)

labeled = ([("testgun", EXTREME, 1) for _ in range(20)]
           + [("testgun", NORMAL, 0) for _ in range(20)])
learned, info = scoring.refit_weights(model, labeled)
check("refit runs with enough labels", info["status"], "refit")
check("refit weights are non-negative", all(v >= 0 for v in learned.values()), True)
check("refit separates the labelled classes", info["train_accuracy"] >= 0.9, True)
auc = scoring.evaluate_ranking(model, [(w, f) for w, f, _y in labeled],
                               [y for _w, _f, y in labeled])
check("ranking AUC on labelled data", auc, 1.0)
_none, thin_info = scoring.refit_weights(model, labeled[:5])
check("refit refuses too few labels", thin_info["status"], "insufficient_labels")

print("\n-- trained model artifact --")
model_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "model.json")
if os.path.exists(model_path):
    with open(model_path, "r", encoding="utf-8") as handle:
        trained = json.load(handle)
    check("model lists every feature", trained["features"], list(replay.FEATURE_NAMES))
    check("model has weapon baselines",
          len([k for k in trained["baselines"] if k != scoring.GLOBAL_BASELINE_KEY]) > 0, True)
    check("model has score tables", scoring.GLOBAL_BASELINE_KEY in trained["score_tables"], True)
    check("model carries a weapons config", len(trained["weapons_config"]) > 0, True)
    check("every configured weapon has all three fields",
          all({"MaxAccuracy", "SampleCount", "SafeDistance"} <= set(v)
              for v in trained["weapons_config"].values()), True)
    check("MaxAccuracy values are in range",
          all(0.0 < v["MaxAccuracy"] <= 1.0 for v in trained["weapons_config"].values()), True)
    check("SampleCount values are sane",
          all(1 <= v["SampleCount"] <= 200 for v in trained["weapons_config"].values()), True)
    scored = scoring.score(trained, "ak47u", EXTREME)
    check("trained model scores an extreme sample high", scored["ml_confidence"] >= 0.9, True)
else:
    print("model.json not found — run train.py first (skipping artifact checks)")

print("\n-- endpoints --")
try:
    import flask  # noqa: F401
except ImportError:
    print("flask not installed — skipping endpoint checks")
else:
    import tempfile

    os.environ["ML_DATA_DIR"] = tempfile.mkdtemp(prefix="mogyac-selftest-")
    os.environ["ML_PERSIST_EVENTS"] = "0"
    import server

    server.load_model()
    client = server.app.test_client()

    check("GET /health", client.get("/health").status_code, 200)
    check("GET /model-info", client.get("/model-info").status_code, 200)
    check("GET /config-recommend", client.get("/config-recommend").status_code, 200)
    check("GET /penalty-suggestion without player_id",
          client.get("/penalty-suggestion").status_code, 400)
    check("POST /ingest rejects a non-array body",
          client.post("/ingest", json={"nope": 1}).status_code, 400)

    # Feed a burst that should push one player to the top of the distribution: every shot hits,
    # all at long range, all headshots.
    events = []
    ts = 1770000000000
    for i in range(60):
        events.append({"TimestampMs": ts + i * 130, "PlayerHash": "selftest_cheater",
                       "WeaponName": "ak47u", "EventType": "shot", "PingMs": 40, "DeltaPingMs": 0})
        events.append({"TimestampMs": ts + i * 130 + 60, "PlayerHash": "selftest_cheater",
                       "WeaponName": "ak47u", "EventType": "hit", "Distance": 120.0,
                       "PingMs": 40, "DeltaPingMs": 0, "HitArea": "head"})
    response = client.post("/ingest", json=events)
    check("POST /ingest accepts a bare array", response.status_code, 200)
    check("POST /ingest scored the events", response.get_json()["scored"] > 0, True)

    suggestion = client.get("/penalty-suggestion?player_id=selftest_cheater").get_json()
    check("suggestion returned for the ingested player", "ak47u" in suggestion["weapons"], True)
    if "ak47u" in suggestion["weapons"]:
        entry = suggestion["weapons"]["ak47u"]
        check("perfect long-range headshot run scores high", entry["ml_confidence"] >= 0.95, True)
        check("and produces a nerf suggestion", entry["suggested_nerf_pct"] > 0, True)
        check("and explains itself", bool(entry["reason"]), True)
        check("and recommends action", entry["recommended_action"], "apply_nerf")

    # An ordinary player mixing hits and misses at close range should stay quiet.
    events = []
    for i in range(80):
        events.append({"TimestampMs": ts + i * 200, "PlayerHash": "selftest_normal",
                       "WeaponName": "ak47u", "EventType": "shot", "PingMs": 60,
                       "DeltaPingMs": 1})
        if i % 4 == 0:
            events.append({"TimestampMs": ts + i * 200 + 90, "PlayerHash": "selftest_normal",
                           "WeaponName": "ak47u", "EventType": "hit", "Distance": 8.0,
                           "PingMs": 60, "DeltaPingMs": 1, "HitArea": "stomach"})
    client.post("/ingest", json=events)
    normal = client.get("/penalty-suggestion?player_id=selftest_normal").get_json()
    if "ak47u" in normal["weapons"]:
        check("ordinary player suggests no nerf",
              normal["weapons"]["ak47u"]["suggested_nerf_pct"], 0)
        check("ordinary player scores below the cheater",
              normal["weapons"]["ak47u"]["ml_confidence"]
              < suggestion["weapons"]["ak47u"]["ml_confidence"], True)

    check("POST /feedback rejects a bad outcome",
          client.post("/feedback", json={"player_id": "x", "outcome": "nope"}).status_code, 400)
    check("POST /feedback records a verdict",
          client.post("/feedback", json={"player_id": "selftest_cheater",
                                         "outcome": "confirmed_cheater"}).status_code, 200)
    check("feedback persisted to disk", os.path.exists(server.FEEDBACK_PATH), True)
    check("POST /reload-model", client.post("/reload-model").status_code, 200)

print("")
if FAILURES:
    print("%d FAILURE(S):" % len(FAILURES))
    for failure in FAILURES:
        print("  - " + failure)
    sys.exit(1)
print("all checks passed")
