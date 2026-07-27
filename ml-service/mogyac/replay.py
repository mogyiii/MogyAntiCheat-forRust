"""
Faithful Python replica of the plugin's per-weapon shot bookkeeping.

Why replicate instead of trusting the logged `AccuracyInWindow`? Because the trainer has to
be able to answer "what would the accuracy have been under a *different* SampleCount", which
is exactly the knob being tuned. Replaying from raw shot/hit events lets the calibrator
iterate config -> metrics -> config to a fixed point.

The state machine mirrors `WeaponData` in MogyAntiCheat.cs (AddMiss / RegisterHit /
GetAccuracy / GetWeightedScore) and `EvaluateWeapon`'s penalty math. Keep the two in sync:
if the plugin's algorithm changes, `docs/SOURCE_OF_TRUTH.md` and this module both need the
update, or every trained threshold silently drifts off-scale.
"""

from collections import deque

from . import statsutil as su

PENDING_CAP = 100          # WeaponData.PendingMisses hard cap
DEFAULT_SAMPLE_COUNT = 40  # DefaultWeaponSampleCount
MIN_HISTORY_FOR_PENALTY = 10  # EvaluateWeapon: HasEnoughData = History.Count >= 10
DEFAULT_MISS_EXPIRY_MS = 20000.0

# Hit distance is computed as Vector3.Distance(info.HitPositionWorld, info.PointStart).
# When PointStart is unset it degenerates to the world-origin distance, which on a 4k map
# yields values around 1000-2000 m. Those are measurement artifacts, not long-range hits.
# Mirrors the plugin's `MaxHitDistance` config key (SanitizeHitDistance).
MAX_PLAUSIBLE_DISTANCE = 500.0

# Prefab short names the plugin cannot bridge to config keys by pattern alone.
# Mirrors WeaponKeyAliases in MogyAntiCheat.cs.
WEAPON_KEY_ALIASES = {
    "smg": "smg.2",
    "semi_auto_rifle": "rifle.semiauto",
    "semi_auto_pistol": "pistol.semiauto",
    "hunting_bow": "bow.hunting",
}

# Mirrors WeaponFamilyPatterns in MogyAntiCheat.cs. First match wins.
FAMILY_PATTERNS = (
    ("explosive", ("rocket_launcher", "rpg", "mgl", "grenade", "launcher", "flamethrower")),
    ("lmg", ("m249", "hmlmg", "minigun", "lmg")),
    ("sniper", ("l96", "bolt", "sniper")),
    ("semi_rifle", ("semi_auto_rifle", "semiauto_rifle", "sks", "m39")),
    ("auto_rifle", ("ak47", "ak47u", "lr300", "m16", "assault", "custom_smg")),
    ("smg", ("smg", "mp5", "thompson")),
    ("shotgun", ("shotgun", "spas12", "blunderbuss")),
    ("pistol", ("pistol", "python", "revolver", "glock", "m92", "nailgun")),
    ("projectile", ("bow", "crossbow", "speargun", "compound")),
)


def classify_family(weapon):
    """Family of a weapon by name fragment. Mirrors ClassifyWeaponFamily in the plugin."""
    name = (weapon or "").lower()
    for family, fragments in FAMILY_PATTERNS:
        for fragment in fragments:
            if fragment in name:
                return family
    return "other"

AUX_WINDOW = 128  # ring-buffer length for timing/ping/hit-area features

# Feature names in a fixed order. `direction` is +1 when a HIGH value is suspicious and
# -1 when a LOW value is (scripted fire is *too* regular, so a low cadence CV is the signal).
FEATURE_SPEC = (
    ("accuracy", 1),
    ("weighted_score", 1),
    ("longrange_share", 1),
    ("headshot_ratio", 1),
    ("head_streak", 1),
    ("hit_streak", 1),
    ("cadence_cv", -1),
    ("dping_spike_rate", 1),
    ("ping_cv", 1),
    # Aim kinematics — only present in logs written by a plugin with AimTracking enabled.
    # UNKNOWN until then, and the trainer leaves a feature without data out of the model.
    ("aim_snap_speed", 1),
    ("aim_settle_ms", -1),
)

# A feature value the log could not supply. Kept out of baselines and of scoring.
UNKNOWN = -1.0
FEATURE_NAMES = tuple(name for name, _ in FEATURE_SPEC)
FEATURE_DIRECTION = dict(FEATURE_SPEC)


def weapon_token_signature(name):
    """
    Sorted, separator-insensitive token signature. Mirrors WeaponTokenSignature in the plugin:
    "shotgun_pump" and "shotgun.pump" both become "pump.shotgun".
    """
    if not name:
        return ""
    parts = [p for p in name.lower().replace("_", ".").replace("-", ".").split(".") if p]
    return ".".join(sorted(parts))


