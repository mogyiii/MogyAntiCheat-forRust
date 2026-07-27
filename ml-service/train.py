#!/usr/bin/env python3
"""
Offline trainer for the MogyAntiCheat ML service.

Reads the plugin's own event logs, replays them through a faithful copy of the plugin's
window logic, and produces:

  * `model.json`                 — baselines + weights for live scoring in server.py
  * `config-recommendation.json` — calibrated per-weapon MaxAccuracy / SampleCount / SafeDistance
  * `reports/training-report.md` — what changed, why, and what it would do to flag rates

Usage:
    python train.py                             # reads ../logs, writes model.json next to this file
    python train.py --logs /path/to/logs --flag-rate 0.01
    python train.py --config oxide/config/MogyAntiCheat.json   # compare against the live config

Stdlib only — no numpy, no sklearn.
"""

import argparse
import datetime as dt
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mogyac import MODEL_FORMAT_VERSION, calibrate, logparse, scoring
from mogyac import statsutil as su
from mogyac.replay import (FEATURE_NAMES, MIN_HISTORY_FOR_PENALTY, MAX_PLAUSIBLE_DISTANCE,
                           ReplayEngine, resolve_config_key, weapon_settings)

HERE = os.path.dirname(os.path.abspath(__file__))

# The weapon block shipped in MogyAntiCheat.cs, used as the "current" baseline when no live
# server config is supplied. Keep in sync with LoadDefaultConfig().
PLUGIN_DEFAULT_WEAPONS = {
    "rifle.ak": {"MaxAccuracy": 0.38, "SampleCount": 40, "SafeDistance": 25.0},
    "rifle.lr300": {"MaxAccuracy": 0.40, "SampleCount": 40, "SafeDistance": 25.0},
    "rifle.semiauto": {"MaxAccuracy": 0.45, "SampleCount": 30, "SafeDistance": 30.0},
    "rifle.m39": {"MaxAccuracy": 0.50, "SampleCount": 25, "SafeDistance": 40.0},
    "smg.2": {"MaxAccuracy": 0.35, "SampleCount": 40, "SafeDistance": 15.0},
    "smg.thompson": {"MaxAccuracy": 0.35, "SampleCount": 40, "SafeDistance": 18.0},
    "smg.mp5": {"MaxAccuracy": 0.35, "SampleCount": 45, "SafeDistance": 20.0},
    "ak47u": {"MaxAccuracy": 0.35, "SampleCount": 40, "SafeDistance": 15.0},
    "pistol.semiauto": {"MaxAccuracy": 0.40, "SampleCount": 20, "SafeDistance": 15.0},
    "pistol.m92": {"MaxAccuracy": 0.42, "SampleCount": 25, "SafeDistance": 15.0},
    "pistol.revolver": {"MaxAccuracy": 0.38, "SampleCount": 15, "SafeDistance": 12.0},
    "pistol.python": {"MaxAccuracy": 0.45, "SampleCount": 15, "SafeDistance": 20.0},
    "rifle.bolt": {"MaxAccuracy": 0.65, "SampleCount": 12, "SafeDistance": 50.0},
    "rifle.l96": {"MaxAccuracy": 0.70, "SampleCount": 10, "SafeDistance": 70.0},
    "rifle.m249": {"MaxAccuracy": 0.30, "SampleCount": 60, "SafeDistance": 30.0},
    "hmlmg": {"MaxAccuracy": 0.30, "SampleCount": 50, "SafeDistance": 25.0},
    "bow.hunting": {"MaxAccuracy": 0.50, "SampleCount": 15, "SafeDistance": 20.0},
    "bow.compound": {"MaxAccuracy": 0.60, "SampleCount": 10, "SafeDistance": 30.0},
    "crossbow": {"MaxAccuracy": 0.55, "SampleCount": 10, "SafeDistance": 25.0},
    "shotgun.pump": {"MaxAccuracy": 0.70, "SampleCount": 15, "SafeDistance": 10.0},
    "shotgun.spas12": {"MaxAccuracy": 0.70, "SampleCount": 20, "SafeDistance": 10.0},
}

# The plugin's WeaponFallback block: applied to weapons the Weapons block does not name, so that
# an unrecognised prefab is still checked instead of silently exempt.
# Keep in sync with BuildDefaultWeaponFallbackConfig() in MogyAntiCheat.cs.
PLUGIN_DEFAULT_FALLBACK = {
    "Enabled": True,
    "Families": {
        "auto_rifle": {"MaxAccuracy": 0.85, "SampleCount": 40, "SafeDistance": 45.0},
        "smg": {"MaxAccuracy": 0.95, "SampleCount": 40, "SafeDistance": 15.0},
        "lmg": {"MaxAccuracy": 0.85, "SampleCount": 50, "SafeDistance": 30.0},
        "semi_rifle": {"MaxAccuracy": 0.75, "SampleCount": 30, "SafeDistance": 40.0},
        "sniper": {"MaxAccuracy": 0.93, "SampleCount": 15, "SafeDistance": 60.0},
        "shotgun": {"MaxAccuracy": 0.95, "SampleCount": 15, "SafeDistance": 12.0},
        "pistol": {"MaxAccuracy": 0.88, "SampleCount": 20, "SafeDistance": 15.0},
        "projectile": {"MaxAccuracy": 0.90, "SampleCount": 12, "SafeDistance": 25.0},
        "explosive": {"MaxAccuracy": 1.0, "SampleCount": 20, "SafeDistance": 25.0},
    },
}


# ---------------------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------------------
def log(msg):
    print(msg)
    sys.stdout.flush()


class BaselineConfig(object):
    """The plugin config the recommendations are measured against."""

    def __init__(self, weapons, fallback, expiry_seconds, max_hit_distance, source):
        self.weapons = weapons
        self.fallback = fallback
        self.expiry_seconds = expiry_seconds
        self.max_hit_distance = max_hit_distance
        self.source = source


