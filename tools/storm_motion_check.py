#!/usr/bin/env python3
"""
storm_motion_check.py - unit-test the app's AUTOMATIC storm motion (VAD -> Bunkers), no JS runtime needed.

`radar-decode.js` `computeStormMotion` derives the Storm-Relative-Velocity storm motion from a volume's
OWN Doppler velocity, fully offline: at a fixed conical elevation phi a uniform horizontal wind (u east,
v north) makes the radial velocity vary sinusoidally with azimuth,
    Vr(az) = a0 + cos(phi)*(u*sin az + v*cos az),
so a per-range-ring least-squares harmonic fit recovers (u,v) at that ring's beam height h. Sweeping the
ring outward builds a (height,u,v) VAD profile, which feeds the Bunkers (2000) right-moving supercell
estimate (0-6 km mean wind + 7.5 m/s right of the 0-6 km shear), falling back to the mean wind when the
shear is weak.

This machine has no Node/deno/bun to run the JS, so -- exactly like tools/dealias_check.py mirrors the JS
dealiaser -- this script re-implements `computeStormMotion` (+ `solve3`) in Python as a FAITHFUL
transcription of the JS, then drives it with SYNTHETIC sweeps whose answer is known ANALYTICALLY:

  * uniform wind, no shear    -> recovers that wind EXACTLY; source "Mean wind"
  * linear-shear profile      -> per-ring VAD recovers wind(h); final = an INDEPENDENTLY hand-computed
                                 Bunkers right-mover (mean + 7.5 m/s right of the shear); source "Bunkers R"
  * too-sparse sweep          -> None (never reaches VAD_MIN_PTS on any ring)
  * wedge-clustered azimuths   -> None (resultant length > VAD_MAX_CLUSTER: can't fit a full circle)

No network, no Py-ART, deterministic. Exit 0 = all pass, 1 = a failure.

WARNING: if you edit `computeStormMotion` / `solve3` / the VAD_* / BUNKERS_* / AE_M constants in
radar-decode.js, mirror the edit in the "JS mirror" section below and re-run, or this stops guarding the
shipped code. Usage:  py -3.12 tools/storm_motion_check.py [-v]

Does NOT touch the C# app.
"""
import sys
import math

VERBOSE = "-v" in sys.argv or "--verbose" in sys.argv
D2R = math.pi / 180.0

# ============================================================================================
# ==  Python MIRROR of the JS `computeStormMotion` (radar-decode.js). Keep in sync.          ==
# ============================================================================================
# Constants transcribed verbatim from radar-decode.js:
VAD_MIN_PTS = 30          # min valid gates around a ring to trust its harmonic fit
VAD_MAX_CLUSTER = 0.6     # reject a ring whose az are clustered (resultant length > this => a wedge)
VAD_MAX_RESID = 6.0       # max RMS fit residual (m/s) -- rejects convectively contaminated rings
VAD_MAX_SPEED = 80.0      # reject an implausible fitted wind speed (m/s)
AE_M = 8494667.0          # 4/3-Earth effective radius (m): h ~ r*sinphi + r^2/2ae
BUNKERS_D = 7.5           # Bunkers deviation magnitude (m/s), right of the 0-6 km shear
BUNKERS_MIN_SHEAR = 3.0   # below this 0-6 km shear (m/s) use the mean wind, not a supercell deviation


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


def compute_storm_motion(radials, phi_deg, first_gate_km, gate_size_km):
    """Faithful transcription of `computeStormMotion`. `radials` = list of {'az': deg, 'data': [m/s | None]}
    standing in for the dealiased Doppler cut; `phi_deg` stands in for medianElevationAngle. Returns
    (result | None, prof) where result mirrors the JS dict (+ mu/mv for testing) and prof is the built
    (h,u,v) VAD profile (for independent per-ring assertions)."""
    n = len(radials)
    phi = phi_deg
    if not (phi == phi) or phi <= 0:   # NaN or non-positive -> the JS 0.5 fallback
        phi = 0.5
    phi_rad = phi * D2R
    cos_phi = math.cos(phi_rad)
    sin_phi = math.sin(phi_rad)

    # Range geometry is shared across a cut's radials -- first radial that carries data.
    ref = None
    for d in radials:
        if d and d.get('data'):
            ref = d
            break
    if ref is None:
        return None, []
    first_gate_m = first_gate_km * 1000.0
    gate_size_m = gate_size_km * 1000.0
    n_gates = len(ref['data'])
    if not (first_gate_m == first_gate_m) or not (gate_size_m > 0):
        return None, []

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
    prof = []
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
        prof.append({'h': r * sin_phi + r * r / (2.0 * AE_M), 'u': u, 'v': vv})

    if len(prof) < 2:
        return None, prof
    prof.sort(key=lambda p: p['h'])

    def mean_layer(h0, h1):
        u = v = 0.0
        c = 0
        for p in prof:
            if h0 <= p['h'] <= h1:
                u += p['u']; v += p['v']; c += 1
        return {'u': u / c, 'v': v / c} if c else None

    top = prof[-1]['h']
    mean = mean_layer(0, 6000) or mean_layer(0, top)
    if mean is None:
        return None, prof
    bot = mean_layer(0, 500) or {'u': prof[0]['u'], 'v': prof[0]['v']}
    tp = mean_layer(5500, 6000) or {'u': prof[-1]['u'], 'v': prof[-1]['v']}
    shu = tp['u'] - bot['u']
    shv = tp['v'] - bot['v']
    sh_mag = math.hypot(shu, shv)
    mu, mv, source = mean['u'], mean['v'], 'Mean wind'
    if sh_mag > BUNKERS_MIN_SHEAR:
        mu = mean['u'] + BUNKERS_D * (shv / sh_mag)
        mv = mean['v'] + BUNKERS_D * (-shu / sh_mag)
        source = 'Bunkers R'
    dir_deg = math.atan2(mu, mv) / D2R
    if dir_deg < 0:
        dir_deg += 360
    return {'speedMs': math.hypot(mu, mv), 'dirDeg': dir_deg, 'source': source,
            'layers': len(prof), 'topM': round(top), 'mu': mu, 'mv': mv}, prof


