#!/usr/bin/env python3
"""
kdp_scorecard.py - score OUR KDP estimator against the NWS's own operational answer (Level III N0K).

KDP is the one product with NO Level II counterpart: it is not a transmitted moment, so both we and the
ORPG DERIVE it from PhiDP. That makes N0K a genuine answer key rather than a second opinion - it is the
same storm, the same volume time, run through the operational algorithm. Nothing else in the radar path
has ground truth this direct.

WHAT THIS SCORES, AND WHAT IT DOES NOT
--------------------------------------
It scores `dualpol_check.kdp_mirror`, the Python port of radar-decode.js `kdpFromPhi` - NOT the shipped
JavaScript. There is no node/deno/bun on this machine, so the JS cannot be executed here. The mirror is
kept in sync with the JS by rule (see CLAUDE.md and docs/radar-products-history.md); if you change one
without the other, this tool silently scores the wrong thing. That is the standing risk of this design.

EXPECTED DIFFERENCES THAT ARE NOT ERRORS (docs/radar/ Brief B, Q3)
-----------------------------------------------------------------
  * Level III is 8-bit quantized: N0K min -2.0 deg/km, increment 0.05 (MEASURED, not assumed - see
    --show-encoding). Our floats differ from the L3 bin by up to half a step even when the physics agrees.
  * L3 dual-pol base products are 1 degree in azimuth; our super-res sweep is 0.5. We average into the
    coarser bins, which blurs fine structure.
  * L3 truncates at 230 km; we run further out.
  * L3 applies its own masks before quantization, so either side can have data where the other does not.
    Only co-valid gates are scored.
So the bar is STRUCTURAL, never gate-for-gate identity. READ THE CORRELATION FIRST: KDP is a range
DERIVATIVE of a noisy phase, so two implementations disagree by many quantization steps on individual
gates even when both are right. A tolerance test is the wrong instrument here and the tool says so in
its own output.
⚠️ Correlation over ALL gates is misleading in the other direction: the field is near zero almost
everywhere, so light-rain noise dominates it. Judge on r restricted to real signal (the tool reports
coreMAE on |N0K| >= 1.0; the --diagnose path reports r over |N0K| >= 0.5).

USAGE
  py -3.12 kdp_scorecard.py                    # score every corpus volume
  py -3.12 kdp_scorecard.py --compare-windows  # ALSO score the pre-2026-09-10 single-window KDP, to
                                               #   measure whether the two-window change moved us toward
                                               #   the operational answer. This is the tool's main job.
  py -3.12 kdp_scorecard.py --volume KILX_20260812_010848.V06
  py -3.12 kdp_scorecard.py --show-encoding    # print the N0K quantization actually observed

Level III products are fetched from the `unidata-nexrad-level3` bucket and cached under the system temp
dir. NOTE the bucket is "real-time SELECT" data: N0K and N0C are carried, N0Q is NOT (verified
2026-09-10), and retention is undocumented - measured at least ~2 months, which covers the current corpus.
If a corpus volume ever stops resolving, that is retention expiring, not a bug.

Does NOT touch the C# app.
"""
import argparse
import datetime
import glob
import os
import sys
import tempfile
import urllib.parse
import urllib.request
import warnings
import xml.etree.ElementTree as ET

import numpy as np

warnings.filterwarnings("ignore")
import pyart  # noqa: E402

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dualpol_check as dp  # noqa: E402

CORPUS = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                      "..", "Anvil.App", "Assets", "RadarCorpus")
BUCKET = "https://unidata-nexrad-level3.s3.amazonaws.com/"
S3NS = "{http://s3.amazonaws.com/doc/2006-03-01/}"
PRODUCT = "N0K"
CACHE = os.path.join(tempfile.gettempdir(), "anvil_l3_cache")
MAX_OFFSET_S = 300          # a product further than this from the volume time is a different scan
MIN_CORE_GATES = 200        # below this, coreMAE is a handful of gates and reports nothing; the corpus
                            # is quiet-day volumes, so most have single-digit core counts
L3_STEP = 0.05              # deg/km, the N0K quantization step (measured; see --show-encoding)


# ---------------------------------------------------------------- Level III fetch

def three_letter(icao):
    """BUF from KBUF. Last three chars, matching Level3NvwProvider.ToThreeLetterSite."""
    return icao.strip().upper()[-3:]


