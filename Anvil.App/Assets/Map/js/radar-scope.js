// The radar SCOPE furniture: the range rings around the site, and the one-shot sweep pulse that rotates
// out to the reflectivity edge.
//
//                  ╱▔▔▔▔▔▔▔╲              level2-range      REFLECTIVITY OUTLINE — where the DISPLAYED
//               ┆╱  ┌┄┄┄┄┄┐  ╲┆                             frame's reflectivity stops (reach.refl)
//              ┆│  ┆╱████▏┆   │┆          level2-range-vel  VELOCITY REACH, dashed — where its velocity
//              ┆│  ┆╳───▶ ┆   │┆                            stops (reach.vel); past it no vel / SRV
//              ┆│  ┆╲▒▒▒▒ ┆   │┆          level2-range-dist DISTANCE RINGS, faint dotted, every step of
//               ┆╲  └┄┄┄┄┄┘  ╱┆           (+ -label)        the distance unit out to DIST_EXTENT_M,
//                  ╲▁▁▁▁▁▁▁╱                                labelled at north ("100 km")
//                                          level2-sweep-*    arm + comet tail, one revolution then a
//                                                            fade, only on a genuinely NEW frame
//
//   WHICH RINGS: Settings → Radar → Range rings & ruler (setRings; default outline + velocity). SIZE: the
//   DISPLAYED frame's reach (radar.js syncScope → setReach), never the last frame to decode. The distance
//   rings do NOT follow the data — they run to DIST_EXTENT_M so they stay put when a TDWR's upper tilt
//   shrinks the other two to 89 km; only Auto spacing reads the reach.
//   ⚠️ The user's ring colour (--anvil-scope-ring) is the OUTLINE's only; velocity and distance keep their
//   own colours so the three never read as one. The ruler ends at reach.refl even when the outline is off.
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

const RANGE_SRC = 'level2-range', RANGE_LAYER = 'level2-range';                 // reflectivity outline
const VEL_SRC = 'level2-range-vel', VEL_LAYER = 'level2-range-vel';             // velocity reach
const DIST_SRC = 'level2-range-dist', DIST_LAYER = 'level2-range-dist';         // distance rings
const DIST_LABEL_LAYER = 'level2-range-dist-label';
const DIST_EXTENT_M = 460000; // distance rings run to a NEXRAD's full reach whatever is on screen
const DIST_MAX_RINGS = 40;    // a cap, not a design value: 25 nm to 460 km is 10 rings
const SWEEP_SRC = 'level2-sweep', SWEEP_FILL_LAYER = 'level2-sweep-fill', SWEEP_ARM_LAYER = 'level2-sweep-arm';
const SWEEP_MS = 1300;        // duration of one revolution
const SWEEP_FADE_MS = 400;    // brief fade-out of the trail once the revolution completes
const SWEEP_TRAIL_DEG = 75;   // angular length of the trailing afterglow behind the leading arm
const SWEEP_TRAIL_N = 64;     // wedge triangles across the trail — high so the taper reads smooth (no spokes)
const SWEEP_PEAK = 0.42;      // peak fill opacity right behind the arm (the wedge is a translucent glow)
const SWEEP_GAMMA = 1.6;      // trailing-fade shape (>1 = fades to nothing faster → a comet-tail falloff)

// { forEachView(fn), viewCount(), beforeId(map), getSite() -> {lat,lon} } — supplied by radar.js.
let host = null;
let reflMeters = 0, velMeters = 0;                          // the DISPLAYED frame's reach (setReach)
let rings = { refl: true, vel: true, dist: false, spacing: 0 }; // setRings; spacing 0 = Auto
let units = 'km';                                            // setUnits — the distance rings' unit
let sweepAnimStart = 0, sweepRaf = 0;

export function init(h) { host = h; }

// ---- Range rings ----
// Each ring is a 128-point circle around the site, using the same equirectangular metres-per-degree
// approximation as the gate geometry (radar-decode buildGates) so a ring lines up exactly with the data's
// edge. The RADII are shared (one site, one displayed frame); the SOURCES + LAYERS are per map, so each pane
// draws its own rings from the same numbers.
function circle(R) {
    const N = 128, s = host.getSite(), coords = [];
    for (let k = 0; k <= N; k++) coords.push(Geo.siteToLngLat(s.lat, s.lon, R, (k / N) * 2 * Math.PI));
    return coords;
}
function ringFeature(R) {
    return { type: 'Feature', geometry: { type: 'LineString', coordinates: circle(R) } };
}

