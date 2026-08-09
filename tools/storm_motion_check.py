#!/usr/bin/env python3
"""
storm_motion_check.py - unit-test the app's AUTOMATIC storm motion (full-volume VAD -> Bunkers), no JS runtime.

`radar-decode.js` derives the Storm-Relative-Velocity storm motion from the radar's OWN Doppler velocity,
fully offline. At a fixed conical elevation phi a uniform horizontal wind (u east, v north) makes the radial
velocity vary sinusoidally with azimuth:
    Vr(az) = a0 + cos(phi)*(u*sin az + v*cos az),
so a per-range-ring least-squares harmonic fit recovers (u,v) at that ring's beam height h. ONE low tilt only
samples ~2-3 km of clean profile near convection -- far short of the 0-6 km a Bunkers estimate needs -- so the
app builds the profile across SEVERAL velocity tilts (vadPointsForCut per cut, each with its own phi) and
MERGES their points into one deep VWP (bunkersFromProfile), which feeds the Bunkers (2000) right-moving
supercell estimate (0-6 km mean wind + 7.5 m/s right of the 0-6 km shear).

This machine has no Node/deno/bun to run the JS, so -- exactly like tools/dealias_check.py mirrors the JS
dealiaser -- this script re-implements `vadPointsForCut` + `bunkersFromProfile` + `solve3` in Python as a
FAITHFUL transcription of the JS, then drives them with SYNTHETIC sweeps whose answer is known ANALYTICALLY:

  * uniform wind, deep single cut  -> recovers that wind EXACTLY; source "Mean wind"
  * linear-shear single cut        -> per-ring VAD recovers wind(h); final = hand-computed Bunkers R
  * MULTI-CUT combine (4 tilts)    -> per-cut motion then componentwise median = the wind exactly
  * combine rejects an outlier     -> a contaminated cut can't drag the median off the consistent cluster
  * shallow-but-usable single cut  -> MEAN WIND (below the Bunkers depth but above the mean-wind floor)
  * too-shallow single cut         -> insufficient (below VWP_MEAN_MIN_TOP -> just boundary-layer flow)
  * too-sparse / wedge-clustered   -> insufficient (no ring can be fit)

No network, no Py-ART, deterministic. Exit 0 = all pass, 1 = a failure. (For a REAL-DATA cross-check against
Py-ART's vad_browning, see tools/storm_motion_realcheck.py.)

WARNING: if you edit vadPointsForCut / bunkersFromProfile / solve3 / the VAD_* / BUNKERS_* / VWP_* constants
in radar-decode.js, mirror the edit below and re-run, or this stops guarding the shipped code. Usage:
  py -3.12 tools/storm_motion_check.py [-v]

Does NOT touch the C# app.
"""
import sys
import math

VERBOSE = "-v" in sys.argv or "--verbose" in sys.argv
D2R = math.pi / 180.0

# ============================================================================================
# ==  Python MIRROR of the JS storm-motion math (radar-decode.js). Keep in sync.             ==
# ============================================================================================
# Constants transcribed verbatim from radar-decode.js:
VAD_MIN_PTS = 30          # min valid gates around a ring to trust its harmonic fit
VAD_MAX_CLUSTER = 0.6     # reject a ring whose az are clustered (resultant length > this => a wedge)
VAD_MAX_RESID = 6.0       # max RMS fit residual (m/s) -- rejects convectively contaminated rings
VAD_MAX_SPEED = 80.0      # reject an implausible fitted wind speed (m/s)
AE_M = 8494667.0          # 4/3-Earth effective radius (m): h ~ r*sinphi + r^2/2ae
BUNKERS_D = 7.5           # Bunkers deviation magnitude (m/s), right of the 0-6 km shear
BUNKERS_MIN_SHEAR = 3.0   # below this 0-6 km shear (m/s) use the mean wind, not a supercell deviation
VWP_MIN_TOP = 5000.0      # merged profile must reach >= this height (m) to trust a 0-6 km Bunkers DEVIATION
VWP_MIN_PTS = 8           # ...and carry at least this many ring points (for the Bunkers deviation)
VWP_MEAN_MIN_TOP = 2500.0 # a shallower profile still gives a serviceable MEAN-WIND motion (no deviation)
VWP_MEAN_MIN_PTS = 5      # ...with at least this many ring points; below this floor -> "insufficient"


