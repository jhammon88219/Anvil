#!/usr/bin/env python3
"""
rhohv_snr_check.py - is the Level II rhoHV moment already SNR-noise-corrected by the RDA, or raw?

This answers open question O-2 from the Brief B research doc (docs/radar/), which is the GATE on whether
Anvil should apply an SNR-based rhoHV correction at all. Nothing should be built for that until this
says there is something to correct.

THE PHYSICS, and why this is measurable without an SNR field
-----------------------------------------------------------
rhoHV is biased LOW at low signal-to-noise, because receiver noise decorrelates the H and V returns. In
uniform light rain the TRUE rhoHV is ~0.98-0.99 and essentially flat - rain is rain. So if we bin gates
by reflectivity (the only SNR proxy the volume gives us for free) and rhoHV *climbs* with Z across that
band, the low-Z end is being dragged down by noise and the moment is NOT corrected. A flat profile means
the RDA already corrected it and there is nothing for us to add.

⚠️⚠️ THE CONFOUND IS THE WHOLE DIFFICULTY, and a reflectivity band alone does NOT control for it.
rhoHV varies with SCATTERER TYPE far more than with noise: biological targets and ground clutter sit at
0.5-0.8 for physical reasons. A low-Z bin is full of them, so a naive "rhoHV climbs with Z" reading just
rediscovers bugs-give-way-to-rain. Measured on this corpus without a control, KBUF reads ~0.55 in EVERY
bin (it is all biology) and KILX climbs 0.83 -> 0.99 purely by changing scatterer.
So the rain filter below is load-bearing, and it must not be rhoHV itself or the test is circular:
ZDR is the independent discriminator. Rain sits near 0-3 dB; biological scatterers are strongly
non-spherical and run much higher. Filtering to |ZDR| <= RAIN_ZDR_MAX leaves gates that are rain by a
measure the test is not about, and THEN a residual low-Z droop is attributable to SNR.

Usage:  py -3.12 rhohv_snr_check.py            # every corpus volume
        py -3.12 rhohv_snr_check.py --volume KGRB_20260720_153535.V06

Does NOT touch the C# app.
"""
import argparse
import glob
import os
import sys
import warnings

import numpy as np

warnings.filterwarnings("ignore")
import pyart  # noqa: E402

CORPUS = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                      "..", "Anvil.App", "Assets", "RadarCorpus")

# The light-rain band. Below ~5 dBZ is mostly clear-air/biological (rhoHV genuinely low, not a noise
# artefact); above ~35 dBZ hydrometeor variety starts moving rhoHV for real physical reasons.
Z_LO, Z_HI, Z_STEP = 5.0, 35.0, 5.0
MIN_PER_BIN = 200          # below this a bin's median is not worth reading
RAIN_ZDR_MAX = 3.0         # dB. The independent rain gate - see the confound note above.
DROOP_THRESHOLD = 0.02     # a climb bigger than this, AFTER the rain filter, is a real low-SNR droop
# ⚠️ THIRD CONTROL, and it is not optional. A volume whose rhoHV never REACHES rain values contains no
# rain to testify about - the ZDR filter cannot rescue a scene that is all insects (Phoenix) or all
# biology (KBUF sits at ~0.53 in every bin). Such a volume still produces a big "climb" as scatterer type
# changes, and averaging it in flips the verdict. Only volumes that actually reach rain may vote.
RAIN_PRESENT_RHO = 0.97


