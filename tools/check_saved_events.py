"""
check_saved_events.py - prove every saved-event leg points at real radar data.

Reads Anvil.Core/Assets/saved-events.json (or --file) and, for each leg, lists the NEXRAD Level II
archive bucket (unidata-nexrad-level2) for that site over the leg's UTC window, counting the volumes
the app would actually load. A leg with fewer than --min volumes FAILS, so a curated built-in can never
ship pointing at an empty loop.

The volume filter MIRRORS Level2RadarService: IsVolumeKey (modern *_Vnn, legacy <ICAO><yyyymmdd>_<hhmmss>,
optional .gz, never _MDM) and MinVolumeBytes (100 KB - smaller objects are aborted scans). Change both or
neither.

Also re-checks the rules SavedEventLibrary.Validate enforces (window length on the picker, start on a
5-minute mark, a leg's key time overlapping its window), and that every built-in names a type
(tornado/hurricane/derecho) and every leg a key time, so the script is useful before the app is even built.

    py -3 tools/check_saved_events.py
    py -3 tools/check_saved_events.py --file some-other.json --min 5

Stdlib only. ASCII output (Windows consoles).
"""
import argparse
import json
import os
import re
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone

BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com/"
S3 = "{http://s3.amazonaws.com/doc/2006-03-01/}"
MIN_VOLUME_BYTES = 100_000
ALLOWED_MINUTES = (30, 60, 120, 180, 360, 720)
EVENT_TYPES = ("tornado", "hurricane", "derecho")  # MIRRORS SavedEventLibrary.ReadKind; built-ins must name one
KEY_TIME = re.compile(r"^[A-Z0-9]{4}(\d{8})_(\d{6})")


def is_volume_key(key):
    name = key[:-3] if key.endswith(".gz") else key
    name = name[name.rfind("/") + 1:]
    if "_MDM" in name:
        return False
    if len(name) >= 4 and name[-4] == "_" and name[-3] == "V" and name[-2:].isdigit():
        return True
    return (len(name) == 19 and name[0].isalpha() and name[12] == "_"
            and name[4:12].isdigit() and name[13:19].isdigit())


def list_day(site, day):
    prefix = f"{day:%Y/%m/%d}/{site}/"
    token = None
    while True:
        url = f"{BUCKET}?list-type=2&prefix={urllib.parse.quote(prefix)}&max-keys=1000"
        if token:
            url += f"&continuation-token={urllib.parse.quote(token)}"
        with urllib.request.urlopen(url, timeout=30) as resp:
            root = ET.fromstring(resp.read())
        for c in root.iter(S3 + "Contents"):
            key = c.findtext(S3 + "Key") or ""
            size = int(c.findtext(S3 + "Size") or "0")
            if is_volume_key(key) and size >= MIN_VOLUME_BYTES:
                yield key
        if root.findtext(S3 + "IsTruncated") == "true":
            token = root.findtext(S3 + "NextContinuationToken")
        else:
            return


def volumes_in_window(site, start, end):
    found = []
    day = datetime(start.year, start.month, start.day, tzinfo=timezone.utc)
    while day <= end:
        for key in list_day(site, day):
            m = KEY_TIME.match(key[key.rfind("/") + 1:])
            if not m:
                continue
            t = datetime.strptime(m.group(1) + m.group(2), "%Y%m%d%H%M%S").replace(tzinfo=timezone.utc)
            if start <= t <= end:
                found.append(t)
        day += timedelta(days=1)
    return sorted(found)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    ap.add_argument("--file", default=os.path.join(here, "..", "Anvil.Core", "Assets", "saved-events.json"))
    ap.add_argument("--min", type=int, default=3, help="fewest volumes a leg may hold (default 3)")
    args = ap.parse_args()

    with open(args.file, encoding="utf-8") as f:
        events = json.load(f).get("events", [])

    failures = 0
    for ev in events:
        print(f"{ev.get('id')}  {ev.get('name')}")
        if not ev.get("source"):
            print("  WARN  no source recorded for the times")
        if ev.get("type") not in EVENT_TYPES:
            print(f"  FAIL  type {ev.get('type')!r} is not one of {', '.join(EVENT_TYPES)}")
            failures += 1
        for i, leg in enumerate(ev.get("legs", [])):
            site = leg.get("site")
            start = datetime.fromisoformat(leg["startUtc"].replace("Z", "+00:00")).astimezone(timezone.utc)
            minutes = leg["minutes"]
            end = start + timedelta(minutes=minutes)
            label = f"  leg {i}  {site or '----'}  {start:%Y-%m-%d %H:%MZ} +{minutes}m"

            problems = []
            if minutes not in ALLOWED_MINUTES:
                problems.append(f"{minutes} min is not a Timeframe window")
            if start.minute % 5 or start.second:
                problems.append("start is not on a 5-minute mark")
            # The leg's KEY time. MIRRORS SavedEventLibrary.ValidateKey: end after start, and the key must
            # OVERLAP the window (not sit inside it). Change both or neither.
            key = leg.get("key")
            if key is not None:
                k_start = datetime.fromisoformat(key["startUtc"].replace("Z", "+00:00")).astimezone(timezone.utc)
                k_end = (datetime.fromisoformat(key["endUtc"].replace("Z", "+00:00")).astimezone(timezone.utc)
                         if key.get("endUtc") else None)
                if k_end is not None and k_end <= k_start:
                    problems.append("key ends before it starts")
                if k_start > end or (k_end or k_start) < start:
                    problems.append("key time doesn't overlap the window")
            else:
                print("  WARN  leg %d has no key time" % i)
            if problems:
                print(f"{label}  FAIL  {'; '.join(problems)}")
                failures += 1
                continue
            if site is None:
                print(f"{label}  ok    window only (no site to check)")
                continue

            try:
                vols = volumes_in_window(site, start, end)
            except Exception as ex:  # network / XML: report and count, don't crash the run
                print(f"{label}  FAIL  listing error: {ex}")
                failures += 1
                continue

            if len(vols) < args.min:
                print(f"{label}  FAIL  {len(vols)} volume(s), need {args.min}")
                failures += 1
            else:
                print(f"{label}  ok    {len(vols)} volumes  {vols[0]:%H:%M}Z .. {vols[-1]:%H:%M}Z")

    print(f"\n{len(events)} event(s), {failures} failing leg(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
