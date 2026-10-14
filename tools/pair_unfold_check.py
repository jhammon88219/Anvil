#!/usr/bin/env python3
"""
pair_unfold_check.py - the Python MIRROR of radar-decode.js's dual-PRF pair unfold (findPairCut / pairUnfold), plus
its two checks.

A dual-PRF pass (VCP 112) scans the base tilt twice for velocity at two PRFs: cut A (higher Nyquist N1, shorter
unambiguous range) and cut B (lower N2, longer range). pairUnfold combines them gate by gate; this file is the same
algorithm, line for line (same constants, same window edges: wrap in azimuth, clamp at the first/last gate; same
rounding: floor(x + 0.5), the JS Math.round). Keep the two in step.

Modes:
  --export FRAME OUT      run the mirror on one of OUR single-tilt frames (TiltCheck -- SITE DAY HH:MM --out FRAME)
                          and write OUT.f64 (N x G, NaN = none, rows sorted by azimuth), OUT.az.f64, OUT.json. The
                          browser harness decodes the same FRAME with the real JS and compares gate by gate.
  --score SITE YYYYMMDD_HHMM   the measurement: pair vs today's single-cut region dealias, both scored against the
                          NWS's own dealiased 0.5 deg velocity (Level III N0U, or N0G from 2023), by range band.

Measured 2026-10-14 (docs/RadarEngineSpecs/digests/dealias.md "Option 3 phase 1"): coverage of N0U +4-9 points, wrong
folds lower on Ida KLIX 2021-08-29 16:58/18:59Z and Beryl KHGX 2024-07-08 07:27/08:27Z.

Setup: pip install arm_pyart scipy. Usage: py -3.12 -I pair_unfold_check.py --score KLIX 20210829_1900
"""
import datetime
import json
import os
import re
import sys
import urllib.request
import warnings
import xml.etree.ElementTree as ET

import numpy as np
from scipy.ndimage import generic_filter

warnings.filterwarnings("ignore")
import pyart  # noqa: E402

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dealias_check as dc  # noqa: E402  the region dealias mirror (the backbone)

# --- mirrors radar-decode.js PAIR_* ---
PAIR_TOL = 4.0
PAIR_MARGIN = 5.0
PAIR_REF_TOL = 8.0
PAIR_SPECKLE_TOL = 10.0
PAIR_REF_WIN = (2, 5)        # half-window (rays, gates) -> 5 x 11
PAIR_MIN_NYQ_GAP = 1.0


def alias(v, n):
    return np.mod(v + n, 2 * n) - n


def nearest_fold(v, n, ref):
    return v + 2 * n * np.floor((ref - v) / (2 * n) + 0.5)


def window_median(grid, hr, hg):
    """nan-median over rows r +- hr (wrapping) x gates g +- hg (clamped), at every gate."""
    padded = np.concatenate([grid[-hr:], grid, grid[:hr]]) if hr else grid
    m = generic_filter(padded, np.nanmedian, size=(2 * hr + 1, 2 * hg + 1), mode="nearest")
    return m[hr:-hr] if hr else m


