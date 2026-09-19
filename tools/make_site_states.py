#!/usr/bin/env python3
"""Stamp a US state/territory code onto every bundled radar site.

The three site files ship as {id, name, lat, lon}. The Radar Atlas wants to filter by state and by
region (CONUS / Alaska / Hawaii / Caribbean / Pacific), and neither is derivable at runtime without
carrying polygon data into the app -- so it is resolved OFFLINE, here, and written back into the
same files as an extra "st" field. This mirrors the other generated catalogs (SPC risk, radar ramps,
places): REGENERATE, NEVER HAND-EDIT the "st" values.

Source of truth for the polygons is the basemap's own state layer,
Anvil.App/Assets/Map/state-boundaries.geojson (52 features: 50 states + DC + Puerto Rico), so the
Atlas can never disagree with the state lines drawn on the map.

Sites the polygons cannot answer for (Guam, the Virgin Islands, and anything whose antenna sits
offshore of its own state) fall back in two steps: nearest polygon within NEAR_KM, then the
hand-kept OUTLYING table below. A site that survives both is reported and left without a state --
it will simply not appear under any state filter.

Usage (from the repo root):
    py -3 tools/make_site_states.py            # rewrite the three site files in place
    py -3 tools/make_site_states.py --check    # report only, touch nothing (CI-friendly)
"""

import json
import math
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
STATES_GEOJSON = ROOT / "Anvil.App" / "Assets" / "Map" / "state-boundaries.geojson"
SITE_FILES = [
    ROOT / "Anvil.App" / "Assets" / "Radar" / "radar-sites.json",
    ROOT / "Anvil.App" / "Assets" / "Radar" / "research-radar-sites.json",
    ROOT / "Anvil.App" / "Assets" / "Radar" / "tdwr-sites.json",
]

# How far offshore an antenna may sit and still be claimed by the nearest state.
NEAR_KM = 120.0

USPS = {
    "Alabama": "AL", "Alaska": "AK", "Arizona": "AZ", "Arkansas": "AR", "California": "CA",
    "Colorado": "CO", "Connecticut": "CT", "Delaware": "DE", "District of Columbia": "DC",
    "Florida": "FL", "Georgia": "GA", "Hawaii": "HI", "Idaho": "ID", "Illinois": "IL",
    "Indiana": "IN", "Iowa": "IA", "Kansas": "KS", "Kentucky": "KY", "Louisiana": "LA",
    "Maine": "ME", "Maryland": "MD", "Massachusetts": "MA", "Michigan": "MI", "Minnesota": "MN",
    "Mississippi": "MS", "Missouri": "MO", "Montana": "MT", "Nebraska": "NE", "Nevada": "NV",
    "New Hampshire": "NH", "New Jersey": "NJ", "New Mexico": "NM", "New York": "NY",
    "North Carolina": "NC", "North Dakota": "ND", "Ohio": "OH", "Oklahoma": "OK", "Oregon": "OR",
    "Pennsylvania": "PA", "Puerto Rico": "PR", "Rhode Island": "RI", "South Carolina": "SC",
    "South Dakota": "SD", "Tennessee": "TN", "Texas": "TX", "Utah": "UT", "Vermont": "VT",
    "Virginia": "VA", "Washington": "WA", "West Virginia": "WV", "Wisconsin": "WI",
    "Wyoming": "WY",
}

# Places with no polygon in the basemap's state layer: US territories, and the overseas military
# WSR-88Ds. Keyed by ICAO because there is nothing else to key on. The overseas four get their HOST
# country's ISO code rather than a faked state -- the field means "state or territory", and the app's
# region mapping (RadarSiteRegion) is what turns any of these into Pacific / Caribbean / Atlantic.
OUTLYING = {
    "PGUA": "GU",   # Andersen AFB, Guam
    "TIST": "VI",   # St Thomas
    "TISX": "VI",   # St Croix
    "RODN": "JP",   # Kadena AB, Okinawa
    "RKJK": "KR",   # Kunsan AB
    "RKSG": "KR",   # Camp Humphreys
    "LPLA": "PT",   # Lajes Field, Azores
}