def profile(path, rain_only):
    radar = pyart.io.read_nexrad_archive(path)
    if "cross_correlation_ratio" not in radar.fields:
        return None
    s0 = radar.get_slice(0)
    z = np.ma.filled(radar.fields["reflectivity"]["data"][s0], np.nan)
    r = np.ma.filled(radar.fields["cross_correlation_ratio"]["data"][s0], np.nan)
    g = min(z.shape[1], r.shape[1])
    ok2 = np.isfinite(z[:, :g]) & np.isfinite(r[:, :g])
    if rain_only:
        if "differential_reflectivity" not in radar.fields:
            return None
        d = np.ma.filled(radar.fields["differential_reflectivity"]["data"][s0], np.nan)[:, :g]
        ok2 &= np.isfinite(d) & (np.abs(d) <= RAIN_ZDR_MAX)
    z, r = z[:, :g][ok2], r[:, :g][ok2]

    rows = []
    edges = np.arange(Z_LO, Z_HI + Z_STEP, Z_STEP)
    for lo, hi in zip(edges[:-1], edges[1:]):
        m = (z >= lo) & (z < hi)
        n = int(m.sum())
        rows.append((lo, hi, n, float(np.median(r[m])) if n >= MIN_PER_BIN else float("nan")))
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--volume", help="one corpus file by name (default: all)")
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(CORPUS, "*.V06")))
    if args.volume:
        files = [f for f in files if os.path.basename(f) == args.volume]

    edges = np.arange(Z_LO, Z_HI + Z_STEP, Z_STEP)
    hdr = f"{'volume':<26} " + " ".join(f"{int(lo)}-{int(hi)}" .rjust(8) for lo, hi in zip(edges[:-1], edges[1:]))
    print("RAIN-FILTERED (|ZDR| <= %.1f dB). Unfiltered numbers are shown after, to expose the confound."
          % RAIN_ZDR_MAX)
    print(hdr + f"   {'climb':>7}  verdict")
    print("-" * (len(hdr) + 20))

    climbs = []
    for path in files:
        rows = profile(path, rain_only=True)
        name = os.path.basename(path)
        if rows is None:
            print(f"{name:<26} single-pol, no rhoHV")
            continue
        cells = " ".join((f"{v:8.3f}" if np.isfinite(v) else f"{'n<' + str(MIN_PER_BIN):>8}")
                         for _, _, _, v in rows)
        vals = [v for _, _, _, v in rows if np.isfinite(v)]
        # The signature: how much the median rhoHV rises from the lowest readable bin to the highest.
        climb = (vals[-1] - vals[0]) if len(vals) >= 2 else float("nan")
        has_rain = bool(vals) and max(vals) >= RAIN_PRESENT_RHO
        if np.isfinite(climb) and has_rain:
            climbs.append(climb)
        verdict = ("n/a" if not np.isfinite(climb)
                   else "no rain" if not has_rain
                   else "DROOPS" if climb > DROOP_THRESHOLD else "flat")
        print(f"{name:<26} {cells}   {climb:>+7.3f}  {verdict}")

    print("-" * (len(hdr) + 20))
    print("Cells are the MEDIAN rhoHV in each reflectivity bin (dBZ). 'climb' is the rise from the lowest")
    print("readable bin to the highest.")
    print()
    print("UNFILTERED, for comparison - any climb here is mostly scatterer type, not noise:")
    for path in files:
        rows = profile(path, rain_only=False)
        if rows is None:
            continue
        vals = [v for _, _, _, v in rows if np.isfinite(v)]
        raw = (vals[-1] - vals[0]) if len(vals) >= 2 else float("nan")
        cells = " ".join((f"{v:8.3f}" if np.isfinite(v) else f"{'-':>8}") for _, _, _, v in rows)
        print(f"  {os.path.basename(path):<26} {cells}   {raw:>+7.3f}")
    print()
    print(f"'no rain' = the volume's rhoHV never reaches {RAIN_PRESENT_RHO}, so there is no rain in it to")
    print("testify about SNR droop. Those volumes are EXCLUDED from the verdict - including them inverts it.")
    print()
    # ⚠️ The verdict requires AGREEMENT, not an average. These volumes are different weather - averaging a
    # clean stratiform sweep with a mixed convective one produces a number describing neither, and a mean
    # is exactly how the first two versions of this tool talked themselves into the wrong answer.
    if not climbs:
        print("NO VERDICT: not one corpus volume contains rain by this test. That is itself a finding -")
        print("the corpus is quiet-day dealias volumes and cannot answer a precipitation question.")
    elif all(c <= DROOP_THRESHOLD for c in climbs):
        print(f"VERDICT: FLAT in every rain-bearing volume (climbs {min(climbs):+.3f} to {max(climbs):+.3f}).")
        print("No low-Z droop to correct: the RDA appears to have already applied the SNR correction, so")
        print("building one in Anvil would be work with nothing behind it.")
        print("This CLOSES Brief B open question O-2 in the negative.")
    elif all(c > DROOP_THRESHOLD for c in climbs):
        print(f"VERDICT: DROOPS in every rain-bearing volume (climbs {min(climbs):+.3f} to {max(climbs):+.3f}).")
        print("The Level II rhoHV looks UNCORRECTED; an SNR correction would let Anvil keep more low-SNR")
        print("gates without speckle.")
    else:
        flat = sum(1 for c in climbs if c <= DROOP_THRESHOLD)
        print(f"VERDICT: INCONCLUSIVE - {flat} of {len(climbs)} rain-bearing volumes are flat and the rest")
        print(f"droop (climbs {min(climbs):+.3f} to {max(climbs):+.3f}). Read the per-volume rows: the flat")
        print("ones are clean widespread rain, and a volume that droops while its lowest bin still sits far")
        print("below rain rhoHV is reporting scatterer type, not noise - the filter did not fully isolate")
        print("rain there. DO NOT build an SNR correction on this evidence. What it actually shows is")
        print("that the corpus cannot answer the question: it is six quiet-day DEALIAS volumes, and a")
        print("precipitation question needs volumes chosen for precipitation.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
