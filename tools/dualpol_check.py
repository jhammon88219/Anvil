#!/usr/bin/env python3
"""
dualpol_check.py - NUMERIC dual-pol validation against Py-ART, no JS runtime needed.

Sibling of dealias_check.py (velocity). The app decodes four dual-pol products in radar-decode.js:
  * ZDR  (differential_reflectivity)  - DIRECT read  -> gate-for-gate vs Py-ART
  * CC   (cross_correlation_ratio)    - DIRECT read  -> gate-for-gate vs Py-ART
  * SW   (spectrum_width)             - DIRECT read  -> gate-for-gate vs Py-ART
  * KDP  (specific differential phase)- DERIVED from PhiDP (kdpFromPhi) -> STRUCTURAL vs Py-ART

Two halves, matching how velocity was validated:
  1. REFERENCE + range sanity: decode the SAME AWS volume with Py-ART and print each product's
     value distribution (count/min/max/mean/percentiles). These are the physical ranges the app's
     JS decoder MUST reproduce; an out-of-range value in-app = a decoder scale/offset bug. (The
     actual app values come from the vendored JS decoder, which needs the app to read back - this
     script is the answer key for that in-app diff.)
  2. KDP ALGORITHM check (fully offline): a faithful Python mirror of radar-decode.js `kdpFromPhi`
     (unwrap the ~360 deg PhiDP fold -> QC-mask rhoHV<0.85 or refl<minDbz -> fixed 3 km LS slope ->
     KDP = 1/2 * dPhiDP/dr), run on Py-ART's PhiDP/rhoHV/refl, then compared to Py-ART's own KDP
     retrieval (kdp_maesaka). KDP is algorithm-dependent so the bar is STRUCTURAL: correlation over
     co-valid gates + magnitude ballpark of the heavy-rain cores, NOT gate-for-gate.

Keep the mirror in sync with radar-decode.js kdpFromPhi if that algorithm changes.

Setup (one-time): python -m pip install arm_pyart
Usage (TIME IS UTC):  py -3.12 dualpol_check.py SITE YYYY MM DD HH MM
Example:  py -3.12 dualpol_check.py KTLX 2013 05 20 20 10    # Moore EF5 supercell (debris/ZDR arc/KDP foot)

Does NOT touch the C# app.
"""
import sys, gzip, tempfile, os, datetime, urllib.request
import xml.etree.ElementTree as ET
import numpy as np

BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com/"
S3NS = "{http://s3.amazonaws.com/doc/2006-03-01/}"
MIN_DBZ = 10            # radar.js MIN_DBZ
KDP_RHO_MIN = 0.90      # radar-decode.js kdpFromPhi constants — keep in sync
KDP_WINDOW_KM = 3.0         # LONG (light-rain) half-window ~= operational 25-gate branch
KDP_WINDOW_SHORT_KM = 1.0   # SHORT (convective) half-window ~= operational 9-gate branch
KDP_SHORT_DBZ = 40.0        # reflectivity at/above which the SHORT window is used
KDP_MIN_VALID = 5
KDP_ABS_MAX = 15.0      # reject |KDP| beyond this (unphysical unwrap/short-window spikes)
# HARNESS SWITCH ONLY. The shipped JS has no such flag - it always smooths. kdp_scorecard.py flips this
# off to reproduce the pre-2026-09-10 behaviour (raw PhiDP) so it can measure the smoothing's effect.
# Leave it True for any real validation run.
KDP_SMOOTH = True


def list_keys(site, day):
    prefix = "%04d/%02d/%02d/%s/" % (day.year, day.month, day.day, site)
    with urllib.request.urlopen("%s?list-type=2&max-keys=1000&prefix=%s" % (BUCKET, prefix), timeout=60) as r:
        root = ET.fromstring(r.read())
    return [el.text for el in root.iter(S3NS + "Key")
            if not (el.text[:-3] if el.text.endswith(".gz") else el.text).endswith("_MDM")]


def key_time(k):
    return datetime.datetime.strptime(k.rsplit("/", 1)[-1][4:19], "%Y%m%d_%H%M%S")


def nearest(keys, t):
    return min(keys, key=lambda k: abs((key_time(k) - t).total_seconds()))


def download(key):
    with urllib.request.urlopen(BUCKET + key, timeout=300) as r:
        d = r.read()
    return gzip.decompress(d) if key.endswith(".gz") else d


def stats(name, a, unit):
    """Print a product's value distribution over its finite gates."""
    f = a[np.isfinite(a)]
    if not f.size:
        print("  %-6s no finite gates" % name); return
    pct = np.percentile(f, [1, 50, 99])
    print("  %-6s n=%-8d min=%7.2f  p1=%7.2f  med=%7.2f  p99=%7.2f  max=%7.2f  mean=%7.2f  %s"
          % (name, f.size, f.min(), pct[0], pct[1], pct[2], f.max(), f.mean(), unit))


