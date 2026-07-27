"""
Per-weapon config calibration.

The plugin's decision rule is `accuracy > MaxAccuracy` over a rolling window of `SampleCount`
shots, amplified by a distance term relative to `SafeDistance`. All three are hand-picked
constants in the shipped config, and the accuracy they are compared against is *not* raw
hits/shots — it is the plugin's own window metric, which is systematically higher (a dry spray
that never lands is largely discarded by RegisterHit). Thresholds picked by intuition therefore
sit far below the real distribution and flag a large slice of ordinary players.

This module derives the three values from the observed distribution instead:

* **SafeDistance** — the 90th percentile of plausible hit distances for that weapon, so the
  weighted score stays at 1.0 for normal engagements and only rises for genuinely long shots.
* **MaxAccuracy** — the accuracy that only a `flag_rate` tail of *players* sustains on that
  weapon, floored by a margin above the population median so window noise alone cannot cross it.
* **SampleCount** — the window size at which +-2 sigma of binomial noise around a median
  player's accuracy still fits inside the margin below MaxAccuracy.

Calibration is a fixed-point problem (accuracy depends on SampleCount, which is being tuned),
so `train.py` alternates replay and calibration a few times.
"""

import math

from . import statsutil as su
# The family classifier lives in replay.py because the plugin performs the same classification
# at runtime for its own fallback — one definition, mirrored once.
from .replay import FAMILY_PATTERNS, classify_family  # noqa: F401  (re-exported)

# Families where hit-ratio carries no signal: a rocket or grenade "hit" registers on virtually
# every shot, so accuracy is ~1.0 for everyone and thresholding it only produces false positives.
DISABLED_FAMILIES = ("explosive",)

MIN_MAX_ACCURACY = 0.15
MAX_MAX_ACCURACY = 0.99
MIN_SAFE_DISTANCE = 8.0
MAX_SAFE_DISTANCE = 150.0
MIN_SAMPLE_COUNT = 10
MAX_SAMPLE_COUNT = 80
AUTOMATIC_INTERVAL_MS = 250.0  # median inter-shot gap below this = full-auto fire rate


class WeaponStats(object):
    """Distribution summary for one weapon, accumulated over a replay pass."""

    def __init__(self, weapon):
        self.weapon = weapon
        self.family = classify_family(weapon)
        self.accuracies = []          # window accuracy, penalisable and unsaturated only
        self.by_player = {}           # player -> [accuracy, ...], same filter
        self.player_ids = set()       # every player seen, saturated windows included
        self.distances = []           # plausible hit distances
        self.bogus_distances = 0
        self.shots = 0
        self.hits = 0
        self.intervals = []           # intra-burst inter-shot gaps (ms)
        self.flagged = 0              # evaluations the plugin would call suspicious
        self.zero_damage = 0          # evaluations where damage was nulled outright
        self.evaluated = 0            # evaluations with enough history to be penalisable
        self.saturated = 0            # penalisable evaluations sitting at accuracy == 1.0
        self.total_evaluations = 0
        self.weighted_scores = []
        self.weighted_scores_unclamped = []

    def add_evaluation(self, ev, min_history):
        self.total_evaluations += 1
        if ev.distance_is_bogus:
            self.bogus_distances += 1
        else:
            self.distances.append(ev.distance)
        self.weighted_scores.append(ev.weighted_score)
        self.weighted_scores_unclamped.append(ev.weighted_score_unclamped)
        # Only windows the plugin can actually act on (History >= 10) shape the thresholds.
        # Including the warm-up windows would drag the percentiles toward 1.0, since a window
        # holding two hits and no misses reads as 100% accuracy.
        if ev.sample_count >= min_history:
            self.evaluated += 1
            self.player_ids.add(ev.player)
            if ev.accuracy >= 1.0:
                # Saturated windows are a metric artifact, not a measurement: RegisterHit dropped
                # this player's misses, so the window reads 100% regardless of how they aim. They
                # are counted for the diagnostic but kept out of threshold fitting, where they
                # would push every percentile to 1.0 and make the weapon unflaggable. A player who
                # really does sit at 100% is still caught by any threshold below it.
                self.saturated += 1
            else:
                self.accuracies.append(ev.accuracy)
                self.by_player.setdefault(ev.player, []).append(ev.accuracy)
            if ev.suspicious:
                self.flagged += 1
            if ev.nerf == 0.0:
                self.zero_damage += 1

    @property
    def players(self):
        return len(self.player_ids)

    @property
    def flag_rate(self):
        return (self.flagged / float(self.evaluated)) if self.evaluated else 0.0

    @property
    def zero_damage_rate(self):
        return (self.zero_damage / float(self.evaluated)) if self.evaluated else 0.0

    @property
    def bogus_distance_share(self):
        total = len(self.distances) + self.bogus_distances
        return (self.bogus_distances / float(total)) if total else 0.0

    @property
    def saturation_share(self):
        """
        Share of actionable windows reading exactly 100% accuracy.

        RegisterHit only backfills pending shots fired within `MissExpirySeconds` of the hit, so
        a player who fires slowly has their misses silently dropped and reads as perfect. A high
        share here means the metric is saturated for this weapon and no MaxAccuracy below 1.0 can
        separate a cheater from a patient player.
        """
        return (self.saturated / float(self.evaluated)) if self.evaluated else 0.0

    @property
    def median_interval_ms(self):
        return su.median(self.intervals) if self.intervals else 0.0

    @property
    def is_automatic(self):
        interval = self.median_interval_ms
        return bool(interval) and interval < AUTOMATIC_INTERVAL_MS

    def player_medians(self, min_evals):
        """Median window accuracy per player, for players with enough evaluations to be stable."""
        return [su.median(accs) for accs in self.by_player.values() if len(accs) >= min_evals]

    def flagged_players(self, min_evals, threshold):
        """How many qualifying players would sustain a median accuracy above `threshold`."""
        medians = self.player_medians(min_evals)
        if not medians:
            return 0, 0
        return sum(1 for m in medians if m > threshold), len(medians)


