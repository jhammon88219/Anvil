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
in radar-decode.js, mirror the edit below and re-run, or this stops guarding the shipped code.

DELIBERATE non-mirror: radar-decode.js also carries a per-ring REJECTION TALLY (the `rej` counters in
vadFitFromRadials, rendered by rejDetail into the `vwp result ... [...]` diagnostics line, and passed through
bunkersFromProfile as an extra 'rej' key). That is instrumentation only -- it changes no branch, no threshold
and no returned number -- so it is not mirrored here. This script reads results with .get(), so the extra key
is invisible to it. Mirror the MATH; leave the counters to the app.

Usage:
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
# QC constants are SPEC VALUES (docs/radar/02-vad-spec.md sections 3.3/4), not tuned ones.
VAD_MIN_PTS = 30          # min valid gates around a ring (spec 25; ours stricter)
VAD_MIN_COVERAGE_DEG = 180  # 4.2 -- min azimuthal SPAN populated, measured in occupied bins
VAD_COVERAGE_BIN_DEG = 10   # 4.2
VAD_MAX_RESID = 2.0       # 4.1 -- was 6.0; a fully-aliased ring scores RMS 3.86 and slipped through
VAD_SYMMETRY_MAX = 7.0    # 4   -- |a0| ceiling (divergence + fall speed), m/s
VAD_FIT_PASSES = 2        # 4.3 -- refit once with |residual| > RMS gates dropped
VAD_MAX_SPEED = 60.0      # 5.2 -- reject an implausible fitted wind speed (m/s); was 80
VAD_SANITY_GAP_M = 500.0  # 5.2 -- a >90 deg direction swing across a gap smaller than this is a fold
VAD_SANITY_DIR_DEG = 90.0
VAD_MIN_RANGE_M = 10000.0 # 3.3 -- inside this, ground clutter saturates the ring
VAD_MAX_RANGE_M = 60000.0 # 3.3 -- beyond this, beam broadening breaks the uniform-wind assumption
AE_M = 8494667.0          # 4/3-Earth effective radius (m): h ~ r*sinphi + r^2/2ae
BUNKERS_D = 7.5           # Bunkers deviation magnitude (m/s), right of the 0-6 km shear
BUNKERS_MIN_SHEAR = 1.0   # doc 03 section 3 step 7 -- below this the orthogonal direction is undefined.
                          # Was 3.0 (invented). Below it we return the mean wind, not NoSolution -- a
                          # documented divergence: no provider chain to fall through to.
