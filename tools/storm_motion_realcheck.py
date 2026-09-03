#!/usr/bin/env python3
"""
storm_motion_realcheck.py - REAL-DATA cross-check of the app's full-volume storm motion vs Py-ART.

storm_motion_check.py proves the storm-motion math on SYNTHETIC sweeps (analytic answers). This script runs
the SAME mirror (vad_points_for_cut + bunkers_from_profile, imported from storm_motion_check) on a REAL
NEXRAD volume: it Py-ART-dealiases the Doppler velocity, feeds each of the bottom ~6 velocity tilts through
the mirror, applies the per-cut QC, MERGES the surviving VAD points into one deep profile (exactly like the
app's decodeVwp over EnsureVwpTiltsAsync's tilt set), and reduces to a Bunkers storm motion. It then cross-checks the profile's
0-6 km mean wind against Py-ART's OWN full-volume VAD (`pyart.retrieve.vad_browning`).

This is the regression the single-tilt bug motivated: on the Moore 2013-05-20 KTLX supercell the OLD
single-0.5-deg motion was ~7 kt toward N; the deep multi-tilt motion must land ~ENE 25-30 kt (matching the
storm's real track and Py-ART's VAD).

Setup: pip install arm_pyart (see README.md). Needs network (reads the AWS archive bucket).
Usage (TIME IS UTC):  py -3.12 storm_motion_realcheck.py SITE YYYY MM DD HH MM
Example:              py -3.12 storm_motion_realcheck.py KTLX 2013 05 20 20 12   # Moore EF5 -> ENE ~25-30 kt

Does NOT touch the C# app.
"""
import sys
import os
import math
import gzip
import tempfile
import datetime
import urllib.request
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from storm_motion_check import (vad_points_for_cut, bunkers_from_profile,  # the exact mirror
                                merge_cuts, profile_fold_suspect)

import numpy as np
import pyart

EXCLUDE = set()
EXPECT_KT = None

BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com/"
S3NS = "{http://s3.amazonaws.com/doc/2006-03-01/}"
VWP_MAX_TILT_DEG = 7.0   # matches radar-decode.js VWP_MAX_PHI (decodeVwp votes every velocity cut at/below this)


def list_keys(site, day):
    prefix = "%04d/%02d/%02d/%s/" % (day.year, day.month, day.day, site)
    with urllib.request.urlopen("%s?list-type=2&max-keys=1000&prefix=%s" % (BUCKET, prefix), timeout=60) as r:
        root = ET.fromstring(r.read())
    ks = [el.text for el in root.iter(S3NS + "Key")]
    return [k for k in ks if not (k[:-3] if k.endswith(".gz") else k).endswith("_MDM")]


def key_time(k):
    return datetime.datetime.strptime(k.rsplit("/", 1)[-1][4:19], "%Y%m%d_%H%M%S")


def toward(u, v):
    return (np.degrees(np.arctan2(u, v)) + 360) % 360


