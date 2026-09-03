// Shared NEXRAD Level II decode + gate-geometry build. No DOM / GL / host dependencies,
// so it runs inside a Web Worker (radar-worker.js) as well as on the main thread (radar.js
// fallback). The heavy cost here is the pure-JS bzip2 decompression inside Level2Radar
// (~5 s for a full volume), which is exactly why we run it off the UI thread.
//
// FROM BYTES TO PIXELS — what this file turns a .V06 into:
//
//   .V06 bytes ──► radials ──► gates ──────────────► triangles + colors ──► (transferred to radar.js)
//   (one tilt)     one per     a gate is one          two triangles per
//                  azimuth,    (azimuth, range)       gate, colored once
//                  ~720/sweep  cell of the sweep      here via radar-ramps
//
//            az 0°                        Each radial is a spoke; each gate is a cell along it.
//              │╱│╲│                      buildGates walks (radial × gate) — MILLIONS per sweep,
//         ─────╳──┼──── the sweep,        which is why its projection math is inlined rather than
//              │╲│╱│    fanned out        calling geo.js per gate.
//                                         Velocity's spokes are DEALIASED first (dealiasSweep) —
//                                         see docs/velocity-dealias.md before touching that.
//
//   A decode builds reflectivity ALWAYS plus only the products the host ASKED for (buildProducts) —
//   not all seven — and can also emit an inspector value GRID per product (the Int16 lookup the
//   Inspector reads). Everything comes back as keyed maps {moments:{id:…}, grids:{id:…}, built:{id}}.

import { REFLECTIVITY_RAMP, VELOCITY_RAMP, SRV_RAMP, CORRELATION_RAMP, KDP_RAMP, ZDR_RAMP, SPECTRUM_WIDTH_RAMP, rampColor } from './radar-ramps.js';
import { PRODUCTS, PRODUCT_IDS } from './radar-products.js';
import { metersPerDeg } from './geo.js';

const HALF_BEAM_DEG = 0.5; // half the super-res azimuthal spacing (~1° beam)
const D2R = Math.PI / 180;
const GRID_NODATA = -32768; // sentinel in the inspector value grid (Int16) for "no data here"

// Color scales live in radar-ramps.js (shared with the eventual legend) — see rampColor below.

// Lazily import the vendored decoder once. We use the decoder's OWN Buffer class
// (re-exported from the bundle): its constructor gates on `input instanceof <its Buffer>`,
// so a Buffer from any other module is rejected ("Unknown data provided"). The decoder also
// has a couple of process.* guards; satisfy them before importing.
let decoderPromise = null;
function loadDecoder() {
    if (!decoderPromise) {
        globalThis.process = globalThis.process || { env: {}, browser: true };
        decoderPromise = import('../vendor/nexrad-level-2-data.esm.js').then(function (mod) {
            return { Buffer: mod.Buffer, Level2Radar: mod.Level2Radar };
        });
    }
    return decoderPromise;
}

// Per-elevation radials for one moment, normalized to the { moment_data, first_gate (km),
// gate_size (km) } shape buildGates/buildGrid/dealias expect — bridging the two on-disk formats:
//   • Message 31 (super-res, 2008+): radar.getHighres*() already returns that object per radial,
//     so this just hands those through unchanged.
//   • Message 1 (legacy, pre-2008): the vendored decoder stores the moment as a FLAT value array
//     on the record (record.reflect = dBZ[], record.velocity = m/s[]) with the range geometry in
//     SEPARATE record fields (surveillance_/doppler_range + *_sample_interval, in km). We wrap
//     those into the same object shape so the rest of the pipeline is format-agnostic.
// Azimuth is read identically for both (radar.getAzimuth(i) -> record.azimuth), so callers keep
// using getAzimuth. `moment` is 'reflect' | 'velocity' | 'rho'.
function momentRadials(radar, moment) {
    const elev = radar.elevation;
    const scans = radar.data && radar.data[elev];
    if (!scans || !scans.length) return [];
    return scans.map(function (s) {
        const rec = s && s.record;
        if (!rec) return null;
        const m = rec[moment];
        if (m === null || m === undefined) return null;
        if (Array.isArray(m)) {
            // Legacy Message 1: flat array + per-record range fields. CC/ρHV doesn't exist here
            // (single-pol), so only 'reflect'/'velocity' ever reach this branch.
            const isRefl = moment === 'reflect';
            return {
                moment_data: m,
                first_gate: isRefl ? rec.surveillance_range : rec.doppler_range,
                gate_size: isRefl ? rec.surveillance_range_sample_interval : rec.doppler_range_sample_interval,
            };
        }
        return m; // Message 31 moment object { moment_data, first_gate, gate_size, ... }
    });
}

// Builds gate-quad geometry from a list of radials: mercator x,y per vertex + rgba per vertex
// (2 triangles per gate). `colorFn(value)` returns [r,g,b] for a gate, or null to skip it
// (no-data / below threshold). Returns null if nothing is drawn. Shared by reflectivity and
// velocity so the geometry math stays identical.
function buildGates(radials, getAzimuth, siteLat, siteLon, colorFn) {
    if (!radials || !radials.length) return null;
    const { mPerDegLat, mPerDegLon } = metersPerDeg(siteLat); // canonical projection — see geo.js (per-gate formula stays inline below for perf)
    const positions = [];
    const colors = [];
    const PI = Math.PI;
    function my(lat) {
        return (180 - (180 / PI) * Math.log(Math.tan(PI / 4 + lat * PI / 360))) / 360;
    }

    for (let i = 0; i < radials.length; i++) {
        const d = radials[i];
        if (!d || !d.moment_data) continue;
        const az = getAzimuth(i);
        if (typeof az !== 'number') continue;

        // This decoder reports gate geometry in KILOMETRES; moment_data is already in physical
        // units (dBZ for reflectivity, m/s for velocity; null = no data).
        const firstGate = d.first_gate; // km to first gate
        const gateSize = d.gate_size;   // km per gate
        const data = d.moment_data;
        if (!isFinite(firstGate) || !isFinite(gateSize)) continue;

        const firstGateM = firstGate * 1000;
        const gateSizeM = gateSize * 1000;
        const azL = (az - HALF_BEAM_DEG) * D2R, azR = (az + HALF_BEAM_DEG) * D2R;
        const sinL = Math.sin(azL), cosL = Math.cos(azL);
        const sinR = Math.sin(azR), cosR = Math.cos(azR);

        for (let j = 0; j < data.length; j++) {
            const col = colorFn(data[j]);
            if (!col) continue;

            const rNear = firstGateM + j * gateSizeM;
            const rFar = rNear + gateSizeM;

            // Four corners (near-left, far-left, far-right, near-right) -> mercator x,y.
            const xnL = (180 + siteLon + (rNear * sinL) / mPerDegLon) / 360;
            const ynL = my(siteLat + (rNear * cosL) / mPerDegLat);
            const xfL = (180 + siteLon + (rFar * sinL) / mPerDegLon) / 360;
            const yfL = my(siteLat + (rFar * cosL) / mPerDegLat);
            const xfR = (180 + siteLon + (rFar * sinR) / mPerDegLon) / 360;
            const yfR = my(siteLat + (rFar * cosR) / mPerDegLat);
            const xnR = (180 + siteLon + (rNear * sinR) / mPerDegLon) / 360;
            const ynR = my(siteLat + (rNear * cosR) / mPerDegLat);

            const r = col[0], g = col[1], b = col[2];
            // Two triangles: nL,fL,fR and nL,fR,nR
            positions.push(xnL, ynL, xfL, yfL, xfR, yfR, xnL, ynL, xfR, yfR, xnR, ynR);
            colors.push(r, g, b, 255, r, g, b, 255, r, g, b, 255, r, g, b, 255, r, g, b, 255, r, g, b, 255);
        }
    }

    if (!positions.length) return null;
    return {
        positions: new Float32Array(positions),
        colors: new Uint8Array(colors),
        count: positions.length / 2,
    };
}

// Builds the INSPECTOR value grid for a sweep: a compact polar lookup the host (radar.js) indexes
// by cursor position to read the moment value under the pointer (RadarScope-style). It's the raw
// per-gate values laid out [radial][gate], so the main thread can do range/azimuth -> value with no
// re-decode and no GL readback. Values are quantized to Int16 (scale carried alongside) so the whole
// grid transfers zero-copy and stays small; GRID_NODATA marks empty gates. `firstGate`/`gateSize` are
// in km (one representative pair — uniform within a sweep). `unit`/`digits` drive the tooltip text.
// NOTE: unlike the rendered geometry this is NOT thresholded/masked — the inspector reports the true
// measured value at any gate that has data (e.g. dBZ below the display threshold).
// wantValues (default true) gates the HEAVY part: the Int16 values + Float32 azimuth arrays (the
// per-gate inspector data, ~Int16 N×G per product per frame). The scalar range metadata
// (firstGate/gateSize/nGates) is always cheap to compute and is what the range ring needs, so it's
// returned regardless; the value arrays are built only when the inspector is on (see decodeAndBuild's
// buildGrids). A metadata-only grid returns az:null / values:null.
function buildGrid(radials, getAzimuth, scale, unit, digits, wantValues) {
    if (wantValues === undefined) wantValues = true;
    if (!radials || !radials.length) return null;
    const N = radials.length;
    let G = 0, fg = NaN, gs = NaN;
    for (let i = 0; i < N; i++) {
        const d = radials[i];
        if (d && d.moment_data) {
            if (d.moment_data.length > G) G = d.moment_data.length;
            if (!isFinite(fg)) { fg = d.first_gate; gs = d.gate_size; }
        }
    }
    if (!G || !isFinite(fg) || !isFinite(gs) || !(gs > 0)) return null;

    // Metadata-only (inspector off): skip the ~Int16 N×G allocation entirely. rangeMeters still works.
    if (!wantValues) {
        return { az: null, firstGate: fg, gateSize: gs, nGates: G, values: null, scale: scale, unit: unit, digits: digits };
    }

    const az = new Float32Array(N);
    const values = new Int16Array(N * G);
    values.fill(GRID_NODATA);
    for (let i = 0; i < N; i++) {
        const a = getAzimuth(i);
        az[i] = (typeof a === 'number') ? a : NaN;
        const md = radials[i] && radials[i].moment_data;
        if (!md) continue;
        for (let j = 0; j < md.length; j++) {
            const v = md[j];
            if (v === null || v === undefined || !isFinite(v)) continue;
            let q = Math.round(v * scale);
            if (q <= GRID_NODATA) q = GRID_NODATA + 1; else if (q > 32767) q = 32767;
            values[i * G + j] = q;
        }
    }
    return { az: az, firstGate: fg, gateSize: gs, nGates: G, values: values, scale: scale, unit: unit, digits: digits };
}

// Lowest-tilt reflectivity geometry (gates >= minDbz) + the inspector value grid. Reflectivity always
// lives at the lowest elevation NUMBER present (the surveillance cut), which the C# extractor writes
// first. Returns { geom, grid }: geom may be null (nothing above threshold) while grid still has data.
function buildReflectivity(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevations = radar.listElevations();
    if (!elevations || !elevations.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevations));
    const radials = momentRadials(radar, 'reflect');
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(radials, getAz, siteLat, siteLon, function (dbz) {
        if (dbz === null || dbz === undefined || dbz < minDbz) return null;
        return rampColor(REFLECTIVITY_RAMP, dbz);
    });
    return { geom: geom, grid: buildGrid(radials, getAz, 10, REFLECTIVITY_RAMP.unit, 1, wantGrid) };
}

// The elevation (number) carrying velocity. In a split-cut precip VCP that's the Doppler
// companion (a higher number than the surveillance cut); in clear-air it's the single combined
// cut. Returns null if no velocity is present (e.g. an older file without the Doppler cut).
function findVelocityElevation(radar) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return null;
    for (let k = 0; k < elevs.length; k++) {
        try {
            radar.setElevation(elevs[k]);
            const arr = momentRadials(radar, 'velocity');
            for (let i = 0; i < arr.length; i++) {
                if (arr[i] && arr[i].moment_data) return elevs[k];
            }
        } catch (e) { /* try the next elevation */ }
    }
    return null;
}

// Azimuth coverage of a sweep: how many radials carry data and the angular span they cover.
// A full sweep spans ~360°; a partial/in-progress Doppler cut shows up here as a small span
// (e.g. ~90° = the "quarter circle" wedge), which is the fastest way to spot a bugged frame.
function sweepStats(radials, getAzimuth) {
    let rad = 0, lo = Infinity, hi = -Infinity, gates = 0;
    for (let i = 0; i < radials.length; i++) {
        const d = radials[i];
        if (!d || !d.moment_data) continue;
        const az = getAzimuth(i);
        if (typeof az !== 'number') continue;
        rad++;
        if (az < lo) lo = az;
        if (az > hi) hi = az;
        if (!gates) gates = d.moment_data.length;
    }
    if (!rad) return { rad: 0, azLo: 0, azHi: 0, span: 0, gates: 0 };
    return { rad: rad, azLo: Math.round(lo), azHi: Math.round(hi), span: Math.round(hi - lo), gates: gates };
}

// Last sweep's dealiasing diagnostics (region count, seed mean, global shift, value range),
// surfaced into the debug log so the unfold can be verified without guessing at the picture.
let _dealiasInfo = '';

