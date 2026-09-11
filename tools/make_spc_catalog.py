#!/usr/bin/env python3
"""Regenerates Anvil.Core/Assets/spc-risk-catalog.json from NOAA's PUBLISHED symbology.

Why this exists: every SPC colour Anvil draws used to be hand-typed into C#, and hand-typing is how
the app ended up rendering a 15% wind risk red when SPC renders it yellow. Nothing in the catalog is
authored by a human -- this walks the same ArcGIS services SPC's own viewers draw from and writes
down what they say. Re-run it when NWS issues a service change; commit the diff.

    py -3.12 tools/make_spc_catalog.py

Sources (both are each layer's `drawingInfo.renderer`, i.e. the symbology the service publishes):
  * outlooks/SPC_wx_outlks  -- convective categorical / tornado / wind / hail / day 3 / days 4-8,
    plus a SEPARATE "Conditional Intensity" layer per hazard: the CIG1/2/3 hatching that replaced
    the old single SIGN area in NWS service change 26-11, effective 2026-03-02.
  * fire_weather/SPC_firewx -- fire weather.

Two things this asserts rather than assumes:
  1. Every day in a product family must yield an IDENTICAL scale before they are collapsed into one
     entry. If SPC ever gives Day 2 a level Day 1 lacks, generation fails loudly instead of quietly
     picking whichever layer happened to be read first.
  2. Where a level's short code is derived rather than read (see CODE_* below), any live feed that
     carries DN and LABEL together is cross-checked against the derived value.
"""

import json
import sys
import urllib.request
from collections import OrderedDict
from datetime import datetime, timezone
from pathlib import Path

WX = "https://mapservices.weather.noaa.gov/vector/rest/services/outlooks/SPC_wx_outlks/MapServer/"
FIRE = "https://mapservices.weather.noaa.gov/vector/rest/services/fire_weather/SPC_firewx/MapServer/"
SCN = "NWS service change 26-11 (conditional intensity), effective 2026-03-02"
OUT = Path(__file__).resolve().parent.parent / "Anvil.Core" / "Assets" / "spc-risk-catalog.json"

# Which service layers make up each Anvil SpcOutlookType. `solid` = the probability/category layers,
# `cig` = that hazard's Conditional Intensity layers (absent where SPC defines no hatching).
# NB days 3-8 fire weather: the sub-layer order FLIPS after day 2. Day 1/2 are
# [outlook, dry thunderstorm] but day 3+ are [dry thunderstorm, winds and low humidity], so the
# extended outlook is the SECOND sub-layer. Reading the first serves dry-thunderstorm areas instead.
FAMILIES = OrderedDict([
    ("Categorical",           {"svc": WX,   "solid": [1, 9, 17],              "cig": []}),
    ("Tornado",               {"svc": WX,   "solid": [3, 11],                 "cig": [2, 10]}),
    ("Wind",                  {"svc": WX,   "solid": [7, 15],                 "cig": [6, 14]}),
    ("Hail",                  {"svc": WX,   "solid": [5, 13],                 "cig": [4, 12]}),
    ("ProbabilisticCombined", {"svc": WX,   "solid": [19],                    "cig": [18]}),
    ("ExtendedProbabilistic", {"svc": WX,   "solid": [21, 22, 23, 24, 25],    "cig": []}),
    ("FireWeather",           {"svc": FIRE, "solid": [1, 4],                  "cig": []}),
    ("ExtendedFireWeather",   {"svc": FIRE, "solid": [8, 11, 14, 17, 20, 23], "cig": []}),
])

# Esri fill style -> the pattern name Anvil's renderer and legend swatch both draw.
HATCH = {
    "esriSFSBackwardDiagonal": "BackwardDiagonal",
    "esriSFSForwardDiagonal": "ForwardDiagonal",
    "esriSFSDiagonalCross": "DiagonalCross",
}