def list_keys(site3, day, product=None):
    """Level III keys for one site/day/product. `product` defaults to this tool's N0K; the dual-pol
    scorecard passes N0C / N0X through the same plumbing."""
    prefix = f"{site3}_{product or PRODUCT}_{day:%Y_%m_%d}_"
    url = f"{BUCKET}?list-type=2&prefix={urllib.parse.quote(prefix)}&max-keys=1000"
    with urllib.request.urlopen(url, timeout=60) as r:
        root = ET.fromstring(r.read())
    out = []
    for c in root.findall(S3NS + "Contents"):
        k = c.find(S3NS + "Key").text
        try:
            out.append((k, datetime.datetime.strptime(k.split("_", 2)[2], "%Y_%m_%d_%H_%M_%S")))
        except (ValueError, IndexError):
            pass
    return out


def fetch_nearest(icao, when, product=None):
    """The Level III product nearest `when`, or None. A volume near 00Z can be filed under the
    previous day, so both are listed."""
    site3 = three_letter(icao)
    keys = []
    for day in (when.date() - datetime.timedelta(days=1), when.date()):
        try:
            keys += list_keys(site3, day, product)
        except Exception as e:
            print(f"    listing failed for {site3} {day} {product or PRODUCT}: {type(e).__name__}: {e}")
    if not keys:
        return None, None
    key, stamp = min(keys, key=lambda kv: abs((kv[1] - when).total_seconds()))
    offset = (stamp - when).total_seconds()
    if abs(offset) > MAX_OFFSET_S:
        return None, offset
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, key)
    if not os.path.exists(path):
        with urllib.request.urlopen(BUCKET + key, timeout=90) as r:
            data = r.read()
        with open(path, "wb") as f:
            f.write(data)
    return path, offset


# ---------------------------------------------------------------- geometry

def resample_to_l3(ours, l2_az, l2_rng, l3_az, l3_rng):
    """Average our polar field into the Level III lattice (1 deg azimuth, its range gates).

    Each of our rays is assigned to the nearest L3 azimuth bin and each L3 gate to the nearest of our
    range gates; the mean is taken over our rays that land in the bin, ignoring NaN. This is the
    azimuthal blur the docstring warns about - it is the honest comparison, not a shortcut.
    """
    az_bin = np.argmin(np.abs((l2_az[:, None] - l3_az[None, :] + 180.0) % 360.0 - 180.0), axis=1)
    g_idx = np.argmin(np.abs(l3_rng[:, None] - l2_rng[None, :]), axis=1)
    picked = ours[:, g_idx]                                  # (n_rays, n_l3_gates)

    out = np.full((len(l3_az), len(l3_rng)), np.nan)
    valid = np.isfinite(picked)
    acc = np.zeros_like(out)
    cnt = np.zeros_like(out)
    np.add.at(acc, az_bin, np.where(valid, picked, 0.0))
    np.add.at(cnt, az_bin, valid.astype(float))
    hit = cnt > 0
    out[hit] = acc[hit] / cnt[hit]
    return out


# ---------------------------------------------------------------- scoring

def score(ours_l3, ref):
    """Agreement between our resampled KDP and N0K over gates where BOTH have data."""
    both = np.isfinite(ours_l3) & ~np.ma.getmaskarray(ref)
    n = int(both.sum())
    if not n:
        return None
    a = ours_l3[both]
    b = np.ma.filled(ref, np.nan)[both]
    d = a - b
    return {
        "n": n,
        "bias": float(np.mean(d)),
        "mae": float(np.mean(np.abs(d))),
        "p95": float(np.percentile(np.abs(d), 95)),
        "within1": 100.0 * float(np.mean(np.abs(d) <= 1 * L3_STEP)),
        "within2": 100.0 * float(np.mean(np.abs(d) <= 2 * L3_STEP)),
        "within4": 100.0 * float(np.mean(np.abs(d) <= 4 * L3_STEP)),
        # Cores are where the two-window change acts, so they are scored separately: a whole-sweep
        # average is dominated by light rain, where both algorithms agree by construction.
        "core_mae": float(np.mean(np.abs(d[np.abs(b) >= 1.0]))) if np.any(np.abs(b) >= 1.0) else float("nan"),
        "core_n": int(np.sum(np.abs(b) >= 1.0)),
        # STRUCTURAL agreement. This is the metric that matters for a DERIVED product: it separates
        # "different estimator, same storm" (high r, differences are smoothing/magnitude) from
        # "misaligned or broken" (low r). A gate-for-gate tolerance cannot make that distinction.
        "r": float(np.corrcoef(a, b)[0, 1]) if n > 2 and np.std(a) > 0 and np.std(b) > 0 else float("nan"),
        # NOISE. Our spread against the operational spread over the same gates. This is what the PhiDP
        # pre-smoothing moves, and unlike coreMAE it is computed over every co-valid gate, so it is not
        # hostage to a corpus that barely has cores. 1.0 = as tight as the NWS; higher = noisier.
        "sdratio": float(np.std(a) / np.std(b)) if np.std(b) > 0 else float("nan"),
    }