// Storm motion for Storm-Relative Velocity (buildSrv), pushed per decode from the host (radar.js →
// decodeAndBuild/decodeGridOnly). speedMs = storm speed in m/s; dirDeg = the compass bearing (0 = N,
// clockwise) the storm is MOVING TOWARD. The host RESOLVES this before each decode: in manual mode it's the
// user's value, in auto mode it's the volume's deep-VWP motion (decodeVwp, computed once per volume) or
// {0,0} until that's ready. So buildSrv just subtracts speedMs/dirDeg — the auto derivation is NOT done per
// frame here anymore (a single tilt is too shallow for a correct VWP; see the storm-motion section below).
let _stormMotion = { speedMs: 0, dirDeg: 0 };

// Per-decode memo of the dealiased Doppler sweep, so velocity AND SRV (both ride the same cut) share ONE
// dealias — the priciest step (~1.5 s/frame). Reset at the top of every decodeAndBuild/decodeGridOnly, so
// it never leaks across volumes; within a decode the first of velocity/SRV computes it, the second reuses.
// { radials, elev } once computed; radials = null when the volume has no Doppler cut.
let _sharedDealiased = null;
// Temporal dealias seed, per-decode (set by decodeAndBuild/decodeGridOnly from the host):
//   _decodeSeedProfile — the PREVIOUS cut's VAD wind profile, fed INTO this cut's dealias anchor.
//   _decodeVadProfile  — this cut's own VAD profile, fit from the dealiased result, RETURNED as the next seed.
let _decodeSeedProfile = null;
let _decodeVadProfile = null;
function velocityDealiased(radar) {
    if (_sharedDealiased) return _sharedDealiased;
    const elev = findVelocityElevation(radar);
    if (elev === null) { _sharedDealiased = { radials: null, elev: null }; return _sharedDealiased; }
    radar.setElevation(elev);
    const t0 = performance.now();
    const dz = dealiasSweep(momentRadials(radar, 'velocity'), radar, _decodeSeedProfile);
    _dealiasInfo += ' ms=' + Math.round(performance.now() - t0); // ISOLATED dealias time → frame `dealias` field
    _sharedDealiased = { radials: dz, elev: elev };
    // Produce THIS cut's wind profile (radar is at the velocity elevation here) to seed the NEXT decode.
    // Defensive: a fit failure just yields no seed → the next decode falls back to the default anchor.
    if (_decodeVadProfile === null) {
        try { const p = vadFitFromRadials(dz, radar); _decodeVadProfile = (p && p.length) ? p : null; }
        catch (e) { _decodeVadProfile = null; }
    }
    return _sharedDealiased;
}

// Per-radial Nyquist velocity (m/s) from the decoded RAD block; NaN if unavailable. The unfold
// interval for dealiasing is 2*Nyquist, so this is the one value the whole thing hinges on.
// NOTE: this decoder path reports the field in cm/s (e.g. 2584 = 25.84 m/s), NOT m/s — using it
// raw made 2*Nyquist ~5168, so round((ref-raw)/2Nyq) was always 0 and NOTHING ever unfolded
// (velocity rendered raw/aliased: red where strong inbound should read bright green). Real m/s
// Nyquists are < ~70, so normalize anything above 100 by /100.
// `out` (optional {src}) reports which field the value came from: 'rad' = the RAD per-radial sub-block
// (the CORRECT per-cut Nyquist Py-ART/RadarScope use), 'vol' = the coarser VOLUME-level fallback,
// 'none' = unavailable. Selection is unchanged (RAD when present, else VOL) — this only OBSERVES it.
function nyquistForRadial(radar, i, out) {
    try {
        const rec = radar.data[radar.elevation][i] && radar.data[radar.elevation][i].record;
        if (!rec) { if (out) out.src = 'none'; return NaN; }
        // Message 31 (super-res) carries Nyquist in the RAD sub-block (cm/s); legacy Message 1 puts
        // it on the record itself, already in m/s (the decoder divides by 100 there). The >100 guard
        // below normalizes whichever path supplied cm/s, and leaves an already-m/s value alone.
        // ⚠️ The RAD (per-radial) and VOL (volume-level) blocks can hold DIFFERENT Nyquists — using
        // the VOL fallback (~2 m/s higher) corrupts the fold arithmetic and is the suspected cause of
        // couplet mis-folds in the live path (`out.src` surfaces this to the diagnostics log).
        let nv, src;
        if (rec.radial && typeof rec.radial.nyquist_velocity === 'number') {
            nv = rec.radial.nyquist_velocity; src = 'rad';
        } else {
            nv = rec.nyquist_velocity; src = 'vol';
        }
        if (typeof nv !== 'number' || !(nv > 1)) { if (out) out.src = 'none'; return NaN; }
        if (nv > 100) nv /= 100; // cm/s -> m/s
        if (out) out.src = src;
        return nv;
    } catch (e) { if (out) out.src = 'none'; return NaN; }
}

// The sweep's representative Nyquist (m/s), or NaN if none. PREFERS the RAD per-radial value (the correct
// per-CUT Nyquist) sweep-wide: collect RAD and VOL candidates separately and return the RAD median if ANY
// radial carries it, falling back to VOL only when NO radial does. (Was: median of a per-radial RAD-else-VOL
// mix, which on a sweep with a few RAD-less radials could blend in the ~2 m/s-higher VOL value and shift
// folds at couplet edges. NB the live path was SUSPECTED of feeding VOL — 2026-08-03 investigation refuted
// that: live .V06s carry the RAD block (RRAD, 1440×) and both the decoder and Py-ART read it; this is a
// belt-and-suspenders guard against a hypothetical partial sweep, not a fix for an observed bug. See
// docs/velocity-dealias.md.)
function sweepNyquist(radar, count) {
    const radV = [], volV = [], out = {};
    for (let i = 0; i < count; i++) {
        const n = nyquistForRadial(radar, i, out);
        if (isFinite(n)) (out.src === 'vol' ? volV : radV).push(n);
    }
    const med = function (a) { if (!a.length) return NaN; a.sort(function (x, y) { return x - y; }); return a[a.length >> 1]; };
    return radV.length ? med(radV) : med(volV);
}

// Instrumentation companion to sweepNyquist: the same median value PLUS which source it came from and
// the median of each candidate (RAD per-radial vs VOL volume-level). A 'vol' src on a sweep means the
// per-radial cut Nyquist was unavailable and the dealiaser fell back to the coarser volume value — the
// suspected root of live-path couplet fold misses. Cheap; runs once per decoded velocity sweep.
function sweepNyquistDetail(radar, count) {
    const all = [], radV = [], volV = [];
    let radN = 0, volN = 0;
    const out = {};
    for (let i = 0; i < count; i++) {
        const v = nyquistForRadial(radar, i, out);
        if (out.src === 'rad') { radN++; if (isFinite(v)) radV.push(v); }
        else if (out.src === 'vol') { volN++; if (isFinite(v)) volV.push(v); }
        if (isFinite(v)) all.push(v);
    }
    const med = function (a) { if (!a.length) return NaN; a = a.slice().sort(function (x, y) { return x - y; }); return a[a.length >> 1]; };
    const src = radN && volN ? 'mixed' : radN ? 'rad' : volN ? 'vol' : 'none';
    return { med: med(all), src: src, radMed: med(radV), volMed: med(volV), radN: radN, volN: volN };
}

// Region-based velocity DEALIASING (v2 — a port of Py-ART's region_based algorithm, a "dynamic
// network reduction"). The previous v1 (grow regions where adjacent gates differ by < Nyquist, then a
// guarded MST + VAD anchor) COULD NOT UNFOLD A VIOLENT COUPLET: when both sides of a couplet exceed
// Nyquist they FOLD to similar raw values (Moore-2013: true -60 and +40 m/s both land near raw -8..-12
// at Nyquist 26), so the raw field is SMOOTH across the couplet — v1 merged the folded core into the
// ambient wind and rendered it ~raw (capped near Nyquist). Validated gate-for-gate against Py-ART's
// dealiaser, v2 recovers the Moore-2013 (-135 mph) and Bridge Creek-1999 (-100 mph) couplet cores
// exactly. All single-sweep, no external data. Steps:
//   1. SEGMENT: split the Nyquist interval into INTERVAL_SPLITS bands and label connected components
//      WITHIN each band. Binning (vs v1's grow-by-difference) is the crux — a folded couplet core sits
//      in a different band than the ambient wind, so it forms its OWN region instead of merging in.
//   2. EDGES: between adjacent regions accumulate weight (# adjacent gate pairs) + summed velocity
//      difference (in 2·Nyquist units).
//   3. REDUCE: repeatedly merge the two regions joined by the HEAVIEST boundary, unfolding the smaller
//      by the whole number of 2·Nyquist that best fits their shared boundary. Parallel edges to shared
//      neighbours COMBINE as regions merge, so evidence accumulates and one weak/noisy boundary can't
//      mis-fold a subtree — which is why v2 needs NONE of v1's fold cap / foldTol / min-region guards.
//   4. CENTER: shift all fold counts so the gate-weighted mean fold is ~0 (the sweep's absolute anchor).
// Input unchanged if no Nyquist is available.
const INTERVAL_SPLITS = 3; // Py-ART default; splitting the Nyquist interval finer separates folds better
const EDGE_SKIP = 100;     // Py-ART default skip_along_ray/skip_between_rays: bridge gaps of up to this
                           // many masked gates when finding region edges (see the edge loop below)

// Wrapper: on any unexpected error, fall back to the raw (folded) radials rather than blanking the
// whole velocity frame — same graceful degradation as "no Nyquist available".
// ── Temporal first-guess seed (mirrors tools/dealias_check.py) ────────────────────────────────────
// The absolute-fold anchor (step 4 below) defaults to "mean fold ≈ 0", which breaks under a strong mean
// wind or a mis-folded fragment (the over-unfold case). A SEED — a wind profile [{h,u,v}] from the loop's
// previous velocity cut (already dealiased a scan ago, so clean; winds barely change in ~5 min) — lets us
// instead anchor to the EXPECTED radial velocity, the NWS environmental-first-guess technique sourced
// temporally to stay offline. It only moves the ABSOLUTE anchor; the Py-ART-validated relative folds from
// steps 1-3 are untouched. seedProfile omitted/empty → byte-identical to the pre-seed behavior.
// Integer global fold shifts searched when anchoring to the seed (±3·2·Nyq ≫ any real ambiguity),
// ordered so S=0 wins ties (conservative — prefer no shift). Mirror of tools/dealias_check.py.
const SEED_SHIFT_ORDER = [0, -1, 1, -2, 2, -3, 3];

// Per-gate expected radial velocity from a wind profile: Vr = cosφ·(u(h)·sinAz + v(h)·cosAz), h ≈ r·sinφ +
// r²/2aₑ — the vadPointsForCut forward model, run in reverse. Returns { vr, used }; vr is a Float64Array
// (NaN where the geometry/profile can't supply a value) or null when the seed can't be built at all.
function seedExpectedVr(seedProfile, radials, radar, N, G) {
    if (!seedProfile || !seedProfile.length) return { vr: null, used: 0 };
    let phi = medianElevationAngle(radar, N);
    if (!isFinite(phi) || phi <= 0) phi = 0.5;
    const phiRad = phi * D2R, cosPhi = Math.cos(phiRad), sinPhi = Math.sin(phiRad);
    let ref = null;
    for (let i = 0; i < N; i++) { if (radials[i] && radials[i].moment_data) { ref = radials[i]; break; } }
    if (!ref) return { vr: null, used: 0 };
    const firstGateM = ref.first_gate * 1000, gateSizeM = ref.gate_size * 1000;
    if (!isFinite(firstGateM) || !(gateSizeM > 0)) return { vr: null, used: 0 };
    const prof = seedProfile.slice().sort(function (a, b) { return a.h - b.h; });
    const P = prof.length;
    function windAt(h) {
        if (h <= prof[0].h) return prof[0];
        if (h >= prof[P - 1].h) return prof[P - 1];
        let lo = 0, hi = P - 1;
        while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (prof[mid].h <= h) lo = mid; else hi = mid; }
        const a = prof[lo], b = prof[hi], t = (h - a.h) / (b.h - a.h);
        return { u: a.u + t * (b.u - a.u), v: a.v + t * (b.v - a.v) };
    }
    // Height-based wind is shared across radials (range geometry is per-gate); apply each radial's azimuth.
    const uAt = new Float64Array(G), vAt = new Float64Array(G);
    for (let j = 0; j < G; j++) {
        const rm = firstGateM + j * gateSizeM, h = rm * sinPhi + rm * rm / (2 * AE_M), w = windAt(h);
        uAt[j] = w.u; vAt[j] = w.v;
    }
    const vr = new Float64Array(N * G); vr.fill(NaN);
    let used = 0;
    for (let r = 0; r < N; r++) {
        if (!radials[r] || !radials[r].moment_data) continue;
        const az = radar.getAzimuth(r);
        if (typeof az !== 'number' || !isFinite(az)) continue;
        const a = az * D2R, azS = Math.sin(a), azC = Math.cos(a), base = r * G;
        for (let j = 0; j < G; j++) { vr[base + j] = cosPhi * (uAt[j] * azS + vAt[j] * azC); used++; }
    }
    return { vr: used ? vr : null, used: used };
}

function dealiasSweep(radials, radar, seedProfile) {
    try { return dealiasSweepCore(radials, radar, seedProfile); }
    catch (e) { _dealiasInfo = 'dealias error: ' + (e && e.message ? e.message : e); return radials; }
}