# The MapServer publishes human labels ("Marginal", "Elevated"); the GeoJSON feeds and the IEM
# archive key on SHORT CODES ("MRGL", "ELEV"). These two maps are the bridge, and they are the only
# hand-written values in this file -- SPC's stable product vocabulary, never a colour. cross_check()
# re-verifies them against any live feed carrying DN and LABEL together.
CODE_CATEGORICAL = {2: "TSTM", 3: "MRGL", 4: "SLGT", 5: "ENH", 6: "MDT", 8: "HIGH"}
CODE_FIRE = {5: "ELEV", 8: "CRIT", 10: "EXTM"}

# Live feeds used ONLY to cross-check the derived codes above -- never for colours.
CROSS_CHECK = [
    ("Categorical", "https://www.spc.noaa.gov/products/outlook/day1otlk_cat.lyr.geojson"),
    ("Tornado", "https://www.spc.noaa.gov/products/outlook/day1otlk_torn.lyr.geojson"),
    ("Wind", "https://www.spc.noaa.gov/products/outlook/day1otlk_wind.lyr.geojson"),
    ("Hail", "https://www.spc.noaa.gov/products/outlook/day1otlk_hail.lyr.geojson"),
]


def fetch(url):
    req = urllib.request.Request(url, headers={"User-Agent": "Anvil-catalog-generator/1.0"})
    with urllib.request.urlopen(req, timeout=45) as response:
        return json.load(response)


def hex_of(color):
    """#RRGGBB, or None when there is no colour -- including a FULLY TRANSPARENT one.

    ⚠️ The alpha channel is load-bearing and easy to drop. SPC draws the days 3-8 fire-weather
    outlook OUTLINE-ONLY: its fills are [130,130,130,0] and [189,229,252,0] -- alpha 0. Ignoring
    the fourth channel records those as an opaque grey and a pale blue, which is how a legend ends
    up showing a colour SPC never draws. Every other layer in both services is alpha 255.
    """
    if not color:
        return None
    if len(color) > 3 and color[3] == 0:
        return None
    return "#%02X%02X%02X" % (color[0], color[1], color[2])


def code_for(family, raw, label):
    """The short code a level is known by in the GeoJSON feeds and the IEM archive."""
    if str(label).startswith("CIG"):
        return str(label)
    if family == "Categorical":
        return CODE_CATEGORICAL.get(int(raw))
    if family == "FireWeather":
        return CODE_FIRE.get(int(raw))
    if family == "ExtendedFireWeather":
        return str(raw)                      # already a probability string ("0.40")
    return "%.2f" % (int(raw) / 100.0)       # 15 -> "0.15", matching the feed's LABEL


def read_layer(svc, layer_id, family, kind):
    meta = fetch("%s%d?f=json" % (svc, layer_id))
    renderer = meta.get("drawingInfo", {}).get("renderer", {})
    infos = renderer.get("uniqueValueInfos")
    if not infos:
        raise SystemExit("layer %d (%s) publishes no uniqueValueInfos" % (layer_id, meta.get("name")))

    levels = []
    for info in infos:
        symbol = info.get("symbol", {})
        raw = info.get("value")
        label = info.get("label") or str(raw)

        if kind == "ConditionalIntensity":
            # CIG layers key on `label` ("CIG1"); the severity ordinal is its trailing digit, and the
            # hatch PATTERN -- not the fill colour -- is what tells the groups apart.
            tail = str(raw)[3:]
            dn = int(tail) if tail.isdigit() else len(levels) + 1
            hatch = HATCH.get(symbol.get("style"))
            if hatch is None:
                raise SystemExit("layer %d: unmapped CIG fill style %r"
                                 % (layer_id, symbol.get("style")))
        else:
            dn = float(raw) if "." in str(raw) else int(raw)
            hatch = "None"

        fill = hex_of(symbol.get("color"))
        stroke = hex_of((symbol.get("outline") or {}).get("color")) or fill
        if not fill:
            # An OUTLINE-ONLY level (transparent fill): SPC draws the days 3-8 fire outlook this way.
            # The legend swatch is a filled chip, so it takes the outline colour -- the only colour
            # SPC actually puts on screen for that level. Derived, and said out loud here because it
            # is the one place this file does not copy a value verbatim.
            fill = stroke
        if not fill:
            raise SystemExit("layer %d: level %r has neither a fill nor an outline colour"
                             % (layer_id, raw))

        code = code_for(family, raw, label)
        if code is None:
            raise SystemExit("layer %d: no short code derived for %r" % (layer_id, raw))

        levels.append(OrderedDict([
            ("code", code),
            ("dn", dn),
            ("officialName", label),
            ("fill", fill),
            ("stroke", stroke),
            ("kind", kind),
            ("hatch", hatch),
        ]))

    levels.sort(key=lambda lv: (lv["dn"], lv["code"]))
    return meta.get("name"), levels


