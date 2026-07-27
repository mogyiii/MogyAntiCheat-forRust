#!/usr/bin/env python3
"""
Build an anonymized per-player statistics page from the event logs.

One dot per player, no names: the page shows where each player sits on accuracy against
volume, hit distance, headshot rate and anomaly percentile, with the current and calibrated
MaxAccuracy drawn as reference lines. The point is to make a threshold choice visible — you can
see how much of the population each line cuts off before you apply it.

    python train.py            # produces model.json (needed for the anomaly percentile)
    python report_charts.py    # writes reports/player-statistics.html

Player identifiers are replaced with sequential `P-nnn` labels assigned in anomaly-rank order.
Nothing derived from the SteamID or its hash reaches the output, so the page is safe to share.

Stdlib only.
"""

import argparse
import datetime as dt
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mogyac import logparse, scoring
from mogyac import statsutil as su
from mogyac.replay import MIN_HISTORY_FOR_PENALTY, ReplayEngine, resolve_config_key
from train import HERE, load_current_config

MIN_EVALUATIONS = 10  # below this a player's median is noise, not a position
MIN_WEAPON_PLAYERS = 6  # weapons with fewer players than this stay out of the filter


def log(msg):
    print(msg)
    sys.stdout.flush()


def collect(events, baseline, model):
    """
    Replay every event once and aggregate per (player, weapon).

    The replay uses the *calibrated* config from the model, so the accuracy plotted is the
    accuracy the recommended settings would measure. Both thresholds are drawn on top of it.
    """
    engine = ReplayEngine(
        model.get("weapons_config") or baseline.weapons,
        miss_expiry_ms=float(model.get("miss_expiry_seconds", baseline.expiry_seconds)) * 1000.0,
        fallback_cfg=model.get("weapon_fallback") or baseline.fallback,
        max_hit_distance=float(model.get("max_hit_distance", baseline.max_hit_distance)),
    )
    headshot_index = list(model["features"]).index("headshot_ratio")

    pairs = {}
    shots = {}
    for ev in events:
        if ev.kind == "shot" and ev.weapon:
            key = (ev.player, ev.weapon)
            shots[key] = shots.get(key, 0) + 1
        result = engine.feed(ev)
        if result is None or result.sample_count < MIN_HISTORY_FOR_PENALTY:
            continue
        key = (result.player, result.weapon)
        entry = pairs.get(key)
        if entry is None:
            entry = {"accuracies": [], "distances": [], "confidences": [], "headshots": [],
                     "raw_scores": [], "flagged": 0, "evaluations": 0}
            pairs[key] = entry
        entry["evaluations"] += 1
        entry["accuracies"].append(result.accuracy)
        entry["headshots"].append(result.features[headshot_index])
        if not result.distance_is_bogus and result.distance > 0:
            entry["distances"].append(result.distance)
        if result.suspicious:
            entry["flagged"] += 1
        scored = scoring.score(model, result.weapon, result.features,
                               sample_count=result.sample_count)
        entry["confidences"].append(scored["ml_confidence"])
        # The raw score is what has a shape. `ml_confidence` is its percentile rank, which is
        # near-uniform by construction and therefore cannot show clustering even if clustering
        # exists — plotting the rank was hiding the very thing the chart is for.
        entry["raw_scores"].append(scored["raw_score"])
    return pairs, shots


def build_rows(pairs, shots, decision):
    rows = []
    for (player, weapon), entry in pairs.items():
        if entry["evaluations"] < MIN_EVALUATIONS:
            continue
        confidences = sorted(entry["confidences"])
        rows.append({
            "player": player,
            "weapon": weapon,
            "evaluations": entry["evaluations"],
            "shots": shots.get((player, weapon), 0),
            "accuracy": round(su.median(entry["accuracies"]), 4),
            "distance": round(su.median(entry["distances"]), 1) if entry["distances"] else None,
            "headshot": round(su.median(entry["headshots"]), 4),
            "confidence": round(su.mean(confidences), 4),
            "confidence_p90": round(su.percentile(confidences, 90.0), 4),
            "score": round(su.percentile(sorted(entry["raw_scores"]), 90.0), 3),
            "flag_share": round(entry["flagged"] / float(entry["evaluations"]), 4),
        })

    # A player stands out when at least a tenth of their windows land in the top (1 - decision)
    # of the whole population. One hot window is not a pattern.
    for row in rows:
        row["standout"] = row["confidence_p90"] >= decision
    rows.sort(key=lambda r: (-r["confidence_p90"], -r["confidence"]))

    # Opaque labels in rank order — nothing about the SteamID survives into the page.
    labels = {}
    for row in rows:
        if row["player"] not in labels:
            labels[row["player"]] = "P-%03d" % (len(labels) + 1)
    for row in rows:
        row["label"] = labels[row["player"]]
        del row["player"]
    return rows


