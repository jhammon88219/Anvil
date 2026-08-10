#!/usr/bin/env python3
"""Dissolve the contiguous US states (with the Great Lakes filled in) into a CONUS boundary for the map mask.

Anvil masks "everything outside CONUS" with an inverted fill (one polygon: a world-rectangle outer ring
with CONUS punched out as holes — see Assets/Map/js/states.js). The holes MUST be disjoint: two holes that
touch/share an edge make earcut bridge between them and gore the interior with triangles. So we pre-dissolve
the states into their exterior boundary (one mainland ring + coastal islands), which is disjoint and cuts
clean.

⚠️ THE GREAT LAKES. The Census cartographic state polygons are SHORELINE-CLIPPED — states end at the water's
edge, so the Great Lakes are NOT inside the dissolved CONUS and fall in the masked "outside" area. Because
the mask fill sits on top of everything, that hid radar returns AND the basemap's lake labels over the lakes
(a lake and the water-colored mask look identical, so the only tell was the missing radar/labels). We fix it
by UNIONING the actual Great Lakes water polygons into the land before dissolving, so the CONUS boundary runs
out to the *Canadian* lake shore and the full lake water reads as interior (radar + labels show through). Any
land island inside a lake (e.g. Isle Royale) is swallowed into CONUS — we keep only EXTERIOR rings, so a lake
island renders as filled interior, not a nested hole. Ocean islands (Long Island, the Keys, …) stay their own
disjoint rings.

The union is done with shapely (a build-time-only dependency of this tool — NOT of the app). This replaced an
earlier no-dependency edge-cancellation dissolve; unioning arbitrary lake geometry into the land needs real
polygon booleans, and shapely's unary_union produces clean, disjoint exterior rings for free.

Inputs : Anvil.App/Assets/Map/state-boundaries.geojson  (normalized {id, properties.name, geometry})
         tools/great-lakes.geojson                       (Natural Earth 10m lakes, filtered to the 6 lakes)
Output : Anvil.App/Assets/Map/conus-boundary.geojson     (one Feature, MultiPolygon of exterior rings)

Usage:  py -3.12 tools/make_conus_boundary.py   (needs shapely: `py -3.12 -m pip install shapely`)
Re-run whenever state-boundaries.geojson changes.
"""
import io
import json
import os

from shapely.geometry import shape, mapping
from shapely.ops import unary_union

COORD_DECIMALS = 5  # output precision (keeps the file small; the mask doesn't need more)
EXCLUDE = {"Alaska", "Hawaii", "Puerto Rico"}  # non-contiguous — not part of CONUS
# The Great Lakes water bodies to fill into CONUS so returns/labels show over them (see header).
GREAT_LAKES = {"Lake Superior", "Lake Michigan", "Lake Huron", "Lake Erie", "Lake Ontario", "Lake Saint Clair"}

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "Anvil.App", "Assets", "Map", "state-boundaries.geojson")
LAKES = os.path.join(HERE, "great-lakes.geojson")
OUT = os.path.join(HERE, "..", "Anvil.App", "Assets", "Map", "conus-boundary.geojson")


def load(path):
    return json.load(io.open(path, encoding="utf-8"))


def round_ring(ring):
    return [[round(x, COORD_DECIMALS), round(y, COORD_DECIMALS)] for x, y in ring]


def exterior_rings(geom):
    """The EXTERIOR ring of every polygon part (interior holes dropped — lake islands become filled CONUS)."""
    rings = []
    if geom.geom_type == "Polygon":
        rings.append(list(geom.exterior.coords))
    elif geom.geom_type == "MultiPolygon":
        for poly in geom.geoms:
            rings.append(list(poly.exterior.coords))
    return rings


def main():
    states = load(SRC)
    lakes = load(LAKES)

    polys = [shape(f["geometry"]) for f in states["features"] if f["properties"]["name"] not in EXCLUDE]
    polys += [shape(f["geometry"]) for f in lakes["features"] if f["properties"]["name"] in GREAT_LAKES]

    merged = unary_union(polys)  # dissolves state borders + fills the lakes in one shot
    rings = [round_ring(r) for r in exterior_rings(merged)]
    rings.sort(key=len, reverse=True)

    fc = {"type": "FeatureCollection", "features": [{
        "type": "Feature", "properties": {"name": "CONUS"},
        "geometry": {"type": "MultiPolygon", "coordinates": [[r] for r in rings]},
    }]}
    json.dump(fc, io.open(OUT, "w", encoding="utf-8"), separators=(",", ":"))
    print(f"rings: {len(rings)}  mainland pts: {len(rings[0])}  -> {os.path.relpath(OUT, HERE)} "
          f"({round(os.path.getsize(OUT) / 1024, 1)} KB)")


if __name__ == "__main__":
    main()