function dealiasSweepCore(radials, radar, seedProfile) {
    const med = sweepNyquist(radar, radials.length);
    if (!isFinite(med)) return radials;

    const N = radials.length;
    let G = 0;
    for (let r = 0; r < N; r++) {
        const md = radials[r] && radials[r].moment_data;
        if (md && md.length > G) G = md.length;
    }
    if (!N || !G) return radials;

    const nyq = med, nyq2 = 2 * med;
    // PERF: flatten velocity into ONE flat typed array (NaN = no data / masked) up front, so every gate read
    // in the band scans / flood-fill / edge-finding / apply loops (millions of accesses) is a direct
    // typed-array index — NOT a per-access property lookup + null/undefined/isFinite check on a boxed, ragged
    // JS array. NaN compares false to every band bound, so it drops out exactly like the old isFinite guard.
    // ⚠️ MUST be Float64Array, NOT Float32: moment_data is float64, and the dealias rounds region folds at
    // half-integer boundaries (Math.round of the sumdiff), so truncating to float32 flips folds by a whole
    // ±2·Nyquist and CHANGES the output (measured: shifted every corpus volume's over-unfold ratio). Float64
    // preserves the values bit-for-bit → gate-for-gate identical result. Ragged/absent rows read NaN.
    const vel = new Float64Array(N * G);
    for (let r = 0; r < N; r++) {
        const md = radials[r] && radials[r].moment_data;
        const base = r * G;
        const len = md ? md.length : 0;
        for (let j = 0; j < G; j++) {
            const v = j < len ? md[j] : undefined;
            vel[base + j] = (v === null || v === undefined || !isFinite(v)) ? NaN : v;
        }
    }

    // --- 1. segment into bands, connected-component per band (label: <0 = masked/unlabeled) ---
    // Band edges cover [-Nyq, Nyq]; extend outward if any gate reads slightly past Nyquist so every
    // valid gate lands in a band.
    let vLo = Infinity, vHi = -Infinity;
    for (let i = 0, n = vel.length; i < n; i++) {
        const v = vel[i];
        if (v < vLo) vLo = v; if (v > vHi) vHi = v; // NaN comparisons are false → no-data gates skipped
    }
    const interval = nyq2 / INTERVAL_SPLITS;
    const addStart = vHi > nyq ? Math.ceil((vHi - nyq) / interval) : 0;
    const addEnd = vLo < -nyq ? Math.ceil((-(vLo + nyq)) / interval) : 0;
    const bandStart = -nyq - addEnd * interval;
    const nBands = INTERVAL_SPLITS + addStart + addEnd;

    const label = new Int32Array(N * G).fill(-1);
    const regionCnt = [];
    const stack = [];
    for (let b = 0; b < nBands; b++) {
        const lmin = bandStart + b * interval, lmax = lmin + interval;
        for (let r0 = 0; r0 < N; r0++) {
            for (let j0 = 0; j0 < G; j0++) {
                const id0 = r0 * G + j0;
                if (label[id0] !== -1) continue;
                const v0 = vel[id0];
                if (!(v0 >= lmin && v0 < lmax)) continue; // NaN or out-of-band → skip (== old !isFinite||range)
                const rid = regionCnt.length; regionCnt.push(0);
                stack.length = 0; stack.push(r0, j0); label[id0] = rid;
                while (stack.length) {
                    const j = stack.pop(), r = stack.pop();
                    regionCnt[rid]++;
                    // 4-neighborhood INLINED (no per-gate array alloc — the old `nb` cost millions of tiny
                    // allocations) with direct `vel[]` reads. Order left/right/up-wrap/down-wrap as before;
                    // NaN fails both band comparisons, so masked gates drop out exactly like the old isFinite.
                    const rowBase = r * G;
                    let id2, vv;
                    if (j > 0)     { id2 = rowBase + j - 1; if (label[id2] === -1) { vv = vel[id2]; if (vv >= lmin && vv < lmax) { label[id2] = rid; stack.push(r, j - 1); } } }
                    if (j + 1 < G) { id2 = rowBase + j + 1; if (label[id2] === -1) { vv = vel[id2]; if (vv >= lmin && vv < lmax) { label[id2] = rid; stack.push(r, j + 1); } } }
                    const ru = (r - 1 + N) % N; id2 = ru * G + j; if (label[id2] === -1) { vv = vel[id2]; if (vv >= lmin && vv < lmax) { label[id2] = rid; stack.push(ru, j); } }
                    const rd = (r + 1) % N;     id2 = rd * G + j; if (label[id2] === -1) { vv = vel[id2]; if (vv >= lmin && vv < lmax) { label[id2] = rid; stack.push(rd, j); } }
                }
            }
        }
    }
    const numReg = regionCnt.length;
    if (numReg < 2) return radials;

    // --- 2. edges: g[a] = Map(b -> [weight, sumdiff]) where sumdiff = Σ (v_a - v_b)/nyq2 over the
    // shared boundary (kept symmetric: g[b].get(a) mirrors it with negated sumdiff). ---
    const g = new Array(numReg);
    for (let i = 0; i < numReg; i++) g[i] = new Map();
    function addEdge(a, b, dv) { // dv = v_a - v_b (raw m/s)
        let e = g[a].get(b);
        if (!e) { e = [0, 0]; g[a].set(b, e); const f = [0, 0]; g[b].set(a, f); e.mate = f; f.mate = e; }
        e[0]++; e[1] += dv / nyq2;
        const f = e.mate; f[0]++; f[1] -= dv / nyq2;
    }
    // ⚠️ GAP-SKIPPING (Py-ART skip_along_ray/skip_between_rays, default 100): when the next gate is
    // masked, look PAST up to EDGE_SKIP masked gates to the next labelled region and connect to it. Without
    // this, sparse FAR-RANGE regions (separated from the main body by data gaps) stay disconnected, get no
    // edge, and after centering land on the wrong absolute fold — measured KLVX 2026-07-21: only 87%
    // agreement with Py-ART (a uniform −1 fold beyond ~120 km), fixed to 100% by bridging the gaps. Dense
    // data is unaffected: the scan finds the adjacent gate immediately (no skip), so directly-touching
    // regions edge exactly as before (Moore-2013 couplet unchanged, core −135 mph).
    for (let r = 0; r < N; r++) {
        const rowBase = r * G;
        for (let j = 0; j < G; j++) {
            const la = label[rowBase + j];
            if (la < 0) continue;
            const va = vel[rowBase + j];
            // Along the ray: next labelled gate to the right, skipping up to EDGE_SKIP masked gates.
            let jj = j + 1, s = 0;
            while (jj < G && label[rowBase + jj] < 0 && s < EDGE_SKIP) { jj++; s++; }
            if (jj < G) { const lb = label[rowBase + jj]; if (lb >= 0 && lb !== la) addEdge(la, lb, va - vel[rowBase + jj]); }
            // Across rays: next labelled gate downward (+wrap), skipping up to EDGE_SKIP masked rays.
            let rr = (r + 1) % N; s = 0;
            while (rr !== r && label[rr * G + j] < 0 && s < EDGE_SKIP) { rr = (rr + 1) % N; s++; }
            if (rr !== r) { const lb = label[rr * G + j]; if (lb >= 0 && lb !== la) addEdge(la, lb, va - vel[rr * G + j]); }
        }
    }

    // --- 3. dynamic network reduction: merge the heaviest boundary first, combining parallel edges. A
    // binary max-heap orders merges by (current) weight; entries are lazily validated on pop (an entry
    // is stale if either node died or the live edge weight no longer matches). ---
    const heap = []; // array of [weight, a, b]
    function heapPush(w, a, b) {
        heap.push([w, a, b]); let i = heap.length - 1;
        while (i > 0) { const p = (i - 1) >> 1; if (heap[p][0] >= heap[i][0]) break; const t = heap[p]; heap[p] = heap[i]; heap[i] = t; i = p; }
    }
    function heapPop() {
        if (!heap.length) return null;
        const top = heap[0], last = heap.pop();
        if (heap.length) { heap[0] = last; let i = 0; const n = heap.length;
            for (;;) { let l = 2 * i + 1, rr = l + 1, m = i;
                if (l < n && heap[l][0] > heap[m][0]) m = l;
                if (rr < n && heap[rr][0] > heap[m][0]) m = rr;
                if (m === i) break; const t = heap[m]; heap[m] = heap[i]; heap[i] = t; i = m; } }
        return top;
    }
    for (let a = 0; a < numReg; a++) g[a].forEach(function (e, b) { if (b > a) heapPush(e[0], a, b); });

    const alive = new Uint8Array(numReg).fill(1);
    const size = Int32Array.from(regionCnt);
    const unwrap = new Int32Array(numReg);            // fold count applied to each ORIGINAL region
    const regionsIn = new Array(numReg);              // original regions currently inside each node
    for (let i = 0; i < numReg; i++) regionsIn[i] = [i];

    function doUnwrap(node, nw) {
        if (!nw) return;
        const mem = regionsIn[node];
        for (let i = 0; i < mem.length; i++) unwrap[mem[i]] += nw;
        g[node].forEach(function (e, nb) { e[1] += e[0] * nw; e.mate[1] -= e[0] * nw; });
    }

    while (heap.length) {
        const top = heapPop();
        const w = top[0], a = top[1], b = top[2];
        if (!alive[a] || !alive[b]) continue;
        const e = g[a].get(b);
        if (!e || e[0] !== w) continue; // stale heap entry (weight changed by a combine)
        const rdiff = Math.round(e[1] / e[0]);
        let base, mrg, nw;
        if (size[a] >= size[b]) { base = a; mrg = b; nw = rdiff; }
        else { base = b; mrg = a; nw = -rdiff; }
        doUnwrap(mrg, nw);
        // detach the base<->mrg edge, then fold mrg's other edges into base (combining parallels)
        g[base].delete(mrg); g[mrg].delete(base);
        g[mrg].forEach(function (e2, nb) {
            g[nb].delete(mrg);
            const be = g[base].get(nb);
            if (be) { be[0] += e2[0]; be[1] += e2[1]; be.mate[0] += e2[0]; be.mate[1] -= e2[1]; heapPush(be[0], base, nb); }
            else {
                const f = [e2[0], -e2[1]]; const ne = [e2[0], e2[1]];
                ne.mate = f; f.mate = ne; g[base].set(nb, ne); g[nb].set(base, f); heapPush(ne[0], base, nb);
            }
        });
        g[mrg] = new Map();
        const bm = regionsIn[base], mm = regionsIn[mrg];
        for (let i = 0; i < mm.length; i++) bm.push(mm[i]);
        regionsIn[mrg] = null;
        size[base] += size[mrg]; alive[mrg] = 0;
    }

    // --- 4. center: choose the ABSOLUTE fold anchor (steps 1-3's relative folds are untouched) ---
    // ⚠️ Mirror any change here in tools/dealias_check.py (dealias_v2), then re-run --selftest + the
    // in-app Validate card (null seed must stay Δ=0 vs the corpus baselines).
    let seedShift = null;
    const seed = seedExpectedVr(seedProfile, radials, radar, N, G);
    if (seed.vr) {
        // SEEDED: pick the integer global fold shift best matching the expected radial velocity (L1). The
        // temporal wind profile fixes the mean-wind bias the "mean ≈ 0" default gets wrong.
        const seedVr = seed.vr;
        let bestS = 0, bestCost = Infinity;
        for (let k = 0; k < SEED_SHIFT_ORDER.length; k++) {
            const S = SEED_SHIFT_ORDER[k];
            let cost = 0, cnt = 0;
            for (let r = 0; r < N; r++) {
                const rowBase = r * G;
                for (let j = 0; j < G; j++) {
                    const id = rowBase + j, lid = label[id];
                    if (lid < 0) continue;
                    const sv = seedVr[id]; if (sv !== sv) continue; // NaN
                    const v = vel[id]; if (v !== v) continue;
                    cost += Math.abs(v + (unwrap[lid] + S) * nyq2 - sv); cnt++;
                }
            }
            if (cnt && cost < bestCost) { bestCost = cost; bestS = S; }
        }
        if (bestS) for (let i = 0; i < numReg; i++) unwrap[i] += bestS;
        seedShift = bestS;
    } else {
        // DEFAULT (no seed): shift so the gate-weighted mean fold is ~0.
        let totalGates = 0, totalFolds = 0;
        for (let i = 0; i < numReg; i++) { totalGates += regionCnt[i]; totalFolds += regionCnt[i] * unwrap[i]; }
        const off = totalGates ? Math.round(totalFolds / totalGates) : 0;
        if (off) for (let i = 0; i < numReg; i++) unwrap[i] -= off;
    }

    // --- 5. apply per-region fold ---
    let vmin = Infinity, vmax = -Infinity, hi = 0, tot = 0;
    const out = new Array(N);
    for (let r = 0; r < N; r++) {
        const src = radials[r];
        if (!src || !src.moment_data) { out[r] = src; continue; }
        const data = src.moment_data;
        const rowBase = r * G;
        const mdOut = new Array(data.length);
        for (let j = 0; j < data.length; j++) {
            const lid = label[rowBase + j];
            const v = vel[rowBase + j];
            if (lid < 0 || !isFinite(v)) { mdOut[j] = null; continue; }
            const dv = v + unwrap[lid] * nyq2;
            mdOut[j] = dv;
            tot++;
            if (dv < vmin) vmin = dv;
            if (dv > vmax) vmax = dv;
            if (dv > 55 || dv < -55) hi++; // implausibly fast at 0.5° => over-unfolded / noise
        }
        out[r] = { moment_data: mdOut, first_gate: src.first_gate, gate_size: src.gate_size };
    }
    _dealiasInfo = numReg + 'reg splits' + INTERVAL_SPLITS +
        ' v[' + (isFinite(vmin) ? Math.round(vmin) : '?') + ',' +
        (isFinite(vmax) ? Math.round(vmax) : '?') + '] hi=' + hi + '/' + tot +
        (seedShift !== null ? ' seed=' + seedShift : '');
    return out;
}