def weapon_thresholds(baseline, model, weapons):
    """Current vs calibrated MaxAccuracy per weapon, for the reference lines."""
    calibrated = model.get("weapons_config") or {}
    out = {}
    for weapon in weapons:
        current_key = resolve_config_key(baseline.weapons, weapon)
        new_key = resolve_config_key(calibrated, weapon)
        out[weapon] = {
            "current": (baseline.weapons.get(current_key) or {}).get("MaxAccuracy")
                       if current_key else None,
            "recommended": (calibrated.get(new_key) or {}).get("MaxAccuracy") if new_key else None,
        }
    return out


# ---------------------------------------------------------------------------------------
# page
# ---------------------------------------------------------------------------------------
PAGE_TEMPLATE = u"""<title>%(title)s</title>
<style>
  .viz-root {
    color-scheme: light;
    --surface-1: #fcfcfb;
    --plane: #f9f9f7;
    --text-primary: #0b0b0b;
    --text-secondary: #52514e;
    --muted: #898781;
    --grid: #e1e0d9;
    --axis: #c3c2b7;
    --border: rgba(11,11,11,0.10);
    --series-1: #2a78d6;
    --critical: #d03b3b;
  }
  @media (prefers-color-scheme: dark) {
    :root:where(:not([data-theme="light"])) .viz-root {
      color-scheme: dark;
      --surface-1: #1a1a19;
      --plane: #0d0d0d;
      --text-primary: #ffffff;
      --text-secondary: #c3c2b7;
      --muted: #898781;
      --grid: #2c2c2a;
      --axis: #383835;
      --border: rgba(255,255,255,0.10);
      --series-1: #3987e5;
      --critical: #d03b3b;
    }
  }
  :root[data-theme="dark"] .viz-root {
    color-scheme: dark;
    --surface-1: #1a1a19;
    --plane: #0d0d0d;
    --text-primary: #ffffff;
    --text-secondary: #c3c2b7;
    --muted: #898781;
    --grid: #2c2c2a;
    --axis: #383835;
    --border: rgba(255,255,255,0.10);
    --series-1: #3987e5;
    --critical: #d03b3b;
  }

  .viz-root {
    background: var(--plane);
    color: var(--text-primary);
    font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
    line-height: 1.5;
    padding: 32px 20px 64px;
    min-height: 100vh;
  }
  .wrap { max-width: 1080px; margin: 0 auto; }
  h1 { font-size: 1.6rem; font-weight: 650; margin: 0 0 6px; letter-spacing: -0.01em; }
  .sub { color: var(--text-secondary); margin: 0 0 4px; max-width: 68ch; }
  .meta { color: var(--muted); font-size: 0.82rem; margin: 0 0 28px; }
  h2 { font-size: 1.02rem; font-weight: 620; margin: 0 0 2px; }
  .note { color: var(--text-secondary); font-size: 0.85rem; margin: 0 0 14px; max-width: 76ch; }

  .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
           gap: 12px; margin-bottom: 26px; }
  .tile { background: var(--surface-1); border: 1px solid var(--border); border-radius: 10px;
          padding: 14px 16px; }
  .tile .k { color: var(--text-secondary); font-size: 0.78rem; text-transform: uppercase;
             letter-spacing: 0.04em; }
  .tile .v { font-size: 1.72rem; font-weight: 640; margin-top: 4px; }
  .tile .d { color: var(--muted); font-size: 0.78rem; }

  .controls { display: flex; flex-wrap: wrap; gap: 12px; align-items: center;
              margin-bottom: 22px; }
  label.ctl { color: var(--text-secondary); font-size: 0.85rem; display: flex; gap: 7px;
              align-items: center; }
  select, button { font: inherit; font-size: 0.86rem; color: var(--text-primary);
                   background: var(--surface-1); border: 1px solid var(--border);
                   border-radius: 8px; padding: 6px 10px; }
  button { cursor: pointer; }
  button[aria-pressed="true"] { border-color: var(--series-1); color: var(--series-1); }
  select:focus-visible, button:focus-visible { outline: 2px solid var(--series-1);
                                               outline-offset: 2px; }

  .card { background: var(--surface-1); border: 1px solid var(--border); border-radius: 12px;
          padding: 18px 18px 8px; margin-bottom: 20px; }
  .plot { width: 100%%; overflow-x: auto; }
  svg { display: block; width: 100%%; height: auto; }
  .legend { display: flex; gap: 18px; flex-wrap: wrap; padding: 4px 0 12px;
            color: var(--text-secondary); font-size: 0.83rem; }
  .legend span.key { display: inline-flex; align-items: center; gap: 7px; }
  .swatch { width: 11px; height: 11px; border-radius: 50%%; display: inline-block; }
  .swatch.ring { box-shadow: 0 0 0 2px var(--surface-1), 0 0 0 3px var(--critical); }

  @media (prefers-reduced-motion: reduce) { .tt { transition: none; } }
  .tt { position: fixed; pointer-events: none; z-index: 20; opacity: 0;
        transition: opacity 90ms linear; background: var(--surface-1);
        border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px;
        font-size: 0.8rem; box-shadow: 0 4px 16px rgba(0,0,0,0.16); max-width: 240px; }
  .tt b { font-weight: 620; }
  .tt div { color: var(--text-secondary); }
  .tt .n { color: var(--text-primary); font-variant-numeric: tabular-nums; }

  .tablewrap { overflow-x: auto; background: var(--surface-1); border: 1px solid var(--border);
               border-radius: 12px; }
  table { border-collapse: collapse; width: 100%%; font-size: 0.84rem; }
  th, td { text-align: right; padding: 7px 12px; border-bottom: 1px solid var(--grid);
           white-space: nowrap; font-variant-numeric: tabular-nums; }
  th:first-child, td:first-child, th:nth-child(2), td:nth-child(2) { text-align: left;
           font-variant-numeric: normal; }
  th { color: var(--text-secondary); font-weight: 600; position: sticky; top: 0;
       background: var(--surface-1); }
  tr.standout td:first-child { color: var(--critical); font-weight: 620; }
  .foot { color: var(--muted); font-size: 0.8rem; margin-top: 26px; max-width: 76ch; }
  .hidden { display: none; }
</style>

<div class="viz-root">
<div class="wrap">
  <h1>%(title)s</h1>
  <p class="sub">One dot per player per weapon. Names are removed — each player is an opaque
  <code>P-nnn</code> label assigned in anomaly rank order. The horizontal lines are the accuracy
  thresholds: everything above a line is what that setting would penalise.</p>
  <p class="meta">%(meta)s</p>

  <div class="tiles">%(tiles)s</div>

  <div class="controls">
    <label class="ctl">Weapon
      <select id="weapon">%(weapon_options)s</select>
    </label>
    <label class="ctl">Minimum evaluations
      <select id="minev">
        <option value="10">10</option>
        <option value="25" selected>25</option>
        <option value="50">50</option>
        <option value="100">100</option>
      </select>
    </label>
    <button id="toggle-table" aria-pressed="false">Show table view</button>
    <span class="meta" id="count" style="margin:0"></span>
  </div>

  <div class="card">
    <h2>Accuracy against volume</h2>
    <p class="note">The suspicious corner is top-right: high accuracy sustained over many shots.
    High accuracy on the left is small-sample luck, which is why the plugin needs a full window
    before it acts.</p>
    <div class="legend">
      <span class="key"><span class="swatch" style="background:var(--series-1)"></span>Within population norms</span>
      <span class="key"><span class="swatch ring" style="background:var(--critical)"></span>Above the anomaly decision percentile (larger, ringed)</span>
    </div>
    <div class="plot"><svg id="c1" viewBox="0 0 760 430" role="img"
      aria-label="Scatter plot of median window accuracy against shots fired, one dot per player"></svg></div>
  </div>

  <div class="card">
    <h2>Accuracy against engagement distance</h2>
    <p class="note">Aim assistance shows up as accuracy that does not fall off with distance.
    Ordinary players slide down and to the left.</p>
    <div class="plot"><svg id="c2" viewBox="0 0 760 430" role="img"
      aria-label="Scatter plot of median window accuracy against median hit distance"></svg></div>
  </div>

  <div class="card">
    <h2>Accuracy against headshot rate</h2>
    <p class="note">Headshot rate is measured only over hits the server labelled with a body part,
    so a dot at zero can mean "no labelled hits" rather than "no headshots".</p>
    <div class="plot"><svg id="c3" viewBox="0 0 760 430" role="img"
      aria-label="Scatter plot of median window accuracy against median headshot rate"></svg></div>
  </div>

  <div class="card">
    <h2>Where the suspicion score actually lands</h2>
    <p class="note">This is the raw weighted anomaly score, not its percentile rank — a rank is
    flat by construction and cannot show clustering even where clustering exists. <b>There are no
    two groups here.</b> The population is one lump with a thin tail to the right, which is what
    you get when cheating is rare and a mild cheat looks like a good player. Treat the tail as a
    queue to check, not as a verdict; confirming cases through <code>/ac-ml-feedback</code> is what
    turns this into a model that can actually separate the two.</p>
    <div class="plot"><svg id="c4" viewBox="0 0 760 430" role="img"
      aria-label="Histogram of the raw anomaly score across players"></svg></div>
  </div>

  <div id="tablesection" class="hidden">
    <h2>Table view</h2>
    <p class="note">Every value in the charts, reachable without colour or hover.</p>
    <div class="tablewrap"><table id="table"><thead><tr>
      <th>Player</th><th>Weapon</th><th>Shots</th><th>Windows</th><th>Accuracy</th>
      <th>Hit dist. (m)</th><th>Headshot rate</th><th>Anomaly p90</th><th>Flagged windows</th>
    </tr></thead><tbody></tbody></table></div>
  </div>

  <p class="foot">%(footer)s</p>
</div>
<div class="tt" id="tt" role="status" aria-live="polite"></div>
</div>

<script>
const DATA = %(data)s;

const W = 760, H = 430, M = {t: 14, r: 26, b: 46, l: 58};
const PW = W - M.l - M.r, PH = H - M.t - M.b;
const SVGNS = "http://www.w3.org/2000/svg";

function el(name, attrs, text) {
  const node = document.createElementNS(SVGNS, name);
  for (const k in attrs) node.setAttribute(k, attrs[k]);
  if (text !== undefined) node.textContent = text;
  return node;
}
function fmtPct(v) { return (v * 100).toFixed(0) + "%%"; }

// log scale that tolerates zero by flooring at 1
function logScale(max) {
  const hi = Math.log10(Math.max(10, max));
  return v => M.l + (Math.log10(Math.max(1, v)) / hi) * PW;
}
function linScale(max) {
  return v => M.l + (Math.min(v, max) / max) * PW;
}
const yScale = v => M.t + (1 - Math.min(1, Math.max(0, v))) * PH;

function logTicks(max) {
  const out = [];
  for (let p = 0; Math.pow(10, p) <= Math.max(10, max); p++) {
    out.push(Math.pow(10, p));
    const half = 3 * Math.pow(10, p);
    if (half <= max) out.push(half);
  }
  return out;
}

function frame(svg, xTicks, xFmt, xLabel, yTicks, yFmt, yLabel, xPos) {
  for (const t of yTicks) {
    const y = yScale(t);
    svg.appendChild(el("line", {x1: M.l, x2: M.l + PW, y1: y, y2: y,
      stroke: "var(--grid)", "stroke-width": 1}));
    svg.appendChild(el("text", {x: M.l - 10, y: y + 4, "text-anchor": "end",
      fill: "var(--muted)", "font-size": 11}, yFmt(t)));
  }
  svg.appendChild(el("line", {x1: M.l, x2: M.l + PW, y1: M.t + PH, y2: M.t + PH,
    stroke: "var(--axis)", "stroke-width": 1}));
  for (const t of xTicks) {
    const x = xPos(t);
    if (x > M.l + PW + 1) continue;
    svg.appendChild(el("text", {x: x, y: M.t + PH + 18, "text-anchor": "middle",
      fill: "var(--muted)", "font-size": 11}, xFmt(t)));
  }
  svg.appendChild(el("text", {x: M.l + PW / 2, y: H - 8, "text-anchor": "middle",
    fill: "var(--text-secondary)", "font-size": 12}, xLabel));
  svg.appendChild(el("text", {x: 14, y: M.t + PH / 2, "text-anchor": "middle",
    fill: "var(--text-secondary)", "font-size": 12,
    transform: "rotate(-90 14 " + (M.t + PH / 2) + ")"}, yLabel));
}

function thresholdLine(svg, value, label, color, dashed) {
  if (value === null || value === undefined || value > 1) return;
  const y = yScale(value);
  const attrs = {x1: M.l, x2: M.l + PW, y1: y, y2: y, stroke: color, "stroke-width": 2};
  if (dashed) { attrs["stroke-dasharray"] = "6 4"; attrs["stroke-width"] = 1.5; }
  svg.appendChild(el("line", attrs));
  const text = el("text", {x: M.l + PW, y: y - 6, "text-anchor": "end", fill: color,
    "font-size": 11, "font-weight": 600}, label + " " + fmtPct(value));
  svg.appendChild(text);
}

let hoverTargets = [];

function drawScatter(svgId, rows, xKey, xPos, xTicks, xFmt, xLabel, lines) {
  const svg = document.getElementById(svgId);
  svg.textContent = "";
  if (!rows.length) { empty(svg); return; }
  frame(svg, xTicks, xFmt, xLabel, [0, 0.25, 0.5, 0.75, 1], fmtPct,
        "Median window accuracy", xPos);
  if (lines) {
    thresholdLine(svg, lines.current, "current", "var(--muted)", true);
    thresholdLine(svg, lines.recommended, "calibrated", "var(--text-secondary)", false);
  }
  const targets = [];
  // Standouts drawn last so their ring is never covered by an ordinary dot.
  const ordered = rows.slice().sort((a, b) => (a.standout ? 1 : 0) - (b.standout ? 1 : 0));
  for (const row of ordered) {
    const xv = row[xKey];
    if (xv === null || xv === undefined) continue;
    const cx = xPos(xv), cy = yScale(row.accuracy);
    const dot = el("circle", {cx: cx, cy: cy, r: row.standout ? 5 : 3.4,
      fill: row.standout ? "var(--critical)" : "var(--series-1)",
      "fill-opacity": row.standout ? 0.95 : 0.62});
    if (row.standout) {
      dot.setAttribute("stroke", "var(--surface-1)");
      dot.setAttribute("stroke-width", 2);
    }
    svg.appendChild(dot);
    targets.push({x: cx, y: cy, row: row});
  }
  attachHover(svg, targets);
}

function empty(svg) {
  svg.appendChild(el("text", {x: W / 2, y: H / 2, "text-anchor": "middle",
    fill: "var(--muted)", "font-size": 13},
    "No players match this filter - lower the minimum evaluations."));
}

function drawHistogram(svgId, rows) {
  const svg = document.getElementById(svgId);
  svg.textContent = "";
  if (!rows.length) { empty(svg); return; }

  const values = rows.map(r => r.score);
  const lo = Math.min(...values), hi = Math.max(...values);
  const span = (hi - lo) || 1;
  const bins = 32;
  const counts = new Array(bins).fill(0);
  const members = Array.from({length: bins}, () => []);
  for (const row of rows) {
    const i = Math.min(bins - 1, Math.floor((row.score - lo) / span * bins));
    counts[i]++;
    members[i].push(row);
  }
  const peak = Math.max(...counts);
  const xPos = v => M.l + ((v - lo) / span) * PW;
  const yCount = c => M.t + (1 - c / peak) * PH;

  // y grid in counts
  const steps = 4;
  for (let s = 0; s <= steps; s++) {
    const c = Math.round(peak * s / steps);
    const y = yCount(c);
    svg.appendChild(el("line", {x1: M.l, x2: M.l + PW, y1: y, y2: y,
      stroke: "var(--grid)", "stroke-width": 1}));
    svg.appendChild(el("text", {x: M.l - 10, y: y + 4, "text-anchor": "end",
      fill: "var(--muted)", "font-size": 11}, String(c)));
  }
  svg.appendChild(el("line", {x1: M.l, x2: M.l + PW, y1: M.t + PH, y2: M.t + PH,
    stroke: "var(--axis)", "stroke-width": 1}));
  for (let s = 0; s <= 4; s++) {
    const v = lo + span * s / 4;
    svg.appendChild(el("text", {x: xPos(v), y: M.t + PH + 18, "text-anchor": "middle",
      fill: "var(--muted)", "font-size": 11}, v.toFixed(1)));
  }
  svg.appendChild(el("text", {x: M.l + PW / 2, y: H - 8, "text-anchor": "middle",
    fill: "var(--text-secondary)", "font-size": 12},
    "Raw anomaly score (weighted robust z-sum, higher = more unusual)"));
  svg.appendChild(el("text", {x: 14, y: M.t + PH / 2, "text-anchor": "middle",
    fill: "var(--text-secondary)", "font-size": 12,
    transform: "rotate(-90 14 " + (M.t + PH / 2) + ")"}, "Players in bin"));

  const barW = PW / bins;
  const targets = [];
  for (let i = 0; i < bins; i++) {
    if (!counts[i]) continue;
    const anyStandout = members[i].some(r => r.standout);
    const x = M.l + i * barW;
    const y = yCount(counts[i]);
    // 2px surface gap between bars, and a 4px rounded top anchored to the baseline
    svg.appendChild(el("rect", {x: x + 1, y: y, width: Math.max(1, barW - 2),
      height: M.t + PH - y, rx: 3,
      fill: anyStandout ? "var(--critical)" : "var(--series-1)",
      "fill-opacity": anyStandout ? 0.9 : 0.55}));
    targets.push({x: x + barW / 2, y: y, bin: i, count: counts[i],
                  from: lo + span * i / bins, to: lo + span * (i + 1) / bins});
  }

  const decisionRows = rows.filter(r => r.standout);
  if (decisionRows.length) {
    const cut = Math.min(...decisionRows.map(r => r.score));
    const x = xPos(cut);
    svg.appendChild(el("line", {x1: x, x2: x, y1: M.t, y2: M.t + PH,
      stroke: "var(--text-secondary)", "stroke-width": 2}));
    svg.appendChild(el("text", {x: x - 8, y: M.t + 14, "text-anchor": "end",
      fill: "var(--text-secondary)", "font-size": 11, "font-weight": 600},
      "decision cut-off"));
  }

  svg.onpointermove = event => {
    const box = svg.getBoundingClientRect();
    const scale = W / box.width;
    const px = (event.clientX - box.left) * scale;
    let best = null, bestDist = Infinity;
    for (const t of targets) {
      const d = Math.abs(t.x - px);
      if (d < bestDist) { bestDist = d; best = t; }
    }
    if (!best || bestDist > barW) { tt.style.opacity = 0; return; }
    tt.innerHTML = "<b>" + best.count + " player" + (best.count === 1 ? "" : "s") + "</b>" +
      "<div>score <span class='n'>" + best.from.toFixed(2) + "</span> to " +
      "<span class='n'>" + best.to.toFixed(2) + "</span></div>";
    tt.style.opacity = 1;
    let left = event.clientX + 14, top = event.clientY + 14;
    if (left + 220 > window.innerWidth) left = event.clientX - 220;
    tt.style.left = Math.max(4, left) + "px";
    tt.style.top = Math.max(4, top) + "px";
  };
  svg.onpointerleave = () => { tt.style.opacity = 0; };
}

const tt = document.getElementById("tt");

function attachHover(svg, targets) {
  svg.onpointermove = event => {
    const box = svg.getBoundingClientRect();
    const scale = W / box.width;
    const px = (event.clientX - box.left) * scale;
    const py = (event.clientY - box.top) * scale;
    let best = null, bestDist = Infinity;
    for (const target of targets) {
      const d = (target.x - px) ** 2 + (target.y - py) ** 2;
      if (d < bestDist) { bestDist = d; best = target; }
    }
    // ~24px hit radius in viewBox units, so a dot never needs a dead-centre landing.
    if (!best || bestDist > (24 * scale) ** 2) { tt.style.opacity = 0; return; }
    const row = best.row;
    tt.innerHTML = "<b>" + row.label + "</b> &middot; " + row.weapon +
      "<div>accuracy <span class='n'>" + fmtPct(row.accuracy) + "</span></div>" +
      "<div>shots <span class='n'>" + row.shots.toLocaleString() + "</span> in " +
      "<span class='n'>" + row.evaluations + "</span> windows</div>" +
      (row.distance === null ? "" :
        "<div>median hit distance <span class='n'>" + row.distance + " m</span></div>") +
      "<div>headshot rate <span class='n'>" + fmtPct(row.headshot) + "</span></div>" +
      "<div>anomaly p90 <span class='n'>" + fmtPct(row.confidence_p90) + "</span></div>";
    tt.style.opacity = 1;
    const pad = 14;
    let left = event.clientX + pad, top = event.clientY + pad;
    if (left + 250 > window.innerWidth) left = event.clientX - 250 - pad;
    if (top + 150 > window.innerHeight) top = event.clientY - 150;
    tt.style.left = Math.max(4, left) + "px";
    tt.style.top = Math.max(4, top) + "px";
  };
  svg.onpointerleave = () => { tt.style.opacity = 0; };
}

function currentRows() {
  const weapon = document.getElementById("weapon").value;
  const minev = parseInt(document.getElementById("minev").value, 10);
  return DATA.rows.filter(r => (weapon === "__all__" || r.weapon === weapon)
                               && r.evaluations >= minev);
}

function render() {
  const rows = currentRows();
  const weapon = document.getElementById("weapon").value;
  const lines = weapon === "__all__" ? null : DATA.thresholds[weapon];
  const maxShots = Math.max(10, ...rows.map(r => r.shots));
  const distances = rows.map(r => r.distance).filter(v => v !== null);
  const maxDist = Math.max(10, ...distances);

  drawScatter("c1", rows, "shots", logScale(maxShots), logTicks(maxShots),
              t => t >= 1000 ? (t / 1000) + "k" : String(t), "Shots fired (log scale)", lines);
  drawScatter("c2", rows, "distance", logScale(maxDist), logTicks(maxDist),
              t => t + " m", "Median hit distance (log scale)", lines);
  drawScatter("c3", rows, "headshot", linScale(1), [0, 0.2, 0.4, 0.6, 0.8, 1],
              fmtPct, "Median headshot rate in window", lines);
  drawHistogram("c4", rows);

  const standouts = rows.filter(r => r.standout).length;
  document.getElementById("count").textContent =
    rows.length + " player-weapon pairs shown, " + standouts + " above the decision percentile" +
    (weapon === "__all__" ? " — thresholds are per weapon, pick one to see its lines" : "");

  const body = document.querySelector("#table tbody");
  body.textContent = "";
  for (const row of rows) {
    const tr = document.createElement("tr");
    if (row.standout) tr.className = "standout";
    const cells = [row.label, row.weapon, row.shots.toLocaleString(), row.evaluations,
                   fmtPct(row.accuracy), row.distance === null ? "-" : row.distance,
                   fmtPct(row.headshot), fmtPct(row.confidence_p90), fmtPct(row.flag_share)];
    for (const value of cells) {
      const td = document.createElement("td");
      td.textContent = value;
      tr.appendChild(td);
    }
    body.appendChild(tr);
  }
}

document.getElementById("weapon").addEventListener("change", render);
document.getElementById("minev").addEventListener("change", render);
const toggle = document.getElementById("toggle-table");
toggle.addEventListener("click", () => {
  const open = toggle.getAttribute("aria-pressed") === "true";
  toggle.setAttribute("aria-pressed", String(!open));
  toggle.textContent = open ? "Show table view" : "Hide table view";
  document.getElementById("tablesection").classList.toggle("hidden", open);
});
render();
</script>
"""