# ============================================================================================
# ==  Synthetic sweeps (analytic answer keys) + independent expectations                    ==
# ============================================================================================
FIRST_GATE_KM = 2.125
GATE_SIZE_KM = 0.25
N_GATES = 920            # ~232 km range, like a real velocity cut


def ring_heights(phi_deg):
    """The exact beam heights the mirror will build (same j-stride + h formula), for independent expectation."""
    gate_m = GATE_SIZE_KM * 1000.0
    first_m = FIRST_GATE_KM * 1000.0
    sin_phi = math.sin(phi_deg * D2R)
    stride = max(1, round(1000.0 / gate_m))
    hs = []
    for j in range(0, N_GATES, stride):
        r = first_m + j * gate_m
        hs.append(r * sin_phi + r * r / (2.0 * AE_M))
    return hs


def build_uniform(u, v, phi_deg):
    """Uniform wind (u east, v north) at every gate -> constant with height, zero shear."""
    cos_phi = math.cos(phi_deg * D2R)
    radials = []
    for k in range(360):
        az = k + 0.5
        a = az * D2R
        val = cos_phi * (u * math.sin(a) + v * math.cos(a))
        radials.append({'az': az, 'data': [val] * N_GATES})
    return radials


def build_shear(uf, vf, phi_deg):
    """Wind that varies with beam height per uf(h)/vf(h) -> a VAD profile with real shear."""
    cos_phi = math.cos(phi_deg * D2R)
    gate_m = GATE_SIZE_KM * 1000.0
    first_m = FIRST_GATE_KM * 1000.0
    sin_phi = math.sin(phi_deg * D2R)
    heights = [ (first_m + j * gate_m) * sin_phi + (first_m + j * gate_m) ** 2 / (2.0 * AE_M)
                for j in range(N_GATES) ]
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
    print("Test 1: uniform wind, no shear -> recovers the wind exactly, source 'Mean wind'")
    u, v, phi = 15.0, -8.0, 0.5
    res, prof = compute_storm_motion(build_uniform(u, v, phi), phi, FIRST_GATE_KM, GATE_SIZE_KM)
    check("returns a result", res is not None)
    if res is None:
        return
    exp_speed = math.hypot(u, v)
    exp_dir = math.atan2(u, v) / D2R
    if exp_dir < 0:
        exp_dir += 360
    check("recovered u", abs(res['mu'] - u) < 1e-6, f"got {res['mu']:.9f} want {u}")
    check("recovered v", abs(res['mv'] - v) < 1e-6, f"got {res['mv']:.9f} want {v}")
    check("speed (m/s)", abs(res['speedMs'] - exp_speed) < 1e-6, f"got {res['speedMs']:.6f} want {exp_speed:.6f}")
    check("direction (deg toward)", abs(res['dirDeg'] - exp_dir) < 1e-4, f"got {res['dirDeg']:.4f} want {exp_dir:.4f}")
    check("source is 'Mean wind' (no shear)", res['source'] == 'Mean wind', f"got '{res['source']}'")
    # Every ring must recover the SAME uniform wind (independent per-ring VAD check).
    worst = max(max(abs(p['u'] - u), abs(p['v'] - v)) for p in prof)
    check("all rings recover the uniform wind", worst < 1e-6, f"worst ring error {worst:.2e} over {len(prof)} rings")