// The distance rings' step, in the distance UNIT. A fixed setting wins; Auto picks from the displayed
// reflectivity reach so a short reach gets finer rings (a TDWR's 89 km upper tilt → every 25; a NEXRAD's
// 460 km → every 100 km, or 50 mi).
function autoStep() {
    if (rings.spacing > 0) return rings.spacing;
    const v = reflMeters / (Geo.UNIT_METERS[units] || 1000);
    return v <= 120 ? 25 : v <= 300 ? 50 : 100;
}
function distCollection() {
    const per = Geo.UNIT_METERS[units] || 1000, step = autoStep() * per, s = host.getSite();
    const extent = Math.max(DIST_EXTENT_M, reflMeters), features = [];
    for (let r = step, n = 0; r <= extent + 1 && n < DIST_MAX_RINGS; r += step, n++) {
        features.push(ringFeature(r));
        features.push({
            type: 'Feature', properties: { label: Math.round(r / per) + ' ' + units },
            geometry: { type: 'Point', coordinates: Geo.siteToLngLat(s.lat, s.lon, r, 0) },
        });
    }
    return { type: 'FeatureCollection', features: features };
}

function setSource(map, id, data) {
    if (map.getSource(id)) map.getSource(id).setData(data);
    else map.addSource(id, { type: 'geojson', data: data });
}
function drop(map, layerIds, srcId) {
    layerIds.forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
    if (map.getSource(srcId)) map.removeSource(srcId);
}
// Stacking, bottom to top: distance rings, velocity, outline — then everything the host puts above radar.
// A ring switched on later is slotted under the next ring UP that exists, so the order holds.
function beforeFor(map, id) {
    const above = id === DIST_LAYER ? [VEL_LAYER, RANGE_LAYER] : id === VEL_LAYER ? [RANGE_LAYER] : [];
    for (let i = 0; i < above.length; i++) if (map.getLayer(above[i])) return above[i];
    return host.beforeId(map);
}

// Bring ONE pane's rings in line with the state: add what should show, update the radii, drop the rest.
function drawRings(v) {
    const map = v && v.map;
    if (!map) return;
    const have = reflMeters > 0;

    if (have && rings.dist) {
        setSource(map, DIST_SRC, distCollection());
        if (!map.getLayer(DIST_LAYER)) {
            map.addLayer({
                id: DIST_LAYER, type: 'line', source: DIST_SRC, filter: ['==', ['geometry-type'], 'LineString'],
                paint: { 'line-color': Theme.color('--anvil-scope-dist', '#c7cdd4'), 'line-width': 0.8, 'line-opacity': 0.45, 'line-dasharray': [1, 3] },
            }, beforeFor(map, DIST_LAYER));
        }
        if (!map.getLayer(DIST_LABEL_LAYER)) {
            map.addLayer({
                id: DIST_LABEL_LAYER, type: 'symbol', source: DIST_SRC, filter: ['==', ['geometry-type'], 'Point'],
                layout: {
                    // ⚠️ 'Noto Sans Medium' — the one stack the bundled glyph host serves (see radar-ruler.js).
                    'text-field': ['get', 'label'], 'text-font': ['Noto Sans Medium'], 'text-size': 10,
                    'text-offset': [0, -0.7], 'text-allow-overlap': false, 'text-padding': 4,
                },
                paint: {
                    'text-color': Theme.color('--anvil-scope-dist', '#c7cdd4'),
                    'text-halo-color': Theme.color('--anvil-ruler-casing', '#000000'), 'text-halo-width': 1.2,
                },
            }, beforeFor(map, DIST_LAYER));
        }
    } else {
        drop(map, [DIST_LABEL_LAYER, DIST_LAYER], DIST_SRC);
    }

    if (have && rings.vel && velMeters > 0) {
        setSource(map, VEL_SRC, ringFeature(velMeters));
        if (!map.getLayer(VEL_LAYER)) {
            map.addLayer({
                id: VEL_LAYER, type: 'line', source: VEL_SRC,
                paint: { 'line-color': Theme.color('--anvil-scope-vel', '#ffa07a'), 'line-width': 1.2, 'line-opacity': 0.6, 'line-dasharray': [4, 3] },
            }, beforeFor(map, VEL_LAYER));
        }
    } else {
        drop(map, [VEL_LAYER], VEL_SRC);
    }

    if (have && rings.refl) {
        setSource(map, RANGE_SRC, ringFeature(reflMeters));
        if (!map.getLayer(RANGE_LAYER)) {
            map.addLayer({
                id: RANGE_LAYER, type: 'line', source: RANGE_SRC,
                paint: { 'line-color': Theme.color('--anvil-scope-ring', '#9fe0ff'), 'line-width': 1.3, 'line-opacity': 0.55, 'line-blur': 0.3 },
            }, beforeFor(map, RANGE_LAYER));
        }
    } else {
        drop(map, [RANGE_LAYER], RANGE_SRC);
    }
}
function removeRings(v) {
    const map = v && v.map;
    if (!map) return;
    drop(map, [DIST_LABEL_LAYER, DIST_LAYER], DIST_SRC);
    drop(map, [VEL_LAYER], VEL_SRC);
    drop(map, [RANGE_LAYER], RANGE_SRC);
}
// Every pane has every ring it should — a pane added since the last frame fails this, which is what gets
// it rings without a new decode.
function allUp() {
    let up = host.viewCount() > 0;
    host.forEachView(function (v) {
        const m = v.map;
        if (rings.refl && !m.getLayer(RANGE_LAYER)) up = false;
        if (rings.vel && velMeters > 0 && !m.getLayer(VEL_LAYER)) up = false;
        if (rings.dist && !m.getLayer(DIST_LAYER)) up = false;
    });
    return up;
}

