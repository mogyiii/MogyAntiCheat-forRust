"""
Anomaly scorer: fit per-weapon baselines offline, apply them online.

There are no ground-truth cheater labels in the event logs, so this is deliberately an
*unsupervised outlier* model rather than a classifier pretending to know what a cheater is.
Each feature is converted to a robust z-score against the population that used the same weapon
(median / MAD, so a handful of extreme players cannot move the baseline), the directed z-scores
are combined with weights, and the resulting raw score is turned into a percentile rank against
the population's own score distribution.

That makes `ml_confidence` mean something honest and stable: "more anomalous than X% of the
observed player-weapon population". Where the decision boundary belongs is the operator's call
via `decision_percentile`.

Once `/feedback` has collected real admin verdicts, `refit_weights()` replaces the hand-set
weights with logistic-regression weights learned from those labels — the same feature pipeline,
supervised. Until then the defaults below apply.
"""

import math

from . import statsutil as su
from .replay import FEATURE_NAMES, FEATURE_DIRECTION, MIN_HISTORY_FOR_PENALTY, UNKNOWN

# Hand-set priors, in FEATURE_NAMES order. Accuracy dominates because it is the feature the
# plugin itself acts on; the rest exist to separate "good player having a good fight" from
# "mechanically impossible consistency".
DEFAULT_WEIGHTS = {
    "accuracy": 1.6,
    "weighted_score": 0.8,
    "longrange_share": 1.0,
    "headshot_ratio": 1.2,
    "head_streak": 1.3,
    "hit_streak": 0.6,
    "cadence_cv": 0.7,
    "dping_spike_rate": 0.5,
    "ping_cv": 0.3,
    # Aim kinematics. Weighted high because they describe the mechanism directly rather than its
    # after-effect — but they contribute nothing until logs actually carry them, since a feature
    # without a fitted baseline scores zero.
    "aim_snap_speed": 1.5,
    "aim_settle_ms": 1.0,
}

# A directed z below this is clipped, so one very ordinary feature cannot mask a real outlier.
Z_FLOOR = -1.5
Z_CEIL = 8.0

ANOMALY_LABELS = {
    "accuracy": "high_accuracy_stability",
    "weighted_score": "long_range_precision",
    "longrange_share": "long_range_precision",
    "headshot_ratio": "headshot_clustering",
    "hit_streak": "improbable_hit_streak",
    "cadence_cv": "scripted_fire_cadence",
    "dping_spike_rate": "network_manipulation",
    "ping_cv": "network_manipulation",
}

GLOBAL_BASELINE_KEY = "__global__"
MIN_BASELINE_SAMPLES = 40  # below this a weapon borrows the global baseline


def fit_baselines(samples_by_weapon):
    """
    samples_by_weapon: {weapon: [feature_tuple, ...]}
    Returns {weapon_or_global: {feature: {"center": c, "scale": s}}}

    A feature whose value is UNKNOWN in a row contributes nothing from that row, and a feature
    with too few real observations gets no baseline entry at all. `directed_z` then scores it 0,
    so a feature the logs cannot supply stays inert instead of poisoning the score with a
    sentinel value. This is what lets the aim-kinematics features ship before any log contains
    them.
    """
    baselines = {}
    pooled = {name: [] for name in FEATURE_NAMES}

    for weapon, rows in samples_by_weapon.items():
        columns = {name: [] for name in FEATURE_NAMES}
        for row in rows:
            for name, value in zip(FEATURE_NAMES, row):
                if value == UNKNOWN:
                    continue
                columns[name].append(value)
                pooled[name].append(value)
        if len(rows) < MIN_BASELINE_SAMPLES:
            continue
        fitted = {name: {"center": round(su.median(values), 6),
                         "scale": round(su.robust_scale(values), 6)}
                  for name, values in columns.items() if len(values) >= MIN_BASELINE_SAMPLES}
        if fitted:
            baselines[weapon] = fitted

    baselines[GLOBAL_BASELINE_KEY] = {
        name: {"center": round(su.median(values), 6),
               "scale": round(su.robust_scale(values), 6)}
        for name, values in pooled.items() if len(values) >= MIN_BASELINE_SAMPLES
    }
    return baselines