def discrimination(stats, min_player_evals):
    """
    How much of the variance in window accuracy is *between* players rather than within one.

    Returns an intraclass-correlation-style ratio in [0, 1] (None when there is too little data).
    This is the property that decides whether a threshold can work at all: if a single player's
    accuracy swings as widely as the gap between players, no MaxAccuracy separates them and every
    threshold trades false positives for misses one-for-one. Maximising it is what makes the
    metric worth thresholding, which is why the MissExpirySeconds sweep optimises this rather
    than the absolute accuracy level.
    """
    groups = [accs for accs in stats.by_player.values() if len(accs) >= min_player_evals]
    if len(groups) < 3:
        return None
    within = su.mean([su.stdev(accs) ** 2 for accs in groups])
    between = su.stdev([su.mean(accs) for accs in groups]) ** 2
    total = within + between
    if total <= 1e-12:
        return None
    return between / total


def _confidence(n_players, n_evals):
    """
    Sample-size confidence in [0, 1]: needs both enough players (so one outlier cannot set the
    threshold) and enough evaluation points (so the percentile is stable).
    """
    player_term = n_players / float(n_players + 8)
    eval_term = min(1.0, n_evals / 400.0)
    return round(min(0.95, player_term * eval_term), 3)


def _noise_sample_count(median_accuracy, margin):
    """Smallest window where +-2 sigma of binomial noise fits inside `margin`."""
    if margin <= 0:
        return MAX_SAMPLE_COUNT
    variance = max(0.01, median_accuracy * (1.0 - median_accuracy))
    return int(math.ceil(4.0 * variance / (margin * margin)))


def _sample_count_floor(stats):
    """
    Preferred minimum window per weapon class.

    Samples are cheap on a fast-firing weapon, and a longer window is strictly better for
    precision: the accuracy of an ordinary player concentrates around their true rate, so the
    threshold can sit closer to it without catching lucky streaks. Slow weapons need a short
    window or they never fill at all.
    """
    if stats.family == "lmg":
        return 50
    if stats.is_automatic:
        return 40
    if stats.family in ("sniper", "projectile", "shotgun"):
        return 12
    return 20


def _safe_distance(stats, params):
    """
    SafeDistance from two competing requirements.

    The weighted score is `distance / SafeDistance` for hits beyond it, and the penalty term
    *squares* that ratio. So the value has to be high enough that ordinary engagements do not
    amplify anything (hence the p90 of observed hit distances), but also high enough that a
    legitimate long shot cannot multiply the penalty without bound — a 100 m hit against an
    8 m SafeDistance scores 12.5, squared to 156. The second term caps the amplification a
    near-maximum-range hit can produce.
    """
    if not stats.distances:
        return MIN_SAFE_DISTANCE
    ordered = sorted(stats.distances)
    typical = su.percentile(ordered, params["safe_distance_pct"])
    extreme = su.percentile(ordered, 99.0)
    bounded = extreme / params["max_weighted_amplification"]
    return round(min(MAX_SAFE_DISTANCE, max(MIN_SAFE_DISTANCE, max(typical, bounded))), 1)


