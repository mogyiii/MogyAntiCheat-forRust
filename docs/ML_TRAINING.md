# ML Training — tuning the config from real telemetry

The plugin writes every shot and hit to `MogyAntiCheat_Events_<date>.log`. `ml-service/train.py`
reads those logs, replays them through a copy of the plugin's own detection logic, and produces
calibrated config values plus a scoring model for the ML service.

The point is not to add a black box. It is that `MaxAccuracy`, `SampleCount` and `SafeDistance`
were hand-picked numbers compared against a metric nobody had measured. Once you measure it, most
of them turn out to be in the wrong place.

## Quick start

```bash
cd ml-service
python train.py                       # reads ../logs, writes model.json + a report
python selftest.py                    # 100+ checks, no network or server needed
```

Outputs:

| File | Contents |
|---|---|
| `model.json` | Per-weapon baselines + weights. `server.py` loads this for live scoring. |
| `config-recommendation.json` | Paste-ready `Weapons` block, plus per-weapon evidence. |
| `reports/training-report.md` | What changed, why, and the effect on flag rates. |

`model.json` and `config-recommendation.json` contain only aggregates and are safe to share. The
report's review queue names individual players, so `ml-service/reports/` is gitignored — logs
predating the plugin's identity hashing hold raw SteamIDs.

Useful flags:

```bash
python train.py --config /path/to/oxide/config/MogyAntiCheat.json   # diff against your live config
python train.py --flag-rate 0.01        # stricter: single out ~1% of players per weapon
python train.py --logs /srv/rust/oxide/data --iterations 5
python train.py --feedback data/feedback.jsonl   # learn feature weights from admin verdicts
python train.py --no-tune-expiry        # leave MissExpirySeconds alone
```

Requirements: Python 3.8+ and nothing else. The trainer is stdlib only — no numpy, no sklearn.
Flask is needed only to run `server.py`.

## How the thresholds are derived

Everything hinges on one thing: **the accuracy the plugin compares against `MaxAccuracy` is not
hits/shots.** `RegisterHit` only records a miss for a pending shot that was fired within
`MissExpirySeconds` of a hit; everything older is dropped from the window without ever counting.
On the reference dataset raw accuracy is ~7% while the plugin's own window accuracy has a median
of ~33%. A threshold of `0.35` reads as "very high" and is in fact slightly above average.

So the trainer replays the raw events through `mogyac/replay.py` — a line-by-line replica of
`WeaponData.AddMiss` / `RegisterHit` / `GetAccuracy` / `GetWeightedScore` and the penalty math in
`EvaluateWeapon` — and fits each value to the distribution that replica produces:

- **`MaxAccuracy`** — the accuracy only a `--flag-rate` tail of *players* sustains on that weapon.
  Player-level percentiles are used rather than event-level ones so that one heavy player cannot
  set the threshold, with a floor of `population median + 0.12` so ordinary play stays clear of it
  even on a hot streak.
- **`SampleCount`** — large enough that ±2σ of binomial noise around a median player's accuracy
  still fits inside the gap below `MaxAccuracy`, with a per-class floor (LMGs 50, other automatics
  40, snipers/bows 12). Longer windows are strictly better for precision when samples are cheap.
- **`SafeDistance`** — the p90 of observed hit distances, raised if needed so that a
  near-maximum-range hit cannot multiply the penalty by more than 3×. The weighted score is
  *squared* in the penalty term, so a value ordinary engagements exceed multiplies the punishment
  for normal play.
- **`MissExpirySeconds`** — swept over candidate values, keeping the one where accuracy varies most
  *between* players rather than within a single player's session. That between-player share is what
  decides whether any threshold can work at all.

`SampleCount` changes the window the accuracy is measured over, which changes the accuracy the
thresholds are fitted to, so the trainer alternates replay and calibration until it settles
(`--iterations`, default 3).

### Weapons the trainer refuses to threshold

It emits `MaxAccuracy = 1.0` (never flagged) with a recorded reason when:

- **The family carries no signal.** A rocket or grenade "hit" registers on essentially every shot,
  so accuracy is ~1.0 for everyone and thresholding it only produces false positives.
- **The metric is saturated.** If more than 25% of a weapon's actionable windows read exactly 100%
  accuracy, misses are being discarded for that weapon and no threshold below 1.0 separates a
  cheat from a patient player.

Saturated windows are also excluded from threshold fitting — they would drag every percentile to
1.0 and make the weapon unflaggable. A player genuinely sitting at 100% is still caught by any
threshold below it.

## What the first run found

Trained on 22 days of one live server: 592,448 events, 540,842 shots, 571 players, 43 weapons.
Findings 1 and 3 are now **fixed in the plugin**; the numbers below are what they cost beforehand.

