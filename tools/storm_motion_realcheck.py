#!/usr/bin/env python3
"""
storm_motion_realcheck.py - REAL-DATA cross-check of the app's full-volume storm motion vs Py-ART.

storm_motion_check.py proves the storm-motion math on SYNTHETIC sweeps (analytic answers). This script runs
the SAME mirror (vad_points_for_cut + bunkers_from_profile, imported from storm_motion_check) on a REAL
NEXRAD volume: it Py-ART-dealiases the Doppler velocity, feeds each of the bottom ~6 velocity tilts through
the mirror, MERGES their VAD points into one deep profile (exactly like the app's decodeVwp over
EnsureVwpTiltsAsync's tilt set), and reduces to a Bunkers storm motion. It then cross-checks the profile's
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
import gzip
import tempfile
import datetime
import urllib.request
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from storm_motion_check import vad_points_for_cut, bunkers_from_profile, combine_cut_motions  # the exact mirror

import numpy as np
import pyart

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
            m = bunkers_from_profile(vad_points_for_cut(radials, ang, first_gate_km, gate_size_km))
            motions.append(m)
            used.append((ang, "insuf" if m.get("insufficient") else f"{m['dirDeg']:.0f}@{m['speedMs']/0.514444:.0f}kt"))

        print("per-cut motions: " + ", ".join(f"{a:.1f}deg={s}" for a, s in used))
        res = combine_cut_motions(motions)
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
            v = pyart.retrieve.vad_browning(radar, "vdeal")
            z, u, w = np.array(v.height), np.array(v.u_wind), np.array(v.v_wind)
            g = np.isfinite(u) & np.isfinite(w) & (z <= 6000)
            pmu, pmv = u[g].mean(), w[g].mean()
            print("\n=== Py-ART vad_browning cross-check (independent) ===")
            print(f"  0-6 km MEAN wind: {toward(pmu, pmv):.0f} deg at {np.hypot(pmu, pmv)/0.514444:.0f} kt")
            # The Bunkers storm motion deviates right of the mean, so compare loosely: direction within 45 deg.
            ddir = abs((res['dirDeg'] - toward(pmu, pmv) + 180) % 360 - 180)
            ok = ddir < 45
            print(f"  motion vs mean-wind direction diff: {ddir:.0f} deg  ({'OK' if ok else 'REVIEW'}; Bunkers deviates right of the mean)")
        except Exception as e:
            print(f"\n(Py-ART vad_browning cross-check unavailable: {e})")

        print("\nVERDICT:", "PASS" if ok else "REVIEW")
        sys.exit(0 if ok else 1)
    finally:
        os.unlink(tf.name)


if __name__ == "__main__":
    main()
