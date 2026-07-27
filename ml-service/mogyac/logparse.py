"""
Streaming reader for MogyAntiCheat event logs.

Two on-disk formats exist in the wild and both are supported:

1. **JSON Lines** (plugin >= 1.9): one bare event object per line, no wrapper.
2. **Wrapped batches** (older builds): concatenated pretty-printed objects of the shape
   `{"server_id": ..., "timestamp": ..., "batch_id": ..., "count": N, "events": [...]}`.

Player identity is read from `PlayerHash` when present (current plugin, irreversible
per-server hash) and falls back to `PlayerId` for archives produced before hashing was
added. Either way it is only ever used as an opaque grouping key.
"""

import json
import os

_DECODER = json.JSONDecoder()

EVENT_TYPES = ("shot", "hit", "kill", "death")

# Sentinel the plugin writes for a value it could not measure.
UNKNOWN = -1.0


class Event(object):
    """Normalized telemetry event. __slots__ keeps ~600k of these affordable."""

    __slots__ = ("ts", "player", "weapon", "kind", "distance", "ping", "dping", "hit_area",
                 "accuracy_in_window", "aim_delta_deg", "snap_deg", "snap_settle_ms")

    def __init__(self, ts, player, weapon, kind, distance, ping, dping, hit_area,
                 accuracy_in_window, aim_delta_deg=UNKNOWN, snap_deg=UNKNOWN,
                 snap_settle_ms=UNKNOWN):
        self.ts = ts
        self.player = player
        self.weapon = weapon
        self.kind = kind
        self.distance = distance
        self.ping = ping
        self.dping = dping
        self.hit_area = hit_area
        self.accuracy_in_window = accuracy_in_window
        # Aim kinematics. -1 means "not measured": logs written before AimTracking existed have
        # none of these, and the plugin also reports -1 when a shot has no usable trail.
        self.aim_delta_deg = aim_delta_deg
        self.snap_deg = snap_deg
        self.snap_settle_ms = snap_settle_ms


def _num(value, default=0.0):
    if value is None:
        return default
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def normalize(raw):
    """Convert a raw log dict into an Event, or None when it is not a usable shot/hit record."""
    kind = raw.get("EventType") or raw.get("event_type")
    if kind not in EVENT_TYPES:
        return None
    player = raw.get("PlayerHash") or raw.get("player_hash")
    if player is None:
        player = raw.get("PlayerId", raw.get("player_id"))
    if player is None:
        return None
    ts = raw.get("TimestampMs", raw.get("timestamp_ms"))
    if ts is None:
        return None
    weapon = raw.get("WeaponName") or raw.get("weapon") or ""
    area = raw.get("HitArea")
    if area in ("-1", "", None):
        area = None
    return Event(
        ts=int(ts),
        player=str(player),
        weapon=weapon,
        kind=kind,
        distance=_num(raw.get("Distance")),
        ping=int(_num(raw.get("PingMs"))),
        dping=int(_num(raw.get("DeltaPingMs"))),
        hit_area=area,
        accuracy_in_window=_num(raw.get("AccuracyInWindow")),
        aim_delta_deg=_num(raw.get("AimDeltaDeg"), UNKNOWN),
        snap_deg=_num(raw.get("SnapDeg"), UNKNOWN),
        snap_settle_ms=_num(raw.get("SnapSettleMs"), UNKNOWN),
    )


def iter_raw_objects(path):
    """Yield raw event dicts from one log file, tolerating both on-disk formats."""
    with open(path, "r", encoding="utf-8-sig", errors="replace") as handle:
        buf = handle.read()
    pos, end = 0, len(buf)
    while pos < end:
        while pos < end and buf[pos] in " \t\r\n":
            pos += 1
        if pos >= end:
            return
        try:
            obj, pos = _DECODER.raw_decode(buf, pos)
        except ValueError:
            # Truncated tail (server killed mid-write) — stop at the last good object.
            return
        if isinstance(obj, list):
            for item in obj:
                if isinstance(item, dict):
                    yield item
        elif isinstance(obj, dict):
            if "events" in obj and isinstance(obj["events"], list):
                for item in obj["events"]:
                    if isinstance(item, dict):
                        yield item
            else:
                yield obj


def find_log_files(paths):
    """Expand files/directories (recursively) into a sorted list of *.log paths."""
    found = []
    for entry in paths:
        if os.path.isfile(entry):
            found.append(entry)
        elif os.path.isdir(entry):
            for root, _dirs, files in os.walk(entry):
                for name in files:
                    if name.endswith(".log") and "MogyAntiCheat" in name:
                        found.append(os.path.join(root, name))
    return sorted(set(found))


def load_events(paths, progress=None):
    """
    Read every log file under `paths` and return a timestamp-sorted list of Events.

    Sorting globally matters: the plugin's window logic is order-dependent, and one
    server day can be split across several files (or files can be replayed out of order).
    """
    files = find_log_files(paths)
    events = []
    for i, path in enumerate(files):
        before = len(events)
        for raw in iter_raw_objects(path):
            ev = normalize(raw)
            if ev is not None:
                events.append(ev)
        if progress:
            progress("[%d/%d] %s (+%d events)" % (i + 1, len(files), os.path.basename(path),
                                                  len(events) - before))
    events.sort(key=lambda e: e.ts)
    return files, events