def _disabled_recommendation(stats, reason, current):
    """Recommendation that leaves a weapon unpenalised, with the reason recorded."""
    return {
        "weapon": stats.weapon,
        "family": stats.family,
        "disabled_reason": reason,
        "current": dict(current) if current else None,
        "recommended": {
            "MaxAccuracy": 1.0,
            "SampleCount": max(MIN_SAMPLE_COUNT, min(MAX_SAMPLE_COUNT, _sample_count_floor(stats))),
            "SafeDistance": _safe_distance(stats, DEFAULT_PARAMS),
        },
        "confidence": _confidence(stats.players, stats.evaluated),
        "observed": _observed_block(stats, sorted(stats.accuracies)),
        "current_behaviour": {
            "flag_rate": round(stats.flag_rate, 4),
            "zero_damage_rate": round(stats.zero_damage_rate, 4),
        },
        "projected_behaviour": {
            "event_flag_rate": 0.0,
            "flagged_players": 0,
            "qualifying_players": len(stats.player_medians(1)),
        },
        "noise_sample_count": None,
    }


def _observed_block(stats, sorted_acc):
    return {
        "evaluations": stats.total_evaluations,
        "penalisable_evaluations": stats.evaluated,
        "players": stats.players,
        "qualifying_players": None,
        "shots": stats.shots,
        "hits": stats.hits,
        "raw_accuracy": round(stats.hits / float(stats.shots), 4) if stats.shots else 0.0,
        "window_accuracy_pct": {
            "p50": round(su.percentile(sorted_acc, 50.0), 3) if sorted_acc else 0.0,
            "p75": round(su.percentile(sorted_acc, 75.0), 3) if sorted_acc else 0.0,
            "p90": round(su.percentile(sorted_acc, 90.0), 3) if sorted_acc else 0.0,
            "p95": round(su.percentile(sorted_acc, 95.0), 3) if sorted_acc else 0.0,
            "p99": round(su.percentile(sorted_acc, 99.0), 3) if sorted_acc else 0.0,
        },
        "saturation_share": round(stats.saturation_share, 4),
        "hit_distance_pct": {
            "p50": round(su.percentile(sorted(stats.distances), 50.0), 1) if stats.distances else 0.0,
            "p90": round(su.percentile(sorted(stats.distances), 90.0), 1) if stats.distances else 0.0,
            "p99": round(su.percentile(sorted(stats.distances), 99.0), 1) if stats.distances else 0.0,
        },
        "bogus_distance_share": round(stats.bogus_distance_share, 4),
        "median_shot_interval_ms": round(stats.median_interval_ms, 1),
        "weighted_score_p95": round(su.percentile(sorted(stats.weighted_scores), 95.0), 2)
                              if stats.weighted_scores else 0.0,
        "weighted_score_p95_unclamped": round(su.percentile(sorted(stats.weighted_scores_unclamped), 95.0), 2)
                                    if stats.weighted_scores_unclamped else 0.0,
    }