**1. A third of all shots were never checked.** 33.6% of shots came from weapons with no config
entry, so `EvaluateWeapon` set `MaxAccuracy = 1.0` and skipped them. The config ships Rust's Oxide
item shortnames (`smg.2`, `rifle.bolt`, `shotgun.pump`) while `ResolveWeaponConfigKey` only matched
the segment after the last dot, which cannot reach the prefab names the server actually reports
(`smg`, `bolt_rifle`, `shotgun_pump`). `smg` alone — 132,173 shots, 24% of the server's total —
was invisible to the anti-cheat.

The plugin now matches keys case-insensitively, through a small alias table (`smg` → `smg.2`), and
by separator-insensitive token signature (`bolt_rifle` ↔ `rifle.bolt`). Anything still unnamed
falls back to its **weapon family** (`WeaponFallback` config block) instead of being exempt. On this
dataset that takes unnamed weapons from 33.6% of shots to 6.6%, all of which the family fallback
covers; the only weapons left unpenalised are explosives, deliberately.

**2. The thresholds that did apply were flagging ordinary play.** Share of actionable windows the
shipped config penalises, once the coverage fix means they actually apply:

| Weapon | Flagged before | After calibration |
|---|---|---|
| `bolt_rifle` | 56.1% | 1.6% |
| `mp5` | 54.6% | 0.0% |
| `hmlmg` | 50.2% | 1.3% |
| `ak47u` | 46.4% | 4.1% |
| `smg` | 44.5% | 1.7% |
| `l96` | 41.4% | 7.0% |
| `m249` | 34.9% | 1.1% |
| **overall** | **39.1%** | **2.3%** |

**The coverage fix on its own makes this worse, not better** — it applies the same bad thresholds to
a third more shots, taking the overall flag rate from 23.3% to 39.1%. Only calibrating on top of it
brings it to 2.3%. The trainer reports all three numbers in one run so the effect is not something
you have to take on faith. Damage nulled outright: 4.7% → 1.4%. The holdout check (calibrate on the
first 70% of the timeline, measure on the unseen rest) gives 43.4% → 4.4%.

**3. Some reported hit distances are impossible.** 3.7% of hits report a distance over 500 m, up to
1,872 m on a 4k map. `Vector3.Distance(info.HitPositionWorld, info.PointStart)` degenerates into a
world-origin distance when `PointStart` is unset. Because the weighted score is squared in the
penalty term, a single bogus reading is enough to null a player's damage: `mp5`'s weighted-score p95
was 175 with those readings and 1.1 without.

The plugin now bounds this with `MaxHitDistance` (default 500 m): the hit still counts, only the
distance is discarded. The raw measurement is still written to the event log, so the trainer can
keep reporting how often the reading breaks.

Numbers above come from the reference dataset. Re-run the trainer on your own logs; a low-pop
server or a different game mode will land somewhere else.

## Seeing where players actually sit

```bash
python report_charts.py      # writes reports/player-statistics.html
```

A self-contained page with one dot per player per weapon — accuracy against volume, against hit
distance, against headshot rate, and a ranked anomaly curve — with the current and calibrated
`MaxAccuracy` drawn as reference lines. It answers the question a threshold number cannot: *how much
of my player base does this line cut off?*

Names never reach the page. Each player becomes an opaque `P-nnn` label assigned in anomaly-rank
order, with no relationship to their SteamID or its hash, so the page is safe to share with other
admins. It has a table view for every value and works without colour vision.

### Why there are no two clusters

The natural expectation is that cheats and legitimate players separate into two visible groups.
On this data they do not, and it is worth being precise about why:

- **The score distribution is unimodal.** The raw anomaly score peaks in one lump and tapers into a
  thin tail. Measured across every candidate feature — first-shot hit rate, hit-interval regularity,
  headshot rate, trigger-interval quantisation — not one is bimodal. The most extreme single value
  in 22 days is a run of 40 consecutive headshots, which is one player, not a cluster.
- **Two clusters need the cheat to be both extreme and common.** Blatant aimbot at 90% accuracy would
  stand apart, but a mild aim assist sitting at 30% accuracy lands exactly on top of a good player.
  With cheating rare, the honest picture is one population with a tail.
- **A percentile rank cannot show clusters at all.** An earlier version of this page plotted
  `ml_confidence`, which is a rank and therefore near-uniform by construction — it flattened the
  distribution's real shape. The page now plots the raw score.

So this is a **ranking problem, not a clustering problem**: the output is a queue ordered by
suspicion, and the top of it is where a human looks. Real separation needs labels — which is what
`/ac-ml-feedback` and `train.py --feedback` exist to accumulate.

### Detecting the snap directly

Hit ratio describes the *result* of aiming. What distinguishes assistance is the *approach*: an
aimbot's view crosses a large angle and stops dead on target, firing tens of milliseconds later,
repeatably. A human decelerates onto the target and the delay scatters shot to shot. A human on
full-auto sprays and loses accuracy; that trade-off is the thing assistance removes.

None of that is visible in accuracy, distance or timing of shots alone — it needs the **view angle**,
which the plugin did not record. It now does (`AimTracking`, on by default): the view direction is
sampled at 20 Hz for players holding a ranged weapon, and every shot carries