def solve3(a, b, c, d, e, f, g, h, ii, r0, r1, r2):
    """Symmetric 3x3 normal system via Cramer's rule; None if near-singular. Mirrors radar-decode solve3."""
    det = a * (e * ii - f * h) - b * (d * ii - f * g) + c * (d * h - e * g)
    if abs(det) < 1e-9:
        return None
    inv = 1.0 / det
    x0 = (r0 * (e * ii - f * h) - b * (r1 * ii - f * r2) + c * (r1 * h - e * r2)) * inv
    x1 = (a * (r1 * ii - f * r2) - r0 * (d * ii - f * g) + c * (d * r2 - r1 * g)) * inv
    x2 = (a * (e * r2 - r1 * h) - b * (d * r2 - r1 * g) + r0 * (d * h - e * g)) * inv
    return (x0, x1, x2)


def vad_points_for_cut(radials, phi_deg, first_gate_km, gate_size_km):
    """Faithful transcription of `vadPointsForCut`. `radials` = list of {'az': deg, 'data': [m/s | None]}
    standing in for one cut's dealiased Doppler velocity; `phi_deg` = this cut's median elevation. Returns a
    list of { 'h', 'u', 'v' } ring points (possibly empty)."""
    n = len(radials)
    phi = phi_deg
    if not (phi == phi) or phi <= 0:   # NaN or non-positive -> the JS 0.5 fallback
        phi = 0.5
    phi_rad = phi * D2R
    cos_phi = math.cos(phi_rad)
    sin_phi = math.sin(phi_rad)

    ref = None
    for d in radials:
        if d and d.get('data'):
            ref = d
            break
    if ref is None:
        return []
    first_gate_m = first_gate_km * 1000.0
    gate_size_m = gate_size_km * 1000.0
    n_gates = len(ref['data'])
    if not (first_gate_m == first_gate_m) or not (gate_size_m > 0):
        return []

    az_sin = [0.0] * n
    az_cos = [0.0] * n
    has = [0] * n
    for i in range(n):
        dd = radials[i]
        if not dd or not dd.get('data'):
            continue
        az = dd['az']
        if not isinstance(az, (int, float)):
            continue
        a = az * D2R
        az_sin[i] = math.sin(a)
        az_cos[i] = math.cos(a)
        has[i] = 1

    stride = max(1, round(1000.0 / gate_size_m))
    points = []
    for j in range(0, n_gates, stride):
        sN = Ss = Sc = Sss = Scc = Ssc = Sv = Svs = Svc = 0.0
        for i in range(n):
            if not has[i]:
                continue
            v = radials[i]['data'][j]
            if v is None:
                continue
            s = az_sin[i]
            c = az_cos[i]
            sN += 1.0
            Ss += s; Sc += c; Sss += s * s; Scc += c * c; Ssc += s * c
            Sv += v; Svs += v * s; Svc += v * c
        if sN < VAD_MIN_PTS:
            continue
        if math.hypot(Ss, Sc) / sN > VAD_MAX_CLUSTER:
            continue
        sol = solve3(sN, Ss, Sc, Ss, Sss, Ssc, Sc, Ssc, Scc, Sv, Svs, Svc)
        if sol is None:
            continue
        a0, a1, b1 = sol
        se = 0.0
        for i in range(n):
            if not has[i]:
                continue
            v = radials[i]['data'][j]
            if v is None:
                continue
            e = v - (a0 + a1 * az_sin[i] + b1 * az_cos[i])
            se += e * e
        if math.sqrt(se / sN) > VAD_MAX_RESID:
            continue
        u = a1 / cos_phi
        vv = b1 / cos_phi
        if not (math.hypot(u, vv) < VAD_MAX_SPEED):
            continue
        r = first_gate_m + j * gate_size_m
        points.append({'h': r * sin_phi + r * r / (2.0 * AE_M), 'u': u, 'v': vv})
    return points