def resolve_config_key(weapons_cfg, weapon_name, legacy=False):
    """
    Replica of `ResolveWeaponConfigKey`, in the plugin's order of decreasing certainty:
    exact match, case-insensitive exact, alias, segment after the last dot (Carbon reports
    "m39" where Oxide reports "rifle.m39"), then token signature.

    Returns None when the weapon has no config entry, in which case the plugin falls through
    to its weapon-family defaults.

    `legacy=True` restricts matching to exact + last-dot suffix, reproducing the plugin before
    the coverage fix. The trainer uses it to measure what that gap actually cost.
    """
    if not weapons_cfg or not weapon_name:
        return None
    if weapon_name in weapons_cfg:
        return weapon_name

    lowered = weapon_name.lower()
    if not legacy:
        for key in weapons_cfg:
            if key.lower() == lowered:
                return key

        alias = WEAPON_KEY_ALIASES.get(lowered)
        if alias and alias in weapons_cfg:
            return alias

    for key in weapons_cfg:
        dot = key.rfind(".")
        if dot >= 0 and key[dot + 1:].lower() == lowered:
            return key

    if not legacy:
        signature = weapon_token_signature(weapon_name)
        if signature:
            for key in weapons_cfg:
                if weapon_token_signature(key) == signature:
                    return key
    return None


class WeaponTuning(object):
    """Resolved settings for one weapon. Mirrors the plugin's WeaponTuning."""

    __slots__ = ("max_accuracy", "sample_count", "safe_distance", "source", "resolved")

    def __init__(self, max_accuracy=1.0, sample_count=DEFAULT_SAMPLE_COUNT, safe_distance=1.0,
                 source="unconfigured", resolved=False):
        self.max_accuracy = max_accuracy
        self.sample_count = sample_count
        self.safe_distance = safe_distance
        self.source = source
        self.resolved = resolved

    @property
    def applies_penalty(self):
        # The explosive family resolves on purpose to MaxAccuracy 1.0, which is not the same
        # thing as an unresolved weapon.
        return self.resolved and self.max_accuracy < 1.0


def weapon_settings(weapons_cfg, weapon_name, fallback_cfg=None, legacy=False):
    """
    Resolve a weapon's settings the way `GetWeaponTuning` does: config entry first, then the
    weapon-family fallback, then unresolved (never flagged).

    `legacy=True` reproduces the pre-fix plugin: strict key matching and no family fallback.
    """
    if legacy:
        fallback_cfg = None
    key = resolve_config_key(weapons_cfg, weapon_name, legacy=legacy)
    entry = (weapons_cfg or {}).get(key) if key else None
    if isinstance(entry, dict) and "MaxAccuracy" in entry:
        return WeaponTuning(
            max_accuracy=float(entry.get("MaxAccuracy", 1.0)),
            sample_count=int(entry.get("SampleCount", DEFAULT_SAMPLE_COUNT)),
            safe_distance=float(entry.get("SafeDistance", 1.0)) or 1.0,
            source=key,
            resolved=True,
        )

    if fallback_cfg and fallback_cfg.get("Enabled", True):
        family = classify_family(weapon_name)
        families = fallback_cfg.get("Families") or {}
        family_entry = families.get(family)
        if isinstance(family_entry, dict) and "MaxAccuracy" in family_entry:
            return WeaponTuning(
                max_accuracy=float(family_entry["MaxAccuracy"]),
                sample_count=int(family_entry.get("SampleCount", DEFAULT_SAMPLE_COUNT)),
                safe_distance=float(family_entry.get("SafeDistance", 1.0)) or 1.0,
                source="family:" + family,
                resolved=True,
            )

    return WeaponTuning()


def compute_nerf(accuracy, sample_count, weighted_score, max_accuracy):
    """
    Replica of the penalty half of `EvaluateWeapon`. Returns (nerf_factor, is_suspicious).
    nerf_factor 1.0 = full damage, 0.0 = damage nulled.
    """
    if sample_count < MIN_HISTORY_FOR_PENALTY or accuracy <= max_accuracy or max_accuracy >= 1.0:
        return 1.0, False
    excess = (accuracy - max_accuracy) / (1.0 - max_accuracy)
    penalty = excess * (weighted_score ** 2 if weighted_score > 1.0 else 1.0)
    nerf = 1.0 - penalty
    if accuracy > 0.95 and weighted_score > 1.2:
        nerf = 0.0
    if nerf < 0.30:
        nerf = 0.0
    return max(0.0, min(1.0, nerf)), True


