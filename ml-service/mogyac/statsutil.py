"""Small statistics helpers (stdlib only)."""

import math

MAD_TO_SIGMA = 1.4826  # scale factor making MAD a consistent estimator of sigma for normal data


def percentile(sorted_values, q):
    """Linear-interpolation percentile over an already sorted list. `q` in [0, 100]."""
    n = len(sorted_values)
    if n == 0:
        return 0.0
    if n == 1:
        return float(sorted_values[0])
    pos = (q / 100.0) * (n - 1)
    lo = int(math.floor(pos))
    hi = int(math.ceil(pos))
    if lo == hi:
        return float(sorted_values[lo])
    frac = pos - lo
    return float(sorted_values[lo]) * (1.0 - frac) + float(sorted_values[hi]) * frac


def percentiles(values, qs):
    """Multiple percentiles from an unsorted iterable, sorting once."""
    s = sorted(values)
    return [percentile(s, q) for q in qs]


def median(values):
    s = sorted(values)
    return percentile(s, 50.0)


def mad(values, center=None):
    """Median absolute deviation."""
    if not values:
        return 0.0
    c = median(values) if center is None else center
    return median([abs(v - c) for v in values])


def robust_scale(values, center=None, floor=1e-6):
    """MAD-based sigma estimate with a fallback to std when MAD collapses to zero."""
    if not values:
        return floor
    c = median(values) if center is None else center
    s = mad(values, c) * MAD_TO_SIGMA
    if s > floor:
        return s
    s = stdev(values)
    return s if s > floor else floor


def mean(values):
    return (sum(values) / len(values)) if values else 0.0


def stdev(values):
    n = len(values)
    if n < 2:
        return 0.0
    m = sum(values) / n
    return math.sqrt(sum((v - m) ** 2 for v in values) / (n - 1))


def cv(values):
    """Coefficient of variation (std / mean). 0 when the mean is ~0."""
    m = mean(values)
    if abs(m) < 1e-9:
        return 0.0
    return stdev(values) / m


def sigmoid(x):
    if x >= 0:
        return 1.0 / (1.0 + math.exp(-x))
    e = math.exp(x)
    return e / (1.0 + e)


def make_percentile_table(values, points=101):
    """Compress a distribution into a `points`-entry percentile table for fast rank lookup."""
    s = sorted(values)
    if not s:
        return []
    return [percentile(s, i * 100.0 / (points - 1)) for i in range(points)]


def rank_in_table(table, value):
    """Percentile rank of `value` within a table produced by make_percentile_table (0.0-1.0)."""
    n = len(table)
    if n == 0:
        return 0.0
    if value <= table[0]:
        return 0.0
    if value >= table[-1]:
        return 1.0
    # binary search for the bracketing pair
    lo, hi = 0, n - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if table[mid] <= value:
            lo = mid
        else:
            hi = mid
    span = table[hi] - table[lo]
    frac = 0.0 if span <= 0 else (value - table[lo]) / span
    return (lo + frac) / (n - 1)