def load_current_config(path):
    """Load the detection-relevant keys from a live plugin config, or fall back to the defaults."""
    if not path:
        return BaselineConfig(dict(PLUGIN_DEFAULT_WEAPONS), PLUGIN_DEFAULT_FALLBACK, 20.0,
                              MAX_PLAUSIBLE_DISTANCE, "plugin defaults (MogyAntiCheat.cs)")
    with open(path, "r", encoding="utf-8-sig") as handle:
        cfg = json.load(handle)
    weapons = cfg.get("Weapons") or {}
    cleaned = {}
    for key, entry in weapons.items():
        if isinstance(entry, dict):
            cleaned[key] = {
                "MaxAccuracy": float(entry.get("MaxAccuracy", 1.0)),
                "SampleCount": int(entry.get("SampleCount", 40)),
                "SafeDistance": float(entry.get("SafeDistance", 1.0)),
            }
    return BaselineConfig(
        weapons=cleaned,
        fallback=cfg.get("WeaponFallback") or PLUGIN_DEFAULT_FALLBACK,
        expiry_seconds=float(cfg.get("MissExpirySeconds", 20.0)),
        max_hit_distance=float(cfg.get("MaxHitDistance", MAX_PLAUSIBLE_DISTANCE)),
        source=os.path.basename(path),
    )


def weapon_totals(events):
    """Per-weapon shot/hit counts and intra-burst shot intervals — independent of any config."""
    totals = {}
    last_shot = {}
    for ev in events:
        if not ev.weapon or ev.kind not in ("shot", "hit"):
            continue
        entry = totals.setdefault(ev.weapon, {"shots": 0, "hits": 0, "intervals": []})
        if ev.kind == "shot":
            entry["shots"] += 1
            key = (ev.player, ev.weapon)
            previous = last_shot.get(key)
            if previous is not None:
                gap = ev.ts - previous
                if 0 < gap <= 1000:
                    entry["intervals"].append(float(gap))
            last_shot[key] = ev.ts
        else:
            entry["hits"] += 1
    return totals


def replay_pass(events, weapons_cfg, expiry_seconds, totals, baseline=None, max_hit_distance=None,
                legacy=False):
    """
    One replay of the whole event stream under `weapons_cfg`.

    Returns (stats_by_weapon, samples_by_weapon, per_player_scores_input, summary).
    `samples_by_weapon` holds feature vectors for evaluations with enough history to act on —
    those are the rows the anomaly baselines are fitted to.
    """
    engine = ReplayEngine(
        weapons_cfg,
        miss_expiry_ms=expiry_seconds * 1000.0,
        fallback_cfg=baseline.fallback if baseline else None,
        max_hit_distance=(max_hit_distance if max_hit_distance is not None
                          else (baseline.max_hit_distance if baseline else MAX_PLAUSIBLE_DISTANCE)),
        legacy=legacy,
    )
    stats = {}
    samples = {}
    per_pair = {}
    total_evals = 0
    flagged = 0
    zero_damage = 0
    penalisable = 0
    bogus = 0

    for ev in events:
        result = engine.feed(ev)
        if result is None:
            continue
        total_evals += 1
        weapon_stats = stats.get(result.weapon)
        if weapon_stats is None:
            weapon_stats = calibrate.WeaponStats(result.weapon)
            totals_entry = totals.get(result.weapon, {})
            weapon_stats.shots = totals_entry.get("shots", 0)
            weapon_stats.hits = totals_entry.get("hits", 0)
            weapon_stats.intervals = totals_entry.get("intervals", [])
            stats[result.weapon] = weapon_stats
        weapon_stats.add_evaluation(result, MIN_HISTORY_FOR_PENALTY)

        if result.distance_is_bogus:
            bogus += 1
        if result.sample_count >= MIN_HISTORY_FOR_PENALTY:
            penalisable += 1
            if result.suspicious:
                flagged += 1
            if result.nerf == 0.0:
                zero_damage += 1
            samples.setdefault(result.weapon, []).append(result.features)
            pair = per_pair.setdefault((result.player, result.weapon), [])
            pair.append(result.features)

    summary = {
        "evaluations": total_evals,
        "penalisable_evaluations": penalisable,
        "flagged": flagged,
        "zero_damage": zero_damage,
        "flag_rate": round(flagged / float(penalisable), 4) if penalisable else 0.0,
        "zero_damage_rate": round(zero_damage / float(penalisable), 4) if penalisable else 0.0,
        "bogus_distance_evaluations": bogus,
    }
    return stats, samples, per_pair, summary


def sweep_miss_expiry(events, weapons_cfg, totals, candidates, params, baseline=None):
    """
    Sweep `MissExpirySeconds` and keep the value that makes the accuracy metric most separable.

    RegisterHit only converts pending shots into recorded misses when they were fired within
    MissExpirySeconds of the hit; anything older is dropped from the window entirely. At the
    shipped 20 s that quietly deletes the misses of anyone firing spaced single shots, so their
    window accuracy drifts upward — the metric is measuring fire rhythm as much as aim.

    Selection maximises the between-player share of accuracy variance (see
    `calibrate.discrimination`), subject to keeping the saturated-window share within budget.
    A metric where one player's own swing is as wide as the gap between players cannot be
    thresholded no matter where the threshold goes. The cost of a longer window is that a hit can
    be matched against a shot from an earlier engagement, so ties are broken toward the shorter
    value.
    """
    rows = []
    for expiry in candidates:
        stats, _samples, _pairs, summary = replay_pass(events, weapons_cfg, expiry, totals,
                                                      baseline)
        saturated = sum(s.saturated for s in stats.values())
        penalisable = sum(s.evaluated for s in stats.values())
        accuracies = []
        weighted_icc = 0.0
        icc_weight = 0.0
        for weapon_stats in stats.values():
            accuracies.extend(weapon_stats.accuracies)
            icc = calibrate.discrimination(weapon_stats, params["min_player_evals"])
            if icc is not None:
                weighted_icc += icc * weapon_stats.evaluated
                icc_weight += weapon_stats.evaluated
        rows.append({
            "expiry_seconds": expiry,
            "penalisable_evaluations": penalisable,
            "saturation_share": (saturated / float(penalisable)) if penalisable else 0.0,
            "median_accuracy": su.median(accuracies) if accuracies else 0.0,
            "discrimination": (weighted_icc / icc_weight) if icc_weight else 0.0,
            "flag_rate": summary["flag_rate"],
            "zero_damage_rate": summary["zero_damage_rate"],
        })
        log("  expiry %5.0fs: separation %.3f  saturation %5.2f%%  median accuracy %.3f  "
            "flag rate %5.2f%%" % (
                expiry, rows[-1]["discrimination"], 100 * rows[-1]["saturation_share"],
                rows[-1]["median_accuracy"], 100 * summary["flag_rate"]))

    eligible = [r for r in rows if r["saturation_share"] <= params["max_saturation_share"]] or rows
    best = max(r["discrimination"] for r in eligible)
    # Within 1% of the best separation, prefer the shortest window.
    chosen = min((r for r in eligible if r["discrimination"] >= best * 0.99),
                 key=lambda r: r["expiry_seconds"])
    return chosen["expiry_seconds"], rows