def bunkers_from_profile(prof):
    """Faithful transcription of `bunkersFromProfile`. Merges VAD ring points into a Bunkers storm motion,
    or returns { 'insufficient': True, 'topM': ... } when the profile is too shallow/sparse to trust."""
    if not prof or len(prof) < 2:
        return {'insufficient': True, 'topM': 0}
    prof = sorted(prof, key=lambda p: p['h'])
    top = prof[-1]['h']
    # Genuinely too shallow/sparse for ANY trustworthy motion -> insufficient (the single-low-tilt guard).
    if top < VWP_MEAN_MIN_TOP or len(prof) < VWP_MEAN_MIN_PTS:
        return {'insufficient': True, 'topM': round(top)}

    def mean_layer(h0, h1):
        u = v = 0.0
        c = 0
        for p in prof:
            if h0 <= p['h'] <= h1:
                u += p['u']; v += p['v']; c += 1
        return {'u': u / c, 'v': v / c} if c else None

    mean = mean_layer(0, 6000) or mean_layer(0, top)
    if mean is None:
        return {'insufficient': True, 'topM': round(top)}

    # DEEP enough to trust the 0-6 km shear for a Bunkers deviation? Else the mean wind ALONE (serviceable).
    deep = top >= VWP_MIN_TOP and len(prof) >= VWP_MIN_PTS
    mu, mv, source = mean['u'], mean['v'], 'Mean wind'
    if deep:
        bot = mean_layer(0, 500) or {'u': prof[0]['u'], 'v': prof[0]['v']}
        tp = mean_layer(5500, 6000) or {'u': prof[-1]['u'], 'v': prof[-1]['v']}
        shu = tp['u'] - bot['u']
        shv = tp['v'] - bot['v']
        sh_mag = math.hypot(shu, shv)
        if sh_mag > BUNKERS_MIN_SHEAR:
            mu = mean['u'] + BUNKERS_D * (shv / sh_mag)
            mv = mean['v'] + BUNKERS_D * (-shu / sh_mag)
            source = 'Bunkers R'
    dir_deg = math.atan2(mu, mv) / D2R
    if dir_deg < 0:
        dir_deg += 360
    return {'speedMs': math.hypot(mu, mv), 'dirDeg': dir_deg, 'source': source,
            'layers': len(prof), 'topM': round(top), 'deep': deep, 'mu': mu, 'mv': mv}


VWP_MIN_CUTS = 2


def combine_cut_motions(motions):
    """Faithful transcription of `combineCutMotions`. Componentwise MEDIAN of the sufficient per-cut motions
    (robust to a contaminated cut); { 'insufficient': True } when fewer than VWP_MIN_CUTS cuts are sufficient."""
    good = [m for m in motions if m and not m.get('insufficient')]
    if len(good) < VWP_MIN_CUTS:
        return {'insufficient': True, 'topM': max([m.get('topM', 0) for m in motions] or [0])}
    # Prefer the DEEP (Bunkers-capable) cuts when we have enough; else the shallow mean-wind cuts. Don't blend
    # the tiers (a Bunkers cut carries a +7.5 m/s rightward deviation the mean-wind cuts lack).
    deep_cuts = [m for m in good if m.get('deep')]
    tier = deep_cuts if len(deep_cuts) >= VWP_MIN_CUTS else good
    us = sorted(m['speedMs'] * math.sin(m['dirDeg'] * D2R) for m in tier)
    vs = sorted(m['speedMs'] * math.cos(m['dirDeg'] * D2R) for m in tier)

    def median(a):
        n = len(a)
        h = n >> 1
        return a[h] if (n % 2) else (a[h - 1] + a[h]) / 2.0
    mu, mv = median(us), median(vs)
    dir_deg = (math.degrees(math.atan2(mu, mv)) + 360) % 360
    source = 'Bunkers R' if any(m['source'] == 'Bunkers R' for m in tier) else 'Mean wind'
    return {'speedMs': math.hypot(mu, mv), 'dirDeg': dir_deg, 'source': source, 'cuts': len(tier),
            'layers': sum(m.get('layers', 0) for m in tier),
            'topM': max(m.get('topM', 0) for m in tier), 'mu': mu, 'mv': mv}


# ============================================================================================
# ==  Synthetic sweeps (analytic answer keys)                                               ==
# ============================================================================================
FIRST_GATE_KM = 2.125
GATE_SIZE_KM = 0.25
N_GATES = 920            # ~232 km range, like a real velocity cut


def build_uniform(u, v, phi_deg, n_gates=N_GATES):
    """Uniform wind (u east, v north) at every gate -> constant with height, zero shear."""
    cos_phi = math.cos(phi_deg * D2R)
    radials = []
    for k in range(360):
        az = k + 0.5
        a = az * D2R
        val = cos_phi * (u * math.sin(a) + v * math.cos(a))
        radials.append({'az': az, 'data': [val] * n_gates})
    return radials


