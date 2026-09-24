"""
check_archive_start.py - find the absolute earliest radar PastCast can show, straight from the archive.

PastCast's Timeframe calendar opens at a HARD-CODED year (RadarViewModel.PastEventStartYear, mirrored by
SavedEventLibrary.ArchiveStart). This script asks the NEXRAD Level II archive bucket
(unidata-nexrad-level2) what actually exists, walking its yyyy/mm/dd/SITE/ prefixes in ascending order,
and reports the first volume the app would really load - then compares that to the calendar floor.

Three modes:
    py -3 tools/check_archive_start.py                  # earliest volume anywhere in the archive
    py -3 tools/check_archive_start.py --site KTLX      # earliest volume for one site
    py -3 tools/check_archive_start.py --all-sites      # first day for EVERY bundled NEXRAD site

A "volume" MIRRORS Level2RadarService: IsVolumeKey (modern *_Vnn, legacy <ICAO><yyyymmdd>_<hhmmss>,
optional .gz, never _MDM) and MinVolumeBytes (100 KB - smaller objects are aborted scans). Same filter as
check_saved_events.py. Change all three or none. A day whose only objects are junk does NOT count, and
year folders before 1988 are ignored (the bucket has a 1970/01/01 of TDWR volumes with an unset clock).

Exit 1 if the calendar floor is LATER than real data (the app hides reachable history); a floor earlier
than the data is only reported (the calendar offers empty days, which list nothing - harmless).

This proves the data is LISTED, not that it DECODES: check the earliest volume with
radar_reference.py (Py-ART) and then in the app.

Stdlib only. ASCII output (Windows consoles).
"""
import argparse
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor
from datetime import date, datetime, timedelta, timezone

BUCKET = "https://unidata-nexrad-level2.s3.amazonaws.com/"
S3 = "{http://s3.amazonaws.com/doc/2006-03-01/}"
MIN_VOLUME_BYTES = 100_000
KEY_TIME = re.compile(r"^[A-Z0-9]{4}(\d{8})_(\d{6})")
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..")
WORKERS = 16
FIRST_PLAUSIBLE_YEAR = 1988  # before any WSR-88D existed; earlier folders are clock artefacts


def is_volume_key(key):
    name = key[:-3] if key.endswith(".gz") else key
    name = name[name.rfind("/") + 1:]
    if "_MDM" in name:
        return False
    if len(name) >= 4 and name[-4] == "_" and name[-3] == "V" and name[-2:].isdigit():
        return True
    return (len(name) == 19 and name[0].isalpha() and name[12] == "_"
            and name[4:12].isdigit() and name[13:19].isdigit())


def _list(prefix, delimiter):
    """Yields (common_prefixes, contents) pages for one listing, following continuation tokens."""
    token = None
    while True:
        url = f"{BUCKET}?list-type=2&prefix={urllib.parse.quote(prefix)}&max-keys=1000"
        if delimiter:
            url += "&delimiter=%2F"
        if token:
            url += f"&continuation-token={urllib.parse.quote(token)}"
        for attempt in range(3):
            try:
                with urllib.request.urlopen(url, timeout=30) as resp:
                    root = ET.fromstring(resp.read())
                break
            except Exception:
                if attempt == 2:
                    raise
                time.sleep(1 + attempt)
        prefixes = [p.findtext(S3 + "Prefix") for p in root.iter(S3 + "CommonPrefixes")]
        contents = [(c.findtext(S3 + "Key") or "", int(c.findtext(S3 + "Size") or "0"))
                    for c in root.iter(S3 + "Contents")]
        yield prefixes, contents
        if root.findtext(S3 + "IsTruncated") == "true":
            token = root.findtext(S3 + "NextContinuationToken")
        else:
            return


def children(prefix):
    """The next path segment under a prefix ('' -> years, '1991/' -> months, ...), sorted."""
    out = []
    for prefixes, _ in _list(prefix, True):
        out += [p[len(prefix):].rstrip("/") for p in prefixes]
    return sorted(out)


def volume_times(site, day):
    """Sorted UTC times of every volume the app would load for a site on a day."""
    times = []
    for _, contents in _list(f"{day:%Y/%m/%d}/{site}/", False):
        for key, size in contents:
            if not is_volume_key(key) or size < MIN_VOLUME_BYTES:
                continue
            m = KEY_TIME.match(key[key.rfind("/") + 1:])
            if m:
                times.append((datetime.strptime(m.group(1) + m.group(2), "%Y%m%d%H%M%S")
                              .replace(tzinfo=timezone.utc), key, size))
    return sorted(times)


def archive_years():
    """(real years, bogus years). The bucket holds a 1970/01/01 folder: ~47 TDWR sites whose clock was
    unset (Unix epoch zero), not a 1970 event - no WSR-88D/TDWR data predates the network (~1990)."""
    years = [y for y in children("") if y.isdigit()]
    return [y for y in years if int(y) >= FIRST_PLAUSIBLE_YEAR], [y for y in years if int(y) < FIRST_PLAUSIBLE_YEAR]


def archive_days():
    """Every day prefix in the bucket, ascending (lazy: years/months listed as reached)."""
    for y in archive_years()[0]:
        for m in children(f"{y}/"):
            for d in children(f"{y}/{m}/"):
                try:
                    yield date(int(y), int(m), int(d))
                except ValueError:
                    continue


def sites_on(day):
    return children(f"{day:%Y/%m/%d}/")