def merge_recommendations(current_cfg, recommendations, fallbacks, observed_weapons):
    """
    Build the recommended Weapons block.

    Existing keys are updated in place (so `rifle.ak` stays `rifle.ak`), weapons that only exist
    in the logs are added under their logged short prefab name, and weapons with too little data
    inherit their family's averaged settings. Untouched keys are preserved.
    """
    merged = {key: dict(entry) for key, entry in current_cfg.items()}
    provenance = {}

    for weapon in sorted(observed_weapons):
        rec = recommendations.get(weapon)
        source = "calibrated"
        if rec is not None:
            values = dict(rec["recommended"])
        else:
            family = calibrate.classify_family(weapon)
            if family in calibrate.DISABLED_FAMILIES:
                values = {"MaxAccuracy": 1.0, "SampleCount": 20, "SafeDistance": 25.0}
                source = "family_disabled"
            elif family in fallbacks:
                values = {k: v for k, v in fallbacks[family].items() if k != "derived_from"}
                source = "family_fallback:" + family
            else:
                continue
        key = resolve_config_key(merged, weapon) or weapon
        merged[key] = values
        provenance[key] = {"weapon": weapon, "source": source}

    return merged, provenance


def flat_recommendation_map(current_cfg, merged, provenance, recommendations):
    """
    `recommendations` payload for GET /config-recommend, in the flat
    `Weapons.<weapon>.<Field>` shape the plugin's /ac-suggest already renders.
    Only fields that actually changed are included.
    """
    flat = {}
    for key, values in merged.items():
        current = current_cfg.get(key)
        weapon = provenance.get(key, {}).get("weapon", key)
        rec = recommendations.get(weapon)
        confidence = rec["confidence"] if rec else 0.3
        for field in ("MaxAccuracy", "SampleCount", "SafeDistance"):
            new_value = values[field]
            old_value = current.get(field) if current else None
            if old_value is not None and abs(float(new_value) - float(old_value)) < 1e-9:
                continue
            flat["Weapons.%s.%s" % (key, field)] = {
                "current": old_value if old_value is not None else "unset",
                "recommended": new_value,
                "delta": (round(float(new_value) - float(old_value), 3)
                          if old_value is not None else None),
                "confidence": confidence,
                "source": provenance.get(key, {}).get("source", "unchanged"),
            }
    return flat


def top_suspects(model, per_pair, limit=25, min_evaluations=15):
    """Rank (player, weapon) pairs by mean anomaly confidence — the manual-review queue."""
    ranked = []
    for (player, weapon), rows in per_pair.items():
        if len(rows) < min_evaluations:
            continue
        confidences = []
        types = {}
        for features in rows:
            result = scoring.score(model, weapon, features)
            confidences.append(result["ml_confidence"])
            if result["anomaly_type"]:
                types[result["anomaly_type"]] = types.get(result["anomaly_type"], 0) + 1
        confidences.sort()
        dominant = max(types.items(), key=lambda kv: kv[1])[0] if types else None
        ranked.append({
            "player": player,
            "weapon": weapon,
            "evaluations": len(rows),
            "mean_confidence": round(su.mean(confidences), 3),
            "p90_confidence": round(su.percentile(confidences, 90.0), 3),
            "dominant_anomaly": dominant,
        })
    ranked.sort(key=lambda r: (-r["mean_confidence"], -r["evaluations"]))
    return ranked[:limit]


def load_feedback(path):
    """Read admin verdicts written by POST /feedback (one JSON object per line)."""
    if not path or not os.path.exists(path):
        return []
    records = []
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except ValueError:
                continue
    return records


def build_labeled_set(feedback, per_pair):
    """
    Join admin verdicts onto replayed feature rows by player id.

    A verdict is about a player, so every evaluation row for that player inherits the label.
    `uncertain` verdicts are dropped — they carry no gradient.
    """
    verdicts = {}
    for record in feedback:
        outcome = record.get("outcome")
        if outcome == "confirmed_cheater":
            verdicts[str(record.get("player_id"))] = 1
        elif outcome == "false_positive":
            verdicts[str(record.get("player_id"))] = 0
    labeled = []
    for (player, weapon), rows in per_pair.items():
        label = verdicts.get(str(player))
        if label is None:
            continue
        for features in rows:
            labeled.append((weapon, features, label))
    return labeled


