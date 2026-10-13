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
    # Geometry build (radar-decode.js buildGates, logged per frame per product as js.build.{product}.gates / .pack /
    # .verts): the gate loop's ms per frame, its packing ms, and the loop's cost per million vertices (so a loop with
    # more echo on screen doesn't read as slower).
    ("gates_ms_frame",   "Gate loop ms per frame", "ms", -1),
    ("pack_ms_frame",    "Gate pack ms per frame", "ms", -1),
    ("gates_ms_per_mv",  "Gate loop ms per M verts", "ms", -1),
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
    # Radar-site marker repositioning during gestures (radar-sites.js takeUpdateStats, 2026-10-12), pooled over every
    # sampled gesture: total ms / total frames, so long and short drags weigh by their frames.
    ("mk_upd_per_frame", "Marker updates per pan frame", "",  -1),
    ("mk_upd_ms_frame",  "Marker update ms per pan frame", "ms", -1),
    ("mk_upd_us_each",   "Marker update cost each", "us", -1),
    # perf.ui (UiThreadProbe, DEBUG builds): the UI thread's heartbeat, read ONLY over the seconds radar work
    # was running (a load), so idle time after the load can't dilute it. See load_run's perf.ui branch.
    ("ui_windows",       "UI busy seconds",        "s",   0),
    ("ui_late_p95",      "UI tick lateness p95",   "ms", -1),
    ("ui_late_max",      "UI tick lateness max",   "ms", -1),
    ("ui_late50_pct",    "UI ticks >50ms late",    "%",  -1),
    ("ui_msg_ms",        "UI page-message cost",   "ms", -1),
    ("ui_sync_ms",       "UI radar-service sync",  "ms", -1),
    ("ui_svc_hops",      "UI download hops",       "",   -1),
    # Radar readout refreshes (RaiseRadarReadout, bindings included) — per second the loop was up, so a longer
    # run doesn't read as worse.
    ("ui_readouts_ps",   "Readout refreshes /s",   "",   -1),
    ("ui_readout_ms_ps", "Readout cost /s",        "ms", -1),
    ("ui_readout_avg",   "Readout cost each",      "ms", -1),
    # Overlay-feed refreshes (warnings, ...; RadarPerfCounters Begin/EndFeed, 2026-10-12): parse/merge time that ran ON
    # the UI thread, per refresh and the worst single second.
    ("ui_feeds",         "Feed refreshes",         "",    0),
    ("ui_feed_avg",      "UI feed time per refresh", "ms", -1),
    ("ui_feed_max",      "UI feed time worst second", "ms", -1),
    # Dot-matrix scrubber paints (RadarPerfCounters Begin/EndScrub, 2026-10-12): every segment repaint, lit move and
    # re-lay, timed on the UI thread. Totals per run - the workload (one event load) is fixed.
    ("ui_scrubs",        "Scrubber paints",        "",    0),
    ("ui_scrub_ms",      "Scrubber paint time",    "ms", -1),
    ("ui_scrub_avg",     "Scrubber cost per paint", "ms", -1),
    ("ui_scrub_max",     "Scrubber worst second",  "ms", -1),
    # Loop bookkeeping on the page's main thread (perf.book, radar.js ?perf=1, 2026-10-13): the upgrade queue's scans
    # (issue 9) and building the readiness report (issue 10), totals per run; plus the HOST's handling of that report
    # (perf.ui msgBy radarBuildProgress, UI thread).
    ("book_pump_n",      "Upgrade-queue scans",    "",    0),
    ("book_pump_ms",     "Upgrade-queue scan ms",  "ms", -1),
    ("book_pump_max",    "Upgrade-queue scan worst", "ms", -1),
    ("book_prog_n",      "Progress reports built", "",   -1),
    ("book_prog_ms",     "Progress report JS ms",  "ms", -1),
    ("ui_prog_n",        "Progress msgs handled (UI)", "", -1),
    ("ui_prog_ms",       "Progress msg UI ms",     "ms", -1),
    ("ui_prog_avg",      "Progress msg UI ms each", "ms", -1),
    # Legacy .gz streaming (perf.ui gz*, 2026-10-13): per .gz volume streamed - its completeness checks, their gunzip and
    # tilt-walk CPU (pool threads), and how long the DOWNLOAD sat waiting on them.
    ("gz_vols",          ".gz volumes streamed",   "",    0),
    ("gz_checks_pv",     ".gz checks per volume",  "",   -1),
    ("gz_inflate_pv",    ".gz gunzip ms per volume", "ms", -1),
    ("gz_walk_pv",       ".gz walk ms per volume", "ms", -1),
    ("gz_wait_pv",       ".gz download wait per vol", "ms", -1),
    # RadarDiagnostics cost (perf.ui diag*, 2026-10-13, issue 12): a page radarLog line = debugger echo + Gate wait + Gate
    # hold (UI thread); the 2 s report rebuild renders UNDER that Gate (pool thread) - what a log line waits behind.
    ("diag_logs",        "radarLog lines",         "",    0),
    ("diag_log_avg",     "radarLog UI ms each",    "ms", -1),
    ("diag_echo_avg",    "  debugger echo ms each", "ms", -1),
    ("diag_wait_avg",    "  Gate wait ms each",    "ms", -1),
    ("diag_wait_max",    "  Gate wait worst",      "ms", -1),
    ("diag_held_avg",    "  Gate held ms each",    "ms", -1),
    ("diag_reports",     "Report rebuilds",        "",    0),
    ("diag_render_avg",  "Report render ms each", "ms", -1),
    ("diag_render_max",  "Report render worst",    "ms", -1),
    ("diag_write_avg",   "Report write ms each",   "ms", -1),
    ("diag_lists",       "Report list length (end)", "", 0),
    # .NET GC in the app process (perf.ui gc*, 2026-10-13) - lead for the 1-3 s stall at a load's end: a blocking GC
    # stops EVERY thread. "stall" = the seconds whose lateMax >= 500 ms; compare the two stall rows.
    ("gc_n0",            "GC gen0 collections",    "",   -1),
    ("gc_n1",            "GC gen1 collections",    "",   -1),
    ("gc_n2",            "GC gen2 collections",    "",   -1),
    ("gc_blocking2",     "GC blocking gen2s seen", "",   -1),
    ("gc_pause_ms",      "GC pause total",         "ms", -1),
    ("gc_pause_max",     "GC pause worst second",  "ms", -1),
    ("gc_heap_max",      "Managed heap peak",      "MB", -1),
    ("gc_stall_late",    "Stall seconds: lateness", "ms", -1),
    ("gc_stall_pause",   "Stall seconds: GC pause", "ms", -1),
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
    timing_seen, site_timings = set(), defaultdict(list)
    fetch_ms, wait_ms, round_ms, vol_bytes = [], [], [], []
    gate_frames = []  # (gate loop ms, pack ms, verts) per decoded frame, summed over its products
    frames_decoded = suspects = render_errors = render_blanks = 0
    pan_p50, pan_p95, pan_long, mk_attached, mk_shown = [], [], [], [], []
    pan_cadence, pan_long_ms = [], []
    mk_frames, mk_upd, mk_upd_ms = 0, 0, 0.0
    ui_late, ui_ticks, ui_late50, ui_msg, ui_sync, ui_hops = [], 0, 0, 0.0, 0.0, 0
    ui_ro_windows, ui_ro_count, ui_ro_ms = 0, 0, 0.0
    ui_feeds, ui_feed_ms, ui_feed_max = 0, 0.0, 0.0
    feed_by = defaultdict(lambda: [0, 0.0])  # feed -> [refreshes, UI ms]
    ui_scrubs, ui_scrub_ms, ui_scrub_max = 0, 0.0, 0.0
    scrub_by = defaultdict(float)  # kind -> total ms in the run
    gz = defaultdict(float)  # gzVols / gzChecks / gzInflateMs / gzWalkMs / gzWaitMs summed over the run
    diag = defaultdict(float)  # perf.ui diag*: counts + ms summed, *Max maxed, diagLists = latest
    gc = defaultdict(float)  # perf.ui gc*: per-gen counts + pause summed, worst second, blocking gen2s, stall split
    book = defaultdict(float)  # perf.book: pumpN / pumpMs / progN / progMs summed, pumpMax / progMax maxed
    msg_by = defaultdict(lambda: [0, 0.0])  # perf.ui msgBy: type -> [count, ms]
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
                # ⚠️ FIRST per session only. The app logs the milestone again on later events (a routine live
                # update re-logs "live" minutes later), and a run left idle once read "first live frame 239 s".
                key = (e.get("sid"), which)
                if which and isinstance(sec, (int, float)) and key not in timing_seen:
                    timing_seen.add(key)
                    timings[which].append(sec)
                    if e.get("site"):
                        site_timings[(e["site"], which)].append(sec)
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
                # Per-product geometry build (absent on cached frames and in older logs).
                build = js.get("build") or {}
                if build:
                    g = sum((b.get("gates") or 0) for b in build.values() if isinstance(b, dict))
                    p = sum((b.get("pack") or 0) for b in build.values() if isinstance(b, dict))
                    v = sum((b.get("verts") or 0) for b in build.values() if isinstance(b, dict))
                    gate_frames.append((g, p, v))
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
                # Absent before 2026-10-12 (and 0 when no site list was up) - only gestures that carry it count.
                if e.get("mkUpd") is not None and e.get("n"):
                    mk_frames += e["n"]
                    mk_upd += e["mkUpd"]
                    mk_upd_ms += e.get("mkUpdMs") or 0
            elif cat == "perf.book":
                for k in ("pumpN", "pumpMs", "progN", "progMs"):
                    book[k] += e.get(k) or 0
                for k in ("pumpMax", "progMax"):
                    book[k] = max(book[k], e.get(k) or 0)
            elif cat == "perf.ui":
                # Only the seconds radar work was RUNNING (a CPU job, a UI-thread download hop, or service work
                # timed on the UI thread). The probe also writes idle baseline windows; counting those would
                # make a run with a longer idle tail look healthier. svcHops is absent before 2026-10-12.
                # readouts (2026-10-12) widens "busy" to every second a loop is UP — playback is the workload for the
                # readout fix. Logs without the field keep the old meaning.
                hops = e.get("svcHops") or 0
                readouts = e.get("readouts") or 0
                for part in (e.get("msgBy") or "").split():  # absent before 2026-10-13
                    name, _, rest = part.rpartition(":")
                    n_s, _, ms_s = rest.partition("/")
                    try:
                        msg_by[name][0] += int(n_s)
                        msg_by[name][1] += float(ms_s)
                    except ValueError:
                        pass
                for k in ("gzVols", "gzChecks", "gzInflateMs", "gzWalkMs", "gzWaitMs"):  # absent before 2026-10-13
                    gz[k] += e.get(k) or 0
                # .NET GC in the app process (absent before 2026-10-13): totals, the worst second, blocking gen2s, and how
                # much of the STALL seconds (lateMax >= 500) the GC pause explains.
                for k in ("gc0", "gc1", "gc2", "gcPauseMs"):
                    if k in e:  # keyed only once a log carries it, so older runs read "-" not 0
                        gc[k] += e[k] or 0
                gc["pauseMax"] = max(gc["pauseMax"], e.get("gcPauseMs") or 0)
                gc["heapMax"] = max(gc["heapMax"], e.get("gcHeapMb") or 0)
                if e.get("gcLastGen") == 2 and e.get("gcLastConc") is False:
                    gc["blocking2"] += 1
                if (e.get("lateMax") or 0) >= 500 and "gcPauseMs" in e:
                    gc["stallLate"] += e["lateMax"]
                    gc["stallPause"] += e.get("gcPauseMs") or 0
                for k in ("diagLogs", "diagEchoMs", "diagWaitMs", "diagHeldMs", "diagReports", "diagRenderMs",
                          "diagWriteMs"):  # absent before 2026-10-13 (issue 12)
                    diag[k] += e.get(k) or 0
                for k in ("diagWaitMax", "diagRenderMax"):
                    diag[k] = max(diag[k], e.get(k) or 0)
                if e.get("diagLists"):
                    diag["diagLists"] = e["diagLists"]
                scrubs = e.get("scrubs") or 0  # absent before 2026-10-12
                if scrubs > 0:
                    sm = e.get("scrubMs") or 0
                    ui_scrubs += scrubs
                    ui_scrub_ms += sm
                    ui_scrub_max = max(ui_scrub_max, sm)
                # Per kind ("paint:30/12.0 lit:9/1.1 relay:1/40.2" = count/ms; absent in the first issue-6 runs).
                for part in (e.get("scrubBy") or "").split():
                    name, _, rest = part.partition(":")
                    try:
                        scrub_by[name] += float(rest.partition("/")[2])
                    except ValueError:
                        pass
                feeds = e.get("feeds") or 0  # absent before 2026-10-12
                # Per feed ("warnings:2/14.1 outlook:1/3.0" = refreshes/ms; absent before the per-feed split).
                for part in (e.get("feedBy") or "").split():
                    name, _, rest = part.partition(":")
                    n_s, _, ms_s = rest.partition("/")
                    try:
                        feed_by[name][0] += int(n_s)
                        feed_by[name][1] += float(ms_s)
                    except ValueError:
                        pass
                if feeds > 0:
                    fm = e.get("feedMs") or 0
                    ui_feeds += feeds
                    ui_feed_ms += fm
                    ui_feed_max = max(ui_feed_max, fm)
                if not ((e.get("jobsPeak") or 0) > 0 or hops > 0 or (e.get("syncMs") or 0) > 0 or readouts > 0):
                    continue
                if readouts > 0:
                    ui_ro_windows += 1
                    ui_ro_count += readouts
                    ui_ro_ms += e.get("readoutMs") or 0
                ui_late.append(e.get("lateMax") or 0)
                ui_ticks += e.get("ticks") or 0
                ui_late50 += e.get("late50") or 0
                ui_msg += e.get("msgMs") or 0
                ui_sync += e.get("syncMs") or 0
                ui_hops += hops

    dm = sorted(decode_ms)
    ul = sorted(ui_late)
    return {
        "_path": path,
        "_stamp": stamp,
        "_sites": sorted(sites),
        "_sessions": sessions,
        "_first_ts": first_ts,
        "_last_ts": last_ts,
        "_skipped": skipped,
        # Per-SITE load timings ({"KDVN first": s, ...}). The pooled t_first/t_all medians mix every load in the run
        # (the home site's live load beside the replays), so one missing load swings them; these compare like with like.
        "_site_timings": {"{} {}".format(s, w): statistics.median(v) for (s, w), v in site_timings.items()},
        # Per-FEED UI-thread time: ms per refresh where the feed counts refreshes, else the run's total ms (a reader
        # with no refresh of its own, e.g. outlookRead = the outlook VMs' cache reads).
        "_feed_ms": {("{} ms/refresh".format(k) if n else "{} ms total".format(k)): (ms / n if n else ms)
                     for k, (n, ms) in feed_by.items()},
        "_scrub_ms": {"{} ms total".format(k): v for k, v in scrub_by.items()},
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
        "gates_ms_frame": statistics.median(g for g, _, _ in gate_frames) if gate_frames else None,
        "pack_ms_frame": statistics.median(p for _, p, _ in gate_frames) if gate_frames else None,
        "gates_ms_per_mv": (round(1e6 * sum(g for g, _, _ in gate_frames) / sum(v for _, _, v in gate_frames), 1)
                            if gate_frames and sum(v for _, _, v in gate_frames) else None),
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
        "mk_upd_per_frame": round(mk_upd / mk_frames, 1) if mk_frames else None,
        "mk_upd_ms_frame": round(mk_upd_ms / mk_frames, 3) if mk_frames else None,
        "mk_upd_us_each": round(1000.0 * mk_upd_ms / mk_upd, 2) if mk_upd else None,
        "ui_windows": len(ul) if ul else None,
        "ui_late_p95": pct(ul, 0.95),
        "ui_late_max": ul[-1] if ul else None,
        "ui_late50_pct": (100.0 * ui_late50 / ui_ticks) if ui_ticks else None,
        "ui_msg_ms": round(ui_msg, 1) if ul else None,
        "ui_sync_ms": round(ui_sync, 1) if ul else None,
        "ui_svc_hops": ui_hops if ul else None,
        "ui_readouts_ps": round(ui_ro_count / ui_ro_windows, 2) if ui_ro_windows else None,
        "ui_readout_ms_ps": round(ui_ro_ms / ui_ro_windows, 2) if ui_ro_windows else None,
        "ui_readout_avg": round(ui_ro_ms / ui_ro_count, 3) if ui_ro_count else None,
        "ui_feeds": ui_feeds if ui_feeds else None,
        "ui_feed_avg": round(ui_feed_ms / ui_feeds, 2) if ui_feeds else None,
        "ui_feed_max": round(ui_feed_max, 1) if ui_feeds else None,
        "book_pump_n": int(book["pumpN"]) if book else None,
        "book_pump_ms": round(book["pumpMs"], 1) if book else None,
        "book_pump_max": round(book["pumpMax"], 2) if book else None,
        "book_prog_n": int(book["progN"]) if book else None,
        "book_prog_ms": round(book["progMs"], 1) if book else None,
        "ui_prog_n": msg_by["radarBuildProgress"][0] if "radarBuildProgress" in msg_by else None,
        "ui_prog_ms": round(msg_by["radarBuildProgress"][1], 1) if "radarBuildProgress" in msg_by else None,
        "ui_prog_avg": (round(msg_by["radarBuildProgress"][1] / msg_by["radarBuildProgress"][0], 3)
                        if msg_by.get("radarBuildProgress", [0])[0] else None),
        "_msg_ms": {"{} ms".format(k): v[1] for k, v in msg_by.items()},
        "gc_n0": int(gc["gc0"]) if "gc0" in gc else None,
        "gc_n1": int(gc["gc1"]) if "gc0" in gc else None,
        "gc_n2": int(gc["gc2"]) if "gc0" in gc else None,
        "gc_blocking2": int(gc["blocking2"]) if "gc0" in gc else None,
        "gc_pause_ms": round(gc["gcPauseMs"], 1) if "gc0" in gc else None,
        "gc_pause_max": round(gc["pauseMax"], 1) if "gc0" in gc else None,
        "gc_heap_max": round(gc["heapMax"]) if "gc0" in gc else None,
        "gc_stall_late": round(gc["stallLate"]) if "gc0" in gc else None,
        "gc_stall_pause": round(gc["stallPause"]) if "gc0" in gc else None,
        "diag_logs": int(diag["diagLogs"]) if diag["diagLogs"] else None,
        "diag_log_avg": (round((diag["diagEchoMs"] + diag["diagWaitMs"] + diag["diagHeldMs"]) / diag["diagLogs"], 3)
                         if diag["diagLogs"] else None),
        "diag_echo_avg": round(diag["diagEchoMs"] / diag["diagLogs"], 3) if diag["diagLogs"] else None,
        "diag_wait_avg": round(diag["diagWaitMs"] / diag["diagLogs"], 3) if diag["diagLogs"] else None,
        "diag_wait_max": round(diag["diagWaitMax"], 1) if diag["diagLogs"] else None,
        "diag_held_avg": round(diag["diagHeldMs"] / diag["diagLogs"], 3) if diag["diagLogs"] else None,
        "diag_reports": int(diag["diagReports"]) if diag["diagReports"] else None,
        "diag_render_avg": round(diag["diagRenderMs"] / diag["diagReports"], 2) if diag["diagReports"] else None,
        "diag_render_max": round(diag["diagRenderMax"], 1) if diag["diagReports"] else None,
        "diag_write_avg": round(diag["diagWriteMs"] / diag["diagReports"], 2) if diag["diagReports"] else None,
        "diag_lists": int(diag["diagLists"]) if diag["diagLists"] else None,
        "gz_vols": int(gz["gzVols"]) if gz["gzVols"] else None,
        "gz_checks_pv": round(gz["gzChecks"] / gz["gzVols"], 1) if gz["gzVols"] else None,
        "gz_inflate_pv": round(gz["gzInflateMs"] / gz["gzVols"], 1) if gz["gzVols"] else None,
        "gz_walk_pv": round(gz["gzWalkMs"] / gz["gzVols"], 1) if gz["gzVols"] else None,
        "gz_wait_pv": round(gz["gzWaitMs"] / gz["gzVols"], 1) if gz["gzVols"] else None,
        "ui_scrubs": ui_scrubs if ui_scrubs else None,
        "ui_scrub_ms": round(ui_scrub_ms, 1) if ui_scrubs else None,
        "ui_scrub_avg": round(ui_scrub_ms / ui_scrubs, 3) if ui_scrubs else None,
        "ui_scrub_max": round(ui_scrub_max, 1) if ui_scrubs else None,
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


def print_keyed(before_runs, after_runs, field, title):
    """A before/after table over a run's {name: ms} dict (lower is better), keys seen on EITHER side."""
    keys = sorted(set().union(*(r[field] for r in before_runs)) | set().union(*(r[field] for r in after_runs)))
    if not keys:
        return
    print()
    print("  {:<26} {:>16} {:>16} {:>18}  Verdict".format(title, "Before", "After", "Delta"))
    print("  {} {} {} {}  {}".format("-" * 26, "-" * 16, "-" * 16, "-" * 18, "-" * 7))
    for k in keys:
        bvals = [r[field][k] for r in before_runs if k in r[field]]
        avals = [r[field][k] for r in after_runs if k in r[field]]
        bmed = statistics.median(bvals) if bvals else None
        amed = statistics.median(avals) if avals else None
        blo, bhi = (min(bvals), max(bvals)) if bvals else (None, None)
        bcell = fmt(bmed, "ms") + (" +/-{:,.1f}".format((bhi - blo) / 2) if len(bvals) >= 2 and blo != bhi else "")
        arrow, v = verdict(bmed, amed, blo, bhi, -1, len(bvals))
        print("  {:<26} {:>16} {:>16} {:>18}  {}".format(k, bcell, fmt(amed, "ms"), arrow, v))


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
    # Per-site load timings: the like-for-like read of the load rows above (only sites loaded on BOTH sides).
    keys = sorted(set().union(*(r["_site_timings"] for r in before_runs)) &
                  set().union(*(r["_site_timings"] for r in after_runs)))
    if keys:
        print()
        print("  {:<26} {:>16} {:>16} {:>18}  Verdict".format("Load, per site", "Before", "After", "Delta"))
        print("  {} {} {} {}  {}".format("-" * 26, "-" * 16, "-" * 16, "-" * 18, "-" * 7))
        for k in keys:
            bvals = [r["_site_timings"][k] for r in before_runs if k in r["_site_timings"]]
            avals = [r["_site_timings"][k] for r in after_runs if k in r["_site_timings"]]
            bmed, blo, bhi = statistics.median(bvals), min(bvals), max(bvals)
            amed = statistics.median(avals)
            bcell = fmt(bmed, "s") + (" +/-{:,.1f}".format((bhi - blo) / 2) if len(bvals) >= 2 and blo != bhi else "")
            arrow, v = verdict(bmed, amed, blo, bhi, -1, len(bvals))
            print("  {:<26} {:>16} {:>16} {:>18}  {}".format(k, bcell, fmt(amed, "s"), arrow, v))
    # Per-feed UI-thread time (perf.ui feedBy): which feed a "UI feed time" change came from. Feeds seen on EITHER side
    # — a feed whose time went to zero after an off-thread fix still logs its refreshes, so it stays in the table.
    print_keyed(before_runs, after_runs, "_feed_ms", "UI feed time, per feed")
    # Per-kind scrubber paint time (perf.ui scrubBy): which entry point a "Scrubber paint time" change came from.
    print_keyed(before_runs, after_runs, "_scrub_ms", "Scrubber ms, per kind")
    # Per page-message type UI handling ms (perf.ui msgBy): which message a "UI page-message cost" change came from.
    print_keyed(before_runs, after_runs, "_msg_ms", "UI message ms, per type")
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