// The DISPLAYED frame's reach (metres), from radar.js syncScope. Frames of one site and tilt reach the same
// distance, so this only redraws when a radius really moves (a new site, a tilt) or a pane is missing its
// rings. Returns true when the REFLECTIVITY reach moved — the ruler re-extends on that.
export function setReach(refl, vel) {
    if (!host || !(refl > 0)) return false;
    const v = vel > 0 ? vel : 0;
    const reflMoved = Math.abs(refl - reflMeters) >= 500;
    const velMoved = Math.abs(v - velMeters) >= 500;
    if (!reflMoved && !velMoved && allUp()) return false;
    reflMeters = refl;
    velMeters = v;
    host.forEachView(drawRings);
    return reflMoved;
}

// Which rings to draw (Settings → Radar). spacing = the distance rings' step in the unit, 0 = Auto.
export function setRings(o) {
    rings = { refl: !!o.refl, vel: !!o.vel, dist: !!o.dist, spacing: Number(o.spacing) > 0 ? Number(o.spacing) : 0 };
    if (host) host.forEachView(drawRings);
}

// The distance rings' unit (Settings → Radar → Readouts). Only they (and their labels) use it.
export function setUnits(u) {
    units = Geo.UNIT_METERS[u] ? u : 'km';
    if (host && rings.dist) host.forEachView(drawRings);
}

// The reflectivity reach, in metres (0 = no frame yet). ⚠️ The ONE radius on the page: radar-ruler.js reads
// it through here rather than keeping its own, so the ruler's far end can never disagree with the outline it
// lands on — and it keeps ending there when the outline is switched off.
export function getRange() { return reflMeters; }

// Re-read the ring colours into every pane's live layers — after Settings → Radar overrides the outline's
// (map.js setScopeColor writes --anvil-scope-ring inline on :root) or clears it. A paint property holds the
// colour it was given at add time, so the CSS change alone would not reach a ring already on screen.
// The sweep is deliberately untouched: its warm afterglow is not the ring's colour (see theme.css).
export function refreshColors() {
    if (!host) return;
    const c = Theme.color('--anvil-scope-ring', '#9fe0ff');
    host.forEachView(function (v) {
        if (v.map && v.map.getLayer(RANGE_LAYER)) v.map.setPaintProperty(RANGE_LAYER, 'line-color', c);
    });
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
    const tipAt = function (a) { return Geo.siteToLngLat(s.lat, s.lon, reflMeters, a); };
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
    if (!map || !(reflMeters > 0)) return;
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
    if (!host.viewCount() || !(reflMeters > 0)) return; // nothing to draw (e.g. layer dropped)
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
    if (!host || !(reflMeters > 0)) return;
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
    drawRings(v);
    if (sweepRaf) ensureSweepLayer(v);
}

// Drop the rings + sweep everywhere and forget the radii — a new site, a clear, or a DOW frame. The next
// displayed frame redraws them at its reach. The ring CHOICE (setRings) and unit survive: they're settings.
export function reset() {
    if (!host) return;
    host.forEachView(removeRings);
    stop();
    reflMeters = 0;
    velMeters = 0;
}