| Field | Meaning |
|---|---|
| `AimDeltaDeg` | angle between this shot's view direction and the previous shot's |
| `SnapDeg` | largest single angular step in the 400 ms before the shot |
| `SnapSettleMs` | milliseconds between that step and the trigger |

The trainer derives two features from them — `aim_snap_speed` (degrees per second of that step) and
`aim_settle_ms`. Both are declared but **inert until logs contain the data**: a feature with no
fitted baseline scores zero, so nothing changes until a training run sees real values. Existing logs
predate the field, so the current model lists them as `no data — inert`, which is exactly what the
training report shows.

Once a week or so of logs exist with `AimTracking` on, retrain and check the report: if the two
features light up and rank differently from accuracy, the mechanism is measurable on your server.
That is a hypothesis with a test attached, not a claim.

Two related ideas were measured against the existing logs and **rejected**:

- *First-shot hit rate* (does the opening shot of an engagement land?) correlates with raw accuracy
  at r = +0.88 — it is accuracy measured a second time, so it adds nothing.
- *Trigger-interval quantisation* (share of shot intervals clustered on one exact value) is
  independent (r = +0.11) but the most extreme player sits only 2.9 robust sigmas out. No evidence
  of scripted trigger timing on this server, so it is not worth a feature slot.

By the same measurement, `head_streak` — the longest run of consecutive headshots — reaches **26
sigmas** and is largely independent of accuracy (r = +0.42), so it was added.

## Applying a recommendation

Two options:

1. **In-game, one value at a time** — `/ac-suggest` fetches `GET /config-recommend` and prints the
   diff; `/ac-weapon <weapon> <field> <value>` applies one. Good for a gradual rollout.
2. **Edit the config** — copy the `Weapons` block from `config-recommendation.json` into
   `oxide/config/MogyAntiCheat.json` and reload. `provenance` in that file records whether each
   entry was calibrated directly, inherited from its weapon family, or left unpenalised.

Weapons that only exist in the logs are added under the short prefab name the server reported,
which is what fixes the coverage gap in finding 1. Existing keys keep their names, and config keys
the trainer has no data for are left untouched.

Start with `--flag-rate 0.02` (the default) and tighten toward `0.005` if reports of unfair nerfs
continue. Every recommendation carries a `confidence` derived from how many players and evaluations
backed it — anything below ~0.4 is one server's worth of noise, not a finding.

## The anomaly scorer

`model.json` also contains the scorer behind `GET /penalty-suggestion`. There are no labelled
cheaters in the logs, so it is an **outlier detector, not a classifier**. Each feature becomes a
robust z-score (median / MAD) against the population that used the same weapon, the directed
z-scores are combined with weights, and `ml_confidence` is the percentile rank of that sum against
the same population. It means "more unusual than X% of the observed player-weapon population" —
nothing more.

| Feature | Suspicious when | Why |
|---|---|---|
| `accuracy` | high | the metric the plugin itself acts on |
| `weighted_score` | high | hits concentrated beyond `SafeDistance` |
| `longrange_share` | high | share of window hits past `SafeDistance` |
| `headshot_ratio` | high | aimbots cluster on the head |
| `hit_streak` | high | consecutive hits without a miss |
| `cadence_cv` | **low** | scripted fire is *too* regular |
| `dping_spike_rate` | high | lag-switch signature |
| `ping_cv` | high | unstable connection, or a manufactured one |

Every score carries the per-feature contributions that produced it, and `reason` names the top
factors. A verdict an admin cannot explain is a verdict they cannot act on.

`--decision-percentile` (default 0.99) sets where a score starts producing a nerf suggestion;
below it the service returns `monitor` and 0%.

## Closing the loop with feedback

The unsupervised weights are informed guesses. `POST /feedback` (or `/ac-feedback` in game) records
admin verdicts to `ml-service/data/feedback.jsonl`, and:

```bash
python train.py --feedback data/feedback.jsonl
```

joins those verdicts onto the replayed feature rows and replaces the hand-set weights with logistic
regression weights learned from them, reporting training accuracy and AUC. It needs at least 15
`confirmed_cheater` and 15 `false_positive` verdicts before it will use a learned fit; below that it
says so and keeps the priors. Learned weights are clamped non-negative so thin evidence cannot
invert a feature's meaning.

The review queue at the end of `reports/training-report.md` ranks the highest-scoring
(player, weapon) pairs. That is the cheapest place to source those verdicts.

## Keeping the replica honest

`mogyac/replay.py` is a hand-maintained copy of the plugin's algorithm. If `MogyAntiCheat.cs`
changes how accuracy, the weighted score, or the penalty is computed, the copy has to change with it
or every trained threshold silently drifts off-scale. `selftest.py` pins the behaviour with cases
worked out by hand from the C# source; run it after touching either side.

See also `docs/SOURCE_OF_TRUTH.md` (authoritative algorithm spec), `docs/CONFIG_SCHEMA.md` (config
keys and ranges), and `ml-service/README.md` (service endpoints).
