// The radar SCOPE furniture: the range ring at the data's true outer extent, and the one-shot sweep
// pulse that rotates out to that same edge.
//
//                  ╱▔▔▔▔▔▔▔╲              level2-range      the ring, a 128-pt circle at the data's
//                ╱     ▁▁▁    ╲                              TRUE outer extent (rangeMeters), so it
//               │    ╱█████▏   │                             traces exactly where the returns stop
//               │   ╱███████▏  │           level2-sweep-arm  the crisp leading arm
//               │  ╳────────▶  │           level2-sweep-fill SWEEP_TRAIL_N abutting triangles behind
//               │   ╲▒▒▒▒▒▒▒   │                             it, opacity falling off by SWEEP_GAMMA
//                ╲    ░░░░    ╱                              — a comet tail, not a hard wedge
//                  ╲▁▁▁▁▁▁▁╱
//                                          One revolution (SWEEP_MS) then a fade (SWEEP_FADE_MS),
//                                          fired only on a genuinely NEW frame. No sweep in replay.
//
//   ⚠️ fill-antialias MUST stay off — it outlines every triangle and the tail becomes a fan of spokes.
//   Faint seams between triangles can still show; that is known and cosmetic (a canvas conic-gradient
//   texture is the real fix, if it ever matters enough).
//
// ONE module because they are one thing. The sweep's wedge is defined BY the ring's radius, both are
// geographic MapLibre GeoJSON layers (unlike the WebGL fill in radar.js, or the DOM site markers in
// radar-sites.js), and both are anchored to the same site. Splitting them would mean handing the
// radius across a module boundary every animation frame.
//
// STATE OWNED HERE: the drawn radius and the animation handle — nothing else in radar.js reads them.
// radar.js still owns the view list and the site; this module reads those through the `host` context
// it is given at init, so there is no copy to keep in sync.
//
// geo.js is imported STATICALLY (as radar-decode.js does), so the projection is guaranteed present by
// the time any export here can run. That replaces the `!Geo` guard every draw path used to carry; the
// single remaining guard is in radar.js, which skips these calls until this module has loaded.

import * as Geo from './geo.js';
// The ring + sweep are MapLibre PAINT properties, which can't read a CSS variable — hence theme.js.
// ⚠️ Each read passes a fallback: an empty color string throws inside render, which aborts the frame
// and blanks every layer above the radar.
import * as Theme from './theme.js';

const RANGE_SRC = 'level2-range', RANGE_LAYER = 'level2-range';
const SWEEP_SRC = 'level2-sweep', SWEEP_FILL_LAYER = 'level2-sweep-fill', SWEEP_ARM_LAYER = 'level2-sweep-arm';
const SWEEP_MS = 1300;        // duration of one revolution
const SWEEP_FADE_MS = 400;    // brief fade-out of the trail once the revolution completes
const SWEEP_TRAIL_DEG = 75;   // angular length of the trailing afterglow behind the leading arm
const SWEEP_TRAIL_N = 64;     // wedge triangles across the trail — high so the taper reads smooth (no spokes)
const SWEEP_PEAK = 0.42;      // peak fill opacity right behind the arm (the wedge is a translucent glow)
const SWEEP_GAMMA = 1.6;      // trailing-fade shape (>1 = fades to nothing faster → a comet-tail falloff)

// { forEachView(fn), viewCount(), beforeId(map), getSite() -> {lat,lon} } — supplied by radar.js.
let host = null;
let currentRangeMeters = 0;
let sweepAnimStart = 0, sweepRaf = 0;

export function init(h) { host = h; }