def calibrate_weapon(stats, current, params):
    """
    Derive recommended settings for one weapon.

    `current` is the weapon's active config dict (may be None when the weapon is unconfigured —
    which is itself the finding worth reporting, since unconfigured weapons are never checked).
    Returns a recommendation dict, or None when the weapon has too little data to say anything.
    """
    min_player_evals = params["min_player_evals"]
    player_medians = stats.player_medians(min_player_evals)
    if stats.evaluated < params["min_evals"] and len(player_medians) < params["min_players"]:
        return None

    current_dict = dict(current) if current else None
    if stats.family in DISABLED_FAMILIES:
        return _disabled_recommendation(
            stats, "hit ratio carries no signal for this weapon family", current_dict)

    # When too many actionable windows read a flat 100%, the metric has no headroom left and any
    # threshold below 1.0 punishes whoever fires slowest rather than whoever aims impossibly well.
    if stats.saturation_share > params["max_saturation_share"]:
        return _disabled_recommendation(
            stats,
            "accuracy metric saturated (%.0f%% of actionable windows read 100%%) - misses are "
            "being dropped for this weapon, raise MissExpirySeconds before thresholding it"
            % (100 * stats.saturation_share),
            current_dict)

    sorted_acc = sorted(stats.accuracies)
    population_median = su.percentile(sorted_acc, 50.0)

    flag_rate = params["flag_rate"]
    player_pct = 100.0 * (1.0 - flag_rate)
    event_pct = 100.0 * (1.0 - flag_rate / 2.0)

    candidates = [su.percentile(sorted_acc, event_pct)]
    if len(player_medians) >= params["min_players"]:
        candidates.append(su.percentile(sorted(player_medians), player_pct))
    # A margin above the population median keeps ordinary players clear of the threshold even
    # when a single window happens to run hot.
    candidates.append(population_median + params["min_margin"])

    max_accuracy = round(min(MAX_MAX_ACCURACY, max(MIN_MAX_ACCURACY, max(candidates))), 3)
    family = stats.family

    safe_distance = _safe_distance(stats, params)

    margin = max_accuracy - population_median
    noise_n = _noise_sample_count(population_median, margin)
    sample_count = int(min(MAX_SAMPLE_COUNT,
                           max(MIN_SAMPLE_COUNT, max(noise_n, _sample_count_floor(stats)))))

    would_flag_players, qualifying_players = stats.flagged_players(min_player_evals, max_accuracy)
    event_flag_rate = (sum(1 for a in sorted_acc if a > max_accuracy) / float(len(sorted_acc))
                       if sorted_acc else 0.0)

    observed = _observed_block(stats, sorted_acc)
    observed["qualifying_players"] = qualifying_players

    return {
        "weapon": stats.weapon,
        "family": family,
        "disabled_reason": None,
        "current": current_dict,
        "recommended": {
            "MaxAccuracy": max_accuracy,
            "SampleCount": sample_count,
            "SafeDistance": safe_distance,
        },
        "confidence": _confidence(stats.players, stats.evaluated),
        "observed": observed,
        "current_behaviour": {
            "flag_rate": round(stats.flag_rate, 4),
            "zero_damage_rate": round(stats.zero_damage_rate, 4),
        },
        "projected_behaviour": {
            "event_flag_rate": round(event_flag_rate, 4),
            "flagged_players": would_flag_players,
            "qualifying_players": qualifying_players,
        },
        "noise_sample_count": noise_n,
    }


def family_fallbacks(recommendations):
    """
    Average recommendation per family, used for weapons seen too rarely to calibrate on their own.
    Weighted by confidence so a well-measured weapon dominates a barely-seen sibling.
    """
    buckets = {}
    for rec in recommendations.values():
        if rec["family"] in DISABLED_FAMILIES:
            continue
        buckets.setdefault(rec["family"], []).append(rec)

    fallbacks = {}
    for family, recs in buckets.items():
        weights = [max(0.05, r["confidence"]) for r in recs]
        total = sum(weights)

        def wavg(field):
            return sum(r["recommended"][field] * w for r, w in zip(recs, weights)) / total

        fallbacks[family] = {
            "MaxAccuracy": round(wavg("MaxAccuracy"), 3),
            "SampleCount": int(round(wavg("SampleCount"))),
            "SafeDistance": round(wavg("SafeDistance"), 1),
            "derived_from": sorted(r["weapon"] for r in recs),
        }
    return fallbacks


DEFAULT_PARAMS = {
    # Share of players per weapon the thresholds should single out. Lower = stricter evidence
    # required = fewer false positives but slower/rarer detection.
    "flag_rate": 0.02,
    # Minimum evidence before a weapon gets its own thresholds instead of a family fallback.
    "min_evals": 60,
    "min_players": 5,
    "min_player_evals": 8,
    # Absolute headroom the threshold must keep above the population median.
    "min_margin": 0.12,
    # Percentile of plausible hit distances used for SafeDistance. High on purpose: the weighted
    # score is *squared* in the penalty term, so a SafeDistance that ordinary engagements exceed
    # multiplies the penalty for normal play.
    "safe_distance_pct": 90.0,
    # Ceiling on the weighted-score multiplier a near-maximum-range hit may produce.
    "max_weighted_amplification": 3.0,
    # Above this share of saturated (100% accuracy) windows a weapon is left unpenalised rather
    # than thresholded on a metric that cannot discriminate.
    "max_saturation_share": 0.25,
}

# Candidate MissExpirySeconds values for the sweep in train.py. Longer windows keep more of a
# player's misses in the history instead of discarding them.
EXPIRY_CANDIDATES = (20.0, 30.0, 45.0, 60.0, 90.0, 120.0)