// Lowest-tilt base-velocity geometry (dealiased) + the inspector value grid (also from the DEALIASED
// radials, so the inspected value matches the rendered pixel). Returns { geom, grid }; both null if
// the volume carries no velocity.
// minDbz is accepted (unused) so every builder shares ONE signature and decodeAndBuild can call them
// uniformly through the BUILDERS map — velocity isn't reflectivity-masked (unlike CC/DOW-velocity).
function buildVelocity(radar, siteLat, siteLon, minDbz, wantGrid) {
    const sd = velocityDealiased(radar);          // shared with SRV within this decode (one dealias)
    if (!sd.radials) return { geom: null, grid: null };
    radar.setElevation(sd.elev);                  // buildGates' getAzimuth reads the current cut — pin it
    const dealiased = sd.radials;
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(dealiased, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(VELOCITY_RAMP, v);
    });
    return { geom: geom, grid: buildGrid(dealiased, getAz, 10, VELOCITY_RAMP.unit, 1, wantGrid) };
}

// ── Automatic storm motion (full-volume VWP → Bunkers) ──────────────────────────────────────────────
// RadarScope-style automatic storm motion, computed ENTIRELY from the radar's own Doppler velocity — no
// external/model data (we run fully offline). Physics: at a fixed conical elevation φ a uniform horizontal
// wind (u = eastward, v = northward) makes the radial velocity vary sinusoidally with azimuth az (0 = N, cw):
//     Vr(az) = a0 + cos(φ)·(u·sin az + v·cos az)
// (a0 = the divergence/fall-speed constant, discarded). A per-range-ring least-squares harmonic fit recovers
// (u,v) at that ring's beam height h ≈ r·sin φ + r²/(2·aₑ), aₑ = 4⁄3-Earth radius — the classic VAD.
//
// ⚠️ ONE low tilt is NOT enough: its clean profile only reaches ~2–3 km near convection, far short of the
// 0–6 km a Bunkers estimate needs, so a single 0.5° cut gives a physically WRONG storm motion (measured on
// the Moore 2013 supercell: 7 kt N vs the real ENE ~27 kt; deeper tilts converge on the right answer — see
// docs/velocity-dealias.md-adjacent notes). So the profile is built across SEVERAL velocity tilts
// (vadPointsForCut per cut, each with its OWN φ) and their (h,u,v) points MERGED into one deep VWP
// (bunkersFromProfile). That profile feeds the Bunkers (2000) right-moving supercell estimate (0–6 km mean
// wind + 7.5 m/s to the RIGHT of the 0–6 km shear); weak shear falls back to the plain 0–6 km mean wind.
// decodeVwp(buffers) ties it together: the host (radar.js) fetches a volume's bottom ~5 velocity tilts and
// hands their buffers here; the tiny result is pushed back for the SRV builder + the App Settings readout.
// ⚠️⚠️ PARKED 2026-09-03 — the storm-motion path (VAD fit → merged profile → Bunkers) is FINISHED FOR NOW
// and deliberately left alone to collect real-use logs. Verified on both paths before parking.
// ⚠️ VAD_MAX_RESID BELOW IS 5.0, NOT the 2.0 doc 02 §4.1 recommends — that is a MEASURED amendment, not an
// oversight: 2.0 is tighter than the NWS's own VAD achieves on real returns and cost us ~4× the ring points
// (11 ring points to 2.7 km, vs 46–50 per cut to 6.0 km at 5.0). Re-measure against Level III NVW on a real
// volume before touching it. Full story: docs/radar/storm-motion.md §2.4.
// ⚠️ QC constants are SPEC VALUES, not tuned ones — docs/radar/02-vad-spec.md §3.3/§4. Don't "adjust one
// until a site works"; a looser gate here renders as a clean, confident, wrong vector on the map.
const VAD_MIN_PTS = 30;         // min valid gates around a ring to trust its harmonic fit (spec 25; ours stricter)
const VAD_MIN_COVERAGE_DEG = 180; // §4.2 — min azimuthal SPAN actually populated (NOT gate count)
const VAD_COVERAGE_BIN_DEG = 10;  // §4.2 — span is measured as the sum of occupied 10° azimuth bins
// ⚠️ AMENDED AGAINST REAL DATA — doc 02 §4.1 recommends 2.0 m/s and that is TOO TIGHT. Its case is built
// on synthetic sweeps where a clean fit scores 1.00 m/s, but real returns are not that clean: measured on
// KMVX 2026-09-03 19:19Z, the NWS's OWN operational VAD (Level III NVW) reported per-level fit residuals of
// 4.5–8.1 kt = 2.3–4.2 m/s on that very volume. A 2.0 m/s gate rejects what the reference implementation
// itself accepts — with it we salvaged 11 ring points to 2.7 km where NVW got 41 levels to 5.8 km.
// So: the OPERATIONAL value (18 km/hr). It is still tighter than the invented 6.0 this replaced, and it
// still rejects the fully-aliased case only in combination with the other gates — which is fine, because
// unlike the spec's assumed pipeline we dealias first and have the symmetry + coverage gates as well.
// ⚠️ Do NOT re-tighten this to 2.0 from the spec text alone. Re-measure against NVW on a real volume first.
const VAD_MAX_RESID = 5.0;      // §4.1 as amended — max RMS fit residual (m/s)
const VAD_SYMMETRY_MAX = 7.0;   // §4 — |a0| (divergence + fall speed) ceiling, m/s
const VAD_FIT_PASSES = 2;       // §4.3 — refit once with |residual| > RMS gates dropped
const VAD_MAX_SPEED = 60;       // §5.2 — reject an implausible fitted wind speed (m/s); was 80
const VAD_SANITY_GAP_M = 500;   // §5.2 — a direction swing across a gap SMALLER than this is a fold…
const VAD_SANITY_DIR_DEG = 90;  // §5.2 — …if it exceeds this many degrees
const VAD_MIN_RANGE_M = 10000;  // §3.3 — inside this, ground clutter saturates the ring
const VAD_MAX_RANGE_M = 60000;  // §3.3 — beyond this, beam broadening breaks the uniform-wind assumption
                                // over the ring. ⚠️ This is why DEPTH must come from HIGHER TILTS, not from
                                // longer ranges: at 0.5° the whole legal window only reaches ~700 m.
const AE_M = 8494667;           // 4⁄3-Earth effective radius (m) for beam-height h ≈ r·sinφ + r²/2aₑ
const BUNKERS_D = 7.5;          // Bunkers deviation magnitude (m/s), right of the 0–6 km shear
const BUNKERS_MIN_SHEAR = 1;    // doc 03 §3 step 7 — below this 0–6 km shear (m/s) the direction
                                // orthogonal to the shear is undefined, so no deviation can be placed.
                                // ⚠️ Was 3, which was an invented figure; the spec's floor is a
                                // NUMERICAL one (degenerate shear), not a judgement about when Bunkers
                                // is meteorologically meaningful. ⚠️ We diverge on what happens BELOW
                                // it: the spec returns NoSolution(DegenerateShear), we fall back to the
                                // mean wind, because we have no provider chain to fall through to and a
                                // mean wind still beats base velocity for SRV.
const VWP_MIN_TOP = 5000;       // merged profile must reach ≥ this height (m) to trust a 0–6 km Bunkers estimate
const VWP_MIN_PTS = 8;          // …and carry at least this many ring points (for a Bunkers supercell DEVIATION)
// A shallower profile can't anchor the 0–6 km Bunkers shear, but it CAN give a serviceable MEAN-WIND storm
// motion (no deviation) — far more useful than falling all the way back to base velocity, and what apps like
// RadarScope effectively do. Below THIS floor it's just boundary-layer flow → genuinely "insufficient".
// Validated on WI MCS runs (diagnostics): recoverable cuts topped ~3.3–3.9 km, truly-too-shallow ~1.3–1.8 km,
// so 2.5 km cleanly separates them. Mirrored in tools/storm_motion_check.py.
const VWP_MEAN_MIN_TOP = 2500;
const VWP_MEAN_MIN_PTS = 5;
const VWP_MAX_GAP_M = 1500;     // doc 01 §5 — max gap between consecutive levels inside 0–6 km
const VWP_MAX_PHI = 7.0;        // ignore cuts above this angle — their 0–6 km span is too near-range/sparse to fit

// Median UNAMBIGUOUS RANGE (m) of the current cut's radials, or 0 when unreadable. Echoes beyond it are
// second-trip returns folded back into the ring, so a VAD fit must not reach past it (doc 02 §3.3). Read the
// same way as nyquistForRadial: Message 31 carries it in the RAD sub-block, legacy Message 1 on the record
// itself; the decoder reports it in KM in both cases (readShort()/10).
function unambiguousRangeM(radar, n) {
    const scans = radar.data && radar.data[radar.elevation];
    if (!scans) return 0;
    const arr = [];
    for (let i = 0; i < n; i++) {
        const rec = scans[i] && scans[i].record;
        if (!rec) continue;
        const km = (rec.radial && typeof rec.radial.unambiguous_range === 'number')
            ? rec.radial.unambiguous_range : rec.unambiguous_range;
        if (typeof km === 'number' && km > 1 && isFinite(km)) arr.push(km * 1000);
    }
    if (!arr.length) return 0;
    arr.sort(function (a, b) { return a - b; });
    return arr[arr.length >> 1];
}

// Median elevation angle (deg) of the current cut's radials — φ for the height/VAD math. NaN if none.
function medianElevationAngle(radar, n) {
    const scans = radar.data && radar.data[radar.elevation];
    if (!scans) return NaN;
    const arr = [];
    for (let i = 0; i < n; i++) {
        const rec = scans[i] && scans[i].record;
        if (rec && typeof rec.elevation_angle === 'number' && isFinite(rec.elevation_angle)) arr.push(rec.elevation_angle);
    }
    if (!arr.length) return NaN;
    arr.sort(function (a, b) { return a - b; });
    return arr[arr.length >> 1];
}

// Solve the symmetric 3×3 normal system for the VAD fit via Cramer's rule; null if near-singular.
function solve3(a, b, c, d, e, f, g, h, ii, r0, r1, r2) {
    const det = a * (e * ii - f * h) - b * (d * ii - f * g) + c * (d * h - e * g);
    if (Math.abs(det) < 1e-9) return null;
    const inv = 1 / det;
    const x0 = (r0 * (e * ii - f * h) - b * (r1 * ii - f * r2) + c * (r1 * h - e * r2)) * inv;
    const x1 = (a * (r1 * ii - f * r2) - r0 * (d * ii - f * g) + c * (d * r2 - r1 * g)) * inv;
    const x2 = (a * (e * r2 - r1 * h) - b * (d * r2 - r1 * g) + r0 * (d * h - e * g)) * inv;
    return [x0, x1, x2];
}

// ONE cut's VAD ring points, for the elevation the radar is CURRENTLY set to (decodeVwp iterates the velocity
// cuts and sets each before calling this): dealias that cut's Doppler velocity, then a per-range-ring harmonic
// fit recovers the horizontal wind (u,v) at each ring's beam height, using THIS cut's own elevation φ. Returns
// an array of { h, u, v } (m, m/s, m/s), empty when the current cut has no velocity or every ring is too sparse
// / folded / convectively contaminated to fit. The heavy step is the dealias, run once per cut; each cut casts
// its ring points to ONE merged profile (decodeVwp), which is what spans 0–6 km: no single cut can, since
// the 0–500 m tail needs φ ≤ 2.83° and the 5.5–6 km head needs φ ≥ 5.06° inside the legal 10–60 km window.
// VWP path: dealias the cut, then fit its VAD wind profile.
function vadPointsForCut(radar) {
    return vadFitFromRadials(dealiasSweep(momentRadials(radar, 'velocity'), radar), radar);
}