// ---- Range ring (real outer data extent) ----
// A 128-point circle at currentRangeMeters around the site, using the same equirectangular
// metres-per-degree approximation as the gate geometry (radar-decode buildGates) so the ring
// lines up exactly with the data's edge.
function ringGeoJSON() {
    const N = 128, R = currentRangeMeters, s = host.getSite();
    const coords = [];
    for (let k = 0; k <= N; k++) {
        coords.push(Geo.siteToLngLat(s.lat, s.lon, R, (k / N) * 2 * Math.PI));
    }
    return { type: 'Feature', geometry: { type: 'LineString', coordinates: coords } };
}
// The RADIUS is shared (one site, one range); the SOURCE + LAYER are per map, so each pane draws
// its own ring from the same number.
function addRangeRing(v) {
    const map = v && v.map;
    if (!map || !(currentRangeMeters > 0)) return;
    if (map.getSource(RANGE_SRC)) map.getSource(RANGE_SRC).setData(ringGeoJSON());
    else map.addSource(RANGE_SRC, { type: 'geojson', data: ringGeoJSON() });
    if (!map.getLayer(RANGE_LAYER)) {
        map.addLayer({
            id: RANGE_LAYER, type: 'line', source: RANGE_SRC,
            paint: { 'line-color': Theme.color('--anvil-scope-ring', '#9fe0ff'), 'line-width': 1.3, 'line-opacity': 0.55, 'line-blur': 0.3 },
        }, host.beforeId(map));
    }
}
function removeRangeRing(v) {
    const map = v && v.map;
    if (map && map.getLayer(RANGE_LAYER)) map.removeLayer(RANGE_LAYER);
    if (map && map.getSource(RANGE_SRC)) map.removeSource(RANGE_SRC);
}

// Draw/update the ring for the freshly-decoded range. Per-frame ranges are ~identical, so
// only rebuild when it actually changes (or a layer is missing, e.g. after a re-add). The sweep
// pulse owns its own layer (added on demand in pulse()), so nothing to ensure here.
export function setRange(rangeMeters) {
    if (!host || !(rangeMeters > 0)) return;
    const same = Math.abs(rangeMeters - currentRangeMeters) < 500;
    // Unchanged radius AND every pane already has its ring up -> nothing to do. A pane added since
    // the last frame fails the second test, which is what gets it a ring without a new decode.
    let allUp = host.viewCount() > 0;
    host.forEachView(function (v) { if (!v.map.getLayer(RANGE_LAYER)) allUp = false; });
    if (same && allUp) {
        return;
    }
    currentRangeMeters = rangeMeters;
    host.forEachView(addRangeRing);
}

// ---- Sweep pulse ----
// The trailing afterglow as a FILLED WEDGE: a fan of SWEEP_TRAIL_N abutting triangles from the site out
// to the range-ring edge, spanning SWEEP_TRAIL_DEG BEHIND the leading bearing (0 = due north). Because
// the triangles TILE (share edges, no gaps), it reads as a continuous glow that fades leading→tail —
// unlike the old radial spokes, which diverged with range and looked ragged. Each triangle carries an
// `o` fill-opacity; a separate LineString (the crisp leading arm) is appended and rendered by the line
// layer. `fade` scales everything for the end fade-out. Same metres-per-degree projection as the ring.
function sweepWedgeGeoJSON(leadRad, fade) {
    const feats = [];
    const s = host.getSite();
    const center = [s.lon, s.lat];
    const step = (SWEEP_TRAIL_DEG * Math.PI / 180) / SWEEP_TRAIL_N;
    const tipAt = function (a) { return Geo.siteToLngLat(s.lat, s.lon, currentRangeMeters, a); };
    let prevTip = tipAt(leadRad);
    for (let i = 1; i <= SWEEP_TRAIL_N; i++) {
        const ang = leadRad - i * step;
        if (ang < 0) break; // don't draw behind the sweep's start (north) on the first revolution
        const tip = tipAt(ang);
        // Opacity for the slice between the previous (brighter) and this (dimmer) edge — use its midpoint
        // fraction down the trail, with a gamma falloff so the tail fades to nothing like phosphor decay.
        const o = fade * SWEEP_PEAK * Math.pow(1 - (i - 0.5) / SWEEP_TRAIL_N, SWEEP_GAMMA);
        if (o > 0.004) {
            feats.push({ type: 'Feature', properties: { o: o },
                geometry: { type: 'Polygon', coordinates: [[center, prevTip, tip, center]] } });
        }
        prevTip = tip;
    }
    // Crisp bright leading arm (a LineString → the line layer draws it; the fill layer ignores it).
    feats.push({ type: 'Feature', properties: { o: fade },
        geometry: { type: 'LineString', coordinates: [center, tipAt(leadRad)] } });
    return { type: 'FeatureCollection', features: feats };
}
// One animation drives every pane's sweep: a sweep in only one pane of four would read as broken.
// The per-frame cost is a setData of ~64 small polygons per pane, which is nothing next to the
// basemap they are drawn over.
function ensureSweepLayer(v) {
    const map = v && v.map;
    if (!map || !(currentRangeMeters > 0)) return;
    if (!map.getSource(SWEEP_SRC)) map.addSource(SWEEP_SRC, { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });
    const before = host.beforeId(map);
    // Fill = the fading wedge (renders only the Polygon features); line = the crisp arm (only the
    // LineString). Both on top of the ring + radar fill. Per-feature `o` opacity for the animated fade.
    if (!map.getLayer(SWEEP_FILL_LAYER)) {
        map.addLayer({
            id: SWEEP_FILL_LAYER, type: 'fill', source: SWEEP_SRC,
            // ⚠️ antialias MUST be off: it outlines every polygon, so the 64 abutting triangles' shared
            // radial edges would each draw a 1px seam — reading as a fan of faint lines, exactly the
            // raggedness we're removing. Off, the triangles blend seamlessly (opacity steps are ~1%).
            paint: { 'fill-color': Theme.color('--anvil-scope-sweep-fill', '#ffe6a0'), 'fill-opacity': ['get', 'o'], 'fill-antialias': false },
        }, before);
    }
    if (!map.getLayer(SWEEP_ARM_LAYER)) {
        map.addLayer({
            id: SWEEP_ARM_LAYER, type: 'line', source: SWEEP_SRC,
            paint: { 'line-color': Theme.color('--anvil-scope-sweep-arm', '#fff4c8'), 'line-width': 2, 'line-blur': 1.2, 'line-opacity': ['get', 'o'] },
        }, before);
    }
}
function clearSweepData() {
    host.forEachView(function (v) {
        const src = v.map.getSource(SWEEP_SRC);
        if (src) src.setData({ type: 'FeatureCollection', features: [] });
    });
}
function sweepPulseFrame() {
    sweepRaf = 0;
    if (!host.viewCount() || !(currentRangeMeters > 0)) return; // nothing to draw (e.g. layer dropped)
    const el = performance.now() - sweepAnimStart;
    if (el >= SWEEP_MS + SWEEP_FADE_MS) { clearSweepData(); return; }            // revolution done → hide arm
    let lead, fade;
    if (el < SWEEP_MS) { lead = (el / SWEEP_MS) * 2 * Math.PI; fade = 1; }       // sweeping 0→360°
    else { lead = 2 * Math.PI; fade = 1 - (el - SWEEP_MS) / SWEEP_FADE_MS; }     // hold at north, fade the trail out
    // Build the wedge ONCE and hand the same GeoJSON to every pane — the geometry is geographic, so
    // it is identical in all of them.
    const data = sweepWedgeGeoJSON(lead, fade);
    host.forEachView(function (v) {
        const src = v.map.getSource(SWEEP_SRC);
        if (src) src.setData(data);
    });
    sweepRaf = requestAnimationFrame(sweepPulseFrame);
}

