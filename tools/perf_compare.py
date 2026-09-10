#!/usr/bin/env python3
"""perf_compare.py - before/after scoreboard for Anvil performance work.

Reads the radar diagnostics JSONL files the app already writes, reduces each run to a handful of
numbers, then diffs two SETS of runs and says whether a change is real or inside the noise.

Why sets and not single files: live radar, and even a fixed replay, vary run to run (disk cache state,
what else the machine is doing). One "before" and one "after" cannot tell a 5% win from a 5% wobble.
Give it three runs a side and it reports the spread, then flags any delta landing inside the
before-spread as `noise` rather than letting you read it as a result.

    # capture a baseline: run the SAME fixed PastCast replay 3x, then
    py -3.12 tools/perf_compare.py --before b1.jsonl b2.jsonl b3.jsonl --after a1.jsonl a2.jsonl a3.jsonl

    py -3.12 tools/perf_compare.py run.jsonl     # one run, just summarise it
    py -3.12 tools/perf_compare.py --list        # what runs exist, newest first

Stdlib only, deliberately - unlike radar_reference.py / dealias_check.py this needs no venv, so it can
run against a log the moment the app closes.

*** THE WORKLOAD MATTERS MORE THAN THIS SCRIPT. ***
Compare like with like or the numbers are fiction. Use a FIXED PastCast replay (same site, date, start
time, duration): the archive is immutable, so the same bytes decode every run. PastCast persists its
selection, so a benchmark run is "open the app, press Load, wait for the scrubber to fill, hop three
sites, close". Benchmarking against live radar measures the weather, not the code.
"""

import argparse
import json
import os
import statistics
import sys
from collections import defaultdict

# -- metric registry --------------------------------------------------------------------------------
# key -> (label, unit, direction). direction: -1 lower is better, +1 higher is better, 0 informational
# (reported, never given a verdict - a frame count is context for the other rows, not a score).
METRICS = [
    ("peak_retained_mb", "Peak retained geometry", "MB", -1),
    ("peak_heap_mb",     "Peak JS heap used",      "MB", -1),
    ("heap_limit_mb",    "JS heap limit",          "MB",  0),
    ("peak_frames",      "Peak frames in loop",    "",    0),
    ("peak_cached",      "Peak cached frames",     "",    0),
    ("t_first",          "First paint",            "s",  -1),
    ("t_all",            "Full loop loaded",       "s",  -1),
    ("t_live",           "First live frame",       "s",  -1),
    ("decode_p50",       "Frame decode p50",       "ms", -1),
    ("decode_p95",       "Frame decode p95",       "ms", -1),
    ("volume_mb",        "Volume bytes decoded",   "MB", -1),
    ("fetch_p50",        "Frame fetch p50",        "ms", -1),
    ("wait_p50",         "Queued behind decode p50", "ms", -1),
    ("round_p50",        "Frame dispatch->arrival p50", "ms", -1),
    ("frames_decoded",   "Frames decoded",         "",    0),
    ("suspects",         "Suspect frames",         "",   -1),
    ("render_errors",    "Render errors",          "",   -1),
    ("render_blanks",    "Render blanks",          "",   -1),
    ("pan_cadence",      "Display cadence",        "ms",  0),
    ("pan_p50",          "Pan frame time p50",     "ms", -1),
    ("pan_p95",          "Pan frame time p95",     "ms", -1),
    ("pan_long_ms",      "Long-frame threshold",   "ms",  0),
    ("pan_long_pct",     "Pan long frames",        "%",  -1),
    ("pan_samples",      "Pan gestures sampled",   "",    0),
    ("mk_attached",      "Markers attached",       "",    0),
    ("mk_shown",         "Markers shown",          "",    0),
]


def pct(sorted_vals, q):
    """Nearest-rank percentile. Mirrors perf-probe.js quantile() so the two agree."""
    if not sorted_vals:
        return None
    i = min(len(sorted_vals) - 1, max(0, round((len(sorted_vals) - 1) * q)))
    return sorted_vals[i]


