#!/usr/bin/env python3
"""legacy_volume_scan.py - what a LEGACY (.gz-sourced) Level II volume is made of, and what a
single-tilt extract of it would cost.

WHY: pre-2016 archive volumes gunzip to a fully-uncompressed AR2V with no bzip2 LDM records, so
`Level2Format.TryExtractLowestTilt` - which walks LDM blocks - finds nothing and the caller falls back to
caching the WHOLE volume (see Level2RadarService, the `toWrite is null` path). PastCast therefore ships
~43 MB to the WebView to render one tilt, and pays for it on every frame. This measures what an
uncompressed-AR2V walker would save, so that work can be costed before it is written.

It is also the INDEPENDENT re-derivation of the format, in the same spirit as tools/TiltCheck: this walks
the bytes with no reference to the C# implementation, so if a future extractor disagrees with this, one of
the two is wrong and it is worth finding out which.

    py -3.12 tools/legacy_volume_scan.py          # scans the app's RadarLevel2 cache

Measured 2026-09-08 over 105 cached legacy volumes (KINX/KSGF/KTLX/KVNX, 2013-05-31/06-01):
every volume walked to exactly 100% of its length, every one resolved a Doppler companion, every base cut
carried REF and every companion carried VEL. 41-43 MB -> 7.67 MB, a consistent 5.4-5.6x.

FORMAT (validated by the 100% walk, not assumed):
  24-byte AR2V volume header, then a sequence of messages, each:
    12-byte legacy CTM header, 16-byte message header, body.
    message size = big-endian u16 at messageHeader+0, in HALFWORDS, covering the 16-byte header + body.
    message type = messageHeader+3.  Type 31 (digital radial) is VARIABLE length -> record = 12 + size*2.
    Everything else sits in a fixed 2432-byte frame.
  In a Message 31 body: ICAO at +0, elevation NUMBER at +22, elevation ANGLE (big-endian f32) at +24 -
  the same +22/+24 offsets Level2Format.ElevationOf / ElevationAngleOf use, but reachable at a known
  offset here, so no IndexOf scan for the ICAO is needed.

- WARNING - A CUT'S ANGLE IS ITS MEDIAN, NEVER ITS FIRST RADIAL. The first radial of a cut is a settling
radial that reads low - a real 0.5 deg base reads 0.26 deg. Using it, the 0.48 deg Doppler companion sits
0.22 deg away and falls outside the 0.20 deg pairing tolerance, so the companion is DROPPED and the
extract has no velocity at all. This script got that wrong on its first run. Same trap as
docs/radar-tilts.md.
"""
import struct, sys, os, statistics
from collections import defaultdict

def analyse(path):
    d = open(path,'rb').read()
    pos, meta, n = 24, 0, 0
    eb, er, eang = defaultdict(int), defaultdict(int), defaultdict(list)
    emom = defaultdict(set)
    while pos + 28 <= len(d):
        mh = pos + 12
        size_hw = struct.unpack_from('>H', d, mh)[0]
        mtype = d[mh+3]
        rec = 12 + size_hw*2 if mtype == 31 else 2432
        if rec <= 12 or pos + rec > len(d): break
        if mtype == 31:
            body = mh + 16
            elev = d[body+22]
            eb[elev] += rec; er[elev] += 1
            eang[elev].append(struct.unpack_from('>f', d, body+24)[0])
            blk = d[pos:pos+rec]
            for name in (b'DREF', b'DVEL', b'DCFP', b'DPHI', b'DZDR', b'DRHO'):
                if name in blk: emom[elev].add(name.decode()[1:])
        else:
            meta += rec
        pos += rec; n += 1
    med = {e: statistics.median(v) for e, v in eang.items()}
    if not eb: return None
    base = min(eb)
    # MEDIAN angle, matching Level2Format's rule (a first radial is a settling radial and reads low).
    comp = [e for e in eb if e != base and abs(med[e] - med[base]) < 0.20]
    keep = meta + eb[base] + sum(eb[e] for e in comp)
    return dict(name=os.path.basename(path), size=len(d), walked=pos, msgs=n, meta=meta,
                elevs=len(eb), base=base, base_med=med[base],
                comp=[(e, round(med[e],2)) for e in comp],
                base_mom=sorted(emom[base]), comp_mom=sorted(set().union(*[emom[e] for e in comp]) if comp else []),
                keep=keep, pct=100*keep/len(d), ratio=len(d)/keep)