def build_shear(uf, vf, phi_deg):
    """Wind that varies with beam height per uf(h)/vf(h) -> a VAD profile with real shear (one cut)."""
    cos_phi = math.cos(phi_deg * D2R)
    gate_m = GATE_SIZE_KM * 1000.0
    first_m = FIRST_GATE_KM * 1000.0
    sin_phi = math.sin(phi_deg * D2R)
    heights = [(first_m + j * gate_m) * sin_phi + (first_m + j * gate_m) ** 2 / (2.0 * AE_M)
               for j in range(N_GATES)]
    radials = []
    for k in range(360):
        az = k + 0.5
        a = az * D2R
        sa, ca = math.sin(a), math.cos(a)
        data = [cos_phi * (uf(h) * sa + vf(h) * ca) for h in heights]
        radials.append({'az': az, 'data': data})
    return radials


# ============================================================================================
# ==  The tests                                                                             ==
# ============================================================================================
_failures = 0


def check(name, cond, detail=""):
    global _failures
    status = "PASS" if cond else "FAIL"
    if not cond:
        _failures += 1
    if not cond or VERBOSE:
        print(f"  [{status}] {name}" + (f"  {detail}" if detail else ""))


def test_uniform():
    print("Test 1: uniform wind, deep single cut -> recovers the wind exactly, source 'Mean wind'")
    u, v, phi = 15.0, -8.0, 0.5
    prof = vad_points_for_cut(build_uniform(u, v, phi), phi, FIRST_GATE_KM, GATE_SIZE_KM)
    res = bunkers_from_profile(prof)
    check("profile is sufficient (deep enough)", not res.get('insufficient'), f"topM={res.get('topM')}")
    if res.get('insufficient'):
        return
    exp_speed = math.hypot(u, v)
    exp_dir = (math.degrees(math.atan2(u, v)) + 360) % 360
    check("recovered u", abs(res['mu'] - u) < 1e-6, f"got {res['mu']:.9f} want {u}")
    check("recovered v", abs(res['mv'] - v) < 1e-6, f"got {res['mv']:.9f} want {v}")
    check("speed (m/s)", abs(res['speedMs'] - exp_speed) < 1e-6, f"got {res['speedMs']:.6f} want {exp_speed:.6f}")
    check("direction (deg toward)", abs(res['dirDeg'] - exp_dir) < 1e-4, f"got {res['dirDeg']:.4f} want {exp_dir:.4f}")
    check("source is 'Mean wind' (no shear)", res['source'] == 'Mean wind', f"got '{res['source']}'")
    worst = max(max(abs(p['u'] - u), abs(p['v'] - v)) for p in prof)
    check("all rings recover the uniform wind", worst < 1e-6, f"worst ring error {worst:.2e} over {len(prof)} rings")


def _bunkers_reference(prof, uf, vf):
    """Independent Bunkers expectation from the analytic wind at the profile's own ring heights (a
    differently-structured recompute than bunkers_from_profile's mean_layer, so it cross-checks the wiring)."""
    def layer_mean(h0, h1):
        us = [uf(p['h']) for p in prof if h0 <= p['h'] <= h1]
        vs = [vf(p['h']) for p in prof if h0 <= p['h'] <= h1]
        return (sum(us) / len(us), sum(vs) / len(vs)) if us else None
    mean, bot, tp = layer_mean(0, 6000), layer_mean(0, 500), layer_mean(5500, 6000)
    return mean, bot, tp