def load_run(path):
    """Reduce one JSONL run to the metric dict above.

    Tolerates truncated lines: the app appends on a 2 s flush timer, so a log from a hard-killed
    session can end mid-line. A bad line is counted and skipped, never fatal - a partial run is still
    worth reading, and refusing to parse one is how you lose the run that actually crashed.
    """
    mem_retained, mem_heap, mem_frames, mem_cached, heap_limits = [], [], [], [], []
    decode_ms, timings = [], defaultdict(list)
    fetch_ms, wait_ms, round_ms, vol_bytes = [], [], [], []
    frames_decoded = suspects = render_errors = render_blanks = 0
    pan_p50, pan_p95, pan_long, mk_attached, mk_shown = [], [], [], [], []
    pan_cadence, pan_long_ms = [], []
    sites, sessions, stamp = set(), 0, None
    first_ts = last_ts = None
    skipped = 0

    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                e = json.loads(line)
            except ValueError:
                skipped += 1
                continue

            cat = e.get("cat")
            ts = e.get("ts")
            if ts:
                if first_ts is None:
                    first_ts = ts
                last_ts = ts
            if e.get("site"):
                sites.add(e["site"])

            if cat == "run.start":
                stamp = e.get("stamp")
            elif cat == "session.start":
                sessions += 1
            elif cat == "memory":
                mem_retained.append(e.get("retainedMb", 0))
                mem_heap.append(e.get("heapMb", 0))
                mem_frames.append(e.get("frames", 0))
                mem_cached.append(e.get("cached", 0))
                # -1 is the sampler marker for "performance.memory unavailable", not a reading.
                if e.get("heapLimitMb", -1) > 0:
                    heap_limits.append(e["heapLimitMb"])
            elif cat == "timing":
                which, sec = e.get("which"), e.get("sec")
                if which and isinstance(sec, (int, float)):
                    timings[which].append(sec)
            elif cat == "frame":
                frames_decoded += 1
                if e.get("suspect"):
                    suspects += 1
                # The raw JS payload is nested under "js" - decodeMs lives there, not at top level.
                js = e.get("js") or {}
                if isinstance(js.get("decodeMs"), (int, float)):
                    decode_ms.append(js["decodeMs"])
                # Phase split (added 2026-09-08). decodeMs alone hid where a frame's wall clock went;
                # absent in older runs, which is why every one of these is guarded rather than defaulted.
                for key, sink in (("fetchMs", fetch_ms), ("waitMs", wait_ms), ("roundMs", round_ms)):
                    if isinstance(js.get(key), (int, float)):
                        sink.append(js[key])
                if isinstance(js.get("bytes"), (int, float)) and js["bytes"] > 0:
                    vol_bytes.append(js["bytes"])
            elif cat == "render":
                js = e.get("js") or {}
                # radar.js rate-limits these and carries running TOTALS, so take the max, never a count.
                render_errors = max(render_errors, js.get("errs") or 0)
                render_blanks = max(render_blanks, js.get("blanks") or 0)
            elif cat == "perf.pan":
                pan_p50.append(e.get("p50", 0))
                pan_p95.append(e.get("p95", 0))
                pan_long.append(e.get("longPct", 0))
                # The calibration each sample was judged against. Without these, longPct cannot be
                # compared across machines - or even across runs on one machine, if a display changes.
                if e.get("cadence"):
                    pan_cadence.append(e["cadence"])
                if e.get("longMs"):
                    pan_long_ms.append(e["longMs"])
                if e.get("mkAttached"):
                    mk_attached.append(e["mkAttached"])
                if e.get("mkShown") is not None:
                    mk_shown.append(e["mkShown"])

    dm = sorted(decode_ms)
    return {
        "_path": path,
        "_stamp": stamp,
        "_sites": sorted(sites),
        "_sessions": sessions,
        "_first_ts": first_ts,
        "_last_ts": last_ts,
        "_skipped": skipped,
        "peak_retained_mb": max(mem_retained) if mem_retained else None,
        "peak_heap_mb": max(mem_heap) if mem_heap else None,
        "heap_limit_mb": statistics.median(heap_limits) if heap_limits else None,
        "peak_frames": max(mem_frames) if mem_frames else None,
        "peak_cached": max(mem_cached) if mem_cached else None,
        # Median across sessions: one run can load several loops, and the interesting number is the
        # typical load, not whichever happened to go first.
        "t_first": statistics.median(timings["first"]) if timings["first"] else None,
        "t_all": statistics.median(timings["all"]) if timings["all"] else None,
        "t_live": statistics.median(timings["live"]) if timings["live"] else None,
        "decode_p50": pct(dm, 0.50),
        "decode_p95": pct(dm, 0.95),
        # Median volume size actually decoded per frame. A legacy PastCast volume ships ~43 MB to render
        # one tilt against ~7 MB for a live single-tilt frame - so this row is the one that says whether
        # single-tilt extraction landed, and decode_p50 is the row that says what it bought.
        "volume_mb": (statistics.median(vol_bytes) / 1048576.0) if vol_bytes else None,
        "fetch_p50": statistics.median(fetch_ms) if fetch_ms else None,
        "wait_p50": statistics.median(wait_ms) if wait_ms else None,
        "round_p50": statistics.median(round_ms) if round_ms else None,
        "frames_decoded": frames_decoded,
        "suspects": suspects,
        "render_errors": render_errors,
        "render_blanks": render_blanks,
        "pan_p50": statistics.median(pan_p50) if pan_p50 else None,
        "pan_p95": statistics.median(pan_p95) if pan_p95 else None,
        "pan_long_pct": statistics.median(pan_long) if pan_long else None,
        "pan_cadence": statistics.median(pan_cadence) if pan_cadence else None,
        "pan_long_ms": statistics.median(pan_long_ms) if pan_long_ms else None,
        "pan_samples": len(pan_p50),
        "mk_attached": statistics.median(mk_attached) if mk_attached else None,
        "mk_shown": statistics.median(mk_shown) if mk_shown else None,
    }