// Fit a VAD wind profile [{h,u,v}] from ALREADY-DEALIASED velocity radials (radar at that cut's elevation).
// Split out of vadPointsForCut so the per-decode temporal SEED can reuse the decode's shared dealias result
// WITHOUT re-dealiasing (velocityDealiased calls this on the cut it just unfolded).
function vadFitFromRadials(radials, radar) {
    if (!radials) return [];
    const n = radials.length;
    let phi = medianElevationAngle(radar, n);
    if (!isFinite(phi) || phi <= 0) phi = 0.5;      // sane default if the angle is unreadable
    const phiRad = phi * D2R, cosPhi = Math.cos(phiRad), sinPhi = Math.sin(phiRad);

    // Range geometry is shared across a cut's radials — take the first radial that carries data.
    let ref = null;
    for (let i = 0; i < n; i++) { if (radials[i] && radials[i].moment_data) { ref = radials[i]; break; } }
    if (!ref) return [];
    const firstGateM = ref.first_gate * 1000, gateSizeM = ref.gate_size * 1000, nGates = ref.moment_data.length;
    if (!isFinite(firstGateM) || !(gateSizeM > 0)) return [];

    // Per-radial azimuth sin/cos + coverage bin, once (skip null / dataless / bad-azimuth radials).
    const azSin = new Float64Array(n), azCos = new Float64Array(n), has = new Uint8Array(n);
    const nBins = Math.ceil(360 / VAD_COVERAGE_BIN_DEG);
    const azBin = new Uint8Array(n);
    for (let i = 0; i < n; i++) {
        const dd = radials[i]; if (!dd || !dd.moment_data) continue;
        const az = radar.getAzimuth(i); if (typeof az !== 'number') continue;
        const a = az * D2R; azSin[i] = Math.sin(a); azCos[i] = Math.cos(a); has[i] = 1;
        let b = Math.floor(((az % 360) + 360) % 360 / VAD_COVERAGE_BIN_DEG);
        if (b >= nBins) b = nBins - 1;
        azBin[i] = b;
    }

    // One VAD fit per range ring (gate index j), stepping ~1 km in range to keep it cheap.
    const stride = Math.max(1, Math.round(1000 / gateSizeM));
    const points = []; // { h, u, v }
    // DIAGNOSTIC ONLY (no effect on the fit): tally WHY each ring was thrown out, and how far out the last
    // ACCEPTED ring sat. A cut that yields a near-empty profile is indistinguishable in the log from a cut
    // with no velocity at all, which is what stalled the KBGM 2026-09-02 "insufficient" case — 7 of 8 cuts
    // topped under 1.7 km with no stated reason. `rej.pts` dominating means coverage (no data / too few gates
    // around the ring), `clu` means the echo is a WEDGE not an annulus, `res` means the fit is there but noisy
    // (convective contamination or a bad dealias) — three different bugs with three different fixes.
    const rej = { rings: 0, pts: 0, cov: 0, sng: 0, res: 0, sym: 0, spd: 0, lastOkKm: 0, maxKm: 0 };
    // RANGE WINDOW (doc 02 §3.3) — the fit runs ONLY over 10–60 km, and never past this cut's unambiguous
    // range. ⚠️ We previously fit every ring out to the last gate (~300 km), which is why profiles appeared
    // to reach 5–6 km on a 0.5° cut: that depth came from rings whose uniform-wind assumption is long dead.
    let maxRangeM = VAD_MAX_RANGE_M;
    const unamb = unambiguousRangeM(radar, n);
    if (unamb > 0) maxRangeM = Math.min(maxRangeM, unamb);
    rej.maxKm = Math.round(maxRangeM / 1000);
    const jStart = Math.max(0, Math.ceil((VAD_MIN_RANGE_M - firstGateM) / gateSizeM));
    const jEnd = Math.min(nGates - 1, Math.floor((maxRangeM - firstGateM) / gateSizeM));
    // Per-ring sample buffers, allocated ONCE per cut — the dealias rewrite's rule: nothing per-gate in the
    // hot path. `keep` is the two-pass outlier mask, rewritten for each ring as it is filled.
    const sBuf = new Float64Array(n), cBuf = new Float64Array(n), vBuf = new Float64Array(n);
    const keep = new Uint8Array(n), bins = new Uint8Array(nBins);
    for (let j = jStart; j <= jEnd; j += stride) {
        rej.rings++;
        // Collect this ring's valid samples once, and mark which azimuth bins they occupy.
        let m = 0;
        bins.fill(0);
        for (let i = 0; i < n; i++) {
            if (!has[i]) continue;
            const v = radials[i].moment_data[j];
            if (v === null || v === undefined) continue;
            sBuf[m] = azSin[i]; cBuf[m] = azCos[i]; vBuf[m] = v; keep[m] = 1;
            bins[azBin[i]] = 1;
            m++;
        }
        if (m < VAD_MIN_PTS) { rej.pts++; continue; }
        // AZIMUTHAL SPAN, not gate count (doc 02 §4.2): least squares will fit three coefficients to a 45°
        // arc and hand back a confident answer (16.6° mean direction error), so a ring with 300 gates piled
        // into one quadrant must fail. Replaces the old resultant-length proxy, which conflated the spread
        // of the azimuths with the shape of their distribution.
        let occupied = 0;
        for (let b = 0; b < nBins; b++) { if (bins[b]) occupied++; }
        if (occupied * VAD_COVERAGE_BIN_DEG < VAD_MIN_COVERAGE_DEG) { rej.cov++; continue; }
        // TWO-PASS FIT (doc 02 §4.3): after pass 1, gates whose residual exceeds the RMS are dropped and the
        // fit re-run — removes isolated bad gates without a separate despeckle stage.
        let sol = null, rms = Infinity;
        for (let pass = 0; pass < VAD_FIT_PASSES; pass++) {
            let sN = 0, Ss = 0, Sc = 0, Sss = 0, Scc = 0, Ssc = 0, Sv = 0, Svs = 0, Svc = 0;
            for (let i = 0; i < m; i++) {
                if (!keep[i]) continue;
                const s = sBuf[i], c = cBuf[i], v = vBuf[i];
                sN++; Ss += s; Sc += c; Sss += s * s; Scc += c * c; Ssc += s * c; Sv += v; Svs += v * s; Svc += v * c;
            }
            if (sN < VAD_MIN_PTS) { sol = null; break; }   // outlier pass ate too much of the ring
            sol = solve3(sN, Ss, Sc, Ss, Sss, Ssc, Sc, Ssc, Scc, Sv, Svs, Svc);
            if (!sol) break;
            let se = 0;
            for (let i = 0; i < m; i++) {
                if (!keep[i]) continue;
                const e = vBuf[i] - (sol[0] + sol[1] * sBuf[i] + sol[2] * cBuf[i]); se += e * e;
            }
            rms = Math.sqrt(se / sN);
            if (pass + 1 < VAD_FIT_PASSES) {
                for (let i = 0; i < m; i++) {
                    if (!keep[i]) continue;
                    const e = vBuf[i] - (sol[0] + sol[1] * sBuf[i] + sol[2] * cBuf[i]);
                    if (Math.abs(e) > rms) keep[i] = 0;
                }
            }
        }
        if (!sol) { rej.sng++; continue; }
        if (rms > VAD_MAX_RESID) { rej.res++; continue; } // contamination / bad dealias / aliasing on this ring
        const a0 = sol[0], a1 = sol[1], b1 = sol[2];
        // SYMMETRY TEST (doc 02 §4): a0 carries divergence + fall speed. A ring sampling a clean uniform wind
        // has a small a0 relative to the harmonic amplitude; a large one means the circle isn't representative.
        // `amp` is the ORPG's SPW — the amplitude BEFORE the cos φ correction, which is what it compares.
        const amp = Math.hypot(a1, b1);
        if (!(Math.abs(a0) < VAD_SYMMETRY_MAX && Math.abs(a0) - amp <= 0)) { rej.sym++; continue; }
        const u = a1 / cosPhi, vv = b1 / cosPhi;
        if (!(Math.hypot(u, vv) < VAD_MAX_SPEED)) { rej.spd++; continue; }
        const r = firstGateM + j * gateSizeM;
        rej.lastOkKm = Math.round(r / 1000);
        points.push({ h: r * sinPhi + r * r / (2 * AE_M), u: u, v: vv, phi: phi, r: r });
    }
    // Ride the counts along on the returned ARRAY rather than changing the return shape — both callers
    // (vadPointsForCut and the temporal-seed path at _decodeVadProfile) only ever index/length it, and a
    // {points, rej} pair would churn the seed path for a diagnostic. bunkersFromProfile copies it forward.
    points.rej = rej;
    return points;
}

// Bunkers layer bounds (m AGL) — Bunkers et al. (2000); docs/radar/03-bunkers-storm-motion-spec.md §2.
// Named rather than inline so the three layers can't drift apart, and so the Python mirror can cite them.
const BUNKERS_MEAN_TOP = 6000;  // 0–6 km mean wind (advection)
const BUNKERS_TAIL_TOP = 500;   // 0–0.5 km — tail of the shear vector
const BUNKERS_HEAD_BOT = 5500;  // 5.5–6 km — head of the shear vector
const BUNKERS_HEAD_TOP = 6000;

// TARGET-HEIGHT SELECTION (doc 02 §3.2). Reduce every accepted ring point from every cut to ONE level per
// target height on a regular grid — "pick target heights, and for each choose the elevation/range pair that
// lands closest", which is what the operational VWP does and what gives Bunkers the regular grid it wants.
//
// ⚠️ THIS IS NOT OPTIONAL POLISH — a naive merge of every ring is NOT a hodograph. At one height the 0.5° cut
// samples a ~50 km-radius circle while the 2.4° samples ~14 km; in a supercell those are different air. Taking
// all of them weights the profile by however many rings each cut happened to contribute (measured on Moore
// 2013: 206 of 294 points below 2.6 km) and produced a 0-6 km mean wind of 39 kt against Py-ART's 21 kt.
// One level per height, from one sampling geometry, is what makes the profile mean anything.
//
// ⚠️ TIE-BREAK is the LOWEST elevation among candidates equally close to the target. A shallower beam needs a
// smaller 1/cos φ correction to recover the horizontal wind, so it amplifies fit error least.
const VAD_TARGET_STEP_M = 250;                          // doc 02 §3.2 — "e.g. every 250 m to 6 km"
const VAD_TARGET_TOL_M = VAD_TARGET_STEP_M / 2;         // a candidate must land within half a step
function selectTargetHeights(points) {
    const out = [];
    for (let t = 0; t <= BUNKERS_MEAN_TOP; t += VAD_TARGET_STEP_M) {
        let best = null, bestD = Infinity;
        for (let i = 0; i < points.length; i++) {
            const p = points[i], d = Math.abs(p.h - t);
            if (d > VAD_TARGET_TOL_M) continue;
            // A hand-built profile may carry no phi; treat it as the least-preferred tie-break.
            const pPhi = (typeof p.phi === 'number') ? p.phi : Infinity;
            const bPhi = (best && typeof best.phi === 'number') ? best.phi : Infinity;
            if (d < bestD - 1e-9 || (Math.abs(d - bestD) <= 1e-9 && best && pPhi < bPhi)) {
                best = p; bestD = d;
            }
        }
        if (best) out.push({ h: best.h, u: best.u, v: best.v, phi: best.phi, r: best.r });
    }
    return out;
}

// PROFILE SANITY (doc 02 §5.2): the wind direction cannot swing >90° between levels less than 500 m apart.
// That is the signature of a residual velocity FOLD, not of meteorology. Applied PER CUT by decodeVwp before
// a cut's points join the merged profile — a bad cut is dropped rather than allowed to poison the shared fit.
// ⚠️ Do NOT run this on the merged profile: adjacent levels there can come from different beams, where a
// direction step is ordinary sampling difference rather than a fold.
function profileFoldSuspect(prof) {
    for (let i = 1; i < prof.length; i++) {
        const a = prof[i - 1], b = prof[i];
        if (b.h - a.h >= VAD_SANITY_GAP_M) continue;
        let d = Math.abs(Math.atan2(b.u, b.v) - Math.atan2(a.u, a.v)) / D2R;
        if (d > 180) d = 360 - d;
        if (d > VAD_SANITY_DIR_DEG) return true;
    }
    return false;
}

// HEIGHT-WEIGHTED (trapezoidal) mean wind over [h0,h1] m AGL, or null when the layer holds no observation.
// `prof` must be sorted ascending by h.
// ⚠️ NOT a plain average of the levels inside the layer, which is what this used to be. VAD ring heights go
// as r·sinφ + r²/2aₑ, so equal steps in RANGE bunch levels near the ground; an unweighted mean therefore
// silently over-weights whichever part of the layer happens to be densely sampled — always the bottom, for
// us — and drags the 0–6 km mean toward weak low-level flow. That is a candidate mechanism for the ~3 kt
// storm motions in the open low-speed bug. Spec: docs/radar/03-bunkers-storm-motion-spec.md §4, which also
// notes Bunkers (2000) is NON-pressure-weighted and that the height-weighted form is the radar analogue.
// ⚠️ Endpoints are INTERPOLATED to h0/h1 only where the profile genuinely brackets them. Where it simply
// stops, the integral stops with it — this function never extrapolates to manufacture a layer edge.
function meanLayer(prof, h0, h1) {
    const inLayer = [];
    for (let i = 0; i < prof.length; i++) { const p = prof[i]; if (p.h >= h0 && p.h <= h1) inLayer.push(p); }
    if (!inLayer.length) return null;   // no observation IN the layer — the caller decides what that means
    function edge(hEdge, below, above) {
        if (!below || !above || above.h <= below.h) return null;
        const f = (hEdge - below.h) / (above.h - below.h);
        return { h: hEdge, u: below.u + f * (above.u - below.u), v: below.v + f * (above.v - below.v) };
    }
    let lo = null, hi = null;
    for (let i = 0; i < prof.length; i++) { if (prof[i].h < h0) lo = prof[i]; }        // last level below h0
    for (let i = prof.length - 1; i >= 0; i--) { if (prof[i].h > h1) hi = prof[i]; }   // first level above h1
    const knots = inLayer.slice();
    const eLo = edge(h0, lo, knots[0]);
    if (eLo) knots.unshift(eLo);
    const eHi = edge(h1, knots[knots.length - 1], hi);
    if (eHi) knots.push(eHi);
    let iu = 0, iv = 0, dz = 0;
    for (let i = 0; i + 1 < knots.length; i++) {
        const a = knots[i], b = knots[i + 1], d = b.h - a.h;
        if (d <= 0) continue;
        iu += (a.u + b.u) / 2 * d; iv += (a.v + b.v) / 2 * d; dz += d;
    }
    if (dz <= 0) {   // one level (or coincident heights) — the trapezoid degenerates to the level itself
        let u = 0, v = 0;
        for (let i = 0; i < inLayer.length; i++) { u += inLayer[i].u; v += inLayer[i].v; }
        return { u: u / inLayer.length, v: v / inLayer.length };
    }
    return { u: iu / dz, v: iv / dz };
}