def available_features(model):
    """Features the model actually scores on — the rest had no data to fit."""
    baseline = model.get("baselines", {}).get(GLOBAL_BASELINE_KEY, {})
    return [name for name in FEATURE_NAMES if name in baseline]


def directed_z(baseline, features):
    """
    Directed, clipped z-score per feature. Higher always means 'more suspicious'.

    Always returns an entry for every feature name, even when `features` is shorter than the
    current feature list — that happens whenever a model trained by an older build is loaded, and
    silently zipping the short vector would drop features off the end without a word.
    """
    out = {}
    for index, name in enumerate(FEATURE_NAMES):
        value = features[index] if index < len(features) else UNKNOWN
        stat = baseline.get(name)
        # No baseline (feature never had data) or no measurement this window: contribute nothing
        # rather than treating the -1 sentinel as a real, very low value.
        if not stat or value == UNKNOWN:
            out[name] = 0.0
            continue
        scale = stat["scale"] or 1e-6
        z = (value - stat["center"]) / scale
        z *= FEATURE_DIRECTION[name]
        out[name] = max(Z_FLOOR, min(Z_CEIL, z))
    return out


def raw_score(zs, weights):
    return sum(weights.get(name, 0.0) * z for name, z in zs.items())


def resolve_baseline(model, weapon):
    baselines = model.get("baselines", {})
    return baselines.get(weapon) or baselines.get(GLOBAL_BASELINE_KEY) or {}


def resolve_score_table(model, weapon):
    tables = model.get("score_tables", {})
    return tables.get(weapon) or tables.get(GLOBAL_BASELINE_KEY) or []


def score(model, weapon, features, sample_count=None):
    """
    Score one evaluation. Returns a dict shaped for the /penalty-suggestion response, including
    the per-feature contributions that produced it — an anti-cheat verdict nobody can explain
    is a verdict an admin cannot act on.
    """
    weights = model.get("weights", DEFAULT_WEIGHTS)
    baseline = resolve_baseline(model, weapon)
    zs = directed_z(baseline, features)
    raw = raw_score(zs, weights)
    table = resolve_score_table(model, weapon)
    confidence = su.rank_in_table(table, raw) if table else su.sigmoid(raw / 4.0)

    contributions = sorted(
        ((name, round(weights.get(name, 0.0) * z, 3)) for name, z in zs.items()),
        key=lambda kv: -kv[1],
    )
    top_name, top_value = contributions[0] if contributions else (None, 0.0)
    anomaly_type = ANOMALY_LABELS.get(top_name) if top_value > 0.5 else None

    decision = model.get("decision_percentile", 0.99)
    max_nerf = model.get("max_suggested_nerf_pct", 50)
    if sample_count is not None and sample_count < MIN_HISTORY_FOR_PENALTY:
        # Mirror the plugin: too little history to act on, whatever the score says.
        nerf_pct = 0
    elif confidence <= decision or decision >= 1.0:
        nerf_pct = 0
    else:
        over = (confidence - decision) / (1.0 - decision)
        nerf_pct = int(round(min(1.0, over) * max_nerf))

    return {
        "ml_confidence": round(confidence, 3),
        "raw_score": round(raw, 3),
        "suggested_nerf_pct": nerf_pct,
        "anomaly_type": anomaly_type,
        "contributions": dict(contributions),
        "top_factors": [name for name, value in contributions[:3] if value > 0.5],
        "baseline": "weapon" if weapon in model.get("baselines", {}) else "global",
    }


def explain(result, features):
    """One-line human-readable reason string for the /penalty-suggestion payload."""
    values = dict(zip(FEATURE_NAMES, features))
    if not result["top_factors"]:
        return "Within population norms for this weapon."
    parts = []
    for name in result["top_factors"]:
        value = values.get(name, 0.0)
        parts.append("%s=%.2f (z-weighted %+.2f)" % (name, value, result["contributions"][name]))
    return "Outlier on " + "; ".join(parts)