# ---------------------------------------------------------------------------------------
# report
# ---------------------------------------------------------------------------------------
def write_report(path, ctx):
    lines = []
    add = lines.append
    add("# MogyAntiCheat ML training report")
    add("")
    add("Generated: %s" % ctx["generated_at"])
    add("")
    add("## Training data")
    add("")
    add("| | |")
    add("|---|---|")
    add("| Log files | %d |" % ctx["file_count"])
    add("| Events | %d |" % ctx["event_count"])
    add("| Shots / hits | %d / %d |" % (ctx["shots"], ctx["hits"]))
    add("| Distinct players | %d |" % ctx["players"])
    add("| Distinct weapons | %d |" % ctx["weapons"])
    add("| Time span | %s -> %s |" % (ctx["first_event"], ctx["last_event"]))
    add("| Baseline config | %s |" % ctx["config_source"])
    add("| Target flag rate | %.1f%% of players per weapon |" % (100 * ctx["params"]["flag_rate"]))
    add("| MissExpirySeconds | %.0f -> %.0f |" % (ctx["current_expiry"], ctx["tuned_expiry"]))
    add("")

    if ctx["expiry_rows"]:
        add("## MissExpirySeconds and the saturated accuracy metric")
        add("")
        add("`RegisterHit` only turns a pending shot into a recorded miss if it was fired within")
        add("`MissExpirySeconds` of the hit. Older pending shots are dropped from the window without")
        add("ever counting as misses, so a player who fires slowly accumulates a history of almost")
        add("nothing but hits — the metric partly measures fire rhythm rather than aim.")
        add("")
        add("`separation` is the share of accuracy variance that sits *between* players rather than")
        add("within one player's own session, and it is the number that decides whether any threshold")
        add("can work: at low separation a player's own swing is as wide as the gap between players.")
        add("`saturation` is the share of actionable windows (History >= 10) reading exactly 100%,")
        add("which no MaxAccuracy below 1.0 can discriminate.")
        add("")
        add("| MissExpirySeconds | Separation | Saturated | Median accuracy | Flag rate | Windows |")
        add("|---|---|---|---|---|---|")
        for row in ctx["expiry_rows"]:
            marker = " **<- chosen**" if row["expiry_seconds"] == ctx["tuned_expiry"] else ""
            add("| %.0f s%s | %.3f | %.2f%% | %.3f | %.2f%% | %d |" % (
                row["expiry_seconds"], marker, row["discrimination"],
                100 * row["saturation_share"], row["median_accuracy"], 100 * row["flag_rate"],
                row["penalisable_evaluations"]))
        add("")
        add("Longer is not free: a hit can then be matched against a shot from an earlier")
        add("engagement, folding those misses into the current window. Ties within 1% of the best")
        add("separation therefore go to the shorter window, and saturation is capped at %.0f%%."
            % (100 * ctx["params"]["max_saturation_share"]))
        add("")

    add("## Effect on the plugin's behaviour")
    add("")
    add("Measured by replaying the same events through the plugin's own window logic under each")
    add("configuration. `zero-damage` is the harsh outcome: the hit lands but deals nothing.")
    add("")
    add("Read the middle row carefully. Closing the weapon-coverage gap applies the *existing*")
    add("thresholds to a third more shots, so on its own it makes false positives worse, not better.")
    add("Coverage and calibration only pay off together.")
    add("")
    add("| Config | Evaluations | Flagged | Zero-damage |")
    add("|---|---|---|---|")
    for label, summary in ctx["behaviour_rows"]:
        add("| %s | %d | %d (%.2f%%) | %d (%.2f%%) |" % (
            label, summary["penalisable_evaluations"],
            summary["flagged"], 100 * summary["flag_rate"],
            summary["zero_damage"], 100 * summary["zero_damage_rate"]))
    add("")

    if ctx["holdout"]:
        add("### Holdout check")
        add("")
        add("Thresholds calibrated on the first %d%% of the timeline, then measured on the unseen"
            % ctx["holdout"]["train_share"])
        add("remainder. Close numbers between the two mean the thresholds are not fitted to one week's")
        add("player mix.")
        add("")
        add("| Slice | Config | Flag rate | Zero-damage rate |")
        add("|---|---|---|---|")
        for row in ctx["holdout"]["rows"]:
            add("| %s | %s | %.2f%% | %.2f%% |" % (
                row["slice"], row["config"], 100 * row["flag_rate"], 100 * row["zero_damage_rate"]))
        add("")

    add("## Weapon coverage")
    add("")
    if ctx["uncovered"]:
        add("Weapons seen in the logs that the `Weapons` config block does not name — **%.1f%% of all"
            % (100 * ctx["uncovered_share"]))
        add("shots**. `Settings from` shows what the plugin falls back to. A `family:` fallback is a")
        add("guessed threshold, better than no checking but weaker than a calibrated entry; the")
        add("recommended config below adds real entries for the ones with enough data.")
        add("")
        add("| Weapon | Shots | Share of all shots | Family | Settings from |")
        add("|---|---|---|---|---|")
        for row in ctx["uncovered"]:
            add("| `%s` | %d | %.1f%% | %s | %s |" % (row["weapon"], row["shots"],
                                                      100 * row["share"], row["family"],
                                                      row["source"]))
        add("")
        if ctx["unchecked"]:
            add("Of those, **%.1f%% of all shots** are still never checked — nothing names them and no"
                % (100 * ctx["unchecked_share"]))
            add("family fallback recognises them either. Add them to the `Weapons` block by hand:")
            add("")
            for row in ctx["unchecked"]:
                add("- `%s` — %d shots, classified `%s`" % (row["weapon"], row["shots"],
                                                            row["family"]))
            add("")
        else:
            add("Nothing is left unchecked: every weapon in the logs resolves to either its own")
            add("config entry or a family fallback. Weapons in the `explosive` family resolve to")
            add("`MaxAccuracy = 1.0` deliberately — hit ratio carries no signal there.")
            add("")
    else:
        add("Every weapon seen in the logs has its own config entry.")
    add("")

    if ctx["bogus"]["share"] > 0:
        add("## Implausible hit distances")
        add("")
        add("%.2f%% of hits report a distance above the `MaxHitDistance` bound of %.0f m (max seen:" % (
            100 * ctx["bogus"]["share"], ctx["max_hit_distance"]))
        add("%.0f m). `Vector3.Distance(info.HitPositionWorld, info.PointStart)` degenerates to a"
            % ctx["bogus"]["max"])
        add("world-origin distance when `PointStart` is unset. The weighted score is *squared* in the")
        add("penalty term, so one such reading is enough to null a player's damage.")
        add("")
        add("The plugin now discards these distances (the hit still counts, only the distance is")
        add("dropped). The last two columns are the size of what that removes: the weighted score p95")
        add("with the bound applied, versus what it was without one.")
        add("")
        add("| Weapon | Rejected hit share | Weighted score p95 (clamped) | p95 (unclamped) |")
        add("|---|---|---|---|")
        for row in ctx["bogus"]["rows"]:
            add("| `%s` | %.1f%% | %.1f | %.1f |" % (row["weapon"], 100 * row["share"],
                                                     row["ws_p95"], row["ws_p95_unclamped"]))
        add("")

    add("## Calibrated thresholds")
    add("")
    add("`acc p50/p90` are the plugin's own window accuracy for that weapon across all players —")
    add("the scale MaxAccuracy is compared against, saturated windows excluded. A current threshold")
    add("at or below `acc p50` means over half of all actionable windows were being flagged. `sat` is")
    add("the saturated share; `flag rate` is measured by replaying the same events under each config.")
    add("")
    add("| Weapon | Evals | Players | acc p50 | acc p90 | sat | MaxAccuracy | SampleCount | SafeDistance | Flag rate | Conf |")
    add("|---|---|---|---|---|---|---|---|---|---|---|")
    for rec in ctx["recommendation_rows"]:
        obs = rec["observed"]
        cur = rec["current"] or {}
        add("| `%s` | %d | %d | %.2f | %.2f | %.0f%% | %s -> **%.2f** | %s -> %d | %s -> %.0f | %.1f%% -> %.1f%% | %.2f |" % (
            rec["weapon"], obs["penalisable_evaluations"], obs["players"],
            obs["window_accuracy_pct"]["p50"], obs["window_accuracy_pct"]["p90"],
            100 * obs["saturation_share"],
            ("%.2f" % cur["MaxAccuracy"]) if cur else "unset", rec["recommended"]["MaxAccuracy"],
            str(cur.get("SampleCount", "unset")), rec["recommended"]["SampleCount"],
            ("%.0f" % cur["SafeDistance"]) if cur else "unset", rec["recommended"]["SafeDistance"],
            100 * rec["current_behaviour"]["flag_rate"],
            100 * rec.get("calibrated_behaviour", rec["projected_behaviour"]).get(
                "flag_rate", rec["projected_behaviour"]["event_flag_rate"]),
            rec["confidence"]))
    add("")

    disabled = [r for r in ctx["recommendation_rows"] if r["disabled_reason"]]
    if disabled:
        add("### Left unpenalised (MaxAccuracy = 1.0)")
        add("")
        add("The trainer refuses to threshold these rather than emit a number that would mostly")
        add("catch ordinary players.")
        add("")
        for rec in disabled:
            add("- `%s` — %s" % (rec["weapon"], rec["disabled_reason"]))
        add("")

    if ctx["fallbacks"]:
        add("### Family fallbacks")
        add("")
        add("Weapons with too little data for their own calibration inherit their family average.")
        add("")
        add("| Family | MaxAccuracy | SampleCount | SafeDistance | Derived from |")
        add("|---|---|---|---|---|")
        for family, values in sorted(ctx["fallbacks"].items()):
            add("| %s | %.2f | %d | %.0f | %s |" % (
                family, values["MaxAccuracy"], values["SampleCount"], values["SafeDistance"],
                ", ".join("`%s`" % w for w in values["derived_from"][:6])))
        add("")

    add("## Anomaly scorer")
    add("")
    add("Unsupervised: each feature becomes a robust z-score (median / MAD) against the population")
    add("that used the same weapon, and `ml_confidence` is the percentile rank of the weighted sum")
    add("against that same population. There are no cheater labels in the logs, so the model reports")
    add("*how unusual* a player is, not a verdict.")
    add("")
    add("| Feature | Weight | Suspicious when | Status |")
    add("|---|---|---|---|")
    available = set(ctx["available_features"])
    for name in FEATURE_NAMES:
        direction = "high" if ctx["model"]["feature_direction"][name] > 0 else "low"
        status = "fitted" if name in available else "**no data — inert**"
        add("| `%s` | %.2f | %s | %s |" % (name, ctx["model"]["weights"][name], direction, status))
    add("")
    inert = [n for n in FEATURE_NAMES if n not in available]
    if inert:
        add("An inert feature has no baseline, so it scores zero and cannot affect a verdict. These")
        add("need telemetry these logs do not contain (`AimTracking` in the plugin config); they")
        add("start contributing on their own once logs written with it are trained on.")
        add("")
    add("Per-weapon baselines fitted: %d (plus a global fallback). Decision percentile: %.3f." % (
        ctx["baseline_count"], ctx["model"]["decision_percentile"]))
    add("")
    if ctx["refit_info"]:
        add("Supervised refit: %s (%d confirmed / %d false positive labels)." % (
            ctx["refit_info"]["status"], ctx["refit_info"]["positives"], ctx["refit_info"]["negatives"]))
        if ctx["refit_info"].get("train_accuracy") is not None:
            add("Training accuracy %.3f%s." % (
                ctx["refit_info"]["train_accuracy"],
                ", AUC %.3f" % ctx["auc"] if ctx["auc"] is not None else ""))
        add("")

    add("## Review queue")
    add("")
    add("Highest-scoring (player, weapon) pairs. These are candidates for a human look, not")
    add("conclusions — feeding verdicts back via `POST /feedback` and re-running the trainer with")
    add("`--feedback` replaces the hand-set feature weights with learned ones.")
    add("")
    add("| # | Player | Weapon | Evals | Mean conf | p90 conf | Dominant anomaly |")
    add("|---|---|---|---|---|---|---|")
    for i, row in enumerate(ctx["suspects"], 1):
        add("| %d | `%s` | `%s` | %d | %.3f | %.3f | %s |" % (
            i, row["player"], row["weapon"], row["evaluations"],
            row["mean_confidence"], row["p90_confidence"], row["dominant_anomaly"] or "-"))
    add("")

    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")