def pair_unfold(V1, V2, backbone, N1, N2, ra_km, rng_km):
    """V1/V2: cut A / cut B (B aligned to A's rays + gates), NaN = none. Returns (out, counts)."""
    both = np.isfinite(V1) & np.isfinite(V2)
    ks = np.array([-1, 0, 1])
    cand = V1[None] + 2 * N1 * ks[:, None, None]
    res = np.abs(alias(cand - V2[None], N2))
    res = np.where(np.isfinite(res), res, np.inf)
    order = np.argsort(res, axis=0, kind="stable")                  # ties -> the lower k, as the JS loop
    r0 = np.take_along_axis(res, order[:1], 0)[0]
    r1 = np.take_along_axis(res, order[1:2], 0)[0]
    P = np.take_along_axis(cand, order[:1], 0)[0]
    pair_ok = both & (r0 < PAIR_TOL)
    ref = np.where(pair_ok & (r1 - r0 > PAIR_MARGIN), P, np.nan)
    Rf = window_median(ref, *PAIR_REF_WIN)

    out = np.full(V1.shape, np.nan)
    src = np.zeros(V1.shape, int)
    has_any = np.isfinite(V1) | np.isfinite(V2)
    rf_ok = has_any & np.isfinite(Rf)
    pw = rf_ok & pair_ok & (np.abs(P - Rf) <= PAIR_REF_TOL)
    out[pw] = P[pw]; src[pw] = 1
    a = rf_ok & ~pw & np.isfinite(V1)
    out[a] = nearest_fold(V1[a], N1, Rf[a]); src[a] = 2
    inside = ~(np.broadcast_to(rng_km, V1.shape) > ra_km) if np.isfinite(ra_km) else np.ones(V1.shape, bool)
    b = rf_ok & ~pw & ~np.isfinite(V1) & inside
    out[b] = nearest_fold(V2[b], N2, Rf[b]); src[b] = 3
    bk = (~has_any | ~np.isfinite(Rf)) & np.isfinite(backbone)
    out[bk] = backbone[bk]; src[bk] = 4

    m = window_median(out, 1, 2)
    spk = (src != 4) & np.isfinite(out) & np.isfinite(m) & (np.abs(out - m) > PAIR_SPECKLE_TOL)
    out[spk] = np.nan
    wm = window_median(out, 2, 4)
    tol = 0.75 * N2
    odd = (src != 4) & np.isfinite(out) & np.isfinite(wm) & (np.abs(out - wm) > tol)
    raw = np.where(np.isfinite(V1), V1, V2)
    nn = np.where(np.isfinite(V1), N1, N2)
    f = nearest_fold(raw[odd], nn[odd], wm[odd])
    out[odd] = np.where(np.abs(f - wm[odd]) <= tol, f, np.nan)
    return out, dict(both=int(both.sum()), ok=int(pair_ok.sum()), conf=int(np.isfinite(ref).sum()), pair=int(pw.sum()), a=int(a.sum()), b=int(b.sum()), bk=int(bk.sum()), drop=int(spk.sum()),
                     refold=int((np.abs(f - wm[odd]) <= tol).sum()))


def read_pair(radar):
    """The first two base-angle Doppler cuts of the volume (ONE pass), as A (higher Nyquist) and B aligned to A's
    rays (nearest azimuth) and gates. None when the volume has no dual-PRF pair."""
    base = min(float(a) for a in radar.fixed_angle["data"])
    cuts = []
    for s in range(radar.nsweeps):
        if abs(float(radar.fixed_angle["data"][s]) - base) > 0.2:
            continue
        sl = radar.get_slice(s)
        if np.ma.count(radar.fields["velocity"]["data"][sl]) < 1000:
            continue
        cuts.append(s)
    if len(cuts) < 2:
        return None
    ip = radar.instrument_parameters
    sA, sB = cuts[0], cuts[1]
    # ⚠️ Rounded to cm/s, the unit the radar stores and the JS reads: Py-ART hands back a float32 (23.18000030517578),
    # and with 0.5 m/s velocity steps a pair residual lands EXACTLY on PAIR_TOL often enough that the last bit decides.
    N1 = round(float(ip["nyquist_velocity"]["data"][radar.get_slice(sA)][0]), 2)
    N2 = round(float(ip["nyquist_velocity"]["data"][radar.get_slice(sB)][0]), 2)
    if abs(N1 - N2) < PAIR_MIN_NYQ_GAP or not N1 > N2:
        return None
    ra = float(ip["unambiguous_range"]["data"][radar.get_slice(sB)][0]) / 1000.0
    slA, slB = radar.get_slice(sA), radar.get_slice(sB)
    azA, azB = radar.azimuth["data"][slA], radar.azimuth["data"][slB]
    V1 = np.ma.filled(radar.fields["velocity"]["data"][slA].astype(float), np.nan)
    VB = np.ma.filled(radar.fields["velocity"]["data"][slB].astype(float), np.nan)
    o = np.argsort(azA)
    azA, V1 = azA[o], V1[o]
    near = np.argmin(np.abs((azA[:, None] - azB[None, :] + 180) % 360 - 180), axis=1)
    return dict(azA=azA, V1=V1, V2=VB[near], N1=N1, N2=N2, ra=ra, rng=radar.range["data"] / 1000.0)


def run(radar):
    p = read_pair(radar)
    if p is None:
        return None
    backbone, _ = dc.dealias_v2(p["V1"], p["N1"])
    out, counts = pair_unfold(p["V1"], p["V2"], backbone, p["N1"], p["N2"], p["ra"], p["rng"])
    return p, backbone, out, counts