def app_floor():
    """(calendar floor year, saved-event floor) read from the source, so the check can't go stale."""
    vm = os.path.join(ROOT, "Anvil.Core", "ViewModels", "Radar", "RadarViewModel.cs")
    lib = os.path.join(ROOT, "Anvil.Core", "Services", "Radar", "SavedEventLibrary.cs")
    with open(vm, encoding="utf-8") as f:
        year = int(re.search(r"PastEventStartYear\s*=\s*(\d{4})", f.read()).group(1))
    with open(lib, encoding="utf-8") as f:
        m = re.search(r"ArchiveStart\s*=\s*new\(\s*(\d{4}),\s*(\d+),\s*(\d+)", f.read())
    saved = date(int(m.group(1)), int(m.group(2)), int(m.group(3))) if m else None
    return date(year, 1, 1), saved


def first_volume(day_iter, site=None):
    """Walks days ascending; returns (time, key, size, site) of the first real volume, plus skip notes."""
    skipped = []
    for day in day_iter:
        candidates = [site] if site else sites_on(day)
        if site and site not in sites_on(day):
            continue
        best = None
        for s in candidates:
            vols = volume_times(s, day)
            if vols and (best is None or vols[0][0] < best[0]):
                best = (*vols[0], s)
        if best:
            return best, skipped
        skipped.append(day)  # prefixes exist but hold only junk (MDM / aborted scans)
    return None, skipped


def report_floor(earliest_day, what):
    bogus = archive_years()[1]
    if bogus:
        print(f"\n  ignored  year folder(s) {', '.join(bogus)}: clock artefacts (before {FIRST_PLAUSIBLE_YEAR})")
    cal, saved = app_floor()
    print(f"\n  calendar floor (PastEventStartYear)  {cal}")
    print(f"  saved-event floor (ArchiveStart)     {saved}")
    print(f"  earliest real data ({what}){' ' * max(0, 16 - len(what))}{earliest_day}")
    if cal > earliest_day:
        print(f"  FAIL  the calendar hides {(cal - earliest_day).days} day(s) of real data")
        return 1
    gap = (earliest_day - cal).days
    print(f"  ok    calendar reaches all data" + (f" (offers {gap} empty day(s) before it)" if gap else ""))
    return 0


def run_one(site):
    label = site or "any site"
    print(f"Walking the archive for the earliest volume ({label}) ...")
    found, skipped = first_volume(archive_days(), site)
    for d in skipped:
        print(f"  skip  {d}  has prefixes but no loadable volume")
    if not found:
        print(f"  no loadable volume found for {label}")
        return 1
    t, key, size, s = found
    print(f"\n  EARLIEST  {s}  {t:%Y-%m-%d %H:%M:%S}Z  ({size / 1e6:.1f} MB)")
    print(f"  key       {key}")
    print(f"  decode it: py -3.12 tools/radar_reference.py {s} {t:%Y-%m-%d %H:%M}")
    return report_floor(t.date(), label)


def run_all(until):
    path = os.path.join(ROOT, "Anvil.App", "Assets", "Radar", "radar-sites.json")
    with open(path, encoding="utf-8") as f:
        wanted = {s["id"] for s in json.load(f) if not s.get("retired")}
    first = {}
    print(f"Walking every archive day for {len(wanted)} bundled sites (stops when all are seen) ...")
    days = [d for d in archive_days() if d <= until] if until else list(archive_days())
    print(f"  {len(days)} day prefixes, {days[0]} .. {days[-1]}")
    batch = WORKERS * 4
    with ThreadPoolExecutor(WORKERS) as pool:
        for i in range(0, len(days), batch):
            chunk = days[i:i + batch]
            for day, sites in zip(chunk, pool.map(sites_on, chunk)):
                for s in sites:
                    if s in wanted and s not in first:
                        first[s] = day
            if i == 0 or chunk[0].year != chunk[-1].year:
                print(f"  .. {chunk[-1]}  {len(first)}/{len(wanted)} seen")
            if wanted <= first.keys():
                break
    # A prefix can hold only junk: confirm each site's first day has a loadable volume, else look later.
    print("  confirming each first day holds a loadable volume ...")
    with ThreadPoolExecutor(WORKERS) as pool:
        ok = dict(zip(first, pool.map(lambda s: bool(volume_times(s, first[s])), first)))
    for s in [s for s, good in ok.items() if not good]:
        later = [d for d in days if d > first[s]]
        found, _ = first_volume(iter(later), s)
        first[s] = found[0].date() if found else None
    print()
    for s, d in sorted(((s, d) for s, d in first.items() if d), key=lambda x: (x[1], x[0])):
        print(f"  {s}  {d}")
    missing = sorted(wanted - {s for s, d in first.items() if d})
    if missing:
        print(f"\n  never seen{' before ' + str(until) if until else ''}: {' '.join(missing)}")
    earliest = min(d for d in first.values() if d)
    return report_floor(earliest, "all sites")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--site", help="one ICAO, e.g. KTLX")
    g.add_argument("--all-sites", action="store_true", help="first day for every bundled NEXRAD site")
    ap.add_argument("--until", type=date.fromisoformat, help="--all-sites: stop at this day (YYYY-MM-DD)")
    args = ap.parse_args()
    try:
        return run_all(args.until) if args.all_sites else run_one(args.site and args.site.upper())
    except Exception as ex:  # network / XML
        print(f"  FAIL  listing error: {ex}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