// Merge a set of VAD ring points (from one or more cuts) into a storm motion via Bunkers. Sorts by height,
// takes the height-weighted 0–6 km mean wind, and deviates 7.5 m/s to the RIGHT of the 0–6 km shear (the
// right-moving supercell estimate). THREE outcomes, in descending confidence:
//   'Bunkers R'  — a real observation in BOTH 0–0.5 km and 5.5–6 km; the full supercell estimate.
//   'Mean wind'  — the profile is deep enough to mean but a Bunkers layer is empty (or the shear is
//                  degenerate). An honest advection proxy, NOT a Bunkers vector. `why` says which.
//   insufficient — too shallow/sparse for any of it; the caller leaves SRV at base velocity.
// ⚠️ The middle tier exists because we have no fallback wind-profile provider (no Level III NVW, no model
// sounding), so the alternative to a mean wind is base velocity — which for SRV is definitionally wrong.
// It is deliberately NOT labelled Bunkers, so the readout never overstates the claim.
// Returns { speedMs, dirDeg (bearing MOVED TOWARD), source, layers, topM, deep, why, rej }.
function bunkersFromProfile(prof) {
    // Diagnostic ring-rejection tally from vadFitFromRadials (per-cut callers only). Captured BEFORE the
    // slice/sort below, which returns a plain array and would drop it. Absent when the caller hand-built a
    // profile (the unit-test mirror), so every read is guarded.
    const rej = prof ? prof.rej : null;
    if (!prof || prof.length < 2) return { insufficient: true, topM: 0, rej: rej };
    prof = prof.slice().sort(function (p, q) { return p.h - q.h; });
    const top = prof[prof.length - 1].h;
    // Genuinely too shallow/sparse for ANY trustworthy motion → insufficient (SRV stays at base velocity).
    // This still guards the single-low-tilt failure mode: a ~1.3–1.8 km profile is just boundary-layer flow,
    // not a storm motion.
    if (top < VWP_MEAN_MIN_TOP || prof.length < VWP_MEAN_MIN_PTS) return { insufficient: true, topM: Math.round(top), why: 'shallow', rej: rej };

    const mean = meanLayer(prof, 0, BUNKERS_MEAN_TOP) || meanLayer(prof, 0, top); // 0–6 km mean wind
    if (!mean) return { insufficient: true, topM: Math.round(top), rej: rej };

    // DEEP enough to trust the 0–6 km shear for a Bunkers supercell DEVIATION? Otherwise return the mean wind
    // ALONE — a serviceable storm-motion proxy (what RadarScope effectively shows) rather than base velocity.
    // ⚠️ NO SUBSTITUTION FOR A MISSING LAYER. This used to fall back to the top / bottom SAMPLED ring when
    // either Bunkers layer was empty, which manufactures a "6 km wind" out of (say) a 5.1 km one and emits a
    // confident Bunkers vector not grounded in observation — doc 03 §5 names this the single most likely place
    // our behaviour was going wrong. A real observation in BOTH layers is now required; without it there is no
    // shear vector, and the result drops to the honest Mean-wind tier instead of inventing one.
    // ⚠️ This SUPERSEDES the old `top >= VWP_MIN_TOP` proxy: reaching 5000 m says nothing about whether a ring
    // actually landed in 5500–6000 m. VWP_MIN_TOP is kept only as the cheap pre-check below.
    const bot = meanLayer(prof, 0, BUNKERS_TAIL_TOP);
    const tp = meanLayer(prof, BUNKERS_HEAD_BOT, BUNKERS_HEAD_TOP);
    // COVERAGE GAP (doc 01 §5): consecutive levels within 0–6 km must be no more than 1500 m apart, or the
    // trapezoidal mean is interpolating across a hole. ⚠️ This matters far more now the profile is MERGED
    // from several cuts: the tail can come from the 0.5° cut and the head from the 6.4°, leaving the middle
    // of the column unsampled — a shape that looks deep and is actually two clusters with a void between.
    let gapOk = true, prevH = null;
    for (let i = 0; i < prof.length; i++) {
        const h = prof[i].h;
        if (h > BUNKERS_MEAN_TOP) break;
        if (prevH !== null && h - prevH > VWP_MAX_GAP_M) { gapOk = false; break; }
        prevH = h;
    }
    const deep = !!bot && !!tp && gapOk && prof.length >= VWP_MIN_PTS && top >= VWP_MIN_TOP;
    // Why the Bunkers tier was declined, for the diagnostics line — the "make the failure say which" lesson.
    const why = deep ? null : (!tp ? 'noHead' : !bot ? 'noTail' : !gapOk ? 'gapTooLarge' : 'fewPts');
    let mu = mean.u, mv = mean.v, source = 'Mean wind', tierWhy = why;
    if (deep) {
        // Bunkers shear = (5.5–6 km mean) − (0–0.5 km mean), both real observations.
        const shu = tp.u - bot.u, shv = tp.v - bot.v, shMag = Math.hypot(shu, shv);
        if (shMag > BUNKERS_MIN_SHEAR) {
            // Right-moving deviation: 7.5 m/s to the RIGHT of the shear (a 90° clockwise turn of the unit shear).
            mu = mean.u + BUNKERS_D * (shv / shMag);
            mv = mean.v + BUNKERS_D * (-shu / shMag);
            source = 'Bunkers R';
        } else {
            tierWhy = 'weakShear';   // degenerate hodograph — the mean wind IS the answer, not a failure
        }
    }
    let dirDeg = Math.atan2(mu, mv) / D2R; if (dirDeg < 0) dirDeg += 360; // bearing the storm MOVES TOWARD
    return { speedMs: Math.hypot(mu, mv), dirDeg: dirDeg, source: source, layers: prof.length, topM: Math.round(top), deep: deep, why: tierWhy, rej: rej };
}

// Ring-rejection tally as a compact token: "<rings>r<maxKm>@<lastOkKm>km:pts92,cov18,res14" — rings examined,
// the range cap in force, the range of the OUTERMOST accepted ring, then only the NON-ZERO reject reasons,
// largest first. Zeros are omitted so a healthy cut stays short. "" when unavailable.
// ⚠️ This is the ONLY window into WHY a cut's profile stopped where it did. It was accidentally deleted once
// (in the per-cut-median -> merged-profile refactor) while cutTag still called it; every call then threw a
// ReferenceError that decodeVwp's per-buffer try/catch swallowed, so the diagnostics silently went blank AND
// a legacy .gz volume — one buffer holding every cut — lost all cuts after the first. If `detail` ever comes
// back as `[]` in the vwp result line, suspect exactly this.
function rejDetail(rej) {
    if (!rej) return '';
    const parts = [['pts', rej.pts], ['cov', rej.cov], ['res', rej.res], ['sym', rej.sym],
                   ['spd', rej.spd], ['sng', rej.sng]]
        .filter(function (p) { return p[1] > 0; })
        .sort(function (a, b) { return b[1] - a[1]; })
        .map(function (p) { return p[0] + p[1]; });
    return '/' + (rej.rings || 0) + 'r' + (rej.maxKm ? '<' + rej.maxKm : '') + '@' + (rej.lastOkKm || 0)
        + 'km:' + (parts.length ? parts.join(',') : '-');
}

// ONE CUT'S CONTRIBUTION to the merged profile, for the diagnostics line: elevation, ring points kept, the
// height span they cover, and the ring-rejection tally. A dropped cut says why instead.
// e.g. "2.4d:41p/425-2724m/232r<117@60km:pts180" · "5.1d:DROPPED(fold)".
function cutTag(phi, pts, why) {
    const head = (isFinite(phi) ? phi.toFixed(1) : '?') + 'd:';
    if (why) return head + 'DROPPED(' + why + ')' + rejDetail(pts && pts.rej);
    if (!pts || !pts.length) return head + '0p' + rejDetail(pts && pts.rej);
    let lo = Infinity, hi = -Infinity;
    for (let i = 0; i < pts.length; i++) { if (pts[i].h < lo) lo = pts[i].h; if (pts[i].h > hi) hi = pts[i].h; }
    return head + pts.length + 'p/' + Math.round(lo) + '-' + Math.round(hi) + 'm' + rejDetail(pts.rej);
}

// Full-volume VWP storm motion: every velocity-bearing cut contributes its VAD ring points to ONE MERGED
// profile, which is then run through Bunkers once.
// ⚠️ THIS REPLACED A PER-CUT MEDIAN (each cut computed its own Bunkers motion; the componentwise median of
// those was taken). Read before reverting: that design is IMPOSSIBLE under the doc 02 §3.3 range window.
// Inside 10–60 km the 0–500 m shear tail requires φ ≤ 2.83° and the 5.5–6 km head requires φ ≥ 5.06°, so no
// single cut can supply both and every cut would return 'noTail'/'noHead' forever.
// ⚠️ The median was originally chosen because a point-merge measured WRONG on Moore 2013 (~16 kt vs the real
// ENE ~27 kt), attributed to the base tilt's clutter-biased low-level winds. Those rings are exactly what the
// 10–60 km window now excludes, so the premise is gone — but the finding predates this change and the merged
// path has NOT yet been re-measured against Moore 2013. Do that before trusting it on real data.
// Contamination control moved to PER-CUT QC (profileFoldSuspect) applied before a cut joins the merge.
// ⚠️ It iterates ALL velocity elevations in a buffer, not just the lowest, because the two provisioning paths
// differ: a modern volume hands us several single-tilt files (one velocity cut each), but a LEGACY .gz archive
// can't be tilt-extracted at all (`Level2RadarService`: it gunzips to an AR2V with no bzip2 LDM records), so
// its base .V06 is cached WHOLE and arrives as ONE buffer holding every cut. Iterating elevations handles both.
// Off the UI thread (radar-worker 'vwp' task) since the per-cut dealias is the cost.
export function decodeVwp(buffers) {
    return loadDecoder().then(function (dec) {
        const merged = [];
        const detail = [];
        let cuts = 0;
        for (let b = 0; b < buffers.length; b++) {
            try {
                const radar = new dec.Level2Radar(dec.Buffer.from(new Uint8Array(buffers[b])));
                const elevs = radar.listElevations() || [];
                for (let k = 0; k < elevs.length; k++) {
                    radar.setElevation(elevs[k]);
                    const vr = momentRadials(radar, 'velocity');
                    let hasVel = false;
                    for (let i = 0; i < vr.length; i++) { if (vr[i] && vr[i].moment_data) { hasVel = true; break; } }
                    if (!hasVel) continue;                               // reflectivity-only cut → no VAD levels
                    const phi = medianElevationAngle(radar, vr.length);
                    if (isFinite(phi) && phi > VWP_MAX_PHI) continue;    // above the ceiling
                    const pts = vadPointsForCut(radar);
                    // PER-CUT QC BEFORE MERGING. This is what keeps a contaminated cut (storm core in the
                    // beam, a residual fold) out of the shared profile — the job the old per-cut median did.
                    // Run per cut, NOT on the merged profile: across a cut boundary two levels at similar
                    // heights come from different beams, so a cross-cut direction step is not a fold signature.
                    // ⚠️ DIAGNOSTICS MUST NOT BE ABLE TO BREAK THE DATA PATH. cutTag is instrumentation; it
                    // once threw a ReferenceError that the per-buffer catch below swallowed, which silently
                    // blanked the detail line AND aborted every remaining cut in that buffer. Its own guard
                    // keeps a reporting bug from ever costing us a cut again.
                    const tag = function (why) {
                        try { detail.push(cutTag(phi, pts, why)); }
                        catch (e) { detail.push('?d:tag-failed'); }
                    };
                    if (!pts || !pts.length) { tag(null); continue; }
                    if (profileFoldSuspect(pts)) { tag('fold'); continue; }
                    for (let i = 0; i < pts.length; i++) merged.push(pts[i]);
                    cuts++;
                    tag(null);
                }
            } catch (e) { /* skip a buffer that won't decode; the other buffers/cuts still contribute */ }
        }
        // One level per target height (doc 02 §3.2) BEFORE Bunkers — see selectTargetHeights.
        merged.sort(function (a, b) { return a.h - b.h; });
        const profile = selectTargetHeights(merged);
        const res = bunkersFromProfile(profile);
        res.cuts = cuts;
        res.detail = detail;
        res.rings = merged.length;   // candidate rings considered, vs res.layers = levels selected
        return res;
    });
}

// STORM-RELATIVE VELOCITY (m/s). Same dealiased Doppler cut as base velocity, minus the storm motion's
// component along each beam: for a gate at azimuth az, SRV = V − S·cos(az − dir), where S/dir are the storm
// speed/heading the host resolved into _stormMotion — the deep-VWP auto value (decodeVwp) in auto mode, or
// the user's manual value. The subtracted term is per-radial (azimuth only, not
// range), so this is a cheap transform of the already-dealiased field — the expensive dealias is not
// repeated beyond velocity's. Removing the storm's translation makes rotation (mesocyclones) read near
// zero. With S = 0 it equals base velocity. Colored by SRV_RAMP (velocity's scheme under its own id).
function buildSrv(radar, siteLat, siteLon, minDbz, wantGrid) {
    const sd = velocityDealiased(radar);          // reuses velocity's dealias within this decode (no re-dealias)
    if (!sd.radials) return { geom: null, grid: null };
    radar.setElevation(sd.elev);                  // buildGates' getAzimuth reads the current cut — pin it
    const dealiased = sd.radials;
    const getAz = function (i) { return radar.getAzimuth(i); };
    const S = _stormMotion.speedMs, dir = _stormMotion.dirDeg; // host-resolved (manual, or the deep-VWP auto value)
    // Per-azimuth offset applied to each gate; null gates (no data) stay null so they're skipped.
    const srv = dealiased.map(function (d, i) {
        if (!d || !d.moment_data) return d;
        const az = getAz(i);
        if (typeof az !== 'number') return d;
        const off = S * Math.cos((az - dir) * D2R);
        const src = d.moment_data;
        const out = new Array(src.length);
        for (let j = 0; j < src.length; j++) {
            const v = src[j];
            out[j] = (v === null || v === undefined) ? null : (v - off);
        }
        return { moment_data: out, first_gate: d.first_gate, gate_size: d.gate_size };
    });
    const geom = buildGates(srv, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(SRV_RAMP, v);
    });
    return { geom: geom, grid: buildGrid(srv, getAz, 10, SRV_RAMP.unit, 1, wantGrid) };
}