def export(frame, prefix):
    r = pyart.io.read_nexrad_archive(frame)
    got = run(r)
    if got is None:
        print("no dual-PRF pair in", frame); return 1
    p, _, out, counts = got
    out.astype(np.float64).tofile(prefix + ".f64")
    p["azA"].astype(np.float64).tofile(prefix + ".az.f64")
    meta = dict(N=int(out.shape[0]), G=int(out.shape[1]), firstGate=float(p["rng"][0]),
                gateSize=float(p["rng"][1] - p["rng"][0]), N1=p["N1"], N2=p["N2"], ra=p["ra"], counts=counts)
    json.dump(meta, open(prefix + ".json", "w"))
    print(json.dumps(meta))
    return 0


def score(site, stamp):
    import kdp_scorecard as ks
    l2 = "https://unidata-nexrad-level2.s3.amazonaws.com/"
    ns = "{http://s3.amazonaws.com/doc/2006-03-01/}"
    t = datetime.datetime.strptime(stamp, "%Y%m%d_%H%M")
    root = ET.fromstring(urllib.request.urlopen(f"{l2}?list-type=2&prefix={t:%Y/%m/%d}/{site}/", timeout=60).read())
    keys = [k.text for k in root.iter(ns + "Key") if not k.text.endswith("_MDM")]
    kt = lambda k: datetime.datetime.strptime("".join(re.search(r"(\d{8})_(\d{6})", k).groups()), "%Y%m%d%H%M%S")
    key = min(keys, key=lambda k: abs((kt(k) - t).total_seconds()))
    os.makedirs(ks.CACHE, exist_ok=True)
    path = os.path.join(ks.CACHE, key.rsplit("/", 1)[1])
    if not os.path.exists(path):
        open(path, "wb").write(urllib.request.urlopen(l2 + key, timeout=600).read())
    r = pyart.io.read_nexrad_archive(path)
    got = run(r)
    if got is None:
        print(f"{site} {key}: VCP {r.metadata.get('vcp_pattern')} has no dual-PRF pair"); return 0
    p, today, out, counts = got
    l3, off = ks.fetch_nearest(site, kt(key), "N0U")
    if l3 is None:
        l3, off = ks.fetch_nearest(site, kt(key), "N0G")
    print(f"{site} {key.rsplit('/', 1)[1]}  N1 {p['N1']:.1f}  N2 {p['N2']:.1f}  {counts}")
    if l3 is None:
        print("   no N0U/N0G near this volume"); return 0
    r3 = pyart.io.read_nexrad_level3(l3)
    U = np.ma.filled(r3.fields["velocity"]["data"].astype(float), np.nan)
    az3, rg3 = r3.azimuth["data"], r3.range["data"] / 1000.0
    ai = np.argmin(np.abs((az3[:, None] - p["azA"][None, :] + 180) % 360 - 180), axis=1)
    gi = np.argmin(np.abs(rg3[:, None] - p["rng"][None, :]), axis=1)
    inr = np.abs(rg3 - p["rng"][gi]) < 0.3
    R = np.broadcast_to(rg3, U.shape)
    for name, F in (("today (single cut)", today), ("pair unfold", out)):
        S = F[ai][:, gi]; S[:, ~inr] = np.nan
        h3 = np.isfinite(U); both = h3 & np.isfinite(S)
        d = np.abs(S - U)
        bands = []
        for lo, hi in ((0, 117), (117, 162), (162, 230)):
            b = (R >= lo) & (R < hi)
            bb = both & b
            bands.append(f"{lo}-{hi}: cov {100 * bb.sum() / max((h3 & b).sum(), 1):4.1f}% wrong {100 * np.mean(d[bb] > 20) if bb.any() else 0:4.2f}%")
        print(f"   {name:<20} covers {100 * both.sum() / max(h3.sum(), 1):5.1f}% of {os.path.basename(l3)[:7]}  "
              f"wrong folds {100 * np.mean(d[both] > 20):4.2f}%   " + " | ".join(bands))
    return 0


if __name__ == "__main__":
    if len(sys.argv) >= 4 and sys.argv[1] == "--export":
        sys.exit(export(sys.argv[2], sys.argv[3]))
    if len(sys.argv) >= 4 and sys.argv[1] == "--score":
        sys.exit(score(sys.argv[2], sys.argv[3]))
    print(__doc__)
    sys.exit(2)
