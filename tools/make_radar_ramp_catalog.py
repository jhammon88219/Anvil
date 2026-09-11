#!/usr/bin/env python3
"""Regenerates Anvil.App/Assets/Map/js/radar-ramp-tables.js from the NWS's OWN radar colour tables.

Same discipline as tools/make_spc_catalog.py: nothing here is authored by hand. This harvests the
colormaps AWIPS II -- the NWS's operational forecaster display -- actually renders radar with, and
pairs each with the level->value mapping read out of REAL Level III products.

    py -3.12 tools/make_radar_ramp_catalog.py            # regenerate
    py -3.12 tools/make_radar_ramp_catalog.py --verify   # re-harvest and diff (non-zero on drift)

TWO VARIANTS per product where the NWS publishes two, because they are different products' tables and
neither is "better":
  * bands16   -- the classic banded scale (the 8/16-level legacy products). Coarse, instantly
                 recognisable, and what Anvil's reflectivity has always approximated.
  * levels256 -- the 8-bit scale. Matches the resolution Anvil actually renders: we draw Level II
                 super-res, and N0B/N0G/N0C/N0K/N0X are the Level III rendering of those same moments.
Both ship in the generated file; radar-ramps.js picks per product in code. Nothing is switchable at
runtime -- gate colours are BAKED at decode time, so a change means re-decoding every loaded frame.

⚠️ PROVENANCE / LICENSING, recorded deliberately. The upstream .cmap files carry a Raytheon header
reading "U.S. EXPORT CONTROLLED TECHNICAL DATA" (contract DG133W-05-CQ-1067). We do NOT vendor those
files -- this script fetches them and emits a table of numbers whose source is recorded per entry. The
values are also rendered publicly in every NWS radar image, and a colour lookup table is close to pure
fact. Flagged here so the decision is visible rather than implicit; see docs/radar-official-ramps.md.

⚠️ WHAT IS AND IS NOT VERIFIED. Every level->value mapping below was measured, not assumed -- either
read from a real product's threshold block or fitted from its raw-vs-decoded pairs (residual ~1e-11).
The ONE exception is spectrum width: the L3 bucket does not carry NSW, so its mapping is unverifiable
here. Its tables are emitted with verified=false and radar-ramps.js does NOT select them -- putting
official colours at guessed values would be worse than an honest custom ramp.
"""

import argparse
import json
import re
import sys
import urllib.parse
import urllib.request
from collections import OrderedDict
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "Anvil.App" / "Assets" / "Map" / "js" / "radar-ramp-tables.js"

AWIPS = ("https://raw.githubusercontent.com/Unidata/awips2/unidata_18.2.1/edexOsgi/"
         "com.raytheon.uf.common.dataplugin.radar/utility/common_static/base/colormaps/Radar/")

# ⚠️⚠️ AWIPS SAYS WHICH TABLE GOES WITH WHICH PRODUCT -- WE DO NOT CHOOSE. This file pairs every radar
# product code with the colormap AWIPS draws it in. Harvesting it is what turns "which table looks right?"
# into a lookup, and it is not academic: velocity's 8-bit table was picked BY HAND as
# `OSF/256 Level Velocity` because it sat beside the reflectivity one -- and that table is an ORPHAN no
# style rule references. Its inbound extreme fades to grey, so a violent couplet's inbound half lost
# almost all separation from ordinary inbound flow (RGB distance 75 between -45 and -15 m/s, against 230
# on the table AWIPS actually pairs with product 301). Derive, never choose.
STYLE_RULES = ("https://raw.githubusercontent.com/Unidata/awips2/unidata_18.2.1/edexOsgi/"
               "com.raytheon.uf.common.dataplugin.radar/utility/common_static/base/styleRules/"
               "radarImageryStyleRules.xml")

KT = 0.514444  # knots -> m/s; the legacy 16-level velocity products threshold in KNOTS

