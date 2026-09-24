#!/usr/bin/env python3
"""Check (and fix) every bundled radar site's antenna coordinates against the radar's OWN volume header.

WHY: the app projects every decoded gate from the site file's lat/lon (RadarSite.Latitude/Longitude ->
radar.js siteLat/siteLon), so a wrong coordinate moves the whole image. An audit on 2026-09-24 found 42 of
43 TDWRs 0.4-9.6 km off (TMCO 9.6, TMSY 9.5), KIWX 5.6 km off (latitude typo, exactly 0.05 deg) and RKSG
36 km off (the antenna moved) -- the TDWR list claimed header coordinates but did not have them.

SOURCE OF TRUTH: the Message 31 volume data block ("RVOL") of a real Level II volume from the
unidata-nexrad-level2 archive: float32 latitude + longitude right after the block's 8-byte header. It is
the position the radar itself reports -- the same one Py-ART uses -- and the NWS radar-stations API agrees
with it. Only the first ~2.5 MB of one volume is fetched (a Range GET); RVOL is in the first radials.

WHICH VOLUME: a working site uses the most recent day that has data (yesterday UTC, then back off); a
RETIRED site ("retired": "yyyy-MM-dd" -- a moved/renamed radar, see RadarSite.RetiredOn) searches back
from its retired day, because its id has no data after it.

TOLERANCE: a site is rewritten only when it is more than TOL_KM off. The existing lists carry 4-decimal
positions from other sources that sit ~40 m from the header (median 44 m) -- inside one gate, harmless, and
not worth churning 150 lines for. Rewritten values are rounded to 4 decimals (~10 m).

Usage (from the repo root; network required):
    py -3 tools/check_site_coords.py            # rewrite the off sites in place
    py -3 tools/check_site_coords.py --check    # report only, touch nothing; exit 1 if any site is off
Then run tools/make_site_states.py --check (a moved antenna can change state, e.g. none so far).
"""

import bz2
import concurrent.futures as cf
import datetime as dt
import json
import math
import re
import struct
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SITE_FILES = [
    ROOT / "Anvil.App" / "Assets" / "Radar" / "radar-sites.json",
    ROOT / "Anvil.App" / "Assets" / "Radar" / "research-radar-sites.json",
    ROOT / "Anvil.App" / "Assets" / "Radar" / "tdwr-sites.json",
]
BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com"
TOL_KM = 0.1
RANGE_BYTES = 2_500_000
# Days back to try, from the anchor day (yesterday for a working site, the retired day for a retired one).
# Wide on purpose: KCRI publishes intermittently and a site can be down for weeks.
BACKOFF_DAYS = [0, 1, 2, 7, 14, 30, 60, 120, 240, 365]


def km_between(lat1, lon1, lat2, lon2):
    p = math.radians
    a = math.sin(p(lat2 - lat1) / 2) ** 2 + math.cos(p(lat1)) * math.cos(p(lat2)) * math.sin(p(lon2 - lon1) / 2) ** 2
    return 2 * 6371.0 * math.asin(math.sqrt(a))


def fetch(req, timeout):
    """GET with retries: S3 resets the odd connection when 12 threads hit it at once."""
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except OSError:
            if attempt == 3:
                raise
            time.sleep(1.5 * (attempt + 1))


def volume_key(site_id, day):
    url = f"{BUCKET}/?list-type=2&max-keys=20&prefix={day:%Y/%m/%d}/{site_id}/"
    keys = [k for k in re.findall(r"<Key>(.*?)</Key>", fetch(url, 30).decode()) if not k.endswith("_MDM")]
    return keys[len(keys) // 2] if keys else None


def header_position(key):
    """(lat, lon) from the first RVOL block of the volume, or None."""
    req = urllib.request.Request(f"{BUCKET}/{key}", headers={"Range": f"bytes=0-{RANGE_BYTES - 1}"})
    data = fetch(req, 60)
    pos = 24  # the AR2V volume header
    while pos + 4 <= len(data):
        size = abs(struct.unpack(">i", data[pos:pos + 4])[0])
        pos += 4
        record = data[pos:pos + size]
        pos += size
        try:
            raw = bz2.decompress(record)
        except Exception:  # an uncompressed record, or the truncated last one
            raw = record
        i = raw.find(b"RVOL")
        if i >= 0:
            lat, lon = struct.unpack(">ff", raw[i + 8:i + 16])
            return lat, lon
    return None


def locate(site):
    """(day, lat, lon) from the site's own data, or None if no volume was found."""
    retired = site.get("retired")
    anchor = dt.date.fromisoformat(retired) if retired else dt.datetime.now(dt.timezone.utc).date() - dt.timedelta(days=1)
    for back in BACKOFF_DAYS:
        day = anchor - dt.timedelta(days=back)
        key = volume_key(site["id"], day)
        if key:
            found = header_position(key)
            if found:
                return day, found[0], found[1]
    return None


def main():
    check_only = "--check" in sys.argv
    off_total, missing = 0, []
    for path in SITE_FILES:
        sites = json.loads(path.read_text(encoding="utf-8"))
        with cf.ThreadPoolExecutor(12) as pool:
            found = list(pool.map(locate, sites))
        off = []
        for site, hit in zip(sites, found):
            if hit is None:
                missing.append(site["id"])
                continue
            day, lat, lon = hit
            km = km_between(site["lat"], site["lon"], lat, lon)
            if km > TOL_KM:
                off.append((km, site, day, round(lat, 4), round(lon, 4)))
        print(f"{path.name}: {len(sites)} sites, {len(off)} off by > {TOL_KM} km")
        for km, site, day, lat, lon in sorted(off, key=lambda o: -o[0]):
            print(f"    {site['id']}  {km:6.2f} km  ({site['lat']}, {site['lon']}) -> ({lat}, {lon})  [{day}]")
            if not check_only:
                site["lat"], site["lon"] = lat, lon
        off_total += len(off)
        if off and not check_only:
            path.write_text(json.dumps(sites, indent=4) + "\n", encoding="utf-8")

    if missing:
        print(f"NO VOLUME FOUND ({len(missing)}): {', '.join(missing)} -- left as they are")
    if check_only:
        print("check only -- nothing written")
        return 1 if off_total else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