class Evaluation(object):
    """One evaluation point — the plugin evaluates a weapon on every registered hit."""

    __slots__ = ("ts", "player", "weapon", "config_key", "tuning_source", "accuracy",
                 "sample_count", "weighted_score", "weighted_score_unclamped", "max_accuracy",
                 "safe_distance", "nerf", "suspicious", "features", "distance",
                 "distance_is_bogus")

    def __init__(self, **kw):
        for slot in self.__slots__:
            setattr(self, slot, kw.get(slot))

    @property
    def zero_damage(self):
        return self.nerf == 0.0

    def feature_dict(self):
        return dict(zip(FEATURE_NAMES, self.features))


class WeaponWindow(object):
    """Per (player, weapon) rolling state: the plugin's History/PendingMisses plus aux features."""

    __slots__ = ("history", "pending", "shot_ts", "pings", "dpings", "hit_areas", "streak",
                 "head_streak", "snaps")

    def __init__(self):
        # (is_hit, distance_used, distance_raw) — mirrors WeaponData.History, with the raw
        # measurement kept alongside so the report can quantify what the distance clamp removed.
        self.history = []
        self.pending = []            # list of timestamps — mirrors WeaponData.PendingMisses
        self.shot_ts = deque(maxlen=AUX_WINDOW)
        self.pings = deque(maxlen=AUX_WINDOW)
        self.dpings = deque(maxlen=AUX_WINDOW)
        self.hit_areas = deque(maxlen=AUX_WINDOW)
        self.snaps = deque(maxlen=AUX_WINDOW)   # (snap_deg, settle_ms) from shots that reported them
        self.streak = 0              # current consecutive-hit run
        # Consecutive head-labelled hits. Measured separately from `streak` because it is the most
        # extreme signal the telemetry contains: on the reference dataset the population median is
        # 1 and the worst player reached 40, some 26 robust sigmas out.
        self.head_streak = 0

    # -- plugin state machine ------------------------------------------------------------
    def add_miss(self, ts):
        """OnWeaponFired -> AddMiss: the shot is pending until a hit resolves it."""
        self.pending.append(ts)
        if len(self.pending) > PENDING_CAP:
            del self.pending[0]

    def register_hit(self, ts, distance, limit, expiry_ms, distance_raw=None):
        """OnEntityTakeDamage -> RegisterHit: backfill older pending shots as misses."""
        if distance_raw is None:
            distance_raw = distance
        last_index = -1
        for i in range(len(self.pending) - 1, -1, -1):
            if ts - self.pending[i] <= expiry_ms:
                last_index = i
                break
        if last_index != -1:
            for i in range(last_index):
                if ts - self.pending[i] <= expiry_ms:
                    self.history.append((False, 0.0, 0.0))
                    self.streak = 0
            self.history.append((True, distance, distance_raw))
            del self.pending[:last_index + 1]
        else:
            self.history.append((True, distance, distance_raw))
        self.streak += 1
        if len(self.history) > limit:
            del self.history[:len(self.history) - limit]

    def accuracy(self):
        n = len(self.history)
        if n == 0:
            return 0.0
        return sum(1 for entry in self.history if entry[0]) / float(n)

    def hit_distances(self, raw=False):
        index = 2 if raw else 1
        return [entry[index] for entry in self.history if entry[0]]

    def weighted_score(self, safe_distance, raw=False):
        """
        GetWeightedScore: mean of (distance / safeDistance) for hits beyond safeDistance, 1.0
        otherwise. `raw=True` uses the unsanitized measurements, which is what the plugin did
        before `MaxHitDistance` existed — the gap between the two is the bug's magnitude.
        """
        hits = self.hit_distances(raw=raw)
        if not hits:
            return 0.0
        if safe_distance <= 0:
            safe_distance = 1.0
        total = sum((d / safe_distance) if d > safe_distance else 1.0 for d in hits)
        return total / len(hits)

    # -- feature extraction --------------------------------------------------------------
    def features(self, safe_distance):
        """Feature vector in FEATURE_NAMES order, computed from the current window."""
        hits = self.hit_distances()

        longrange_share = 0.0
        if hits:
            longrange_share = sum(1 for d in hits if d > safe_distance) / float(len(hits))

        labeled = [a for a in self.hit_areas if a]
        headshot_ratio = (sum(1 for a in labeled if a == "head") / float(len(labeled))) if labeled else 0.0

        # Aim kinematics: how violently the view arrived on target, and how briefly it rested
        # there before the trigger. Assistance snaps and fires; a human decelerates and the
        # settle time scatters.
        valid_snaps = [(deg, ms) for deg, ms in self.snaps if deg >= 0 and ms >= 0]
        if valid_snaps:
            # deg/s of the largest pre-shot step; the floor keeps a 0 ms settle from dividing by zero
            aim_snap_speed = su.median([deg / max(ms, 20.0) * 1000.0 for deg, ms in valid_snaps])
            aim_settle_ms = su.median([ms for _deg, ms in valid_snaps])
        else:
            aim_snap_speed = UNKNOWN
            aim_settle_ms = UNKNOWN

        intervals = []
        ts_list = list(self.shot_ts)
        for i in range(1, len(ts_list)):
            gap = ts_list[i] - ts_list[i - 1]
            if 0 < gap <= 1000:  # intra-burst only; longer gaps are separate engagements
                intervals.append(float(gap))
        cadence_cv = su.cv(intervals) if len(intervals) >= 5 else 0.5  # 0.5 = "unremarkable"

        dping_spike_rate = 0.0
        if self.dpings:
            dping_spike_rate = sum(1 for d in self.dpings if abs(d) > 100) / float(len(self.dpings))
        ping_cv = su.cv([float(p) for p in self.pings]) if len(self.pings) >= 5 else 0.0

        return (
            self.accuracy(),
            self.weighted_score(safe_distance),
            longrange_share,
            headshot_ratio,
            float(self.head_streak),
            float(self.streak),
            cadence_cv,
            dping_spike_rate,
            ping_cv,
            aim_snap_speed,
            aim_settle_ms,
        )