# The 16-level velocity/SRM thresholds, read from a REAL N0S product's threshold block
# (KTLX, 2026-09-11): BAD, -64, -50, ..., +64, BAD. Level 0 is below-threshold and level 15 is
# range-folded, so 14 colour bands carry data.
VEL16_KT = [-64, -50, -36, -26, -20, -10, -1, 0, 10, 20, 26, 36, 50, 64]

# Reflectivity's 16-level thresholds are the universal 5..75 dBZ by 5 -- the scale Anvil already draws.
REF16_DBZ = [5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75]

# product -> how to build each variant.
#   product   : the NEXRAD Level III product code. AWIPS's own style rules say which colormap draws it,
#               so the .cmap path is DERIVED, never named here (see STYLE_RULES above for why).
#   scale/off : level -> value is (level - off) / scale, MEASURED (see `measured` for the evidence)
SPEC = OrderedDict([
    ("reflectivity", {
        "label": "Reflectivity", "unit": "dBZ", "min": 5, "max": 75,
        "bands16": {"product": 19, "thresholds": REF16_DBZ,
                    "measured": "thresholds are the universal 5-75 dBZ by 5 of the 16-level product"},
        "levels256": {"product": 300, "scale": 2.0, "offset": 66.0,
                      "measured": "fitted from N0B raw-vs-decoded, max|resid| 2.3e-11"},
    }),
    ("velocity", {
        "label": "Base Velocity", "unit": "m/s", "min": -50, "max": 50,
        "bands16": {"product": 25,
                    "thresholds": [v * KT for v in VEL16_KT],
                    "measured": "thresholds read from a real N0S threshold block (same 16-level "
                                "velocity family; N0V is not carried in the bucket to confirm directly)"},
        # ⚠️ Product 301 is super-res velocity (N0G) -- the exact product the mapping was fitted from.
        "levels256": {"product": 301, "scale": 2.0, "offset": 129.0,
                      "measured": "fitted from N0G raw-vs-decoded, max|resid| 7.6e-12"},
    }),
    ("srv", {
        "label": "Storm-Rel Velocity", "unit": "m/s", "min": -50, "max": 50,
        "bands16": {"product": 56,
                    "thresholds": [v * KT for v in VEL16_KT],
                    "measured": "thresholds read from a real N0S (SRM) threshold block"},
        # ⚠️ There is NO 8-bit SRM product, so none of AWIPS's rules name a 256-level SRM table. SRM is
        # the same quantity in a shifted frame, so it borrows VELOCITY's product-301 pairing.
        "levels256": {"product": 301, "scale": 2.0, "offset": 129.0,
                      "measured": "velocity's own table and mapping -- there is no 8-bit SRM product, "
                                  "and SRM is the same quantity in a shifted frame"},
    }),
    ("cc", {
        "label": "Correlation Coeff", "unit": "ρHV", "min": 0.2, "max": 1.05,
        "levels256": {"product": 161, "scale": 300.0, "offset": -60.5,
                      "measured": "scale/offset read from a real N0C product header"},
    }),
    ("kdp", {
        "label": "Specific Differential Phase", "unit": "°/km", "min": -1, "max": 5,
        "levels256": {"product": 163, "scale": 20.0, "offset": 43.0,
                      "measured": "scale/offset read from a real N0K product header"},
    }),
    ("zdr", {
        "label": "Differential Reflectivity", "unit": "dB", "min": -4, "max": 6,
        "levels256": {"product": 159, "scale": 16.0, "offset": 128.0,
                      "measured": "scale/offset read from a real N0X product header"},
    }),
    ("sw", {
        "label": "Spectrum Width", "unit": "m/s", "min": 0, "max": 14,
        # ⚠️ UNVERIFIED. NSW is not carried in the Level III bucket, so neither variant's mapping can
        # be measured. Emitted for completeness, selected by nothing.
        "bands16": {"product": 30, "levels": 8, "thresholds": None, "measured": None},
        "levels256": {"product": 155, "scale": None, "offset": None, "measured": None},
    }),
])