# ---------------------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------------------
def main(argv=None):
    parser = argparse.ArgumentParser(description="Train the MogyAntiCheat ML model from event logs.")
    parser.add_argument("--logs", nargs="+", default=[os.path.join(HERE, os.pardir, "logs")],
                        help="log files or directories to read (default: ../logs)")
    parser.add_argument("--config", default=None,
                        help="live plugin config JSON to compare against (default: plugin defaults)")
    parser.add_argument("--out", default=os.path.join(HERE, "model.json"))
    parser.add_argument("--config-out", default=os.path.join(HERE, "config-recommendation.json"))
    parser.add_argument("--report", default=os.path.join(HERE, "reports", "training-report.md"))
    parser.add_argument("--feedback", default=os.path.join(HERE, "data", "feedback.jsonl"),
                        help="admin verdicts from POST /feedback, used for supervised weight refit")
    parser.add_argument("--flag-rate", type=float, default=calibrate.DEFAULT_PARAMS["flag_rate"],
                        help="share of players per weapon the thresholds should single out")
    parser.add_argument("--min-margin", type=float, default=calibrate.DEFAULT_PARAMS["min_margin"],
                        help="required headroom between the population median and MaxAccuracy")
    parser.add_argument("--iterations", type=int, default=3,
                        help="replay/calibrate rounds (SampleCount changes the accuracy it is fitted to)")
    parser.add_argument("--decision-percentile", type=float, default=0.99,
                        help="anomaly percentile above which the service suggests a nerf")
    parser.add_argument("--holdout", type=float, default=0.7,
                        help="fraction of the timeline used for calibration in the holdout check (0 = skip)")
    parser.add_argument("--no-tune-expiry", dest="tune_expiry", action="store_false", default=True,
                        help="keep MissExpirySeconds as configured instead of sweeping it")
    parser.add_argument("--max-saturation", type=float,
                        default=calibrate.DEFAULT_PARAMS["max_saturation_share"],
                        help="target share of windows reading 100%% accuracy (drives the expiry sweep)")
    args = parser.parse_args(argv)

    log("Reading logs...")
    files, events = logparse.load_events(args.logs, progress=lambda m: log("  " + m))
    if not events:
        log("No usable events found in: %s" % ", ".join(args.logs))
        return 1
    log("  %d files, %d events" % (len(files), len(events)))

    baseline = load_current_config(args.config)
    current_cfg = baseline.weapons
    expiry_seconds = baseline.expiry_seconds
    config_source = baseline.source
    params = dict(calibrate.DEFAULT_PARAMS)
    params["flag_rate"] = args.flag_rate
    params["min_margin"] = args.min_margin
    params["max_saturation_share"] = args.max_saturation

    totals = weapon_totals(events)

    log("Replaying under the current config...")
    base_stats, _base_samples, _base_pairs, base_summary = replay_pass(
        events, current_cfg, expiry_seconds, totals, baseline)
    log("  %d evaluations, flag rate %.2f%%, zero-damage rate %.2f%%" % (
        base_summary["penalisable_evaluations"], 100 * base_summary["flag_rate"],
        100 * base_summary["zero_damage_rate"]))

    # Same thresholds, but with strict weapon-key matching, no family fallback and no distance
    # bound — the plugin as it behaved before those fixes. Isolates what the fixes did on their own,
    # which matters because closing the coverage gap applies the *existing* thresholds to a third
    # more shots: a win only once those thresholds are calibrated too.
    log("Replaying under the pre-fix plugin behaviour...")
    _lst, _lsa, _lpp, legacy_summary = replay_pass(
        events, current_cfg, expiry_seconds, totals, baseline, legacy=True)
    log("  %d evaluations, flag rate %.2f%%, zero-damage rate %.2f%%" % (
        legacy_summary["penalisable_evaluations"], 100 * legacy_summary["flag_rate"],
        100 * legacy_summary["zero_damage_rate"]))

    # MissExpirySeconds decides how much of the accuracy metric is real, so it has to be settled
    # before any threshold is fitted to that metric.
    expiry_rows = []
    tuned_expiry = expiry_seconds
    if args.tune_expiry:
        log("Sweeping MissExpirySeconds (current: %.0fs)..." % expiry_seconds)
        tuned_expiry, expiry_rows = sweep_miss_expiry(
            events, current_cfg, totals, calibrate.EXPIRY_CANDIDATES, params, baseline)
        log("  chosen: %.0fs" % tuned_expiry)
        if tuned_expiry != expiry_seconds:
            stats_at_tuned, _s, _p, tuned_summary = replay_pass(
                events, current_cfg, tuned_expiry, totals, baseline)
            base_stats = stats_at_tuned
            log("  current thresholds at %.0fs expiry: flag rate %.2f%% (was %.2f%% at %.0fs)" % (
                tuned_expiry, 100 * tuned_summary["flag_rate"], 100 * base_summary["flag_rate"],
                expiry_seconds))
    replay_expiry = tuned_expiry

    # Fixed-point loop: recommended SampleCount changes the window the accuracy is measured over,
    # so calibrate, replay under the result, and calibrate again.
    working_cfg = current_cfg
    stats = base_stats
    recommendations = {}
    fallbacks = {}
    merged = current_cfg
    provenance = {}
    observed_weapons = sorted(totals.keys())
    final_samples = {}
    final_pairs = {}
    final_summary = base_summary

    for iteration in range(1, max(1, args.iterations) + 1):
        recommendations = {}
        for weapon, weapon_stats in stats.items():
            key = resolve_config_key(current_cfg, weapon)
            rec = calibrate.calibrate_weapon(weapon_stats, current_cfg.get(key) if key else None, params)
            if rec is not None:
                recommendations[weapon] = rec
        fallbacks = calibrate.family_fallbacks(recommendations)
        merged, provenance = merge_recommendations(current_cfg, recommendations, fallbacks,
                                                   observed_weapons)
        working_cfg = merged
        stats, final_samples, final_pairs, final_summary = replay_pass(
            events, working_cfg, replay_expiry, totals, baseline)
        log("Iteration %d: flag rate %.2f%%, zero-damage rate %.2f%% (%d weapons calibrated)" % (
            iteration, 100 * final_summary["flag_rate"], 100 * final_summary["zero_damage_rate"],
            len(recommendations)))

    # Recompute recommendations once more against the settled statistics so the reported
    # observed/projected numbers describe the config actually being emitted. `current_behaviour`
    # has to come from the very first replay instead — the settled stats were produced *under*
    # the calibrated config, so reading it from there would compare the new config to itself.
    final_recommendations = {}
    for weapon, weapon_stats in stats.items():
        key = resolve_config_key(current_cfg, weapon)
        rec = calibrate.calibrate_weapon(weapon_stats, current_cfg.get(key) if key else None, params)
        if rec is not None:
            rec["recommended"] = merged.get(resolve_config_key(merged, weapon) or weapon,
                                            rec["recommended"])
            baseline_stats = base_stats.get(weapon)
            if baseline_stats is not None:
                rec["current_behaviour"] = {
                    "flag_rate": round(baseline_stats.flag_rate, 4),
                    "zero_damage_rate": round(baseline_stats.zero_damage_rate, 4),
                }
            rec["calibrated_behaviour"] = {
                "flag_rate": round(weapon_stats.flag_rate, 4),
                "zero_damage_rate": round(weapon_stats.zero_damage_rate, 4),
            }
            final_recommendations[weapon] = rec

    log("Fitting anomaly baselines...")
    baselines = scoring.fit_baselines(final_samples)
    model = {
        "model_format_version": MODEL_FORMAT_VERSION,
        "trained_at": dt.datetime.utcnow().replace(microsecond=0).isoformat() + "Z",
        "trained_on": {
            "files": len(files),
            "events": len(events),
            "evaluations": final_summary["penalisable_evaluations"],
            "players": len({ev.player for ev in events}),
            "weapons": len(observed_weapons),
            "first_event_ms": events[0].ts,
            "last_event_ms": events[-1].ts,
        },
        "features": list(FEATURE_NAMES),
        "feature_direction": {name: 1 if scoring.FEATURE_DIRECTION[name] > 0 else -1
                              for name in FEATURE_NAMES},
        "weights": dict(scoring.DEFAULT_WEIGHTS),
        "baselines": baselines,
        "decision_percentile": args.decision_percentile,
        "max_suggested_nerf_pct": 50,
        "miss_expiry_seconds": replay_expiry,
        "max_hit_distance": baseline.max_hit_distance,
        "weapons_config": merged,
        "weapon_fallback": baseline.fallback,
        "label_source": "unsupervised",
    }

    feedback = load_feedback(args.feedback)
    labeled = build_labeled_set(feedback, final_pairs)
    refit_info = None
    auc = None
    if labeled:
        learned, refit_info = scoring.refit_weights(model, labeled)
        if learned:
            model["weights"] = learned
            model["label_source"] = "feedback_refit"
            log("  refit weights from %d labelled rows (%s)" % (len(labeled), refit_info["status"]))
        else:
            log("  feedback present but not usable yet (%s)" % refit_info["status"])
        auc = scoring.evaluate_ranking(model,
                                       [(w, f) for w, f, _y in labeled],
                                       [y for _w, _f, y in labeled])
    model["refit"] = refit_info

    model["score_tables"] = scoring.build_score_tables(model, final_samples)

    # ---- holdout check ----------------------------------------------------------------
    holdout = None
    if 0.0 < args.holdout < 1.0:
        log("Running holdout check...")
        split_index = int(len(events) * args.holdout)
        train_events, test_events = events[:split_index], events[split_index:]
        if train_events and test_events:
            train_totals = weapon_totals(train_events)
            train_stats, _s, _p, _sum = replay_pass(train_events, current_cfg, replay_expiry,
                                                    train_totals, baseline)
            train_recs = {}
            for weapon, weapon_stats in train_stats.items():
                key = resolve_config_key(current_cfg, weapon)
                rec = calibrate.calibrate_weapon(weapon_stats,
                                                 current_cfg.get(key) if key else None, params)
                if rec is not None:
                    train_recs[weapon] = rec
            train_cfg, _prov = merge_recommendations(current_cfg, train_recs,
                                                     calibrate.family_fallbacks(train_recs),
                                                     sorted(train_totals.keys()))
            test_totals = weapon_totals(test_events)
            # "current" keeps the shipped expiry too — it is the behaviour actually in production.
            _st, _sa, _pp, test_current = replay_pass(test_events, current_cfg, expiry_seconds,
                                                      test_totals, baseline)
            _st, _sa, _pp, test_trained = replay_pass(test_events, train_cfg, replay_expiry,
                                                      test_totals, baseline)
            _st, _sa, _pp, train_trained = replay_pass(train_events, train_cfg, replay_expiry,
                                                       train_totals, baseline)
            holdout = {
                "train_share": int(100 * args.holdout),
                "rows": [
                    {"slice": "train", "config": "calibrated on train", **train_trained},
                    {"slice": "holdout", "config": "current", **test_current},
                    {"slice": "holdout", "config": "calibrated on train", **test_trained},
                ],
            }
            log("  holdout flag rate: current %.2f%% -> calibrated %.2f%%" % (
                100 * test_current["flag_rate"], 100 * test_trained["flag_rate"]))

    # ---- outputs ----------------------------------------------------------------------
    flat = flat_recommendation_map(current_cfg, merged, provenance, final_recommendations)
    if abs(replay_expiry - expiry_seconds) > 1e-9:
        flat["MissExpirySeconds"] = {
            "current": expiry_seconds,
            "recommended": replay_expiry,
            "delta": round(replay_expiry - expiry_seconds, 1),
            "confidence": 0.9,
            "source": "saturation_sweep",
        }
    config_payload = {
        "trained_at": model["trained_at"],
        "trained_on_samples": final_summary["penalisable_evaluations"],
        "baseline_config": config_source,
        "params": params,
        "MissExpirySeconds": replay_expiry,
        "MaxHitDistance": baseline.max_hit_distance,
        "expiry_sweep": expiry_rows,
        "Weapons": merged,
        "recommendations": flat,
        "provenance": provenance,
        "family_fallbacks": fallbacks,
        "weapon_details": {w: rec for w, rec in sorted(final_recommendations.items())},
    }
    model["config_recommendation"] = {
        "trained_on_samples": final_summary["penalisable_evaluations"],
        "recommendations": flat,
        "behaviour": {
            "legacy": legacy_summary,
            "current": base_summary,
            "calibrated": final_summary,
        },
    }

    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(model, handle, indent=1, sort_keys=True)
    with open(args.config_out, "w", encoding="utf-8") as handle:
        json.dump(config_payload, handle, indent=2, sort_keys=True)

    total_shots = sum(t["shots"] for t in totals.values())
    # Two distinct gaps: weapons the Weapons block does not name (now caught by the family
    # fallback, at a guessed threshold) and weapons nothing covers at all (still never checked).
    unnamed = []
    unchecked = []
    for weapon in observed_weapons:
        if resolve_config_key(current_cfg, weapon) is not None:
            continue
        shots = totals[weapon]["shots"]
        tuning = weapon_settings(current_cfg, weapon, baseline.fallback)
        row = {"weapon": weapon, "shots": shots,
               "share": shots / float(total_shots) if total_shots else 0.0,
               "family": calibrate.classify_family(weapon),
               "source": tuning.source}
        unnamed.append(row)
        # Not resolved at all — as opposed to resolved-but-exempt, which is what the explosive
        # family is by design.
        if not tuning.resolved:
            unchecked.append(row)
    unnamed.sort(key=lambda r: -r["shots"])
    unchecked.sort(key=lambda r: -r["shots"])

    bogus_rows = []
    bogus_hits = 0
    all_hits = 0
    max_distance = 0.0
    for weapon, weapon_stats in stats.items():
        bogus_hits += weapon_stats.bogus_distances
        all_hits += weapon_stats.bogus_distances + len(weapon_stats.distances)
        if weapon_stats.distances:
            max_distance = max(max_distance, max(weapon_stats.distances))
        if weapon_stats.bogus_distances and weapon_stats.bogus_distance_share > 0.005:
            bogus_rows.append({
                "weapon": weapon,
                "share": weapon_stats.bogus_distance_share,
                "ws_p95": su.percentile(sorted(weapon_stats.weighted_scores), 95.0),
                "ws_p95_unclamped": su.percentile(sorted(weapon_stats.weighted_scores_unclamped), 95.0),
            })
    bogus_rows.sort(key=lambda r: -r["share"])
    observed_max_distance = max([ev.distance for ev in events if ev.kind == "hit"] or [0.0])

    suspects = top_suspects(model, final_pairs)

    report_dir = os.path.dirname(args.report)
    if report_dir and not os.path.isdir(report_dir):
        os.makedirs(report_dir)

    write_report(args.report, {
        "generated_at": model["trained_at"],
        "file_count": len(files),
        "event_count": len(events),
        "shots": sum(t["shots"] for t in totals.values()),
        "hits": sum(t["hits"] for t in totals.values()),
        "players": model["trained_on"]["players"],
        "weapons": len(observed_weapons),
        "first_event": dt.datetime.utcfromtimestamp(events[0].ts / 1000.0).strftime("%Y-%m-%d"),
        "last_event": dt.datetime.utcfromtimestamp(events[-1].ts / 1000.0).strftime("%Y-%m-%d"),
        "config_source": config_source,
        "params": params,
        "current_expiry": expiry_seconds,
        "max_hit_distance": baseline.max_hit_distance,
        "tuned_expiry": replay_expiry,
        "expiry_rows": expiry_rows,
        "behaviour_rows": [
            ("before the coverage + distance fixes", legacy_summary),
            ("after those fixes, thresholds unchanged (%s)" % config_source, base_summary),
            ("after those fixes, thresholds calibrated", final_summary),
        ],
        "holdout": holdout,
        "uncovered": unnamed,
        "uncovered_share": sum(r["share"] for r in unnamed),
        "unchecked": unchecked,
        "unchecked_share": sum(r["share"] for r in unchecked),
        "bogus": {
            "share": (bogus_hits / float(all_hits)) if all_hits else 0.0,
            "max": observed_max_distance,
            "rows": bogus_rows[:12],
        },
        "recommendation_rows": sorted(final_recommendations.values(),
                                      key=lambda r: -r["observed"]["penalisable_evaluations"]),
        "fallbacks": fallbacks,
        "model": model,
        "baseline_count": len([k for k in baselines if k != scoring.GLOBAL_BASELINE_KEY]),
        "available_features": scoring.available_features(model),
        "refit_info": refit_info,
        "auc": auc,
        "suspects": suspects,
    })

    log("")
    log("Wrote:")
    log("  %s" % args.out)
    log("  %s" % args.config_out)
    log("  %s" % args.report)
    log("")
    log("Flag rate: %.2f%% -> %.2f%%   zero-damage: %.2f%% -> %.2f%%" % (
        100 * base_summary["flag_rate"], 100 * final_summary["flag_rate"],
        100 * base_summary["zero_damage_rate"], 100 * final_summary["zero_damage_rate"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