def tile(key, value, detail):
    return ('<div class="tile"><div class="k">%s</div><div class="v">%s</div>'
            '<div class="d">%s</div></div>' % (key, value, detail))


def build_page(rows, thresholds_map, model, baseline, weapons, meta, title):
    standouts = len({r["label"] for r in rows if r["standout"]})
    players = len({r["label"] for r in rows})
    behaviour = (model.get("config_recommendation") or {}).get("behaviour") or {}
    current = behaviour.get("current") or {}
    calibrated = behaviour.get("calibrated") or {}

    tiles = "".join([
        tile("Players charted", str(players),
             "%d player-weapon pairs with %d+ windows" % (len(rows), MIN_EVALUATIONS)),
        tile("Above decision percentile", str(standouts),
             "%.1f%% of charted players" % (100.0 * standouts / players if players else 0)),
        tile("Flagged windows now", "%.1f%%" % (100 * current.get("flag_rate", 0)),
             "under the current thresholds"),
        tile("After calibration", "%.1f%%" % (100 * calibrated.get("flag_rate", 0)),
             "same events, calibrated thresholds"),
    ])

    options = ['<option value="__all__">All weapons</option>']
    for weapon in weapons:
        options.append('<option value="%s">%s</option>' % (weapon, weapon))

    footer = ("Anomaly percentile is a rank against the trained population, not a probability of "
              "cheating: a dot near the top means the player's windows are more unusual than "
              "almost everyone else's on that weapon. Treat it as a queue for a human look. "
              "Model trained %s on %s events." % (model.get("trained_at", "?"),
                                                 "{:,}".format(model.get("trained_on", {})
                                                               .get("events", 0))))

    payload = {
        "rows": rows,
        "thresholds": thresholds_map,
        "decision_percentile": model.get("decision_percentile", 0.99),
    }
    return PAGE_TEMPLATE % {
        "title": title,
        "meta": meta,
        "tiles": tiles,
        "weapon_options": "".join(options),
        "footer": footer,
        "data": json.dumps(payload, separators=(",", ":")),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description="Anonymized per-player statistics page.")
    parser.add_argument("--logs", nargs="+", default=[os.path.join(HERE, os.pardir, "logs")])
    parser.add_argument("--model", default=os.path.join(HERE, "model.json"))
    parser.add_argument("--config", default=None)
    parser.add_argument("--out", default=os.path.join(HERE, "reports", "player-statistics.html"))
    parser.add_argument("--title", default="MogyAntiCheat - player accuracy distribution")
    args = parser.parse_args(argv)

    if not os.path.exists(args.model):
        log("No model at %s - run train.py first." % args.model)
        return 1
    with open(args.model, "r", encoding="utf-8") as handle:
        model = json.load(handle)

    log("Reading logs...")
    files, events = logparse.load_events(args.logs)
    if not events:
        log("No usable events found.")
        return 1
    log("  %d files, %d events" % (len(files), len(events)))

    baseline = load_current_config(args.config)
    log("Replaying and scoring...")
    pairs, shots = collect(events, baseline, model)
    rows = build_rows(pairs, shots, model.get("decision_percentile", 0.99))
    log("  %d player-weapon pairs with %d+ windows" % (len(rows), MIN_EVALUATIONS))

    # Only weapons with a real population get their own filter entry.
    counts = {}
    for row in rows:
        counts[row["weapon"]] = counts.get(row["weapon"], 0) + 1
    weapons = [w for w, c in sorted(counts.items(), key=lambda kv: -kv[1])
               if c >= MIN_WEAPON_PLAYERS]
    thresholds_map = weapon_thresholds(baseline, model, sorted(counts))

    span = "%s to %s" % (
        dt.datetime.utcfromtimestamp(events[0].ts / 1000.0).strftime("%Y-%m-%d"),
        dt.datetime.utcfromtimestamp(events[-1].ts / 1000.0).strftime("%Y-%m-%d"))
    meta = ("%s &middot; %s events &middot; %d log files &middot; window accuracy measured under "
            "the calibrated config" % (span, "{:,}".format(len(events)), len(files)))

    page = build_page(rows, thresholds_map, model, baseline, weapons, meta, args.title)
    out_dir = os.path.dirname(args.out)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    with open(args.out, "w", encoding="utf-8") as handle:
        handle.write(page)
    log("Wrote %s (%.0f KB)" % (args.out, os.path.getsize(args.out) / 1024.0))
    return 0


if __name__ == "__main__":
    sys.exit(main())