def aggregate(runs, key):
    """(median, lo, hi, n) across runs for one metric, ignoring runs that lack it."""
    vals = [r[key] for r in runs if r.get(key) is not None]
    if not vals:
        return None, None, None, 0
    return statistics.median(vals), min(vals), max(vals), len(vals)


def fmt(v, unit):
    if v is None:
        return "-"
    if isinstance(v, float) and not v.is_integer():
        return "{:,.1f}{}".format(v, unit)
    return "{:,}{}".format(int(v), unit)


def verdict(before, after, blo, bhi, direction, n_before):
    """BETTER / WORSE / noise / flat for one metric.

    `noise` is the important verdict, and the reason multiple baseline runs are worth the time: if the
    after-median lands inside the range the before runs already covered on their own, the change is not
    distinguishable from run-to-run variation - however big the percentage looks.
    """
    if before is None or after is None:
        return "", ""
    delta = after - before
    ratio = (delta / before * 100) if before else 0.0
    arrow = "{:+,.1f} ({:+.0f}%)".format(delta, ratio) if before else "{:+,.1f}".format(delta)
    if direction == 0:
        return arrow, ""
    # ⚠️ A ZERO BASELINE HAS NO PERCENTAGE, AND THE PERCENTAGE IS WHAT THE TESTS BELOW READ. Without this,
    # `ratio` stays 0.0 for any move off zero and the row is reported "flat" — so 0 -> 6 suspect frames, or
    # 0 -> N render errors, printed as though nothing happened. Those are exactly the rows that must shout:
    # the counters that are normally zero are the correctness ones. Judge them on direction alone.
    if before == 0 and delta != 0:
        improved = (delta < 0) if direction < 0 else (delta > 0)
        return arrow, "BETTER" if improved else "WORSE"
    # An exact tie is flat, not noise. Counters that are 0 in both runs (suspects, render errors) sit
    # inside their own zero-width before-spread, so the noise test below would call every clean run
    # "noise" - which reads as "we could not tell", when in fact nothing happened at all.
    if delta == 0:
        return arrow, "flat"
    if n_before >= 2 and blo is not None and blo <= after <= bhi:
        return arrow, "noise"
    if abs(ratio) < 2:
        return arrow, "flat"
    improved = (delta < 0) if direction < 0 else (delta > 0)
    return arrow, "BETTER" if improved else "WORSE"


def describe(runs, title):
    print("{}: {} run(s)".format(title, len(runs)))
    for r in runs:
        sites = ",".join(r["_sites"][:4]) + ("..." if len(r["_sites"]) > 4 else "")
        note = "  [{} unparsable line(s)]".format(r["_skipped"]) if r["_skipped"] else ""
        print("  {}  stamp={}  sessions={}  sites={}{}".format(
            os.path.basename(r["_path"]), r["_stamp"], r["_sessions"], sites or "-", note))