def our_kdp(radar, short_dbz):
    """Run the mirror over sweep 0 with the short-window switch at `short_dbz` (1e9 = long window only)."""
    s0 = radar.get_slice(0)
    need = ("differential_phase", "reflectivity", "cross_correlation_ratio")
    if any(f not in radar.fields for f in need):
        return None, None, None
    phi = np.ma.filled(radar.fields["differential_phase"]["data"][s0], np.nan)
    ref = np.ma.filled(radar.fields["reflectivity"]["data"][s0], np.nan)
    rho = np.ma.filled(radar.fields["cross_correlation_ratio"]["data"][s0], np.nan)
    g = min(phi.shape[1], ref.shape[1], rho.shape[1])
    gate_km = float(np.diff(radar.range["data"])[0]) / 1000.0
    saved = dp.KDP_SHORT_DBZ
    dp.KDP_SHORT_DBZ = short_dbz
    try:
        k = dp.kdp_mirror(phi[:, :g], ref[:, :g], rho[:, :g], gate_km)
    finally:
        dp.KDP_SHORT_DBZ = saved
    return k, radar.azimuth["data"][s0], radar.range["data"][:g]


# ---------------------------------------------------------------- main

def volume_time(path):
    """KILX_20260812_010848.V06 -> (ICAO, datetime), or (None, None) if the name isn't in that form.

    ⚠️ Returns rather than throws: the corpus directory is a working folder and a stray file (a
    half-minted extraction, say) should skip that row, not abort the whole run partway through the table.
    """
    parts = os.path.basename(path).split("_")
    if len(parts) < 3:
        return None, None
    try:
        return parts[0], datetime.datetime.strptime(parts[1] + parts[2].split(".")[0], "%Y%m%d%H%M%S")
    except ValueError:
        return None, None


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--volume", help="score one corpus file by name (default: all of them)")
    ap.add_argument("--compare-windows", action="store_true",
                    help="also score the pre-2026-09-10 single-window KDP, to measure the change")
    ap.add_argument("--show-encoding", action="store_true",
                    help="print the N0K quantization observed in the fetched products")
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(CORPUS, "*.V06")))
    if args.volume:
        files = [f for f in files if os.path.basename(f) == args.volume]
        if not files:
            print(f"no corpus volume named {args.volume}")
            return 2

    hdr = f"{'volume':<26} {'gates':>7} {'bias':>7} {'MAE':>6} {'r':>6} {'sd/N0K':>7} {'coreMAE':>8}"
    if args.compare_windows:
        hdr += f" {'sd_before':>11} {'verdict':>9}"
    print(hdr)
    print("-" * len(hdr))

    rows = []
    for path in files:
        name = os.path.basename(path)
        icao, when = volume_time(path)
        if icao is None:
            print(f"{name:<26} SKIP (filename is not SITE_YYYYMMDD_HHMMSS.V06)")
            continue
        try:
            radar = pyart.io.read_nexrad_archive(path)
        except Exception as e:
            print(f"{name:<26} SKIP (L2 read: {type(e).__name__})")
            continue

        ours, l2_az, l2_rng = our_kdp(radar, dp.KDP_SHORT_DBZ)
        if ours is None:
            print(f"{name:<26} SKIP (single-pol: no PhiDP/rhoHV)")
            continue

        l3_path, offset = fetch_nearest(icao, when)
        if l3_path is None:
            off = f"{offset:.0f}s away" if offset is not None else "none listed"
            print(f"{name:<26} SKIP (no N0K within {MAX_OFFSET_S}s: {off})")
            continue

        try:
            r3 = pyart.io.read_nexrad_level3(l3_path)
        except Exception as e:
            print(f"{name:<26} SKIP (L3 read: {type(e).__name__})")
            continue
        fld = next(iter(r3.fields))
        ref = r3.fields[fld]["data"]

        if args.show_encoding:
            vals = np.unique(np.ma.compressed(ref))
            step = float(np.median(np.diff(vals))) if len(vals) > 1 else float("nan")
            print(f"    {name}: N0K levels={len(vals)} min={vals.min():.2f} "
                  f"max={vals.max():.2f} step={step:.3f} (constant L3_STEP={L3_STEP})")

        got = resample_to_l3(ours, l2_az, l2_rng, r3.azimuth["data"], r3.range["data"])
        s = score(got, ref)
        if s is None:
            print(f"{name:<26} SKIP (no co-valid gates)")
            continue

        # ⚠️ A coreMAE over a handful of gates is noise, not a verdict - say so rather than printing it.
        core = (f"{s['core_mae']:>8.3f}" if np.isfinite(s['core_mae']) and s['core_n'] >= MIN_CORE_GATES
                else (f"{'n=' + str(s['core_n']):>8}" if s['core_n'] else f"{'no core':>8}"))
        line = (f"{name:<26} {s['n']:>7,} {s['bias']:>+7.3f} {s['mae']:>6.3f} "
                f"{s['r']:>6.2f} {s['sdratio']:>6.2f}x {core}")
        if args.compare_windows:
            # TRUE pre-2026-09-10 behaviour: long window only AND raw PhiDP. Flipping only the window
            # would leave the smoothing in and silently compare the change against half of itself.
            dp.KDP_SMOOTH = False
            try:
                one, _, _ = our_kdp(radar, 1e9)
            finally:
                dp.KDP_SMOOTH = True
            s1 = score(resample_to_l3(one, l2_az, l2_rng, r3.azimuth["data"], r3.range["data"]), ref)
            # Compare on the SPREAD, not on a 7-gate core sample: sd/N0K is defined over every co-valid
            # gate, so it is the only before/after this corpus can actually support.
            verdict = "n/a"
            if s1 and np.isfinite(s1["sdratio"]) and np.isfinite(s["sdratio"]):
                verdict = ("TIGHTER" if s["sdratio"] < s1["sdratio"] - 0.01 else
                           "looser" if s["sdratio"] > s1["sdratio"] + 0.01 else "same")
            c1 = f"{s1['sdratio']:>10.2f}x" if s1 and np.isfinite(s1['sdratio']) else f"{'n/a':>11}"
            line += f" {c1} {verdict:>9}"
            s["sd_before"] = s1["sdratio"] if s1 else float("nan")
            s["core_mae_1w"] = s1["core_mae"] if s1 else float("nan")
        print(line)
        rows.append(s)

    print("-" * len(hdr))
    if not rows:
        print("nothing scored - see the SKIP reasons above")
        return 1

    tot = sum(r["n"] for r in rows)

    def w(k):
        """Gate-weighted mean over the rows that HAVE the metric.

        ⚠️ NaN-aware on purpose: a volume with a handful of co-valid gates has no defined correlation or
        spread ratio, and including it turned the whole total into NaN - one 10-gate row hid every other
        number in the table.
        """
        num = sum(r[k] * r["n"] for r in rows if np.isfinite(r.get(k, float("nan"))))
        den = sum(r["n"] for r in rows if np.isfinite(r.get(k, float("nan"))))
        return num / den if den else float("nan")
    line = (f"{'WEIGHTED TOTAL':<26} {tot:>7,} {w('bias'):>+7.3f} {w('mae'):>6.3f} "
            f"{w('r'):>6.2f} {w('sdratio'):>6.2f}x")
    if args.compare_windows and any("sd_before" in r for r in rows):
        before = w("sd_before")
        line += f" {'':>8} {before:>10.2f}x"
        if np.isfinite(before) and np.isfinite(w("sdratio")):
            line += f" {'TIGHTER' if w('sdratio') < before else 'looser':>9}"
    print(line)
    print()
    print("bias/MAE/coreMAE in deg/km; r = Pearson correlation over co-valid gates.")
    print("READ r FIRST. KDP is DERIVED, so two implementations differ by far more than one L3")
    print(f"quantization step ({L3_STEP}) on individual gates - a tolerance test is the wrong instrument")
    print(f"(for reference, {w('within1'):.1f}% of gates land within one step, {w('within2'):.1f}% within two).")
    print("What a correct estimator looks like: r high (same structure), bias near zero (no systematic")
    print("offset), coreMAE falling. coreMAE is restricted to |N0K| >= 1.0 deg/km, where the two-window")
    print("switch acts; a whole-sweep mean is dominated by light rain both algorithms agree on.")
    print(f"'no core'/'n=N' = fewer than {MIN_CORE_GATES} gates above that threshold, so coreMAE is a sample")
    print("too small to read - this corpus is quiet-day volumes. sd/N0K is the metric to judge on: it is")
    print("our spread over the operational spread across EVERY co-valid gate. 1.00x = as tight as the NWS.")
    if args.compare_windows:
        print("sd_before is the TRUE pre-2026-09-10 result (long window AND raw PhiDP) on the same gates.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