def load_rings(path):
    """[(state_code, [ring, ...], bbox)] -- ring 0 is the outer boundary, the rest are holes."""
    data = json.loads(path.read_text(encoding="utf-8"))
    out = []
    for feature in data["features"]:
        name = feature["properties"].get("name")
        code = USPS.get(name)
        if code is None:
            print(f"  ! no USPS code for feature '{name}' -- skipped")
            continue
        geometry = feature["geometry"]
        polygons = geometry["coordinates"]
        if geometry["type"] == "Polygon":
            polygons = [polygons]
        for polygon in polygons:
            xs = [p[0] for p in polygon[0]]
            ys = [p[1] for p in polygon[0]]
            out.append((code, polygon, (min(xs), min(ys), max(xs), max(ys))))
    return out


def in_ring(lon, lat, ring):
    """Ray casting. A ring is a closed list of [lon, lat]."""
    inside = False
    n = len(ring)
    j = n - 1
    for i in range(n):
        xi, yi = ring[i][0], ring[i][1]
        xj, yj = ring[j][0], ring[j][1]
        if (yi > lat) != (yj > lat):
            if lon < (xj - xi) * (lat - yi) / (yj - yi) + xi:
                inside = not inside
        j = i
    return inside


def in_polygon(lon, lat, polygon):
    if not in_ring(lon, lat, polygon[0]):
        return False
    return not any(in_ring(lon, lat, hole) for hole in polygon[1:])


def km_between(lon1, lat1, lon2, lat2):
    mean_lat = math.radians((lat1 + lat2) / 2.0)
    dx = (lon2 - lon1) * 111.320 * math.cos(mean_lat)
    dy = (lat2 - lat1) * 110.574
    return math.hypot(dx, dy)


def nearest_state(lon, lat, rings):
    """Closest boundary VERTEX across every polygon -- coarse, but these are coastal near-misses."""
    best, best_km = None, float("inf")
    for code, polygon, bbox in rings:
        if km_between(lon, lat, max(bbox[0], min(lon, bbox[2])), max(bbox[1], min(lat, bbox[3]))) > best_km:
            continue
        for point in polygon[0]:
            km = km_between(lon, lat, point[0], point[1])
            if km < best_km:
                best, best_km = code, km
    return best, best_km


def resolve(site, rings):
    lon, lat = site["lon"], site["lat"]
    for code, polygon, bbox in rings:
        if bbox[0] <= lon <= bbox[2] and bbox[1] <= lat <= bbox[3] and in_polygon(lon, lat, polygon):
            return code, "inside"
    if site["id"].upper() in OUTLYING:
        return OUTLYING[site["id"].upper()], "table"
    code, km = nearest_state(lon, lat, rings)
    if code is not None and km <= NEAR_KM:
        return code, f"nearest {km:.0f} km"
    return None, "unresolved"


def main():
    check_only = "--check" in sys.argv
    print(f"reading {STATES_GEOJSON.relative_to(ROOT)}")
    rings = load_rings(STATES_GEOJSON)
    print(f"  {len(rings)} polygons over {len({r[0] for r in rings})} states")

    unresolved, changed_files = [], 0
    for path in SITE_FILES:
        sites = json.loads(path.read_text(encoding="utf-8"))
        far, changed, offshore = 0, 0, []
        for site in sites:
            code, how = resolve(site, rings)
            if code is None:
                unresolved.append(site["id"])
            elif how != "inside":
                far += 1
                offshore.append(f"{site['id']} -> {code} ({how})")
            if site.get("st") != code:
                changed += 1
            if code is None:
                site.pop("st", None)
            else:
                site["st"] = code
        label = path.name
        print(f"{label}: {len(sites)} sites, {changed} changed, {far} resolved off-polygon")
        for line in offshore:
            print(f"    {line}")
        if not check_only and changed:
            path.write_text(json.dumps(sites, indent=4) + "\n", encoding="utf-8")
            changed_files += 1

    if unresolved:
        print(f"UNRESOLVED ({len(unresolved)}): {', '.join(unresolved)}")
        print("  add them to OUTLYING above, or leave them stateless on purpose.")
    if check_only:
        print("check only -- nothing written")
    else:
        print(f"wrote {changed_files} file(s)")
    return 1 if unresolved else 0


if __name__ == "__main__":
    sys.exit(main())