// Lowest-tilt SPECTRUM WIDTH (m/s) — the spread of velocities within a gate (turbulence / shear). It's a
// Doppler moment, so it lives in the SAME cut as velocity (found via findVelocityElevation), NOT the
// surveillance cut. Unlike velocity there is NO dealiasing (width is a magnitude, not a folded velocity),
// and no reflectivity mask — like velocity it's shown wherever the Doppler cut has data (the cut itself
// restricts it to real returns). Null if the volume has no Doppler cut / no spectrum-width moment.
function buildSpectrumWidth(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elev = findVelocityElevation(radar); // spectrum width rides the Doppler (velocity) cut
    if (elev === null) return { geom: null, grid: null };
    radar.setElevation(elev);
    const radials = momentRadials(radar, 'spectrum');
    if (!radials.some(function (s) { return s && s.moment_data; })) return { geom: null, grid: null };
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(radials, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(SPECTRUM_WIDTH_RAMP, v);
    });
    return { geom: geom, grid: buildGrid(radials, getAz, 100, SPECTRUM_WIDTH_RAMP.unit, 1, wantGrid) };
}

// Lowest-tilt correlation-coefficient (ρHV) geometry. CC is a dual-pol moment collected in the
// long-PRT SURVEILLANCE cut alongside reflectivity, so it lives at the lowest elevation NUMBER (like
// reflectivity), NOT the Doppler cut. Null if the volume carries no CC (legacy single-pol file).
//
// CC is MASKED BY REFLECTIVITY: it's only meaningful where there's actual precip signal. Without the
// mask, clear-air ground clutter / biological / noise returns carry real-but-random low CC and paint
// the whole domain with colorful speckle (RadarScope masks it the same way). We keep a CC gate only
// where the co-located reflectivity gate is >= minDbz — aligned by RANGE, since CC and reflectivity
// can use different gate geometry. The result shows CC exactly where the reflectivity product draws.
function buildCorrelation(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevs));
    const ccR = momentRadials(radar, 'rho');
    const reflR = momentRadials(radar, 'reflect');
    // Legacy Message-1 (single-pol) volumes have no ρHV at all, so bail before building anything.
    if (!ccR.some(function (c) { return c && c.moment_data; })) return { geom: null, grid: null };

    // CC is only meaningful where there's precip — mask it to reflectivity >= minDbz (shared with the
    // DOW velocity mask). Without it, clear-air / clutter ρHV speckles the whole domain.
    const masked = maskByReflectivity(ccR, reflR, minDbz);

    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(masked, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(CORRELATION_RAMP, v);
    });
    // The inspector grid uses the UNMASKED ρHV (ccR), so the cursor reads the true value anywhere
    // there's signal — not only where the reflectivity-masked geometry draws.
    return { geom: geom, grid: buildGrid(ccR, getAz, 1000, CORRELATION_RAMP.unit, 2, wantGrid) };
}

// Lowest-tilt DIFFERENTIAL REFLECTIVITY (ZDR, dB) — dual-pol, collected in the SURVEILLANCE cut
// alongside reflectivity/CC (lowest elevation NUMBER). A DIRECT moment read (not derived, unlike KDP),
// so this is an exact analog of buildCorrelation: read `zdr`, mask the DISPLAY to reflectivity >= minDbz
// (clear-air ZDR is meaningless noise), color by ZDR_RAMP; the inspector grid keeps the UNMASKED value.
// Null on a legacy single-pol volume (no ZDR).
function buildZdr(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevs));
    const zdrR = momentRadials(radar, 'zdr');
    const reflR = momentRadials(radar, 'reflect');
    // Legacy single-pol volumes carry no ZDR — bail before building anything.
    if (!zdrR.some(function (z) { return z && z.moment_data; })) return { geom: null, grid: null };

    const masked = maskByReflectivity(zdrR, reflR, minDbz);
    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(masked, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(ZDR_RAMP, v);
    });
    // Inspector reads the UNMASKED ZDR (dB); scale 100 → 0.01 dB quantization.
    return { geom: geom, grid: buildGrid(zdrR, getAz, 100, ZDR_RAMP.unit, 2, wantGrid) };
}

// Gate index in `to`'s geometry that co-locates (by RANGE, km) with gate j of `from` — the alignment
// used to read one moment's value at another moment's gate (they can have different first_gate/gate_size).
function rangeIndexOf(from, to, j) {
    return Math.round((from.first_gate + j * from.gate_size - to.first_gate) / to.gate_size);
}

// KDP quality/tuning constants (see buildKdp). Fixed 3 km least-squares window is a robust v1; an
// adaptive-by-Z window is a later refinement.
const KDP_RHO_MIN = 0.85;   // gates below this ρHV have too-noisy differential phase to trust
const KDP_WINDOW_KM = 3.0;  // half-length (km) of the ΦDP least-squares range window
const KDP_MIN_VALID = 5;    // min valid samples in a window to estimate a slope
const KDP_ABS_MAX = 15.0;   // reject |KDP| beyond this (°/km): real S-band KDP tops out ~10-12 even in
                            // violent cores, so a larger magnitude is a ΦDP-unwrap / short-window LS blowup.
                            // Measured on KTLX 2013-05-20 (tools/dualpol_check.py): p99 = 3.8, but a 0.35%
                            // tail reached ±50-80 — isolated noise spikes that paint spurious bright pixels;
                            // nulling them (vs clamping) makes them invisible holes instead. Keep in sync
                            // with tools/dualpol_check.py kdp_mirror.

// KDP (°/km) along ONE radial, derived from its ΦDP samples: unwrap the ~360° fold, drop low-quality
// gates (ρHV < KDP_RHO_MIN, or reflectivity < minDbz — aligned by range), then a fixed-window
// least-squares slope of ΦDP vs range; KDP = ½·slope. Returns a value array (null where KDP can't be
// estimated), the same length/geometry as ΦDP so buildGates/buildGrid consume it like any moment.
function kdpFromPhi(phi, refl, rho, minDbz) {
    const pd = phi.moment_data, n = pd.length, gateKm = phi.gate_size;
    const ph = new Float64Array(n);     // unwrapped ΦDP (deg) at valid gates
    const valid = new Uint8Array(n);
    let prev = null, accum = 0;
    for (let j = 0; j < n; j++) {
        let v = pd[j];
        let ok = (v !== null && v !== undefined);
        if (ok && rho && rho.moment_data) {
            const rj = rangeIndexOf(phi, rho, j);
            const rv = (rj >= 0 && rj < rho.moment_data.length) ? rho.moment_data[rj] : null;
            if (rv === null || rv === undefined || rv < KDP_RHO_MIN) ok = false;
        }
        if (ok && refl && refl.moment_data) {
            const zj = rangeIndexOf(phi, refl, j);
            const zv = (zj >= 0 && zj < refl.moment_data.length) ? refl.moment_data[zj] : null;
            if (zv === null || zv === undefined || zv < minDbz) ok = false;
        }
        if (ok) {
            if (prev !== null) { // cumulative unwrap of the ~360° ΦDP fold (compare to last RAW valid value)
                const d = v - prev;
                if (d > 180) accum -= 360; else if (d < -180) accum += 360;
            }
            prev = v;
            ph[j] = v + accum;
            valid[j] = 1;
        } else {
            ph[j] = NaN;
            // keep `prev`/`accum` across isolated dropouts so the unwrap stays continuous
        }
    }
    const w = Math.max(1, Math.round(KDP_WINDOW_KM / gateKm));
    const out = new Array(n);
    for (let i = 0; i < n; i++) {
        if (!valid[i]) { out[i] = null; continue; }
        let lo = i - w, hi = i + w;
        if (lo < 0) lo = 0;
        if (hi >= n) hi = n - 1;
        // Least-squares slope of ΦDP vs range over the window's valid gates (x offset cancels).
        let sx = 0, sy = 0, sxx = 0, sxy = 0, cnt = 0;
        for (let k = lo; k <= hi; k++) {
            if (!valid[k]) continue;
            const x = k * gateKm, y = ph[k];
            sx += x; sy += y; sxx += x * x; sxy += x * y; cnt++;
        }
        if (cnt < KDP_MIN_VALID) { out[i] = null; continue; }
        const denom = cnt * sxx - sx * sx;
        if (denom === 0) { out[i] = null; continue; }
        const kdp = 0.5 * (cnt * sxy - sx * sy) / denom; // ½·(deg/km)
        out[i] = (kdp > KDP_ABS_MAX || kdp < -KDP_ABS_MAX) ? null : kdp; // drop unphysical unwrap/window spikes
    }
    return out;
}

// Lowest-tilt SPECIFIC DIFFERENTIAL PHASE (KDP, °/km) — dual-pol, DERIVED from the ΦDP moment (not a
// direct read; see kdpFromPhi). ΦDP is collected in the SURVEILLANCE cut alongside reflectivity/CC
// (lowest elevation NUMBER), so we read it there. Per radial we turn ΦDP into a KDP value array with the
// SAME gate geometry, so it flows through buildGates/buildGrid like any moment. Null on a legacy
// single-pol volume (no ΦDP). The per-gate ρHV/reflectivity QC already restricts KDP to precip, so no
// separate reflectivity mask is needed (unlike CC, whose raw grid is unmasked).
function buildKdp(radar, siteLat, siteLon, minDbz, wantGrid) {
    const elevs = radar.listElevations();
    if (!elevs || !elevs.length) return { geom: null, grid: null };
    radar.setElevation(Math.min.apply(null, elevs));
    const phiR = momentRadials(radar, 'phi');
    const reflR = momentRadials(radar, 'reflect');
    const rhoR = momentRadials(radar, 'rho');
    // Legacy single-pol volumes carry no differential phase — bail before building anything.
    if (!phiR.some(function (p) { return p && p.moment_data; })) return { geom: null, grid: null };

    const kdpR = phiR.map(function (p, i) {
        if (!p || !p.moment_data) return p;
        return {
            moment_data: kdpFromPhi(p, reflR && reflR[i], rhoR && rhoR[i], minDbz),
            first_gate: p.first_gate, gate_size: p.gate_size,
        };
    });

    const getAz = function (i) { return radar.getAzimuth(i); };
    const geom = buildGates(kdpR, getAz, siteLat, siteLon, function (v) {
        if (v === null || v === undefined) return null;
        return rampColor(KDP_RAMP, v);
    });
    // Inspector reads KDP directly; scale 100 → 0.01 °/km quantization.
    return { geom: geom, grid: buildGrid(kdpR, getAz, 100, KDP_RAMP.unit, 2, wantGrid) };
}

// Masks a moment's radials to only where the co-located reflectivity gate is >= minDbz (aligned by
// RANGE). DOW velocity exists at EVERY gate — including clear-air / biological (insect) returns that
// carry a real-but-meaningless velocity — so without this the whole domain renders as velocity speckle.
// Keeping velocity only where there's actual precip makes it match the (dBZ-thresholded) reflectivity.
// Same idea as the CC reflectivity mask. Returns new radials; input unchanged.
function maskByReflectivity(radials, reflRadials, minDbz) {
    const out = new Array(radials.length);
    for (let i = 0; i < radials.length; i++) {
        const d = radials[i];
        if (!d || !d.moment_data) { out[i] = d; continue; }
        const r = reflRadials && reflRadials[i];
        const rd = r && r.moment_data;
        const md = new Array(d.moment_data.length);
        for (let j = 0; j < d.moment_data.length; j++) {
            let keep = false;
            if (rd) {
                const range = d.first_gate + j * d.gate_size; // km
                const rj = Math.round((range - r.first_gate) / r.gate_size);
                const rv = (rj >= 0 && rj < rd.length) ? rd[rj] : null;
                keep = (rv !== null && rv !== undefined && rv >= minDbz);
            }
            md[j] = keep ? d.moment_data[j] : null;
        }
        out[i] = { moment_data: md, first_gate: d.first_gate, gate_size: d.gate_size };
    }
    return out;
}