def _assert_shear_case(name, prof, uf, vf):
    res = bunkers_from_profile(prof)
    check(f"{name}: sufficient", not res.get('insufficient'), f"topM={res.get('topM')}")
    if res.get('insufficient'):
        return
    worst = max(max(abs(p['u'] - uf(p['h'])), abs(p['v'] - vf(p['h']))) for p in prof)
    check(f"{name}: per-ring VAD recovers wind(h)", worst < 1e-6, f"worst {worst:.2e} over {len(prof)} rings")
    mean, bot, tp = _bunkers_reference(prof, uf, vf)
    check(f"{name}: 0-6 / 0-0.5 / 5.5-6 km layers sampled", mean and bot and tp)
    if not (mean and bot and tp):
        return
    shu, shv = tp[0] - bot[0], tp[1] - bot[1]
    sh_mag = math.hypot(shu, shv)
    check(f"{name}: shear exceeds Bunkers threshold", sh_mag > BUNKERS_MIN_SHEAR, f"shear {sh_mag:.2f} m/s")
    exp_mu = mean[0] + BUNKERS_D * (shv / sh_mag)
    exp_mv = mean[1] + BUNKERS_D * (-shu / sh_mag)
    check(f"{name}: source is 'Bunkers R'", res['source'] == 'Bunkers R', f"got '{res['source']}'")
    check(f"{name}: Bunkers u", abs(res['mu'] - exp_mu) < 1e-4, f"got {res['mu']:.6f} want {exp_mu:.6f}")
    check(f"{name}: Bunkers v", abs(res['mv'] - exp_mv) < 1e-4, f"got {res['mv']:.6f} want {exp_mv:.6f}")


def test_shear_single():
    print("Test 2: linear-shear single cut -> per-ring VAD recovers wind(h), final = hand-computed Bunkers R")
    def uf(h): return 6.0 + 0.003 * h
    def vf(h): return -4.0 + 0.0015 * h
    _assert_shear_case("single", vad_points_for_cut(build_shear(uf, vf, 1.0), 1.0, FIRST_GATE_KM, GATE_SIZE_KM), uf, vf)


def test_multi_cut_combine():
    print("Test 3: MULTI-CUT combine (4 tilts, uniform wind) -> per-cut motion then median = the wind exactly")
    # Uniform wind: every cut, whatever heights it samples, recovers (u,v) as its Mean-wind motion, so the
    # combined median is (u,v) EXACTLY — an analytic check of the per-cut → combine path (decodeVwp).
    u, v = 13.0, -9.0
    motions = [bunkers_from_profile(vad_points_for_cut(build_uniform(u, v, phi), phi, FIRST_GATE_KM, GATE_SIZE_KM))
               for phi in (0.5, 1.5, 2.5, 3.5)]
    check("every cut is sufficient", all(not m.get('insufficient') for m in motions),
          f"{sum(1 for m in motions if not m.get('insufficient'))}/4 sufficient")
    res = combine_cut_motions(motions)
    check("combined is sufficient", not res.get('insufficient'))
    if res.get('insufficient'):
        return
    check("combined u", abs(res['mu'] - u) < 1e-6, f"got {res['mu']:.9f} want {u}")
    check("combined v", abs(res['mv'] - v) < 1e-6, f"got {res['mv']:.9f} want {v}")
    check("source 'Mean wind' (no shear)", res['source'] == 'Mean wind', f"got '{res['source']}'")


def test_combine_rejects_outlier():
    print("Test 4: combine_cut_motions -> median rejects a contaminated cut and ignores insufficient cuts")
    # 4 consistent cuts near 60 deg @ 27 kt + one wild outlier (254 deg @ 15 kt, like Moore's bad 3.1 deg cut)
    # + an insufficient cut. The componentwise median must land on the consistent cluster, not the outlier.
    def m(dirdeg, kt, src='Bunkers R'):
        return {'speedMs': kt * 0.514444, 'dirDeg': dirdeg, 'source': src, 'layers': 100, 'topM': 8000, 'deep': True}
    motions = [m(62, 27), m(64, 29), m(60, 33), m(61, 31), m(254, 15), {'insufficient': True, 'topM': 2500}]
    res = combine_cut_motions(motions)
    check("combined is sufficient", not res.get('insufficient'))
    if res.get('insufficient'):
        return
    check("direction near the cluster (~60 deg), not the outlier", abs(((res['dirDeg'] - 62 + 180) % 360) - 180) < 8,
          f"got {res['dirDeg']:.0f} deg")
    check("speed near the cluster (~30 kt), not the outlier", abs(res['speedMs'] / 0.514444 - 30) < 6,
          f"got {res['speedMs']/0.514444:.0f} kt")
    check("counts only the 5 sufficient cuts", res['cuts'] == 5, f"cuts={res['cuts']}")
    # Fewer than VWP_MIN_CUTS sufficient -> insufficient.
    check("one good cut -> insufficient", combine_cut_motions([m(60, 30), {'insufficient': True, 'topM': 3000}]).get('insufficient') is True)