def print_single(run):
    describe([run], "Run")
    print()
    print("  {:<26} {:>14}".format("Metric", "Value"))
    print("  {} {}".format("-" * 26, "-" * 14))
    for key, label, unit, _ in METRICS:
        print("  {:<26} {:>14}".format(label, fmt(run[key], unit)))
    if not run["pan_samples"]:
        print()
        print("  No pan samples. The probe needs a DEBUG build (?perf=1) and an actual drag or zoom.")


def print_compare(before_runs, after_runs):
    describe(before_runs, "BEFORE")
    print()
    describe(after_runs, "AFTER")
    print()
    print("  {:<26} {:>16} {:>16} {:>18}  Verdict".format("Metric", "Before", "After", "Delta"))
    print("  {} {} {} {}  {}".format("-" * 26, "-" * 16, "-" * 16, "-" * 18, "-" * 7))
    for key, label, unit, direction in METRICS:
        bmed, blo, bhi, bn = aggregate(before_runs, key)
        amed, _alo, _ahi, _an = aggregate(after_runs, key)
        bcell = fmt(bmed, unit)
        if bn >= 2 and blo != bhi:
            bcell += " +/-{:,.0f}".format((bhi - blo) / 2)
        arrow, v = verdict(bmed, amed, blo, bhi, direction, bn)
        print("  {:<26} {:>16} {:>16} {:>18}  {}".format(label, bcell, fmt(amed, unit), arrow, v))
    print()
    print("  Verdicts: BETTER/WORSE = moved more than 2% AND outside the before-spread.")
    print("            noise = the after value sits inside the range the before runs already covered.")
    print("            flat  = moved less than 2%.")
    print()
    print("  Read the memory rows WITH the timing rows. The easiest way to 'fix' memory is to make")
    print("  something slower, and a win on retained MB paid for with a worse first paint is a loss.")


def default_diag_dirs():
    local = os.environ.get("LOCALAPPDATA", "")
    if not local:
        return []
    return [
        # Packaged (MSIX) - how the app actually runs. Package family name per CLAUDE.md.
        os.path.join(local, "Packages", "Anvil_c78sma71wz9qr", "LocalCache", "Local",
                     "Anvil", "RadarLevel2", "Diagnostics"),
        # Unpackaged fallback.
        os.path.join(local, "Anvil", "RadarLevel2", "Diagnostics"),
    ]


def list_runs():
    found = False
    for d in default_diag_dirs():
        if not os.path.isdir(d):
            continue
        found = True
        files = sorted((f for f in os.listdir(d)
                        if f.startswith("radar-diag-") and f.endswith(".jsonl")), reverse=True)
        print()
        print(d)
        if not files:
            print("  (no runs)")
        for f in files[:25]:
            size = os.path.getsize(os.path.join(d, f)) / 1024.0
            print("  {:9,.0f} KB  {}".format(size, f))
    if not found:
        print("No diagnostics folder found. Launch the app once, then retry.")
        for d in default_diag_dirs():
            print("  looked in: {}".format(d))


def main():
    ap = argparse.ArgumentParser(
        description="Before/after performance scoreboard from Anvil radar diagnostics JSONL.",
        epilog="Compare like with like: use a FIXED PastCast replay for every run.")
    ap.add_argument("run", nargs="?", help="a single JSONL to summarise")
    ap.add_argument("--before", nargs="+", metavar="JSONL", help="baseline run(s)")
    ap.add_argument("--after", nargs="+", metavar="JSONL", help="post-change run(s)")
    ap.add_argument("--list", action="store_true", help="list diagnostics runs on this machine")
    ap.add_argument("--json", action="store_true", help="emit raw per-run metrics as JSON")
    args = ap.parse_args()

    if args.list:
        list_runs()
        return 0

    if args.before and args.after:
        before = [load_run(p) for p in args.before]
        after = [load_run(p) for p in args.after]
        if args.json:
            print(json.dumps({"before": before, "after": after}, indent=2, default=str))
        else:
            print_compare(before, after)
        if len(before) < 2:
            print()
            print("  One baseline run cannot establish a noise floor. Capture three;")
            print("  every verdict above is provisional until you do.")
        return 0

    if args.run:
        run = load_run(args.run)
        if args.json:
            print(json.dumps(run, indent=2, default=str))
        else:
            print_single(run)
        return 0

    ap.print_help()
    return 1


if __name__ == "__main__":
    sys.exit(main())