def pairings():
    """{product code -> colormap path}, read from AWIPS's own radarImageryStyleRules.xml."""
    req = urllib.request.Request(STYLE_RULES, headers={"User-Agent": "Anvil-ramp-generator/1.0"})
    with urllib.request.urlopen(req, timeout=60) as r:
        xml = r.read().decode("utf-8", "replace")
    out = {}
    for rule in re.findall(r"<styleRule>(.*?)</styleRule>", xml, re.S):
        cmap = re.search(r"<defaultColormap>(.*?)</defaultColormap>", rule)
        if not cmap:
            continue
        path = cmap.group(1)
        if not path.startswith("Radar/"):
            continue
        for a, b in re.findall(r"<paramLevel>(\d+)</paramLevel>|<parameter>(\d+)</parameter>", rule):
            out[int(a or b)] = path[len("Radar/"):] + ".cmap"
    if not out:
        raise SystemExit("radarImageryStyleRules.xml yielded no product->colormap pairings")
    return out


def fetch(path):
    url = AWIPS + urllib.parse.quote(path)
    req = urllib.request.Request(url, headers={"User-Agent": "Anvil-ramp-generator/1.0"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read().decode("utf-8", "replace")


def colors_of(path):
    """The 256 RGBA entries of one AWIPS colormap, as (r,g,b) 0-255 plus alpha."""
    txt = fetch(path)
    found = re.findall(
        r'<color\s+r\s*=\s*"([\d.]+)"\s+g\s*=\s*"([\d.]+)"\s+b\s*=\s*"([\d.]+)"\s+a\s*=\s*"([\d.]+)"', txt)
    if len(found) != 256:
        raise SystemExit(f"{path}: expected 256 colour entries, got {len(found)}")
    return [((round(float(r) * 255), round(float(g) * 255), round(float(b) * 255)), float(a))
            for r, g, b, a in found]


def hexc(rgb):
    return "#%02X%02X%02X" % rgb


def build_bands(path, thresholds, levels=16):
    """Collapse an N-level AWIPS table (stretched across 256 entries) to its band colours.

    ⚠️ Index 0 is always below-threshold (black) and, on the velocity family, the LAST index is the
    range-folded purple -- neither is a data band, so both are excluded from the ramp's stops.
    """
    cols = colors_of(path)
    width = 256 // levels
    bands = [cols[i * width + width // 2][0] for i in range(levels)]
    data = bands[1:1 + len(thresholds)] if thresholds else bands[1:]
    folded = bands[-1] if levels == 16 and thresholds and len(thresholds) < levels - 1 else None
    return data, folded


def build_levels(path, scale, offset):
    """Levels 2..255 of an 8-bit table as a flat colour list, plus the range-folded entry.

    ⚠️ Level 0 is below-threshold and level 1 is RANGE FOLDED in every 8-bit radar product -- the
    same third gate state radar-decode.js already tracks. Data starts at level 2.
    """
    cols = colors_of(path)
    folded = cols[1][0]
    return [c for c, _a in cols[2:256]], folded


def harvest():
    doc = OrderedDict()
    folded_seen = OrderedDict()
    rules = pairings()

    def cmap_for(variant, prod, which):
        """The colormap AWIPS pairs with this product code. Fails loudly rather than guessing."""
        code = variant.get("product")
        path = rules.get(code)
        if path is None:
            raise SystemExit(
                f"{prod}/{which}: AWIPS's style rules name no colormap for product code {code}. "
                "Pick a code that appears in radarImageryStyleRules.xml rather than choosing a file.")
        return path

    for prod, spec in SPEC.items():
        entry = OrderedDict([("label", spec["label"]), ("unit", spec["unit"]),
                             ("min", spec["min"]), ("max", spec["max"])])
        b = spec.get("bands16")
        if b:
            thresholds = b.get("thresholds")
            if thresholds and b.get("measured"):
                data, folded = build_bands(cmap_for(b, prod, "bands16"), thresholds, b.get("levels", 16))
                entry["bands16"] = OrderedDict([
                    ("verified", True),
                    ("source", cmap_for(b, prod, "bands16")),
                    ("mapping", b["measured"]),
                    ("stops", [OrderedDict([("v", round(v, 6)), ("color", hexc(c))])
                               for v, c in zip(thresholds, data)]),
                ])
                if folded:
                    folded_seen[prod] = hexc(folded)
            else:
                entry["bands16"] = OrderedDict([
                    ("verified", False), ("source", cmap_for(b, prod, "bands16")),
                    ("mapping", None),
                    ("note", "no Level III product carried to measure this product's thresholds"),
                ])
        lv = spec.get("levels256")
        if lv:
            if lv.get("scale") is not None:
                cols, folded = build_levels(cmap_for(lv, prod, "levels256"), lv["scale"], lv["offset"])
                entry["levels256"] = OrderedDict([
                    ("verified", True),
                    ("source", cmap_for(lv, prod, "levels256")),
                    ("mapping", lv["measured"]),
                    ("scale", lv["scale"]), ("offset", lv["offset"]),
                    ("colors", [hexc(c) for c in cols]),
                ])
                folded_seen[prod] = hexc(folded)
            else:
                entry["levels256"] = OrderedDict([
                    ("verified", False), ("source", cmap_for(lv, prod, "levels256")), ("mapping", None),
                    ("note", "NSW is not carried in the Level III bucket, so level->value is unmeasurable"),
                ])
        doc[prod] = entry
    return doc, folded_seen


def render(doc, folded):
    """Emit the generated ES module. Data only -- radar-ramps.js owns the behaviour."""
    stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    # ⚠️ Level 1 is the RANGE-FOLDED entry only on the DOPPLER products. On reflectivity it is a grey
    # low-return colour (#BFBFBF) and on the dual-pol products it is simply the bottom of their own
    # ramp — harvesting "level 1" blindly across all seven yields five different colours and none of
    # them a statement about range folding. Range folding is a short-PRT Doppler phenomenon, which is
    # exactly the rule radar-ramps.js already applies, so velocity is the authority.
    vals = {folded["velocity"]}
    if not vals or None in vals:
        raise SystemExit("velocity's range-folded entry is missing — cannot derive RANGE_FOLDED_HEX")
    lines = [
        "// radar-ramp-tables.js — GENERATED by tools/make_radar_ramp_catalog.py. DO NOT HAND-EDIT.",
        "//",
        "// The NWS's own radar colour tables, harvested from the AWIPS II colormaps its forecasters",
        "// actually see, each paired with the level→value mapping measured out of a real Level III",
        "// product. Two variants where the NWS publishes two (see the generator's header for why).",
        "//",
        "// ⚠️ This file is DATA. rampColor, the variant choice and every ramp object live in",
        "// radar-ramps.js — which is still the single source of truth the decoder and legend import.",
        "// Regenerate after an AWIPS change; `--verify` diffs the committed file against upstream.",
        "//",
        f"// generated: {stamp}",
        f"// source:    {AWIPS}",
        "",
        "export const GENERATED_UTC = %s;" % json.dumps(stamp),
        "export const SOURCE_URL = %s;" % json.dumps(AWIPS),
        "",
        "// The RANGE-FOLDED gate colour — level 1 of every 8-bit radar product, and its own display",
        "// class alongside \"has a value\" and \"no data\". Anvil's previous value (#7A4FAD) came from",
        "// AWIPS's Storm Clear Reflectivity table rather than these products'.",
        "export const RANGE_FOLDED_HEX = %s;" % json.dumps(sorted(vals)[0]),
        "",
        "export const TABLES = %s;" % json.dumps(doc, indent=2),
        "",
    ]
    return "\n".join(lines)


LEVEL_EPS = 1e-6  # must match radar-ramps.js


def check(doc):
    """Port radar-ramps.js's two lookup paths and prove they agree, plus the shape invariants.

    ⚠️ This is the closest thing to a unit test the ramps can have: there is no node/deno/bun on this
    machine, so the JS cannot be executed here. What CAN be checked is the arithmetic the JS performs,
    re-implemented from the same table — which is where the real risk lives. It caught the CC float
    round-trip that LEVEL_EPS now absorbs.
    """
    import math
    import random

    def h2(h):
        return (int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16))

    def scan(stops, value):
        if value <= stops[0][0]:
            return stops[0][1]
        if value >= stops[-1][0]:
            return stops[-1][1]
        i = 0
        while i + 1 < len(stops) and value >= stops[i + 1][0]:
            i += 1
        return stops[i][1]

    random.seed(7)
    probes = mismatches = 0
    for pid, entry in doc.items():
        for style in ("bands16", "levels256"):
            v = entry.get(style)
            if not v or not v.get("verified"):
                continue
            if style == "bands16":
                stops = [(s["v"], h2(s["color"])) for s in v["stops"]]
                vals = [s[0] for s in stops]
                if vals != sorted(vals):
                    raise SystemExit(f"{pid}/{style}: stop values are not ascending")
                print(f"    {pid:13s} {style:9s} {len(stops):3d} bands, ascending OK")
                continue

            scale, offset = v["scale"], v["offset"]
            colors = [h2(c) for c in v["colors"]]
            if len(colors) != 254:
                raise SystemExit(f"{pid}/{style}: expected 254 colours, got {len(colors)}")
            stops = [((i + 2 - offset) / scale, c) for i, c in enumerate(colors)]
            if [s[0] for s in stops] != sorted(s[0] for s in stops):
                raise SystemExit(f"{pid}/{style}: stop values are not ascending")

            def fast(value):
                i = math.floor(value * scale + offset + LEVEL_EPS) - 2
                return colors[min(max(i, 0), len(colors) - 1)]

            lo, hi = stops[0][0], stops[-1][0]
            step = 1.0 / scale
            vals = [lo - 50, lo, hi, hi + 50]
            vals += [lo + (hi - lo) * random.random() for _ in range(40000)]
            # every band edge, and a tenth of a band either side of it — the places the two paths
            # could disagree if the arithmetic were wrong.
            for s in stops:
                vals += [s[0], s[0] - step / 10, s[0] + step / 10]
            bad = sum(1 for x in vals if scan(stops, x) != fast(x))
            probes += len(vals)
            mismatches += bad
            print(f"    {pid:13s} {style:9s} {len(vals):6d} probes, {bad} mismatch(es)")

    print(f"    -> {probes} probes, {mismatches} mismatch(es)")
    return 1 if mismatches else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--verify", action="store_true",
                    help="re-harvest and diff against the committed file; exit 1 on drift")
    ap.add_argument("--check", action="store_true",
                    help="prove the fast and scan lookup paths agree, and the shape invariants hold")
    args = ap.parse_args()

    doc, folded = harvest()
    for prod, e in doc.items():
        bits = []
        for var in ("bands16", "levels256"):
            if var in e:
                v = e[var]
                n = len(v.get("stops") or v.get("colors") or [])
                bits.append(f"{var}={'%d' % n if v['verified'] else 'unverified'}")
        print("  %-13s %s" % (prod, "  ".join(bits)))
    text = render(doc, folded)

    if args.check:
        return check(doc)

    if args.verify:
        if not OUT.exists():
            print("VERIFY FAILED: %s does not exist" % OUT); return 1
        old = OUT.read_text(encoding="utf-8")
        strip = lambda s: re.sub(r"^// generated:.*$|^export const GENERATED_UTC.*$", "", s, flags=re.M)
        if strip(old) == strip(text):
            print("  verify OK - committed tables match upstream AWIPS")
            return 0
        print("  VERIFY FAILED - the committed tables differ from upstream AWIPS.")
        print("  Re-run without --verify and commit the regenerated file.")
        return 1

    OUT.write_text(text, encoding="utf-8")
    print("  wrote %s (%d bytes)" % (OUT, OUT.stat().st_size))
    return 0


if __name__ == "__main__":
    sys.exit(main())
