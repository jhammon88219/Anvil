#!/usr/bin/env python3
"""Dissolve the contiguous US states into a single CONUS boundary for the map mask.

Anvil masks "everything outside CONUS" with an inverted fill (one polygon: a world-rectangle outer ring
with CONUS punched out as holes — see Assets/Map/js/states.js). The holes MUST be disjoint: tiling the raw
49 state rings failed because earcut bridges between the many TOUCHING holes gored the interior with
triangles. So we pre-dissolve the states into their exterior boundary (one mainland ring + coastal islands),
which is disjoint and cuts clean.

No shapely/geopandas dependency — the dissolve is by EDGE CANCELLATION, which works because the Census
cartographic boundaries are topologically consistent (adjacent states share identical border vertices):
an inland border edge appears in exactly two states (opposite directions) and cancels; a coast / national-
border edge appears once and survives. The surviving directed edges stitch head-to-tail into closed rings.

Input : Anvil.App/Assets/Map/state-boundaries.geojson  (normalized {id, properties.name, geometry})
Output: Anvil.App/Assets/Map/conus-boundary.geojson     (one Feature, MultiPolygon of exterior rings)

Usage:  py -3 tools/make_conus_boundary.py
Re-run whenever state-boundaries.geojson changes. Keep COORD_DECIMALS in sync with that file's precision
(shared vertices must round identically for the cancellation to work).
"""
import json
import os

COORD_DECIMALS = 5  # must match state-boundaries.geojson's coordinate precision
EXCLUDE = {"Alaska", "Hawaii", "Puerto Rico"}  # non-contiguous — not part of CONUS

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "Anvil.App", "Assets", "Map", "state-boundaries.geojson")
OUT = os.path.join(HERE, "..", "Anvil.App", "Assets", "Map", "conus-boundary.geojson")


def outer_rings(feature):
    g = feature["geometry"]
    if g["type"] == "Polygon":
        return [g["coordinates"][0]]
    if g["type"] == "MultiPolygon":
        return [poly[0] for poly in g["coordinates"]]
    return []


def key(p):
    return (round(p[0], COORD_DECIMALS), round(p[1], COORD_DECIMALS))


def main():
    gj = json.load(open(SRC))

    # Count undirected edges; keep the directed edges whose undirected form appears exactly once.
    undirected, directed = {}, []
    for f in gj["features"]:
        if f["properties"]["name"] in EXCLUDE:
            continue
        for ring in outer_rings(f):
            for i in range(len(ring) - 1):
                a, b = key(ring[i]), key(ring[i + 1])
                if a == b:
                    continue
                undirected[tuple(sorted((a, b)))] = undirected.get(tuple(sorted((a, b))), 0) + 1
                directed.append((a, b))

    adj = {}
    for a, b in directed:
        if undirected[tuple(sorted((a, b)))] == 1:
            adj.setdefault(a, []).append(b)

    # Stitch surviving directed boundary edges head-to-tail into closed rings.
    rings = []
    for start in list(adj.keys()):
        while adj.get(start):
            ring, cur = [start], start
            while True:
                nxts = adj.get(cur)
                if not nxts:
                    break
                cur = nxts.pop()
                ring.append(cur)
                if cur == start:
                    break
            if len(ring) >= 4 and ring[0] == ring[-1]:
                rings.append(ring)

    rings.sort(key=len, reverse=True)
    leftover = sum(len(v) for v in adj.values())
    if leftover:
        raise SystemExit(f"ERROR: {leftover} boundary edges did not stitch — check for vertex mismatches")

    fc = {"type": "FeatureCollection", "features": [{
        "type": "Feature", "properties": {"name": "CONUS"},
        "geometry": {"type": "MultiPolygon", "coordinates": [[r] for r in rings]},
    }]}
    json.dump(fc, open(OUT, "w"), separators=(",", ":"))
    print(f"rings: {len(rings)}  mainland pts: {len(rings[0])}  -> {os.path.relpath(OUT, HERE)} "
          f"({round(os.path.getsize(OUT) / 1024, 1)} KB)")


if __name__ == "__main__":
    main()