class ReplayEngine(object):
    """
    Drives WeaponWindows for every (player, weapon) pair against a given weapon config.

    `feed(event)` returns an Evaluation on hit events (the moments the plugin itself evaluates
    a weapon) and None otherwise. The same class backs offline training and live /ingest
    scoring, which is what keeps the two on the same scale.
    """

    def __init__(self, weapons_cfg, miss_expiry_ms=DEFAULT_MISS_EXPIRY_MS, fallback_cfg=None,
                 max_hit_distance=MAX_PLAUSIBLE_DISTANCE, legacy=False):
        self.weapons_cfg = weapons_cfg or {}
        self.fallback_cfg = None if legacy else fallback_cfg
        self.miss_expiry_ms = miss_expiry_ms
        # The pre-fix plugin had no distance bound at all.
        self.max_hit_distance = 0.0 if legacy else max_hit_distance
        self.legacy = legacy
        self.state = {}
        self._settings_cache = {}

    def settings(self, weapon):
        cached = self._settings_cache.get(weapon)
        if cached is None:
            cached = weapon_settings(self.weapons_cfg, weapon, self.fallback_cfg,
                                     legacy=self.legacy)
            self._settings_cache[weapon] = cached
        return cached

    def sanitize_distance(self, distance):
        """Mirrors SanitizeHitDistance: the hit still counts, the impossible distance does not."""
        if self.max_hit_distance > 0 and distance > self.max_hit_distance:
            return 0.0
        return distance

    def window(self, player, weapon):
        key = (player, weapon)
        win = self.state.get(key)
        if win is None:
            win = WeaponWindow()
            self.state[key] = win
        return win

    def feed(self, ev):
        if not ev.weapon or ev.kind not in ("shot", "hit"):
            return None

        tuning = self.settings(ev.weapon)
        win = self.window(ev.player, ev.weapon)
        win.pings.append(ev.ping)
        win.dpings.append(ev.dping)

        if ev.kind == "shot":
            win.shot_ts.append(ev.ts)
            win.snaps.append((ev.snap_deg, ev.snap_settle_ms))
            win.add_miss(ev.ts)
            return None

        distance = self.sanitize_distance(ev.distance)
        win.register_hit(ev.ts, distance, tuning.sample_count, self.miss_expiry_ms,
                         distance_raw=ev.distance)
        win.hit_areas.append(ev.hit_area)
        # An unlabelled hit neither extends nor breaks the run — the body part is simply unknown.
        if ev.hit_area == "head":
            win.head_streak += 1
        elif ev.hit_area:
            win.head_streak = 0

        accuracy = win.accuracy()
        weighted = win.weighted_score(tuning.safe_distance)
        nerf, suspicious = compute_nerf(accuracy, len(win.history), weighted,
                                        tuning.max_accuracy if tuning.applies_penalty else 1.0)
        return Evaluation(
            ts=ev.ts,
            player=ev.player,
            weapon=ev.weapon,
            config_key=tuning.source if tuning.resolved else None,
            tuning_source=tuning.source,
            accuracy=accuracy,
            sample_count=len(win.history),
            weighted_score=weighted,
            weighted_score_unclamped=win.weighted_score(tuning.safe_distance, raw=True),
            max_accuracy=tuning.max_accuracy,
            safe_distance=tuning.safe_distance,
            nerf=nerf,
            suspicious=suspicious,
            features=win.features(tuning.safe_distance),
            distance=ev.distance,
            distance_is_bogus=(self.max_hit_distance > 0 and ev.distance > self.max_hit_distance),
        )

    def run(self, events):
        """Yield every Evaluation produced by replaying `events` in order."""
        for ev in events:
            result = self.feed(ev)
            if result is not None:
                yield result