C = os.path.join(os.environ['LOCALAPPDATA'],'Packages','Anvil_c78sma71wz9qr','LocalCache','Local','Anvil','RadarLevel2')
files = sorted(f for f in os.listdir(C) if f.endswith('.V06') and '_live_' not in f)
picks, seen = [], set()
for f in files:                       # one volume per site
    s = f.split('_')[0]
    if s not in seen: seen.add(s); picks.append(f)
print(f"{'volume':<30}{'MB':>7}{'walk%':>7}{'elev':>5}{'base':>6}{'companion':>16}{'keep MB':>9}{'keep%':>7}{'x':>6}  moments")
for f in picks[:8]:
    r = analyse(os.path.join(C,f))
    if not r: print(f"  {f}: walk failed"); continue
    print(f"{r['name']:<30}{r['size']/1048576:>7.1f}{100*r['walked']/r['size']:>6.1f}%"
          f"{r['elevs']:>5}{r['base_med']:>6.2f}{str(r['comp']):>16}"
          f"{r['keep']/1048576:>9.2f}{r['pct']:>6.1f}%{r['ratio']:>5.1f}x  "
          f"base={'+'.join(r['base_mom'])} comp={'+'.join(r['comp_mom'])}")

print("\n=== LEGACY volumes only (unextracted, >20 MB) ===")
legacy = [f for f in files if os.path.getsize(os.path.join(C,f)) > 20*1048576]
print(f"{len(legacy)} legacy volume(s) cached")
import collections
agg = collections.defaultdict(list)
for f in legacy:
    r = analyse(os.path.join(C,f))
    if not r: print(f"  {f}: WALK FAILED"); continue
    agg[f.split('_')[0]].append(r)
    if r['walked'] != r['size']: print(f"  {r['name']}: PARTIAL WALK {100*r['walked']/r['size']:.1f}%")
for site, rs in sorted(agg.items()):
    keeps=[x['ratio'] for x in rs]; pcts=[x['pct'] for x in rs]
    mb=[x['size']/1048576 for x in rs]; km=[x['keep']/1048576 for x in rs]
    comps=set(len(x['comp']) for x in rs); el=set(x['elevs'] for x in rs)
    print(f"  {site}: {len(rs):>3} vols  {min(mb):.1f}-{max(mb):.1f} MB -> {min(km):.2f}-{max(km):.2f} MB"
          f"  ({min(pcts):.1f}-{max(pcts):.1f}%, {min(keeps):.1f}-{max(keeps):.1f}x)  elevs={sorted(el)} companions={sorted(comps)}")
allr=[x for rs in agg.values() for x in rs]
if allr:
    print(f"\n  TOTAL cached legacy: {sum(x['size'] for x in allr)/1048576:,.0f} MB"
          f" -> {sum(x['keep'] for x in allr)/1048576:,.0f} MB"
          f"  ({sum(x['size'] for x in allr)/sum(x['keep'] for x in allr):.1f}x)")
    print(f"  every volume walked 100%: {all(x['walked']==x['size'] for x in allr)}")
    print(f"  every volume found a Doppler companion: {all(x['comp'] for x in allr)}")
    print(f"  every base cut has REF: {all('REF' in x['base_mom'] for x in allr)}"
          f"; every companion has VEL: {all('VEL' in x['comp_mom'] for x in allr)}")