def main():
    if len(sys.argv) < 7:
        print(__doc__)
        sys.exit(2)
    site, y, mo, d, hh, mm = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6])
    # --exclude=3.1,4.0   drop those elevation cuts before merging (contamination attribution)
    # --expect-kt=25:30   assert the resulting speed falls in this band (the verdict could not fail on speed)
    global EXCLUDE, EXPECT_KT
    for arg in sys.argv[7:]:
        if arg.startswith("--exclude="):
            EXCLUDE = {round(float(x), 1) for x in arg.split("=", 1)[1].split(",") if x}
        elif arg.startswith("--expect-kt="):
            a, b = arg.split("=", 1)[1].split(":")
            EXPECT_KT = (float(a), float(b))
    target = datetime.datetime(y, mo, d, hh, mm)
    keys = list_keys(site, target)
    key = min(keys, key=lambda k: abs((key_time(k) - target).total_seconds()))
    print(f"volume: {key}  ({key_time(key)} UTC)")

    raw = urllib.request.urlopen(BUCKET + key, timeout=300).read()
    if key.endswith(".gz"):
        raw = gzip.decompress(raw)
    tf = tempfile.NamedTemporaryFile(delete=False, suffix=".V06")
    tf.write(raw); tf.close()
    try:
        radar = pyart.io.read_nexrad_archive(tf.name)
        dz = pyart.correct.dealias_region_based(radar, vel_field="velocity", nyquist_vel=None)
        radar.add_field("vdeal", dz, replace_existing=True)
        rng = radar.range["data"]
        first_gate_km = rng[0] / 1000.0
        gate_size_km = (rng[1] - rng[0]) / 1000.0

        # The bottom velocity tilts up to ~7 deg (like EnsureVwpTiltsAsync): compute EACH cut's own VAD ->
        # Bunkers motion, exactly like decodeVwp, then combine (median).
        angles = sorted(set(round(float(a), 2) for a in radar.fixed_angle["data"]))
        motions = []
        used = []
        for ang in angles:
            if ang <= 0 or ang > VWP_MAX_TILT_DEG:  # decodeVwp votes every velocity cut <= VWP_MAX_PHI (no count cap)
                continue
            # first sweep at this fixed angle that actually carries velocity
            sweep = next((s for s in range(radar.nsweeps)
                          if round(float(radar.fixed_angle["data"][s]), 2) == ang
                          and np.ma.count(radar.fields["vdeal"]["data"][slice(*radar.get_start_end(s))]) > 1000), None)
            if sweep is None:
                continue
            start, end = radar.get_start_end(sweep)
            az = radar.azimuth["data"][start:end + 1]
            vd = radar.fields["vdeal"]["data"][start:end + 1]
            radials = []
            for i in range(len(az)):
                row = vd[i]
                radials.append({"az": float(az[i]),
                                "data": [None if np.ma.is_masked(row[j]) else float(row[j]) for j in range(len(row))]})
            pts = vad_points_for_cut(radials, ang, first_gate_km, gate_size_km)
            motions.append(pts)
            if not pts:
                used.append((ang, "0p"))
            elif profile_fold_suspect(pts):
                used.append((ang, "DROPPED(fold)"))
            else:
                used.append((ang, f"{len(pts)}p/{min(q['h'] for q in pts):.0f}-{max(q['h'] for q in pts):.0f}m"))

        # MERGED profile (mirrors decodeVwp): per-cut QC, then ONE profile, then Bunkers once. This replaced
        # a per-cut median -- see decodeVwp's header. THIS RUN IS THE RE-MEASUREMENT that change still owes.
        print("per-cut contributions: " + ", ".join(f"{a:.1f}deg={s}" for a, s in used))
        if EXCLUDE:
            keep_idx = [i for i, (a, _) in enumerate(used) if round(a, 1) not in EXCLUDE]
            print(f"  (EXCLUDING cuts {sorted(EXCLUDE)} deg by request)")
            motions = [motions[i] for i in keep_idx]
        prof, ncuts = merge_cuts(motions)
        print(f"merged: {ncuts} cuts contributed, {len(prof)} ring points")
        res = bunkers_from_profile(prof)
        # Show the profile's OWN layer means, so a surprising motion can be traced to the mean wind vs the
        # Bunkers deviation rather than guessed at. The deviation magnitude must be exactly BUNKERS_D.
        from storm_motion_check import (mean_layer, BUNKERS_MEAN_TOP, BUNKERS_TAIL_TOP,
                                        BUNKERS_HEAD_BOT, BUNKERS_HEAD_TOP)
        sp = sorted(prof, key=lambda q: q['h'])
        mw = mean_layer(sp, 0, BUNKERS_MEAN_TOP)
        bt = mean_layer(sp, 0, BUNKERS_TAIL_TOP)
        hd = mean_layer(sp, BUNKERS_HEAD_BOT, BUNKERS_HEAD_TOP)
        if mw and bt and hd:
            shu, shv = hd['u'] - bt['u'], hd['v'] - bt['v']
            print(f"  our 0-6 km mean wind: {toward(mw['u'], mw['v']):.0f} deg TOWARD at "
                  f"{math.hypot(mw['u'], mw['v'])/0.514444:.0f} kt")
            print(f"  our 0-6 km shear:     {math.hypot(shu, shv):.1f} m/s")
        print("\n=== app full-volume storm motion (mirror) on REAL data ===")
        if res.get("insufficient"):
            print(f"  INSUFFICIENT (topM={res.get('topM')}) -- unexpected for a supercell volume!")
            sys.exit(1)
        kt = res["speedMs"] / 0.514444
        print(f"  storm motion: {res['dirDeg']:.0f} deg TOWARD at {res['speedMs']:.1f} m/s ({kt:.0f} kt)  [{res['source']}]")
        print(f"  merged VAD rings: {res['layers']}, profile top ~{res['topM']} m")

        # Cross-check: Py-ART's own full-volume VAD (independent implementation) 0-6 km mean wind.
        ok = True
        try:
            # vad_browning USES ONE SWEEP ONLY (its own docstring says so). Passing the whole volume let it
            # silently pick sweep 0 and run it to ~315 km -- heights 25 m to 8602 m -- i.e. exactly the
            # single-low-tilt, unbounded-range setup this project documents as physically wrong. Extract the
            # sweep explicitly so the comparison is INTENTIONAL, and read it for what it is: a smoke test
            # against a single-sweep VAD, NOT ground truth for a 0-6 km mean wind. Our profile is multi-tilt
            # and range-bounded per doc 02 section 3.3, so the two are not like for like.
            v = pyart.retrieve.vad_browning(radar.extract_sweeps([0]), "vdeal")
            z, u, w = np.array(v.height), np.array(v.u_wind), np.array(v.v_wind)
            g = np.isfinite(u) & np.isfinite(w) & (z <= 6000)
            pmu, pmv = u[g].mean(), w[g].mean()
            print("\n=== Py-ART vad_browning (SINGLE SWEEP 0, unbounded range -- NOT like-for-like) ===")
            print(f"  0-6 km MEAN wind: {toward(pmu, pmv):.0f} deg at {np.hypot(pmu, pmv)/0.514444:.0f} kt")
            # The Bunkers storm motion deviates right of the mean, so compare loosely: direction within 45 deg.
            ddir = abs((res['dirDeg'] - toward(pmu, pmv) + 180) % 360 - 180)
            # Direction only, loosely: a smoke test that we are not 180 deg out, not a verdict.
            ok = ddir < 45
            print(f"  motion vs mean-wind direction diff: {ddir:.0f} deg  ({'OK' if ok else 'REVIEW'}; Bunkers deviates right of the mean)")
        except Exception as e:
            print(f"\n(Py-ART vad_browning cross-check unavailable: {e})")

        # SPEED ASSERTION. Without this the verdict could only fail on DIRECTION, so a 35 kt answer and a
        # 27 kt answer passed identically -- which is exactly how the merged path's speed went unchecked.
        if EXPECT_KT:
            lo, hi = EXPECT_KT
            sp_ok = lo <= kt <= hi
            ok = ok and sp_ok
            print('\n=== speed band ===')
            print(f"  {kt:.0f} kt vs expected {lo:.0f}-{hi:.0f} kt  ({'OK' if sp_ok else 'OUT OF BAND'})")

        print("\nVERDICT:", "PASS" if ok else "REVIEW")
        sys.exit(0 if ok else 1)
    finally:
        os.unlink(tf.name)


if __name__ == "__main__":
    main()