BUNKERS_MEAN_TOP = 6000.0 # 0-6 km mean wind (advection)          -- spec doc 03 section 2
BUNKERS_TAIL_TOP = 500.0  # 0-0.5 km, tail of the shear vector
BUNKERS_HEAD_BOT = 5500.0 # 5.5-6 km, head of the shear vector
BUNKERS_HEAD_TOP = 6000.0
VWP_MIN_TOP = 5000.0      # merged profile must reach >= this height (m) to trust a 0-6 km Bunkers DEVIATION
VWP_MIN_PTS = 8           # ...and carry at least this many ring points (for the Bunkers deviation)
VWP_MEAN_MIN_TOP = 2500.0 # a shallower profile still gives a serviceable MEAN-WIND motion (no deviation)
VWP_MEAN_MIN_PTS = 5      # ...with at least this many ring points; below this floor -> "insufficient"
VAD_TARGET_STEP_M = 250.0 # doc 02 section 3.2 -- one level per target height, every 250 m to 6 km
VAD_TARGET_TOL_M = 125.0  # a candidate must land within half a step
VWP_MAX_GAP_M = 1500.0    # doc 01 section 5 -- max gap between consecutive levels inside 0-6 km


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
    n_bins = math.ceil(360 / VAD_COVERAGE_BIN_DEG)
    az_bin = [0] * n
    for i in range(n):
        if has[i]:
            az_bin[i] = min(n_bins - 1, int((radials[i]['az'] % 360) // VAD_COVERAGE_BIN_DEG))
    points = []
    # RANGE WINDOW (spec 3.3). unambiguous range is not modelled in these synthetic sweeps.
    j_start = max(0, math.ceil((VAD_MIN_RANGE_M - first_gate_m) / gate_size_m))
    j_end = min(n_gates - 1, math.floor((VAD_MAX_RANGE_M - first_gate_m) / gate_size_m))
    for j in range(j_start, j_end + 1, stride):
        sb = []; cb = []; vb = []
        bins = [0] * n_bins
        for i in range(n):
            if not has[i]:
                continue
            v = radials[i]['data'][j]
            if v is None:
                continue
            sb.append(az_sin[i]); cb.append(az_cos[i]); vb.append(v)
            bins[az_bin[i]] = 1
        m = len(vb)
        if m < VAD_MIN_PTS:
            continue
        # Azimuthal SPAN, not gate count (spec 4.2).
        if sum(bins) * VAD_COVERAGE_BIN_DEG < VAD_MIN_COVERAGE_DEG:
            continue
        keep = [1] * m
        sol = None
        rms = float('inf')
        for p in range(VAD_FIT_PASSES):
            sN = Ss = Sc = Sss = Scc = Ssc = Sv = Svs = Svc = 0.0
            for i in range(m):
                if not keep[i]:
                    continue
                sv_, cv_, vv_ = sb[i], cb[i], vb[i]
                sN += 1.0
                Ss += sv_; Sc += cv_; Sss += sv_ * sv_; Scc += cv_ * cv_; Ssc += sv_ * cv_
                Sv += vv_; Svs += vv_ * sv_; Svc += vv_ * cv_
            if sN < VAD_MIN_PTS:
                sol = None
                break
            sol = solve3(sN, Ss, Sc, Ss, Sss, Ssc, Sc, Ssc, Scc, Sv, Svs, Svc)
            if sol is None:
                break
            se = 0.0
            for i in range(m):
                if keep[i]:
                    e = vb[i] - (sol[0] + sol[1] * sb[i] + sol[2] * cb[i])
                    se += e * e
            rms = math.sqrt(se / sN)
            if p + 1 < VAD_FIT_PASSES:      # spec 4.3 -- drop gates worse than the RMS, refit
                for i in range(m):
                    if keep[i]:
                        e = vb[i] - (sol[0] + sol[1] * sb[i] + sol[2] * cb[i])
                        if abs(e) > rms:
                            keep[i] = 0
        if sol is None:
            continue
        if rms > VAD_MAX_RESID:
            continue
        a0, a1, b1 = sol
        # Symmetry test (spec 4): amp is the ORPG SPW -- amplitude BEFORE the cos(phi) correction.
        amp = math.hypot(a1, b1)
        if not (abs(a0) < VAD_SYMMETRY_MAX and abs(a0) - amp <= 0):
            continue
        u = a1 / cos_phi
        vv = b1 / cos_phi
        if not (math.hypot(u, vv) < VAD_MAX_SPEED):
            continue
        r = first_gate_m + j * gate_size_m
        points.append({'h': r * sin_phi + r * r / (2.0 * AE_M), 'u': u, 'v': vv, 'phi': phi, 'r': r})
    return points


def profile_fold_suspect(prof):
    """Mirror of `profileFoldSuspect` (spec 5.2). A >90 deg direction swing across a <500 m gap is a residual
    velocity FOLD, not meteorology. Applied PER CUT before merging -- never to the merged profile, where
    adjacent levels can come from different beams."""
    for a_, b_ in zip(prof, prof[1:]):
        if b_['h'] - a_['h'] >= VAD_SANITY_GAP_M:
            continue
        d = abs(math.atan2(b_['u'], b_['v']) - math.atan2(a_['u'], a_['v'])) / D2R
        if d > 180:
            d = 360 - d
        if d > VAD_SANITY_DIR_DEG:
            return True
    return False


def select_target_heights(points):
    """Mirror of `selectTargetHeights` (doc 02 section 3.2). ONE level per target height, chosen as the
    candidate landing closest to the target; ties go to the LOWEST elevation (smallest 1/cos(phi) blow-up).

    NOT optional: a naive merge of every ring is not a hodograph. At one height a 0.5 deg cut samples a
    ~50 km-radius circle and a 2.4 deg cut ~14 km -- different air in a supercell -- so keeping all of them
    weights the profile by ring count per cut. Measured on Moore 2013: 0-6 km mean 39 kt vs Py-ART's 21 kt."""
    out = []
    t = 0.0
    while t <= BUNKERS_MEAN_TOP:
        best, best_d = None, float('inf')
        for p in points:
            d = abs(p['h'] - t)
            if d > VAD_TARGET_TOL_M:
                continue
            # A hand-built profile may carry no phi; treat it as the least-preferred tie-break (matches JS).
            if d < best_d - 1e-9 or (abs(d - best_d) <= 1e-9 and best is not None
                                     and p.get('phi', float('inf')) < best.get('phi', float('inf'))):
                best, best_d = p, d
        if best is not None:
            out.append(dict(best))
        t += VAD_TARGET_STEP_M
    return out


def merge_cuts(cut_point_lists):
    """Mirror of `decodeVwp`'s merge: per-cut QC, ONE profile sorted by height, then target-height selection.
    Returns (profile, cuts)."""
    merged, cuts = [], 0
    for pts in cut_point_lists:
        if not pts or profile_fold_suspect(pts):
            continue
        merged.extend(pts)
        cuts += 1
    merged.sort(key=lambda q: q['h'])
    return select_target_heights(merged), cuts


def mean_layer(prof, h0, h1):
    """Faithful transcription of `meanLayer`. HEIGHT-WEIGHTED (trapezoidal) mean wind over [h0,h1] m AGL,
    or None when the layer holds no observation. `prof` must be sorted ascending by h.

    NOT a plain average of the levels in the layer (which is what this used to be): VAD ring heights go as
    r*sin(phi) + r^2/2ae, so equal steps in RANGE bunch levels near the ground and an unweighted mean
    over-weights the bottom of the layer. Spec: docs/radar/03-bunkers-storm-motion-spec.md section 4.
    Endpoints are INTERPOLATED to h0/h1 only where the profile brackets them -- never extrapolated."""
    in_layer = [p for p in prof if h0 <= p['h'] <= h1]
    if not in_layer:
        return None

    def edge(h_edge, below, above):
        if below is None or above is None or above['h'] <= below['h']:
            return None
        f = (h_edge - below['h']) / (above['h'] - below['h'])
        return {'h': h_edge,
                'u': below['u'] + f * (above['u'] - below['u']),
                'v': below['v'] + f * (above['v'] - below['v'])}

    lo = None
    for p in prof:
        if p['h'] < h0:
            lo = p                      # last level below h0
    hi = None
    for p in reversed(prof):
        if p['h'] > h1:
            hi = p                      # first level above h1
    knots = list(in_layer)
    e_lo = edge(h0, lo, knots[0])
    if e_lo:
        knots.insert(0, e_lo)
    e_hi = edge(h1, knots[-1], hi)
    if e_hi:
        knots.append(e_hi)
    iu = iv = dz = 0.0
    for a, b in zip(knots, knots[1:]):
        d = b['h'] - a['h']
        if d <= 0:
            continue
        iu += (a['u'] + b['u']) / 2.0 * d
        iv += (a['v'] + b['v']) / 2.0 * d
        dz += d
    if dz <= 0:                          # one level (or coincident heights)
        n = len(in_layer)
        return {'u': sum(p['u'] for p in in_layer) / n, 'v': sum(p['v'] for p in in_layer) / n}
    return {'u': iu / dz, 'v': iv / dz}


def bunkers_from_profile(prof):
    """Faithful transcription of `bunkersFromProfile`. Merges VAD ring points into a Bunkers storm motion,
    or returns { 'insufficient': True, 'topM': ... } when the profile is too shallow/sparse to trust."""
    if not prof or len(prof) < 2:
        return {'insufficient': True, 'topM': 0}
    prof = sorted(prof, key=lambda p: p['h'])
    top = prof[-1]['h']
    # Genuinely too shallow/sparse for ANY trustworthy motion -> insufficient (the single-low-tilt guard).
    if top < VWP_MEAN_MIN_TOP or len(prof) < VWP_MEAN_MIN_PTS:
        return {'insufficient': True, 'topM': round(top), 'why': 'shallow'}

    mean = mean_layer(prof, 0, BUNKERS_MEAN_TOP) or mean_layer(prof, 0, top)
    if mean is None:
        return {'insufficient': True, 'topM': round(top)}

    # DEEP enough to trust the 0-6 km shear for a Bunkers deviation? Else the mean wind ALONE (serviceable).
    # NO SUBSTITUTION for a missing Bunkers layer -- a real observation is required in BOTH. Substituting the
    # top/bottom sampled ring manufactures a "6 km wind" out of a shallower one and emits a confident vector
    # that is not grounded in observation (spec doc 03 section 5). Supersedes the old top >= VWP_MIN_TOP proxy.
    bot = mean_layer(prof, 0, BUNKERS_TAIL_TOP)
    tp = mean_layer(prof, BUNKERS_HEAD_BOT, BUNKERS_HEAD_TOP)
    # COVERAGE GAP (doc 01 section 5) -- matters most on a MERGED profile, where the tail and head can come
    # from different cuts leaving the middle of the column unsampled.
    gap_ok, prev_h = True, None
    for q in prof:
        if q['h'] > BUNKERS_MEAN_TOP:
            break
        if prev_h is not None and q['h'] - prev_h > VWP_MAX_GAP_M:
            gap_ok = False
            break
        prev_h = q['h']
    deep = bot is not None and tp is not None and gap_ok and len(prof) >= VWP_MIN_PTS and top >= VWP_MIN_TOP
    why = None if deep else ('noHead' if tp is None else 'noTail' if bot is None
                             else 'gapTooLarge' if not gap_ok else 'fewPts')
    mu, mv, source = mean['u'], mean['v'], 'Mean wind'
    if deep:
        shu = tp['u'] - bot['u']
        shv = tp['v'] - bot['v']
        sh_mag = math.hypot(shu, shv)
        if sh_mag > BUNKERS_MIN_SHEAR:
            mu = mean['u'] + BUNKERS_D * (shv / sh_mag)
            mv = mean['v'] + BUNKERS_D * (-shu / sh_mag)
            source = 'Bunkers R'
        else:
            why = 'weakShear'
    dir_deg = math.atan2(mu, mv) / D2R
    if dir_deg < 0:
        dir_deg += 360
    return {'speedMs': math.hypot(mu, mv), 'dirDeg': dir_deg, 'source': source,
            'layers': len(prof), 'topM': round(top), 'deep': deep, 'why': why, 'mu': mu, 'mv': mv}


VWP_MIN_CUTS = 2


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
    print("Test 1: uniform wind, single cut -> recovers the wind exactly, source 'Mean wind'")
    # phi = 3.0 deg, not 0.5: inside the legal 10-60 km range window a 0.5 deg cut only reaches ~720 m, which
    # is below VWP_MEAN_MIN_TOP. 3.0 deg spans ~530 m to ~3.3 km -- shallow of Bunkers, fine for a mean wind.
    u, v, phi = 15.0, -8.0, 3.0
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
    """Independent Bunkers expectation from the analytic wind at the profile's own ring heights.

    AMENDED when the layer mean became height-weighted: this used to compute an UNWEIGHTED average of the
    levels, which is the very assumption the code was wrong about -- so the 'independent' reference agreed
    with the bug and the test passed through it. It is now derived from a different property entirely: for a
    LINEAR wind u(z), the height-weighted (trapezoidal) mean over [za,zb] is exactly u((za+zb)/2). No
    trapezoid loop, no shared code path with mean_layer. Both callers supply linear uf/vf, which is what
    makes this valid -- do not reuse it for a curved profile."""
    def layer_mean(h0, h1):
        inl = [p['h'] for p in prof if h0 <= p['h'] <= h1]
        if not inl:
            return None
        # The integral spans the layer edge only where the profile brackets it (mean_layer never extrapolates).
        za = h0 if any(p['h'] < h0 for p in prof) else min(inl)
        zb = h1 if any(p['h'] > h1 for p in prof) else max(inl)
        mid = (za + zb) / 2.0 if zb > za else min(inl)
        return (uf(mid), vf(mid))
    return layer_mean(0, BUNKERS_MEAN_TOP), layer_mean(0, BUNKERS_TAIL_TOP), \
        layer_mean(BUNKERS_HEAD_BOT, BUNKERS_HEAD_TOP)


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


BUNKERS_CUTS = (0.5, 1.3, 2.4, 3.1, 4.0, 5.1, 6.4)
"""Elevations a merged Bunkers profile needs. NO SINGLE CUT can do it: inside the legal 10-60 km range window
the 0-500 m shear tail requires phi <= 2.83 deg and the 5.5-6 km head requires phi >= 5.06 deg."""


def test_shear_merged():
    print("Test 2: linear-shear MERGED cuts -> per-ring VAD recovers wind(h), final = hand-computed Bunkers R")
    def uf(h): return 6.0 + 0.003 * h
    def vf(h): return -4.0 + 0.0015 * h
    cuts = [vad_points_for_cut(build_shear(uf, vf, phi), phi, FIRST_GATE_KM, GATE_SIZE_KM)
            for phi in BUNKERS_CUTS]
    prof, n = merge_cuts(cuts)
    check("all cuts survive per-cut QC", n == len(BUNKERS_CUTS), f"{n}/{len(BUNKERS_CUTS)} merged")
    _assert_shear_case("merged", prof, uf, vf)
    # A single cut CANNOT reach Bunkers under the range window -- the reason this test had to change.
    solo = bunkers_from_profile(vad_points_for_cut(build_shear(uf, vf, 1.0), 1.0, FIRST_GATE_KM, GATE_SIZE_KM))
    check("one 1.0 deg cut alone is NOT Bunkers-capable", solo.get('source') != 'Bunkers R',
          f"got '{solo.get('source')}' why={solo.get('why')}")


def test_multi_cut_merge():
    print("Test 3: MULTI-CUT MERGE (uniform wind) -> one merged profile recovers the wind exactly")
    # Uniform wind: every cut recovers (u,v) at every height it samples, so the merged profile's layer means
    # are (u,v) EXACTLY, whatever mix of heights the cuts contribute. Analytic check of the merge path.
    u, v = 13.0, -9.0
    cuts = [vad_points_for_cut(build_uniform(u, v, phi), phi, FIRST_GATE_KM, GATE_SIZE_KM)
            for phi in BUNKERS_CUTS]
    prof, n = merge_cuts(cuts)
    check("every cut contributes", n == len(BUNKERS_CUTS), f"{n}/{len(BUNKERS_CUTS)}")
    res = bunkers_from_profile(prof)
    check("merged profile is sufficient", not res.get('insufficient'), f"topM={res.get('topM')}")
    if res.get('insufficient'):
        return
    check("merged u", abs(res['mu'] - u) < 1e-6, f"got {res['mu']:.9f} want {u}")
    check("merged v", abs(res['mv'] - v) < 1e-6, f"got {res['mv']:.9f} want {v}")
    check("source 'Mean wind' (uniform -> no shear)", res['source'] == 'Mean wind', f"got '{res['source']}'")


def test_per_cut_qc_drops_contaminated():
    print("Test 4: PER-CUT QC -> a folded cut is dropped before it can contaminate the merged profile")
    # This is what replaced the per-cut median as the contamination control. A cut whose direction swings
    # >90 deg across a <500 m gap carries a residual fold; it must never join the merge.
    good = [{'h': 200.0 + 100 * i, 'u': 12.0, 'v': 5.0, 'phi': 1.5} for i in range(20)]
    folded = [{'h': 200.0 + 100 * i, 'u': 12.0 if i % 2 else -12.0, 'v': 5.0 if i % 2 else -5.0,
               'phi': 1.5} for i in range(20)]
    check("clean cut passes QC", not profile_fold_suspect(good))
    check("folded cut is flagged", profile_fold_suspect(folded))
    prof, n = merge_cuts([good, folded, good])
    check("only the clean cuts merge", n == 2, f"cuts={n}")
    check("folded points are absent", all(q['u'] > 0 for q in prof), "a negative-u level leaked in")
    # A >90 deg swing across a LARGE gap is ordinary shear, not a fold -- it must NOT be flagged.
    sheared = [{'h': 200.0, 'u': 12.0, 'v': 5.0}, {'h': 3000.0, 'u': -12.0, 'v': -5.0}]
    check("wide-gap direction change is not a fold", not profile_fold_suspect(sheared))


def test_shallow_mean_wind():
    print("Test 5: shallow-but-usable cut (tops ~2.5-5 km) -> MEAN WIND (below Bunkers depth, above the floor)")
    # 3.1 deg inside the 10-60 km window tops ~3.5 km: above VWP_MEAN_MIN_TOP, below VWP_MIN_TOP, and with no
    # 5.5-6 km level. So it can't anchor a Bunkers deviation but DOES give a serviceable mean-wind motion (the
    # RadarScope-parity case) instead of falling back to base velocity.
    # (Was a 0.5 deg cut run out to ~175 km -- range the doc 02 section 3.3 window no longer permits.)
    u, v = 14.0, -6.0
    prof = vad_points_for_cut(build_uniform(u, v, 3.1), 3.1, FIRST_GATE_KM, GATE_SIZE_KM)
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


def _profile(gen, z_top=6000.0, step=250.0):
    """Build a synthetic wind profile [{h,u,v}] from a generator u,v = gen(z), levels every `step` m."""
    prof = []
    z = 0.0
    while z <= z_top + 1e-9:
        u, v = gen(z)
        prof.append({'h': z, 'u': u, 'v': v})
        z += step
    return prof


def test_golden_bunkers():
    """Golden vectors from docs/radar/04-test-vectors.md, BUNK-01..03. These pin the LAYER MEAN and the
    right-mover sign convention against externally-computed values -- do not adjust a tolerance to pass.

    BUNK-01 alone cannot distinguish a trapezoidal mean from an unweighted one (its hodograph is linear on
    evenly spaced levels, where both agree). BUNK-02 is curved and CAN: it is the regression guard for the
    height-weighted mean, and it fails on the unweighted average this code used to compute."""
    print("Test 8: GOLDEN BUNK-01/02/03 -- layer mean + right-mover sign vs docs/radar/04-test-vectors.md")

    # BUNK-01 straight westerly: u = 5 + 20z/6000, v = 0.
    res = bunkers_from_profile(_profile(lambda z: (5.0 + 20.0 * z / 6000.0, 0.0)))
    check("BUNK-01 source is 'Bunkers R'", res['source'] == 'Bunkers R', f"got '{res['source']}'")
    check("BUNK-01 right mover u", abs(res['mu'] - 15.0) < 1e-4, f"got {res['mu']:.5f} want 15.00000")
    check("BUNK-01 right mover v", abs(res['mv'] - (-7.5)) < 1e-4, f"got {res['mv']:.5f} want -7.50000")
    check("BUNK-01 speed (m/s)", abs(res['speedMs'] - 16.77051) < 1e-4, f"got {res['speedMs']:.5f}")
    check("BUNK-01 heading toward", abs(res['dirDeg'] - 116.565) < 1e-2, f"got {res['dirDeg']:.3f}")

    # BUNK-02 curved, quarter-circle turning. THE trapezoidal-vs-unweighted discriminator.
    def bunk02(z):
        t = (math.pi / 2.0) * (z / 6000.0)
        return (20.0 * math.sin(t) + 2.0, -20.0 * math.cos(t) + 12.0)
    res = bunkers_from_profile(_profile(bunk02))
    mean = mean_layer(sorted(_profile(bunk02), key=lambda p: p['h']), 0, BUNKERS_MEAN_TOP)
    check("BUNK-02 mean wind u (height-weighted)", abs(mean['u'] - 14.72785) < 1e-4,
          f"got {mean['u']:.5f} want 14.72785")
    check("BUNK-02 mean wind v (height-weighted)", abs(mean['v'] - (-0.72785)) < 1e-4,
          f"got {mean['v']:.5f} want -0.72785")
    check("BUNK-02 right mover u", abs(res['mu'] - 20.03115) < 1e-4, f"got {res['mu']:.5f} want 20.03115")
    check("BUNK-02 right mover v", abs(res['mv'] - (-6.03115)) < 1e-4, f"got {res['mv']:.5f} want -6.03115")
    check("BUNK-02 speed (m/s)", abs(res['speedMs'] - 20.91941) < 1e-4, f"got {res['speedMs']:.5f}")
    check("BUNK-02 heading toward", abs(res['dirDeg'] - 106.756) < 1e-2, f"got {res['dirDeg']:.3f}")

    # BUNK-03 northwest flow -- the regime where a quadrant error in atan2 shows up.
    res = bunkers_from_profile(_profile(
        lambda z: (-(5.0 + 20.0 * z / 6000.0), -(2.0 + 8.0 * z / 6000.0))))
    check("BUNK-03 right mover u", abs(res['mu'] - (-17.78543)) < 1e-4, f"got {res['mu']:.5f}")
    check("BUNK-03 right mover v", abs(res['mv'] - 0.96358) < 1e-4, f"got {res['mv']:.5f}")
    check("BUNK-03 heading toward", abs(res['dirDeg'] - 273.101) < 1e-2, f"got {res['dirDeg']:.3f}")


def test_no_extrapolation():
    """BUNK-05 and the no-substitution rule (spec doc 03 section 5). A profile that stops below 5500 m has no
    shear head; it must NOT borrow the top sampled level to manufacture one. We have no fallback wind-profile
    provider, so the honest outcome is the Mean-wind tier -- never a 'Bunkers R' label."""
    print("\nTest 9: NO EXTRAPOLATION -- a missing Bunkers layer must not be substituted")

    def bunk02(z):
        t = (math.pi / 2.0) * (z / 6000.0)
        return (20.0 * math.sin(t) + 2.0, -20.0 * math.cos(t) + 12.0)

    # BUNK-05: same generator, truncated at 4250 m -> no 5500-6000 m level.
    res = bunkers_from_profile(_profile(bunk02, z_top=4250.0))
    check("BUNK-05 not labelled Bunkers", res.get('source') != 'Bunkers R', f"got '{res.get('source')}'")
    check("BUNK-05 deep is False", res.get('deep') is False, f"got {res.get('deep')}")
    check("BUNK-05 reason is noHead", res.get('why') == 'noHead', f"got {res.get('why')}")

    # Tops at 5200 m: clears the old `top >= VWP_MIN_TOP` (5000) proxy but still has NO head level. This is
    # the exact case the old code mislabelled 'Bunkers R' using the 5200 m ring as its "6 km" wind.
    res = bunkers_from_profile(_profile(bunk02, z_top=5200.0))
    check("5200 m tops VWP_MIN_TOP but still not Bunkers", res.get('source') == 'Mean wind',
          f"got '{res.get('source')}'")
    check("5200 m reason is noHead", res.get('why') == 'noHead', f"got {res.get('why')}")

    # No surface level (starts at 750 m) -> no shear tail.
    prof = [p for p in _profile(bunk02) if p['h'] >= 750.0]
    res = bunkers_from_profile(prof)
    check("no 0-500 m level -> not Bunkers", res.get('source') == 'Mean wind', f"got '{res.get('source')}'")
    check("no 0-500 m level -> reason noTail", res.get('why') == 'noTail', f"got {res.get('why')}")

    # BUNK-04 degenerate shear: constant wind, real levels in both layers -> mean wind, flagged weakShear.
    res = bunkers_from_profile(_profile(lambda z: (12.0, 0.0)))
    check("BUNK-04 degenerate shear -> Mean wind", res['source'] == 'Mean wind', f"got '{res['source']}'")
    check("BUNK-04 reason is weakShear", res.get('why') == 'weakShear', f"got {res.get('why')}")
    check("BUNK-04 mean wind returned, not NaN", abs(res['mu'] - 12.0) < 1e-9 and res['mv'] == 0.0,
          f"got ({res['mu']}, {res['mv']})")


def test_mean_layer_interpolation():
    """meanLayer interpolates to a layer edge only when the profile BRACKETS it, and never extrapolates."""
    print("\nTest 10: meanLayer edge handling")
    # Linear u over z; the 0-500 m mean of a bracketed linear profile is its midpoint value.
    prof = [{'h': z, 'u': z / 100.0, 'v': 0.0} for z in (0.0, 400.0, 900.0)]
    m = mean_layer(prof, 0, 500)
    check("bracketed edge is interpolated (0-500 mean = 2.5)", abs(m['u'] - 2.5) < 1e-9, f"got {m['u']}")
    # Profile starts at 200 m: no level below 0, so the integral starts at 200 -- not extrapolated to 0.
    prof2 = [{'h': z, 'u': z / 100.0, 'v': 0.0} for z in (200.0, 400.0)]
    m2 = mean_layer(prof2, 0, 500)
    check("unbracketed low edge is not extrapolated (mean = 3.0)", abs(m2['u'] - 3.0) < 1e-9, f"got {m2['u']}")
    check("empty layer -> None", mean_layer(prof2, 1000, 2000) is None)
    # One level IN the layer but the upper edge IS bracketed (200 below, 400 above) -> integrate 200..250,
    # whose linear mean is u(225) = 2.25. Interpolating that edge is correct; only extrapolation is banned.
    check("in-layer level + bracketed upper edge", abs(mean_layer(prof2, 150, 250)['u'] - 2.25) < 1e-9,
          f"got {mean_layer(prof2, 150, 250)['u']}")
    # Truly one knot: nothing brackets either edge -> the trapezoid degenerates to the level itself.
    solo = [{'h': 200.0, 'u': 2.0, 'v': 0.0}]
    check("single level, no bracketing -> that level", abs(mean_layer(solo, 150, 250)['u'] - 2.0) < 1e-9)


def main():
    print("storm_motion_check.py -- full-volume storm motion (VAD -> Bunkers) unit test\n")
    test_uniform()
    test_shear_merged()
    test_multi_cut_merge()
    test_per_cut_qc_drops_contaminated()
    test_shallow_mean_wind()
    test_shallow_insufficient()
    test_sparse_and_wedge_insufficient()
    test_golden_bunkers()
    test_no_extrapolation()
    test_mean_layer_interpolation()
    print()
    if _failures:
        print(f"FAILED: {_failures} check(s) failed.")
        sys.exit(1)
    print("OK: all checks passed.")
    sys.exit(0)


if __name__ == "__main__":
    main()