def test_shear_bunkers():
    print("Test 2: linear-shear profile -> per-ring VAD recovers wind(h), final = hand-computed Bunkers R")
    phi = 1.0
    # Linear wind profile; enough shear to trigger the Bunkers deviation.
    def uf(h): return 6.0 + 0.003 * h
    def vf(h): return -4.0 + 0.0015 * h
    res, prof = compute_storm_motion(build_shear(uf, vf, phi), phi, FIRST_GATE_KM, GATE_SIZE_KM)
    check("returns a result", res is not None)
    if res is None:
        return
    # (a) INDEPENDENT per-ring VAD recovery: each ring must recover wind at its own height.
    worst = max(max(abs(p['u'] - uf(p['h'])), abs(p['v'] - vf(p['h']))) for p in prof)
    check("per-ring VAD recovers wind(h)", worst < 1e-6, f"worst ring error {worst:.2e} over {len(prof)} rings")

    # (b) INDEPENDENT Bunkers expectation, averaged over the SAME ring heights via analytic wind (not the
    #     mirror's mean_layer): different structure, so it cross-checks the layer/Bunkers wiring.
    def layer_mean(h0, h1):
        us = [uf(p['h']) for p in prof if h0 <= p['h'] <= h1]
        vs = [vf(p['h']) for p in prof if h0 <= p['h'] <= h1]
        return (sum(us) / len(us), sum(vs) / len(vs)) if us else None
    mean = layer_mean(0, 6000)
    bot = layer_mean(0, 500)
    tp = layer_mean(5500, 6000)
    check("0-6 km layer sampled", mean is not None)
    check("0-0.5 km layer sampled", bot is not None)
    check("5.5-6 km layer sampled", tp is not None, "(need range that reaches 6 km at this tilt)")
    if not (mean and bot and tp):
        return
    shu, shv = tp[0] - bot[0], tp[1] - bot[1]
    sh_mag = math.hypot(shu, shv)
    check("shear exceeds Bunkers threshold", sh_mag > BUNKERS_MIN_SHEAR, f"shear {sh_mag:.2f} m/s")
    exp_mu = mean[0] + BUNKERS_D * (shv / sh_mag)
    exp_mv = mean[1] + BUNKERS_D * (-shu / sh_mag)
    check("source is 'Bunkers R'", res['source'] == 'Bunkers R', f"got '{res['source']}'")
    check("Bunkers u", abs(res['mu'] - exp_mu) < 1e-4, f"got {res['mu']:.6f} want {exp_mu:.6f}")
    check("Bunkers v", abs(res['mv'] - exp_mv) < 1e-4, f"got {res['mv']:.6f} want {exp_mv:.6f}")
    exp_dir = math.atan2(exp_mu, exp_mv) / D2R
    if exp_dir < 0:
        exp_dir += 360
    check("direction (deg toward)", abs(res['dirDeg'] - exp_dir) < 1e-3, f"got {res['dirDeg']:.3f} want {exp_dir:.3f}")


def test_sparse_returns_none():
    print("Test 3: too-sparse sweep -> None (never reaches VAD_MIN_PTS)")
    phi = 0.5
    cos_phi = math.cos(phi * D2R)
    radials = []
    # Only 10 radials carry data, spread around the circle: sN=10 < 30 on every ring.
    for k in range(360):
        az = k + 0.5
        if k % 36 == 0:  # 10 of them
            a = az * D2R
            radials.append({'az': az, 'data': [cos_phi * (12 * math.sin(a) - 5 * math.cos(a))] * N_GATES})
        else:
            radials.append({'az': az, 'data': None})
    res, prof = compute_storm_motion(radials, phi, FIRST_GATE_KM, GATE_SIZE_KM)
    check("returns None", res is None, f"got {res}")
    check("empty profile", len(prof) == 0, f"prof len {len(prof)}")


def test_wedge_returns_none():
    print("Test 4: wedge-clustered azimuths -> None (resultant length > VAD_MAX_CLUSTER)")
    phi = 0.5
    cos_phi = math.cos(phi * D2R)
    radials = []
    # 60 dense radials all within a 40-degree arc: plenty of points, but they don't span a circle.
    for k in range(60):
        az = k * (40.0 / 60.0)  # 0 .. ~40 deg
        a = az * D2R
        radials.append({'az': az, 'data': [cos_phi * (12 * math.sin(a) - 5 * math.cos(a))] * N_GATES})
    res, prof = compute_storm_motion(radials, phi, FIRST_GATE_KM, GATE_SIZE_KM)
    check("returns None", res is None, f"got {res}")
    check("empty profile", len(prof) == 0, f"prof len {len(prof)}")


def main():
    print("storm_motion_check.py -- computeStormMotion (VAD -> Bunkers) unit test\n")
    test_uniform()
    test_shear_bunkers()
    test_sparse_returns_none()
    test_wedge_returns_none()
    print()
    if _failures:
        print(f"FAILED: {_failures} check(s) failed.")
        sys.exit(1)
    print("OK: all checks passed.")
    sys.exit(0)


if __name__ == "__main__":
    main()
