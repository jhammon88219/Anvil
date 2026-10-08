"""Live-poll timing report — is regime-aware polling still right? (docs/regime-aware-polling.md)

Reads the app's permanent LIVE-POLL TIMING LOG (Usage/live-poll-timing.jsonl, one line per new live frame and per
volume change; written by LivePollTimingRecorder in every build) and prints, per polling mode and VCP:

  model error  = when the frame's last cut LANDED in the bucket - when the model said it would END (+ = upload; ~+1-3 s)
  poll lag     = when our poll FOUND it - when it landed (regime-aware aims ~3 s; fixed 30 s averages ~15)
  screen lag   = when the page PAINTED it - when it landed (what the user waits)
  polls/frame  = checks spent per new frame
  volume length error = real length - the length the planner used (the previous volume's; AVSET varies it)

Usage:  py -3 tools/live_poll_report.py [PATH] [--days N]
PATH defaults to the packaged app's copy, then the unpackaged one.
"""

import json
import os
import sys
from collections import defaultdict
from datetime import datetime, timedelta, timezone

PACKAGED = os.path.expandvars(r"%LOCALAPPDATA%\Packages\Anvil_c78sma71wz9qr\LocalCache\Local\Anvil\Usage\live-poll-timing.jsonl")
UNPACKAGED = os.path.expandvars(r"%LOCALAPPDATA%\Anvil\Usage\live-poll-timing.jsonl")

# What "good" means — from the 2026-10-07 offline measurement (TiltCheck --regime). Retune both together.
TARGET_MODEL_ABS = 3.0     # |median model error|, s
TARGET_REGIME_LAG = 6.0    # median regime-aware poll lag, s


def pct(values, p):
    s = sorted(values)
    if not s:
        return None
    k = (len(s) - 1) * p
    lo = int(k)
    hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)


def stats(values):
    v = [x for x in values if x is not None]
    if not v:
        return "n/a"
    return f"median {pct(v, .5):6.1f}  p90 {pct(v, .9):6.1f}  max {max(v):6.1f}  (n={len(v)})"


def main(argv):
    days = None
    path = None
    i = 0
    while i < len(argv):
        if argv[i] == "--days" and i + 1 < len(argv):
            days = float(argv[i + 1])
            i += 2
            continue
        path = argv[i]
        i += 1
    path = path or (PACKAGED if os.path.exists(PACKAGED) else UNPACKAGED)
    if not os.path.exists(path):
        print(f"no log yet at {path}")
        return 1

    since = datetime.now(timezone.utc) - timedelta(days=days) if days else None
    frames, volumes = [], []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except json.JSONDecodeError:
                continue
            at = datetime.fromisoformat(r["at"].replace("Z", "+00:00"))
            if since and at < since:
                continue
            (frames if r.get("kind") == "frame" else volumes).append(r)

    print(f"== {path}")
    print(f"   {len(frames)} frames, {len(volumes)} volume changes" + (f" in the last {days:g} days" if days else ""))

    groups = defaultdict(list)
    for r in frames:
        groups[(r.get("mode", "?"), r.get("vcp", 0))].append(r)
    for (mode, vcp), rs in sorted(groups.items()):
        sites = sorted({r["site"] for r in rs})
        print(f"\n-- {mode}  VCP {vcp}  ({', '.join(sites[:6])}{'…' if len(sites) > 6 else ''})")
        print(f"   model error   {stats([r.get('modelErrorSec') for r in rs])} s")
        print(f"   poll lag      {stats([r.get('pollLagSec') for r in rs])} s")
        print(f"   screen lag    {stats([r.get('screenLagSec') for r in rs])} s")
        print(f"   polls/frame   {stats([r.get('polls') for r in rs])}")
        undrawn = sum(1 for r in rs if r.get("drawn") is False)
        if undrawn:
            print(f"   ({undrawn} frames never drawn — scrubbed back, or the window was minimized/hidden)")
        unmatched = sum(1 for r in rs if r.get("landedUtc") is None)
        if unmatched:
            print(f"   ({unmatched} frames had no landing time — not matched to the plan, or from the previous volume)")

    if volumes:
        errs = [r["actualLengthSec"] - r["predictedLengthSec"] for r in volumes
                if r.get("actualLengthSec") is not None and r.get("predictedLengthSec") is not None]
        print(f"\n-- volume length: real - used   {stats(errs)} s")

    # The verdict, against the measured targets.
    model = [r.get("modelErrorSec") for r in frames if r.get("modelErrorSec") is not None]
    lag = [r.get("pollLagSec") for r in frames if r.get("mode") == "regime" and r.get("pollLagSec") is not None]
    print("\n== verdict")
    if model:
        m = pct(model, .5)
        print(f"   model error median {m:+.1f} s  -> {'OK' if abs(m) <= TARGET_MODEL_ABS else 'RETUNE (LivePollPlanner constants)'}")
    if lag:
        l = pct(lag, .5)
        print(f"   regime poll lag median {l:.1f} s -> {'OK' if l <= TARGET_REGIME_LAG else 'SLOW (check UploadSlack / retries)'}")
    if not model and not lag:
        print("   not enough data yet")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