def build_score_tables(model, samples_by_weapon):
    """Percentile tables of the raw score, per weapon plus a global fallback."""
    tables = {}
    pooled = []
    for weapon, rows in samples_by_weapon.items():
        baseline = resolve_baseline(model, weapon)
        weights = model.get("weights", DEFAULT_WEIGHTS)
        scores = [raw_score(directed_z(baseline, row), weights) for row in rows]
        pooled.extend(scores)
        if len(rows) >= MIN_BASELINE_SAMPLES:
            tables[weapon] = [round(v, 4) for v in su.make_percentile_table(scores)]
    tables[GLOBAL_BASELINE_KEY] = [round(v, 4) for v in su.make_percentile_table(pooled)]
    return tables


# ---------------------------------------------------------------------------------------
# Supervised refit — used once /feedback has produced admin verdicts
# ---------------------------------------------------------------------------------------
def refit_weights(model, labeled, l2=0.05, epochs=400, lr=0.1, min_per_class=15):
    """
    Replace the prior weights with logistic-regression weights learned from admin verdicts.

    `labeled`: [(weapon, feature_tuple, label)] where label is 1 for confirmed_cheater and
    0 for false_positive. Returns (weights, info) — weights is None when there is not yet
    enough labelled data of both classes to learn anything trustworthy.
    """
    positives = sum(1 for _w, _f, y in labeled if y == 1)
    negatives = sum(1 for _w, _f, y in labeled if y == 0)
    info = {"positives": positives, "negatives": negatives, "min_per_class": min_per_class}
    if positives < min_per_class or negatives < min_per_class:
        info["status"] = "insufficient_labels"
        return None, info

    rows = []
    for weapon, features, label in labeled:
        zs = directed_z(resolve_baseline(model, weapon), features)
        rows.append(([zs[name] for name in FEATURE_NAMES], float(label)))

    n_features = len(FEATURE_NAMES)
    weights = [0.0] * n_features
    bias = 0.0
    # Class weighting keeps a small positive class from being ignored by the optimiser.
    pos_weight = (negatives / float(positives)) if positives else 1.0

    for _epoch in range(epochs):
        grad_w = [0.0] * n_features
        grad_b = 0.0
        for x, y in rows:
            z = bias + sum(w * xi for w, xi in zip(weights, x))
            pred = su.sigmoid(z)
            sample_weight = pos_weight if y == 1.0 else 1.0
            err = (pred - y) * sample_weight
            for i in range(n_features):
                grad_w[i] += err * x[i]
            grad_b += err
        scale = lr / len(rows)
        for i in range(n_features):
            weights[i] -= scale * (grad_w[i] + l2 * weights[i])
        bias -= scale * grad_b

    # Keep the "higher = more suspicious" contract: a learned negative weight would invert a
    # feature's meaning on evidence too thin to justify it, so clamp at zero.
    learned = {name: round(max(0.0, w), 4) for name, w in zip(FEATURE_NAMES, weights)}
    if all(v == 0.0 for v in learned.values()):
        info["status"] = "degenerate_fit"
        return None, info

    correct = 0
    for x, y in rows:
        pred = su.sigmoid(bias + sum(w * xi for w, xi in zip(weights, x)))
        if (pred >= 0.5) == (y == 1.0):
            correct += 1
    info["status"] = "refit"
    info["train_accuracy"] = round(correct / float(len(rows)), 3)
    info["bias"] = round(bias, 4)
    return learned, info


def evaluate_ranking(model, samples, labels):
    """
    AUC of the scorer over labelled samples, for the training report. `samples` is
    [(weapon, feature_tuple)], `labels` parallel 0/1. Returns None when a class is missing.
    """
    scored = []
    weights = model.get("weights", DEFAULT_WEIGHTS)
    for (weapon, features), label in zip(samples, labels):
        raw = raw_score(directed_z(resolve_baseline(model, weapon), features), weights)
        scored.append((raw, label))
    pos = [s for s, y in scored if y == 1]
    neg = [s for s, y in scored if y == 0]
    if not pos or not neg:
        return None
    wins = 0.0
    for p in pos:
        for n in neg:
            if p > n:
                wins += 1.0
            elif math.isclose(p, n):
                wins += 0.5
    return round(wins / (len(pos) * len(neg)), 4)