def field2d(radar, name, sweep):
    d = radar.fields[name]["data"][radar.get_slice(sweep)]
    return np.ma.filled(d.astype(float), np.nan)


# --- Python mirror of radar-decode.js kdpFromPhi (keep in sync) ------------------------------------
# Py-ART unifies every field onto the sweep's common range gates, so the app's rangeIndexOf alignment
# is the identity here (refl[j]/rho[j]/phi[j] co-located) - the algorithm is what we're validating.
def kdp_mirror(phi, refl, rho, gate_km):
    R, G = phi.shape
    w_long = max(1, int(round(KDP_WINDOW_KM / gate_km)))
    w_short = max(1, int(round(KDP_WINDOW_SHORT_KM / gate_km)))
    x = np.arange(G) * gate_km
    out = np.full((R, G), np.nan)
    for r in range(R):
        pd, rf, rh = phi[r], refl[r], rho[r]
        ph = np.full(G, np.nan)
        valid = np.zeros(G, bool)
        prev, accum = None, 0.0
        for j in range(G):
            v = pd[j]
            # masked (NaN) rho/refl == the JS decoder's null -> QC fail (ok=false)
            ok = (np.isfinite(v) and np.isfinite(rh[j]) and rh[j] >= KDP_RHO_MIN
                  and np.isfinite(rf[j]) and rf[j] >= MIN_DBZ)
            if ok:
                if prev is not None:
                    d = v - prev
                    if d > 180:
                        accum -= 360
                    elif d < -180:
                        accum += 360
                prev = v
                ph[j] = v + accum
                valid[j] = True
            # else: keep prev/accum across isolated dropouts (unwrap stays continuous)
        vf = valid.astype(float)
        y = np.where(valid, ph, 0.0)
        xv = np.where(valid, x, 0.0)
        c_cnt = np.concatenate([[0.0], np.cumsum(vf)])
        c_x = np.concatenate([[0.0], np.cumsum(xv)])
        c_y = np.concatenate([[0.0], np.cumsum(y)])
        c_xx = np.concatenate([[0.0], np.cumsum(xv * x)])   # x*x only at valid gates
        c_ph = c_y                                          # unwrapped phase at valid gates, for smooth()
        idx = np.arange(G)

        def smooth(w):
            """Mean of the VALID gates within +/- w (STEP 1 of the operational algorithm).

            Mirrors radar-decode.js kdpFromPhi `smooth`. Fitting a slope to RAW PhiDP is what made our
            KDP ~5x noisier than Level III N0K; each branch smooths at its own width before fitting.
            """
            lo = np.clip(idx - w, 0, G - 1)
            hi = np.clip(idx + w, 0, G - 1) + 1
            cnt = c_cnt[hi] - c_cnt[lo]
            tot = c_ph[hi] - c_ph[lo]
            return np.where(cnt > 0, tot / np.where(cnt == 0, 1.0, cnt), np.nan)

        def slope_for(w, src):
            """Halved least-squares slope of `src` vs range over +/- w valid gates (NaN where unsupported)."""
            sv = np.where(valid, np.nan_to_num(src, nan=0.0), 0.0)
            c_y2 = np.concatenate([[0.0], np.cumsum(sv)])
            c_xy2 = np.concatenate([[0.0], np.cumsum(xv * sv)])
            lo = np.clip(idx - w, 0, G - 1)
            hi = np.clip(idx + w, 0, G - 1) + 1
            cnt = c_cnt[hi] - c_cnt[lo]
            sx = c_x[hi] - c_x[lo]
            sy = c_y2[hi] - c_y2[lo]
            sxx = c_xx[hi] - c_xx[lo]
            sxy = c_xy2[hi] - c_xy2[lo]
            denom = cnt * sxx - sx * sx
            good = valid & (cnt >= KDP_MIN_VALID) & (denom != 0)
            return np.where(good, 0.5 * (cnt * sxy - sx * sy) / np.where(denom == 0, 1.0, denom), np.nan)

        # Operational two-window selection, mirroring radar-decode.js kdpFromPhi: SHORT (high-resolution)
        # fit at/above KDP_SHORT_DBZ, LONG (low-noise) fit elsewhere, with a fall back to LONG wherever
        # the short window lacks the samples (or reflectivity is unknown). Each branch fits its OWN
        # smoothed profile - see smooth().
        s_long = slope_for(w_long, smooth(w_long) if KDP_SMOOTH else ph)
        s_short = slope_for(w_short, smooth(w_short) if KDP_SMOOTH else ph)
        zh = np.where(valid, rf, np.nan)
        use_short = np.isfinite(zh) & (zh >= KDP_SHORT_DBZ)
        slope = np.where(use_short & np.isfinite(s_short), s_short, s_long)
        slope = np.where(np.abs(slope) > KDP_ABS_MAX, np.nan, slope)  # drop unphysical unwrap/window spikes
        out[r] = slope
    return out