// Decodes a normalized DOW frame (the "dow-frame/1" JSON from tools/dow_import.py) into the SAME
// { moments, grids, built, rangeMeters, ... } result decodeAndBuild returns — so the host
// renders a mobile-radar sweep through the identical RadarLayer pipeline (WebGL fill + range ring +
// Inspect + legend). A DOW frame is ONE sweep at the truck's lat/lon: true azimuths per radial +
// Int16-quantized moment arrays. Velocity is ALREADY dealiased by the converter (Py-ART), so we do
// NOT run dealiasSweep here. Synchronous — no vendored decoder is needed (this is our own format).
// `minDbz` thresholds reflectivity (and masks CC) exactly like the NEXRAD path.
export function decodeDowFrame(json, minDbz) {
    const t0 = (typeof performance !== 'undefined') ? performance.now() : 0;
    const az = json.azimuth || [];
    const nRad = json.nRadials || az.length;
    const nodata = (typeof json.nodata === 'number') ? json.nodata : -32768;
    const lat = json.lat, lon = json.lon;
    const getAz = function (i) { return az[i]; };

    // Build {moment_data, first_gate(km), gate_size(km)} radials for a named moment (dequantizing the
    // Int16 values), or null if the frame doesn't carry it. Same shape buildGates/buildGrid consume.
    function radialsFor(name) {
        const m = json.moments && json.moments[name];
        if (!m || !m.values) return null;
        const ng = m.nGates, scale = m.scale || 1, vals = m.values;
        const out = new Array(nRad);
        for (let i = 0; i < nRad; i++) {
            const md = new Array(ng);
            const base = i * ng;
            for (let j = 0; j < ng; j++) {
                const q = vals[base + j];
                md[j] = (q === nodata) ? null : q / scale;
            }
            out[i] = { moment_data: md, first_gate: m.firstGateKm, gate_size: m.gateSizeKm };
        }
        return out;
    }

    const reflR = radialsFor('reflectivity');
    const velR = radialsFor('velocity');
    const ccR = radialsFor('rho');

    let geom = null, reflGrid = null;
    if (reflR) {
        geom = buildGates(reflR, getAz, lat, lon, function (dbz) {
            if (dbz === null || dbz === undefined || dbz < minDbz) return null;
            return rampColor(REFLECTIVITY_RAMP, dbz);
        });
        reflGrid = buildGrid(reflR, getAz, 10, REFLECTIVITY_RAMP.unit, 1);
    }

    let velGeom = null, velGrid = null;
    if (velR) {
        // Mask velocity to where reflectivity is meaningful — DOW velocity fills the whole domain
        // (incl. clear-air/bio scatter) otherwise. With no reflectivity present, leave it unmasked.
        const velMasked = reflR ? maskByReflectivity(velR, reflR, minDbz) : velR;
        velGeom = buildGates(velMasked, getAz, lat, lon, function (v) {
            if (v === null || v === undefined) return null;
            return rampColor(VELOCITY_RAMP, v);
        });
        velGrid = buildGrid(velMasked, getAz, 10, VELOCITY_RAMP.unit, 1);
    }

    // CC (dual-pol DOW only), masked by reflectivity — aligned by RANGE, same as the NEXRAD path.
    let ccGeom = null, ccGrid = null;
    if (ccR) {
        const masked = maskByReflectivity(ccR, reflR, minDbz);
        ccGeom = buildGates(masked, getAz, lat, lon, function (v) {
            if (v === null || v === undefined) return null;
            return rampColor(CORRELATION_RAMP, v);
        });
        ccGrid = buildGrid(ccR, getAz, 1000, CORRELATION_RAMP.unit, 2);
    }

    const rangeGrid = reflGrid || velGrid || ccGrid;
    const rangeMeters = rangeGrid && isFinite(rangeGrid.firstGate) && isFinite(rangeGrid.gateSize)
        ? (rangeGrid.firstGate + rangeGrid.nGates * rangeGrid.gateSize) * 1000 : 0;
    const t1 = (typeof performance !== 'undefined') ? performance.now() : 0;
    return {
        // Same keyed shape decodeAndBuild returns (see there). A DOW frame carries refl/vel/cc; velocity
        // is pre-dealiased by the converter so it's always "built" (built.velocity=true, no lazy re-decode).
        moments: { reflectivity: geom, velocity: velGeom, cc: ccGeom },
        grids: { reflectivity: reflGrid, velocity: velGrid, cc: ccGrid },
        built: { reflectivity: true, velocity: true, cc: true },
        gridsBuilt: true,
        rangeMeters: rangeMeters,
        decodeMs: Math.round(t1 - t0), buildMs: 0,
        radials: nRad, gates: reflR && reflR[0] ? reflR[0].moment_data.length : 0, bytes: 0,
        elevList: String(json.elevationDeg), velElev: -1, reflStats: null, velStats: null,
        velNyq: json.nyquistMps || 0, dealias: '',
    };
}

// Decodes a volume ArrayBuffer and returns { moments, grids, built, decodeMs, buildMs, ... }. moments is
// a per-product-id map (radar-products.js) of gate geometry with baked-in vertex colors, each null when
// that product has nothing to draw (e.g. reflectivity below threshold, or no Doppler cut for velocity),
// so the host can toggle product instantly without re-decoding. built[id] reports which builds ran.
//
// The per-product geometry builders, keyed by product id (radar-products.js). Adding a product = add a
// build fn here + a PRODUCTS entry + a ramp; decodeAndBuild then loops over the registry automatically.
// Every builder shares the (radar, siteLat, siteLon, minDbz, wantGrid) signature (buildVelocity ignores
// minDbz — it isn't reflectivity-masked) so the loop can call them uniformly.
const BUILDERS = {
    reflectivity: buildReflectivity,
    velocity: buildVelocity,
    srv: buildSrv,
    cc: buildCorrelation,
    kdp: buildKdp,
    zdr: buildZdr,
    sw: buildSpectrumWidth,
};

// buildLazy (default true) gates the LAZY products (radar-products.js `lazy:true` — today only velocity,
// the ONLY product that must dealias via dealiasSweep, by far the priciest step per frame): when the user
// isn't on a lazy product the host passes buildLazy=false and those builds are skipped. The result's
// built[id] tells the host a refl-only frame must be re-decoded before it can show velocity (see radar.js
// setProduct). Non-lazy products (reflectivity, CC) are cheap and always built.
export function decodeAndBuild(ab, siteLat, siteLon, minDbz, buildProducts, buildGrids, stormMotion, seedProfile) {
    if (buildProducts === undefined) buildProducts = true; // build everything unless told otherwise (dev harness)
    if (buildGrids === undefined) buildGrids = true;
    if (stormMotion) _stormMotion = stormMotion; // for buildSrv (SRV); host-resolved manual or deep-VWP auto value
    _sharedDealiased = null; // reset the per-decode dealias memo
    _decodeSeedProfile = seedProfile || null; // temporal first-guess wind profile for this decode's velocity dealias
    _decodeVadProfile = null;                 // this cut's own profile (fit in velocityDealiased) → returned as next seed
    // ON-DEMAND builds: reflectivity is ALWAYS built (the default view + the source of the range ring), and
    // every OTHER product is built only when the host requests it — `buildProducts` is the ARRAY of extra
    // product ids to build (the active product, plus velocity while prefetching), or the literal `true` to
    // build all (the dev validation harness). This is the big win: a decode builds the ONE product on screen,
    // not all seven (reflectivity + the 4 dual-pol moments + velocity + SRV) every time.
    const wantBuild = function (id) {
        return id === 'reflectivity' || buildProducts === true ||
            (Array.isArray(buildProducts) && buildProducts.indexOf(id) >= 0);
    };
    const bytes = ab.byteLength;
    return loadDecoder().then(function (dec) {
        const t0 = performance.now();
        const radar = new dec.Level2Radar(dec.Buffer.from(new Uint8Array(ab)));
        const t1 = performance.now();
        // buildGrids (default true) gates the per-gate inspector VALUE arrays — the host passes false
        // when Inspect is off (the common case) so long loops don't retain ~Int16 N×G per product per
        // frame. The range ring uses only the grid's scalar metadata, which is always computed, so it's
        // unaffected. Re-decoded on demand when Inspect is toggled on (see radar.js setInspect).
        // Build each product's geometry through the registry (radar-products.js). Non-lazy products
        // (reflectivity, CC) always build; lazy products (velocity — the only one that dealiases) build
        // only when buildLazy is set (the active product is lazy, or velocity prefetch is on). Every
        // BUILDERS entry shares the (radar, lat, lon, minDbz, wantGrid) signature so this stays a
        // data-driven loop; results[] keeps the full {geom,grid} for the range-ring extent below.
        const results = {}, moments = {}, grids = {}, built = {};
        for (let pi = 0; pi < PRODUCT_IDS.length; pi++) {
            const id = PRODUCT_IDS[pi];
            if (!wantBuild(id)) { moments[id] = null; grids[id] = null; built[id] = false; continue; }
            const r = BUILDERS[id](radar, siteLat, siteLon, minDbz, buildGrids);
            results[id] = r;
            moments[id] = r.geom || null;
            grids[id] = buildGrids ? (r.grid || null) : null;
            built[id] = true;
        }
        const t2 = performance.now();
        // Diagnostics: per-moment radial/azimuth-span/gate stats + the elevation NUMBERS present and
        // which one velocity came from. This is what surfaces a partial sweep (small az span) or a
        // missing/odd Doppler cut, so intermittent velocity glitches are visible in the log.
        let radials = 0, gates = 0, elevList = '', velElevNum = -1, velNyq = 0;
        let velNyqSrc = '', velNyqRad = 0, velNyqVol = 0; // instrumentation: which Nyquist field was used
        let reflStats = null, velStats = null;
        try {
            const elevs = radar.listElevations();
            elevList = (elevs || []).join(',');
            if (elevs && elevs.length) {
                radar.setElevation(Math.min.apply(null, elevs));
                const reflArr = momentRadials(radar, 'reflect');
                reflStats = sweepStats(reflArr, function (i) { return radar.getAzimuth(i); });
                radials = reflStats.rad; gates = reflStats.gates;

                const ve = findVelocityElevation(radar);
                if (ve !== null) {
                    velElevNum = ve;
                    radar.setElevation(ve);
                    const velArr = momentRadials(radar, 'velocity');
                    velStats = sweepStats(velArr, function (i) { return radar.getAzimuth(i); });
                    const det = sweepNyquistDetail(radar, velArr.length);
                    velNyq = isFinite(det.med) ? Math.round(det.med * 10) / 10 : 0;
                    velNyqSrc = det.src; // 'rad' (correct) | 'vol' (fallback, suspect) | 'mixed' | 'none'
                    velNyqRad = isFinite(det.radMed) ? Math.round(det.radMed * 10) / 10 : 0;
                    velNyqVol = isFinite(det.volMed) ? Math.round(det.volMed * 10) / 10 : 0;
                }
            }
        } catch (e) { /* stats only */ }
        // Outer data extent (metres) of the lowest tilt = first gate + all gates, from whichever
        // moment grid exists (reflectivity is the widest, ~460 km super-res). This is the radar's
        // REAL maximum range — the radius for the on-map range ring (varies by radar/VCP/product).
        // Range ring radius = outer data extent of the first built product's grid (reflectivity is first
        // in the registry and the widest sweep). The grid's scalar metadata (firstGate/gateSize/nGates)
        // is present even when buildGrids is false, so the ring works whether or not the inspector value
        // grids were shipped.
        let rangeGrid = null;
        for (let pi = 0; pi < PRODUCT_IDS.length && !rangeGrid; pi++) {
            const rr = results[PRODUCT_IDS[pi]];
            if (rr && rr.grid) rangeGrid = rr.grid;
        }
        const rangeMeters = rangeGrid && isFinite(rangeGrid.firstGate) && isFinite(rangeGrid.gateSize)
            ? (rangeGrid.firstGate + rangeGrid.nGates * rangeGrid.gateSize) * 1000 : 0;
        return {
            // Keyed by product id (radar-products.js): the host stores frames[i].moments/built/grids and
            // renders/upgrades via a map lookup instead of the old flat velPositions/ccPositions fields.
            // grids are only shipped when buildGrids (inspector on); rangeMeters above already captured
            // the extent, so a null grid here doesn't affect the range ring.
            moments: moments, grids: grids, built: built, gridsBuilt: !!buildGrids,
            rangeMeters: rangeMeters,
            decodeMs: Math.round(t1 - t0), buildMs: Math.round(t2 - t1),
            radials: radials, gates: gates, bytes: bytes,
            elevList: elevList, velElev: velElevNum, reflStats: reflStats, velStats: velStats, velNyq: velNyq,
            velNyqSrc: velNyqSrc, velNyqRad: velNyqRad, velNyqVol: velNyqVol,
            dealias: (moments.velocity || moments.srv) ? _dealiasInfo : '', // SRV dealiases too
            seedProfile: _decodeVadProfile, // this cut's VAD profile → the host feeds it back as the next decode's seed
        };
    });
}

// Grids-only fast path for the inspector. Parses the volume and builds ONLY the requested product's value
// grid (reusing that product's builder, discarding its geometry), so turning Inspect on doesn't pay a full
// decodeAndBuild — all six products' geometry PLUS a redundant velocity dealias — just to read one product's
// values. Non-velocity grids are cheap (no dealias); velocity's grid still runs its dealias (the inspector
// shows the dealiased value), but only for velocity, not the whole registry. The host MERGES the returned
// grid into the existing frame, leaving its geometry untouched. Returns { grids: { [productId]: grid|null } }.
export function decodeGridOnly(ab, siteLat, siteLon, minDbz, productId, stormMotion, seedProfile) {
    if (stormMotion) _stormMotion = stormMotion; // SRV grid needs the storm motion too
    _sharedDealiased = null; // reset the per-decode dealias memo
    _decodeSeedProfile = seedProfile || null; // seed the inspector's velocity dealias like the geometry decode
    _decodeVadProfile = null;
    return loadDecoder().then(function (dec) {
        const radar = new dec.Level2Radar(dec.Buffer.from(new Uint8Array(ab)));
        const builder = BUILDERS[productId];
        const grids = {};
        grids[productId] = builder ? (builder(radar, siteLat, siteLon, minDbz, true).grid || null) : null;
        return { grids: grids, gridProduct: productId };
    });
}
