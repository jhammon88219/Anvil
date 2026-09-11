#!/usr/bin/env python3
"""
dualpol_scorecard.py - score Anvil's CC/ZDR DISPLAY MASK against the NWS's own products (N0C / N0X).

WHAT THIS IS FOR, AND WHY IT IS NOT THE SAME TEST AS kdp_scorecard.py
--------------------------------------------------------------------
KDP is DERIVED, so scoring it means scoring our algorithm. CC and ZDR are DIRECT MOMENT READS - there is
no Anvil algorithm in the values at all, and this harness reads Level II through Py-ART rather than
through our vendored JS decoder, so comparing values here would score Py-ART against the ORPG and tell us
nothing about Anvil.

What IS ours, and what this therefore scores, is the DISPLAY MASK: which gates we choose to draw.
`maskByQuality` keeps a gate where reflectivity >= minDbz OR rhoHV >= MET_RHO_MIN, and that union was
chosen from first principles plus a corpus measurement - never checked against what a forecaster's own
product actually draws. This is that check, and the mask is currently the least-verified of the
2026-09-10/11 changes.

HOW TO READ IT
--------------
  drawn     share of signal-bearing gates our union mask would draw
  L3        share the operational product actually carries (its own masking, applied before quantization)
  agree     share of gates where we and the product AGREE on draw-or-not - the headline
  we-only   we draw, the product does not  -> our mask is looser than operational
  L3-only   the product draws, we do not   -> our mask is hiding real product data
All three are over GATES WHERE OUR SWEEP HAS SIGNAL - the only gates where a masking decision exists.
Using the union of both grids instead counts pure coverage gaps as mask disagreements and roughly
quadruples L3-only, which is how this tool read before the denominator was fixed.
⚠️ Perfect agreement is NOT the target and would be suspicious: L3 truncates at 230 km, is 1 degree in
azimuth against our 0.5, and applies product-specific masks of its own. A LOPSIDED disagreement is the
signal - large we-only means we paint noise the NWS suppresses, large L3-only means we hide echo it shows.

Usage:  py -3.12 dualpol_scorecard.py                 # N0C, every corpus volume
        py -3.12 dualpol_scorecard.py --product N0C
        py -3.12 dualpol_scorecard.py --volume KMLB_20260905_200222.V06

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

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kdp_scorecard as ks  # noqa: E402  shared Level III fetch + resampling

# Anvil's mask constants - keep in sync with radar-decode.js (MET_RHO_MIN) and radar.js (MIN_DBZ).
MIN_DBZ = 10.0
MET_RHO_MIN = 0.80

PRODUCTS = {
    "N0C": ("cross_correlation_ratio", "CC"),
    "N0X": ("differential_reflectivity", "ZDR"),
}


def our_mask(radar):
    """Anvil's maskByQuality union over sweep 0: Z >= MIN_DBZ OR rhoHV >= MET_RHO_MIN.

    ⚠️ Py-ART puts every moment on the sweep's common range gates, so the app's range-index alignment is
    the identity here - the UNION RULE is what is under test, not the alignment (that has its own unit
    test). Returns (drawn, has_signal) boolean grids.
    """
    s0 = radar.get_slice(0)
    z = np.ma.filled(radar.fields["reflectivity"]["data"][s0], np.nan)
    r = np.ma.filled(radar.fields["cross_correlation_ratio"]["data"][s0], np.nan)
    g = min(z.shape[1], r.shape[1])
    z, r = z[:, :g], r[:, :g]
    has_signal = np.isfinite(z) & np.isfinite(r)
    drawn = has_signal & ((z >= MIN_DBZ) | (r >= MET_RHO_MIN))
    return drawn, has_signal, radar.azimuth["data"][s0], radar.range["data"][:g]


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--volume", help="one corpus file by name (default: all)")
    ap.add_argument("--product", choices=sorted(PRODUCTS), help="default: both")
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(ks.CORPUS, "*.V06")))
    if args.volume:
        files = [f for f in files if os.path.basename(f) == args.volume]
    # ⚠️ N0C AND N0X SHARE ONE MASK. Verified 2026-09-11 on KMLB: distinct files, distinct fields, and
    # the valid-gate masks are byte-identical - 0 differing gates out of 432,000. The ORPG applies a
    # single data-quality mask across the dual-pol trio, so running both products against a MASK question
    # prints the same table twice. N0C is therefore the default; --product N0X re-checks that claim, and
    # if it ever disagrees the assumption has changed and this comment is wrong.
    products = [args.product] if args.product else ["N0C"]

    for product in products:
        field, label = PRODUCTS[product]
        print(f"\n=== {product} ({label}) ===")
        hdr = (f"{'volume':<26} {'signal':>8} {'drawn':>7} {'L3':>7} {'agree':>7} "
               f"{'we-only':>8} {'L3-only':>8}")
        print(hdr)
        print("-" * len(hdr))
        tot_agree, tot_we, tot_l3, tot_n = 0, 0, 0, 0

        for path in files:
            name = os.path.basename(path)
            icao, when = ks.volume_time(path)
            if icao is None:
                continue
            try:
                radar = pyart.io.read_nexrad_archive(path)
            except Exception as e:
                print(f"{name:<26} SKIP (L2 read: {type(e).__name__})")
                continue
            if "cross_correlation_ratio" not in radar.fields:
                print(f"{name:<26} SKIP (single-pol)")
                continue

            l3p, off = ks.fetch_nearest(icao, when, product)
            if l3p is None:
                print(f"{name:<26} SKIP (no {product} within {ks.MAX_OFFSET_S}s)")
                continue
            try:
                r3 = pyart.io.read_nexrad_level3(l3p)
            except Exception as e:
                print(f"{name:<26} SKIP (L3 read: {type(e).__name__})")
                continue
            ref = r3.fields[next(iter(r3.fields))]["data"]

            drawn, signal, az, rng = our_mask(radar)
            # Resample our BOOLEAN decision through the same path the KDP tool uses, then threshold at a
            # half: a 1-degree L3 bin covers two of our rays, so "drawn" means the majority of them were.
            d = ks.resample_to_l3(np.where(signal, drawn.astype(float), np.nan),
                                  az, rng, r3.azimuth["data"], r3.range["data"])
            l3_has = ~np.ma.getmaskarray(ref)
            ours = np.isfinite(d) & (d >= 0.5)
            # ⚠️ THE DENOMINATOR IS "GATES WHERE OUR SWEEP HAS SIGNAL", not the union of both grids.
            # A gate we never sampled (inside our first gate, past the product's range, a radial the
            # product masked wholesale) is a COVERAGE difference, and counting it as a masking
            # disagreement inflates L3-only enormously - it read 26.6% before this was corrected, which
            # would have looked like our mask hiding a quarter of the operational product. The mask
            # decision only exists where there was something to decide about.
            cov = np.isfinite(d)
            n = int(cov.sum())
            if not n:
                print(f"{name:<26} SKIP (no overlap)")
                continue
            agree = int((ours == l3_has)[cov].sum())
            we_only = int((ours & ~l3_has & cov).sum())
            l3_only = int((~ours & l3_has & cov).sum())
            tot_agree += agree; tot_we += we_only; tot_l3 += l3_only; tot_n += n

            print(f"{name:<26} {int(signal.sum()):>8,} "
                  f"{100.0 * drawn.sum() / max(signal.sum(), 1):>6.1f}% "
                  f"{100.0 * l3_has.sum() / max(l3_has.size, 1):>6.1f}% "
                  f"{100.0 * agree / n:>6.1f}% {100.0 * we_only / n:>7.1f}% {100.0 * l3_only / n:>7.1f}%")

        print("-" * len(hdr))
        if tot_n:
            print(f"{'TOTAL':<26} {'':>8} {'':>7} {'':>7} {100.0 * tot_agree / tot_n:>6.1f}% "
                  f"{100.0 * tot_we / tot_n:>7.1f}% {100.0 * tot_l3 / tot_n:>7.1f}%")

    print()
    print("'drawn'/'L3' are shares of different denominators (our signal-bearing gates vs the product's")
    print("own grid), so they are context, not a comparison. The comparison is agree / we-only / L3-only,")
    print("all over the gates either side could speak about.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