def collapse(family, spec):
    """Read every day's layer and require them to agree before collapsing to one scale."""
    solid, cig = [], []
    for layer_id in spec["solid"]:
        name, levels = read_layer(spec["svc"], layer_id, family, "Solid")
        solid.append((layer_id, name, levels))
    for layer_id in spec["cig"]:
        name, levels = read_layer(spec["svc"], layer_id, family, "ConditionalIntensity")
        cig.append((layer_id, name, levels))

    for group in (solid, cig):
        if not group:
            continue
        base_id, base_name, base = group[0]
        for layer_id, name, levels in group[1:]:
            if levels != base:
                raise SystemExit(
                    "%s: layer %d (%s) disagrees with layer %d (%s).\n"
                    "SPC has given one day a scale another lacks, so this family can no longer be a\n"
                    "single entry. Split it before regenerating.\n  %s\n  %s"
                    % (family, layer_id, name, base_id, base_name,
                       json.dumps(levels), json.dumps(base)))

    return list(solid[0][2]) + (list(cig[0][2]) if cig else []), [n for _, n, _ in solid + cig]


def cross_check(scales):
    """Re-verify the derived short codes against any live feed carrying DN and LABEL together."""
    checked = 0
    for family, url in CROSS_CHECK:
        try:
            feed = fetch(url)
        except Exception as exc:                          # a feed being down must not block a rebuild
            print("\n    ! cross-check skipped for %s (%s)" % (family, exc), end="")
            continue
        # Solid levels ONLY: `dn` is unique within a kind, not within a family -- tornado carries
        # both a 2% probability level and CIG2 at dn=2. Merging the two namespaces here would
        # compare a probability against a conditional-intensity group.
        by_dn = {lv["dn"]: lv["code"] for lv in scales[family] if lv["kind"] == "Solid"}
        for feature in feed.get("features", []):
            props = feature.get("properties", {})
            dn, label = props.get("DN"), props.get("LABEL")
            if not dn or not label or str(label).startswith("CIG"):
                continue                                  # DN 0 is the "no risk" sentinel
            expected = by_dn.get(dn)
            if expected is not None and expected != label:
                raise SystemExit("%s: feed says DN %s is %r, catalog derived %r"
                                 % (family, dn, label, expected))
            checked += 1
    return checked


def main():
    scales, provenance = OrderedDict(), OrderedDict()
    for family, spec in FAMILIES.items():
        print("  %-22s" % family, end="", flush=True)
        levels, names = collapse(family, spec)
        scales[family] = levels
        provenance[family] = names
        print("%2d levels  (%s)" % (len(levels), ", ".join(names)))

    print("  cross-checking derived codes", end="", flush=True)
    print(" -- %d level(s) agreed" % cross_check(scales))

    doc = OrderedDict([
        ("_comment", "GENERATED by tools/make_spc_catalog.py -- do not hand-edit. Every colour here "
                     "is NOAA's published symbology, not a transcription."),
        ("_generatedUtc", datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")),
        ("_serviceChange", SCN),
        ("_sources", OrderedDict([("convective", WX), ("fireWeather", FIRE)])),
        ("_layers", provenance),
        ("scales", [OrderedDict([("type", f), ("levels", lv)]) for f, lv in scales.items()]),
    ])

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
    print("  wrote %s (%d bytes)" % (OUT, OUT.stat().st_size))
    return 0


if __name__ == "__main__":
    sys.exit(main())