def test_shallow_mean_wind():
    print("Test 5: shallow-but-usable cut (tops ~2.5-5 km) -> MEAN WIND (below Bunkers depth, above the floor)")
    # 0.5 deg over ~175 km tops ~3.3 km (curvature-dominated): above VWP_MEAN_MIN_TOP, below VWP_MIN_TOP, so
    # it can't anchor a Bunkers deviation but DOES give a serviceable mean-wind motion (the RadarScope-parity
    # case) instead of falling back to base velocity.
    u, v = 14.0, -6.0
    prof = vad_points_for_cut(build_uniform(u, v, 0.5, n_gates=700), 0.5, FIRST_GATE_KM, GATE_SIZE_KM)
    res = bunkers_from_profile(prof)
    check("not insufficient (mean-wind fallback)", not res.get('insufficient'), f"topM={res.get('topM')}, got {res}")
    if res.get('insufficient'):
        return
    check("top in the mean-wind band (2500 <= top < 5000)", VWP_MEAN_MIN_TOP <= res['topM'] < VWP_MIN_TOP, f"topM={res['topM']}")
    check("deep is False (mean wind, no Bunkers deviation)", res.get('deep') is False, f"deep={res.get('deep')}")
    check("source is 'Mean wind'", res['source'] == 'Mean wind', f"got '{res['source']}'")
    check("recovers the uniform wind u", abs(res['mu'] - u) < 1e-6, f"got {res['mu']:.9f} want {u}")
    check("recovers the uniform wind v", abs(res['mv'] - v) < 1e-6, f"got {res['mv']:.9f} want {v}")


def test_shallow_insufficient():
    print("Test 6: too-shallow single cut (short range) -> insufficient (below VWP_MEAN_MIN_TOP)")
    # 0.5 deg over only ~100 km tops out ~1.5 km -> below the mean-wind floor: just boundary-layer flow.
    prof = vad_points_for_cut(build_uniform(12.0, -5.0, 0.5, n_gates=400), 0.5, FIRST_GATE_KM, GATE_SIZE_KM)
    res = bunkers_from_profile(prof)
    check("returns insufficient", res.get('insufficient') is True, f"topM={res.get('topM')}, got {res}")


def test_sparse_and_wedge_insufficient():
    print("Test 7: too-sparse and wedge-clustered sweeps -> insufficient (no ring can be fit)")
    phi = 0.5
    cos_phi = math.cos(phi * D2R)
    # (a) 10 radials spread around the circle -> sN=10 < 30 on every ring.
    sparse = []
    for k in range(360):
        az = k + 0.5
        if k % 36 == 0:
            a = az * D2R
            sparse.append({'az': az, 'data': [cos_phi * (12 * math.sin(a) - 5 * math.cos(a))] * N_GATES})
        else:
            sparse.append({'az': az, 'data': None})
    res_sparse = bunkers_from_profile(vad_points_for_cut(sparse, phi, FIRST_GATE_KM, GATE_SIZE_KM))
    check("sparse -> insufficient", res_sparse.get('insufficient') is True, f"got {res_sparse}")
    # (b) 60 dense radials all within a 40-degree arc -> resultant length > VAD_MAX_CLUSTER.
    wedge = []
    for k in range(60):
        az = k * (40.0 / 60.0)
        a = az * D2R
        wedge.append({'az': az, 'data': [cos_phi * (12 * math.sin(a) - 5 * math.cos(a))] * N_GATES})
    res_wedge = bunkers_from_profile(vad_points_for_cut(wedge, phi, FIRST_GATE_KM, GATE_SIZE_KM))
    check("wedge -> insufficient", res_wedge.get('insufficient') is True, f"got {res_wedge}")


def main():
    print("storm_motion_check.py -- full-volume storm motion (VAD -> Bunkers) unit test\n")
    test_uniform()
    test_shear_single()
    test_multi_cut_combine()
    test_combine_rejects_outlier()
    test_shallow_mean_wind()
    test_shallow_insufficient()
    test_sparse_and_wedge_insufficient()
    print()
    if _failures:
        print(f"FAILED: {_failures} check(s) failed.")
        sys.exit(1)
    print("OK: all checks passed.")
    sys.exit(0)


if __name__ == "__main__":
    main()