// Fire ONE sweep pulse (the app calls this when a genuinely-new frame lands). Restarts if one is
// already mid-flight. No-op until a frame has decoded (no radius to sweep yet).
export function pulse() {
    if (!host || !(currentRangeMeters > 0)) return;
    host.forEachView(ensureSweepLayer);
    sweepAnimStart = performance.now();
    if (!sweepRaf) sweepRaf = requestAnimationFrame(sweepPulseFrame);
}

// Stop any in-flight pulse and drop its layers in every pane (site change / clear / DOW / turn-off).
export function stop() {
    if (!host) return;
    if (sweepRaf) { cancelAnimationFrame(sweepRaf); sweepRaf = 0; }
    host.forEachView(function (v) {
        const map = v.map;
        if (map.getLayer(SWEEP_ARM_LAYER)) map.removeLayer(SWEEP_ARM_LAYER);
        if (map.getLayer(SWEEP_FILL_LAYER)) map.removeLayer(SWEEP_FILL_LAYER);
        if (map.getSource(SWEEP_SRC)) map.removeSource(SWEEP_SRC);
    });
}

// Give ONE pane its scope furniture: the ring (if a radius is known) and, when a pulse is mid-flight,
// the sweep layer so the in-progress revolution keeps drawing there too. Used both when a pane is
// created (setViews) and when a basemap switch drops its layers (reAdd).
export function attachView(v) {
    if (!host) return;
    addRangeRing(v);
    if (sweepRaf) ensureSweepLayer(v);
}

// Drop the ring + sweep everywhere and forget the radius — a new site, a clear, or a DOW frame. The
// next decoded frame redraws the ring at the new range. (radar.js called this exact triad in three
// places; it is one call now.)
export function reset() {
    if (!host) return;
    host.forEachView(removeRangeRing);
    stop();
    currentRangeMeters = 0;
}