def main():
    if len(sys.argv) != 7:
        print(__doc__); sys.exit(1)
    site = sys.argv[1].upper()
    t = datetime.datetime(*[int(x) for x in sys.argv[2:7]])
    key = nearest(list_keys(site, t.date()), t)
    print("Volume:", key.rsplit("/", 1)[-1])
    import pyart
    data = download(key)
    tmp = tempfile.NamedTemporaryFile(suffix=".ar2v", delete=False); tmp.write(data); tmp.close()
    try:
        radar = pyart.io.read_nexrad_archive(tmp.name)
    finally:
        os.unlink(tmp.name)
    fields = set(radar.fields)
    print("fields:", sorted(fields))
    if "differential_reflectivity" not in fields:
        print("No dual-pol moments in this volume (legacy single-pol?) - nothing to check.")
        sys.exit(1)

    gate_km = float(np.median(np.diff(radar.range["data"]))) / 1000.0
    print("gate spacing %.3f km" % gate_km)

    # --- 1. REFERENCE + physical-range sanity (surveillance cut = sweep 0; SW on the Doppler cut) ---
    print("\n[1] Py-ART reference value ranges (the app's JS decoder must reproduce these):")
    stats("ZDR", field2d(radar, "differential_reflectivity", 0), "dB      (expect ~ -8..+8)")
    stats("CC", field2d(radar, "cross_correlation_ratio", 0), "unitless(expect  0..~1.05)")
    stats("PhiDP", field2d(radar, "differential_phase", 0), "deg     (expect  0..360)")
    vd = radar.fields.get("velocity")
    vs = None
    if vd is not None:
        vdd = vd["data"]
        vs = next((s for s in range(radar.nsweeps) if np.ma.count(vdd[radar.get_slice(s)]) > 0), None)
    if "spectrum_width" in fields and vs is not None:
        stats("SW", field2d(radar, "spectrum_width", vs), "m/s     (expect  0..~15)")

    # --- 2. KDP ALGORITHM check: app mirror vs Py-ART retrieval (structural) ---
    print("\n[2] KDP algorithm (mirror of kdpFromPhi) vs Py-ART kdp_maesaka:")
    phi = field2d(radar, "differential_phase", 0)
    refl = field2d(radar, "reflectivity", 0)
    rho = field2d(radar, "cross_correlation_ratio", 0)
    mine = kdp_mirror(phi, refl, rho, gate_km)
    stats("KDPapp", mine, "deg/km  (app kdpFromPhi mirror)")

    # Unphysical-tail rate: real KDP at S-band is ~ -1..+10 deg/km even in violent cores; |KDP|>10 is
    # almost certainly PhiDP-unwrap / short-window noise, which paints spurious bright pixels.
    fin = np.isfinite(mine)
    n_fin = int(fin.sum())
    for thr in (10.0, 20.0):
        n_bad = int((np.abs(mine) > thr).sum())
        print("  unphysical |KDP|>%-4.0f : %d/%d = %.2f%%" % (thr, n_bad, n_fin, 100.0 * n_bad / max(1, n_fin)))

    def rank_corr(a, b):  # Spearman: robust to the retrievals' different smoothing/scaling
        from scipy.stats import rankdata
        return float(np.corrcoef(rankdata(a), rankdata(b))[0, 1])

    # Heavy-precip subset: where KDP is physically meaningful and every method should agree
    heavy = fin & (refl > 35.0) & (rho > 0.95)
    for name, fn in (("kdp_maesaka", pyart.retrieve.kdp_maesaka),
                     ("kdp_vulpiani", pyart.retrieve.kdp_vulpiani)):
        try:
            pa = np.ma.filled(fn(radar)[0]["data"][radar.get_slice(0)].astype(float), np.nan)
        except Exception as e:
            print("  %s failed (%s)" % (name, e)); continue
        co = fin & np.isfinite(pa)
        if co.sum() < 100:
            print("  %s: too few co-valid gates" % name); continue
        try:
            sr = rank_corr(mine[co], pa[co])
        except Exception:
            sr = float("nan")
        hp = heavy & np.isfinite(pa)
        pr_h = float(np.corrcoef(mine[hp], pa[hp])[0, 1]) if hp.sum() > 100 else float("nan")
        print("  vs %-13s co=%-7d Pearson r=%+.3f  Spearman=%+.3f  heavy-precip r=%+.3f  mean|dif|=%.3f"
              % (name, int(co.sum()), float(np.corrcoef(mine[co], pa[co])[0, 1]), sr, pr_h,
                 float(np.nanmean(np.abs(mine[co] - pa[co])))))


if __name__ == "__main__":
    main()
