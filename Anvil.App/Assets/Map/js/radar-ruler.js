// The RANGE RULER: a graduated spoke from the site (or from you) out to the range ring, dragged around to
// measure how far something is and how much coverage is left beyond it.
//
//                    ╱▔▔▔▔▔▔▔╲              radar-ruler-line    the AXIS, origin → ring, plus a
//                  ╱           ╲                                perpendicular tick every step
//                 │      ┌──────┤◄─ knob    radar-ruler-labels  majors carry a range label
//                 │   ┌──┴──────│                               (collision-thinned by MapLibre,
//                 │ ╳─┼──┼──●───┤                                not by us)
//                 │   ╵  ╵  ▲   │           ╳ = the site's TRUE location (the default anchor)
//                  ╲        ╲  ╱            ● = the BEAD: the thing being measured
//                    ╲▁▁▁▁▁▁╲╱              ◄ = the KNOB: rides the ring, swings the spoke
//                             ╲  ┌─────────────────────────────┐
//                              ╲ │ 151 km (94 mi) · 068°       │  ← the chip, anchored to the BEAD
//                                │ 79 km (49 mi) to the ring   │    (not to the cursor — you let go
//                                │ ring 230 km (143 mi)        │     of it and the number stays put)
//                                └─────────────────────────────┘
//
// MILES RIDE ALONG in brackets on every chip distance, the way the Inspector puts mph after m/s: the
// headline speaks the chosen unit, the bracket speaks the one road signs and warnings use. Only on the
// CHIP — tick labels stay single-unit, since two numbers per tick is the mush the ladder exists to avoid.
// When the chosen unit already IS miles the bracket is dropped rather than repeating itself.
//
// ── THE ANCHOR: SITE (default) or MY LOCATION ──────────────────────────────────────────────────────
//
//                    ╱▔▔▔▔▔▔▔▔▔▔▔╲             SITE: the spoke starts at ╳ and is always exactly one
//                  ╱               ╲                 radius long.
//                 │  ⊕─┼──┼──●──┼───┤◄─ knob   MY LOCATION: it starts at ⊕ (the reticle markers.js
//                 │    ╳            │                already draws — nothing new is drawn for it) and
//                  ╲               ╱                 runs to where the RAY meets the ring: the FAR
//                    ╲▁▁▁▁▁▁▁▁▁▁▁╱                   crossing, so it is longer on one side of you than
//                                                     on the other. Ticks and the chip measure from YOU.
//      ┌───────────────────────────────────┐
//      │ 151 km (94 mi) · 068° from you    │   ← headline, now from you
//      │ 60 km (37 mi) to the ring         │   ← along THIS bearing, to the far edge
//      │ site 42 km (26 mi) · 245° from you│   ← where the radar is, which replaces "ring 230 km"
//      └───────────────────────────────────┘
//
// ⚠️ OUTSIDE THE RING a bearing can MISS the coverage entirely. The spoke is then drawn one radius long so
// there is still something to hold, and the chip says it misses instead of inventing a distance. Pointing
// AT the coverage from outside, the spoke runs to the ring's FAR edge (the data is between the crossings).
// ⚠️ "My location" with NO location marker falls back to the site, and the chip says so — the preference is
// never silently ignored, and it is kept (not rewritten to Site) so dropping a marker honours it at once.
// ⚠️ EVERYTHING IS DONE IN THE SITE'S FRAME — metres east/north of the site, the gates' own equirectangular
// projection — including your point. Never a second projection centred on you: the far end would miss the
// ring it is meant to land on by the difference between two cos(lat)s.
//
// TWO HANDLES, TWO JOBS. The BEAD is "point at that thing": drag it anywhere and it takes BOTH the
// range and the azimuth, swinging the whole spoke to follow. The KNOB only takes the azimuth, so you
// can swing the line across a storm without losing the range you measured. One of them would not do:
// a bead alone can't sweep, and a knob alone can't measure.
//
// ⚠️ WHY IT REACHES THE RING AT ALL — this is the whole design. A rubber-band line between two points
// tells you one number, which the Inspector's tooltip already gives you. Tying the far end to the data's
// TRUE outer extent (radar-scope.js's radius, the same number the ring is drawn from) makes the second
// number possible: how much usable radar is left past what you just measured. That is the question you
// can't answer by eye, and it is why this is a RANGE ruler and not a tape measure.
//
// ⚠️ IT IS THE SUCCESSOR TO THE DELETED MILE GRID (see RadarSettingsTab.xaml's note): a square grid
// anchored to the site, which failed because at any opacity that showed over returns it was lost in the
// basemap's own lines. This draws ONE line and cases it in black the way map labels case their text, so
// it is read against returns rather than competing with them. Don't solve legibility with opacity here.
//
// ⚠️ ITS COLOUR IS THE RING'S. Settings → Radar can override both at once: map.js setScopeColor writes the
// two CSS variables (--anvil-scope-ring, --anvil-ruler-ink) INLINE on :root, which outranks either theme
// block, and refresh() below re-reads them into the live layers and the baked handle SVG. Only the ink
// moves; the casing and the bead's accent stay themed, because they are what keep the spoke legible.
//
// ── WHAT LIVES WHERE ────────────────────────────────────────────────────────────────────────────────
// The AXIS + TICKS + LABELS are geographic GeoJSON layers, so they are drawn in EVERY pane (the panes
// share a camera; a measurement visible in one only would read as broken). The two HANDLES and the chip
// are DOM, so they are PRIMARY-PANE ONLY — the same rule the site keys and the location reticle follow,
// for the same reason: per-pane DOM is expensive and four draggable beads for one measurement is four
// ways to ask the same question.
//
// STATE OWNED HERE: the mode, the azimuth, the bead's range, the unit and the anchor. The RADIUS is not
// ours — radar-scope.js owns it and we read it through the host, so there is one radius on the page and
// the ruler can never disagree with the ring it ends on. The LOCATION is not ours either: the host pushes
// it (setAnchor) every time the marker moves; this module never reads markers.js.
//
// ⚠️ TICK SPACING IS DERIVED FROM ZOOM, not fixed. Ticks are geographic, so a fixed step is a grey mush
// at z4 and three marks at z10; the step comes off the ladder below so marks stay ~28 px apart, and the
// tick LENGTH is computed in metres-per-pixel so it stays ~11 px whatever the zoom. That is why this
// module listens to the primary map's move/zoom at all — recomputing is a setData of ~40 small features,
// which is nothing, but it is coalesced to one rebuild per animation frame anyway.
//
// ⚠️ THE SAME FLAT-EARTH PROJECTION AS THE GATES (geo.js, equirectangular — imported STATICALLY, as
// radar-decode.js and radar-scope.js do, so it is present before any export here can run). The number in
// the chip therefore matches the PIXEL you are pointing at rather than a WGS84 great circle; it reads
// ~1% low at 230 km. That is deliberate: a ruler that disagreed with the gate under it would be worse
// than one that is consistently, knowably approximate.
//
// ⚠️ ANTIMERIDIAN: a spoke from an Alaskan site crosses ±180. Generated coordinates are left UNWRAPPED
// (continuous past ±180) exactly as the ring's are, which is what makes the line draw across the seam
// instead of round the world — but a lng arriving FROM MapLibre (a drag) or from the host (your location)
// can come back wrapped, so every one of them goes through toXY → unwrapLng() into the site's frame first.
// Skip that and a drag near PABC reads as a 39,000 km measurement.

import * as Geo from './geo.js';
// ⚠️ Every Theme.color() passes a fallback: an empty colour string throws inside a MapLibre paint
// property, which aborts the frame and blanks every layer above it.
import * as Theme from './theme.js';

const SRC = 'radar-ruler';
const CASING_LAYER = 'radar-ruler-casing', LINE_LAYER = 'radar-ruler-line', LABEL_LAYER = 'radar-ruler-labels';

const MIN_TICK_PX = 28;     // closest two minor ticks may sit before the ladder steps up
const TICK_PX = 11;         // on-screen half-length of a minor tick (majors are 1.7x)
const LABEL_PX = 20;        // how far off the axis a major's label sits
// Minor/major step pairs in METRES, coarsest last. The first pair whose minor clears MIN_TICK_PX wins.
const STEPS = [
    [1000, 5000], [2000, 10000], [5000, 25000], [10000, 50000],
    [25000, 100000], [50000, 250000], [100000, 500000],
];
// ⚠️ The divisors live in geo.js (UNIT_METERS, shared with the distance rings); the "one decimal below 10"
// rule is ours, and both MIRROR Models/Map/DistanceUnits.cs. If they drift, one screen disagrees with another.
const UNIT_METERS = Geo.UNIT_METERS;

// { forEachView(fn), primaryView() -> view | null, getSite() -> {lat,lon}, getRange() -> metres,
//   beforeId(map) -> string|undefined } — supplied by radar.js.
let host = null;

let on = false;
let azRad = 45 * Geo.D2R;   // spoke bearing, clockwise from north
let beadMeters = -1;        // the bead's distance FROM THE ORIGIN (-1 = unset -> half the spoke on next draw)
let units = 'km';
// The anchor as the host last pushed it. `wanted` is the preference; `has` whether there is a location to
// honour it with. See fromLocation().
let anchor = { wanted: false, has: false, lng: 0, lat: 0 };

let knob = null, bead = null, chip = null;
let rebuildRaf = 0;
let boundMap = null;        // the primary map we listen to (move/zoom) — tracked so we can unbind

function radius() { return host ? host.getRange() : 0; }
function live() { return on && radius() > 0; }
function fromLocation() { return anchor.wanted && anchor.has; }

// A lng from outside (MapLibre, the host), moved into the site's own frame — see the antimeridian note.
function unwrapLng(lng, siteLon) { return lng - 360 * Math.round((lng - siteLon) / 360); }

// ---- The site's frame -----------------------------------------------------------------------------
// Metres east (x) / north (y) of the site, by the SAME metres-per-degree geo.js uses for the gates and the
// ring. toLngLat(frame, r·sin az, r·cos az) is exactly Geo.siteToLngLat(site, r, az), so a spoke from the
// site lands on the ring pixel-for-pixel, and a spoke from anywhere else is measured on the same grid.
function frame() {
    const s = host.getSite(), k = Geo.metersPerDeg(s.lat);
    return { s: s, kx: k.mPerDegLon, ky: k.mPerDegLat };
}
function toXY(f, lng, lat) { return [(unwrapLng(lng, f.s.lon) - f.s.lon) * f.kx, (lat - f.s.lat) * f.ky]; }
function toLngLat(f, x, y) { return [f.s.lon + x / f.kx, f.s.lat + y / f.ky]; }

// How far along the ray from `o` (unit direction ux,uy) the ring (radius R, centred on the site = the
// frame's origin) is crossed — the FAR crossing, or null when the ray never reaches it. From inside the
// ring there is always exactly one forward crossing; from outside there are two or none.
function reach(o, ux, uy, R) {
    const b = o[0] * ux + o[1] * uy;                 // o · u
    const c = o[0] * o[0] + o[1] * o[1] - R * R;     // |o|² − R²
    const disc = b * b - c;
    if (disc < 0) return null;                       // the ray's LINE misses the circle
    const t = -b + Math.sqrt(disc);
    return t > 0 ? t : null;                         // both crossings behind you
}

// The spoke as it stands right now: origin, unit direction, length, and whether it meets the ring.
function spoke(f) {
    const o = fromLocation() ? toXY(f, anchor.lng, anchor.lat) : [0, 0];
    const ux = Math.sin(azRad), uy = Math.cos(azRad), R = radius();
    const t = fromLocation() ? reach(o, ux, uy, R) : R;
    return { o: o, ux: ux, uy: uy, len: t === null ? R : t, hits: t !== null };
}
function along(f, sp, m) { return toLngLat(f, sp.o[0] + m * sp.ux, sp.o[1] + m * sp.uy); }

// A dragged point as range + bearing FROM THE ORIGIN (not from the site, once you are the origin).
function polarFromOrigin(ll) {
    const f = frame(), o = spoke(f).o, p = toXY(f, ll.lng, ll.lat);
    const dx = p[0] - o[0], dy = p[1] - o[1];
    let az = Math.atan2(dx, dy);
    if (az < 0) az += 2 * Math.PI;
    return { az: az, range: Math.sqrt(dx * dx + dy * dy) };
}

// ---- Formatting -----------------------------------------------------------------------------------
function fmtDistance(meters) {
    const per = UNIT_METERS[units] || UNIT_METERS.km;
    const v = meters / per;
    return (Math.abs(v) < 10 ? v.toFixed(1) : String(Math.round(v))) + ' ' + units;
}

// A chip distance: the chosen unit, then statute miles in brackets (skipped when the unit is miles).
// ⚠️ The miles part uses the same divisor and decimal rule as fmtDistance — one formatter, two units.
function fmtChipDistance(meters) {
    const main = fmtDistance(meters);
    if (units === 'mi') return main;
    const mi = meters / UNIT_METERS.mi;
    return main + ' (' + (Math.abs(mi) < 10 ? mi.toFixed(1) : String(Math.round(mi))) + ' mi)';
}

function fmtAz(rad) {
    let deg = Math.round((rad / Geo.D2R) % 360);
    if (deg < 0) deg += 360;
    if (deg === 360) deg = 0;
    return (deg < 10 ? '00' : deg < 100 ? '0' : '') + deg + '°';
}

function metersPerPixel(map) {
    return 156543.03392804097 * Math.cos(map.getCenter().lat * Geo.D2R) / Math.pow(2, map.getZoom());
}

// ---- Geometry -------------------------------------------------------------------------------------
// Everything the panes draw, built ONCE per rebuild from the primary map's scale and handed to every
// pane: the features are geographic, so all panes get the identical FeatureCollection (the sweep does
// the same thing for the same reason).
function rulerGeoJSON(f, sp, mpp) {
    const feats = [{
        type: 'Feature', properties: { k: 'axis' },
        geometry: { type: 'LineString', coordinates: [along(f, sp, 0), along(f, sp, sp.len)] },
    }];

    // A tick is a short segment PERPENDICULAR to the axis — (uy, −ux) is u turned a right angle — so ticks
    // stay parallel to each other at every range and on every bearing.
    const px = sp.uy, py = -sp.ux;
    const tick = function (m, h) {
        const cx = sp.o[0] + m * sp.ux, cy = sp.o[1] + m * sp.uy;
        return [toLngLat(f, cx - h * px, cy - h * py), toLngLat(f, cx + h * px, cy + h * py)];
    };

    let pair = STEPS[STEPS.length - 1];
    for (let i = 0; i < STEPS.length; i++) {
        if (STEPS[i][0] / mpp >= MIN_TICK_PX) { pair = STEPS[i]; break; }
    }
    const minorStep = pair[0], majorStep = pair[1];
    const minorHalf = TICK_PX * mpp, majorHalf = minorHalf * 1.7;

    for (let m = minorStep; m <= sp.len + 1; m += minorStep) {
        const major = Math.abs(m / majorStep - Math.round(m / majorStep)) < 1e-6;
        feats.push({
            type: 'Feature', properties: { k: major ? 'major' : 'minor' },
            geometry: { type: 'LineString', coordinates: tick(m, major ? majorHalf : minorHalf) },
        });
        if (!major) continue;
        // The label point is offset in GEOGRAPHIC space rather than by text-offset, so it is always
        // perpendicular to the axis whatever direction the spoke is pointing (text-offset is in screen
        // ems, which would put the label on the wrong side of a spoke pointing west).
        const off = tick(m, -(majorHalf + LABEL_PX * mpp))[0];
        feats.push({
            type: 'Feature', properties: { k: 'label', label: fmtDistance(m) },
            geometry: { type: 'Point', coordinates: off },
        });
    }
    return { type: 'FeatureCollection', features: feats };
}

function inkColor() { return Theme.color('--anvil-ruler-ink', '#e8edf2'); }
function casingColor() { return Theme.color('--anvil-ruler-casing', '#000000'); }

function addLayers(v, data) {
    const map = v && v.map;
    if (!map) return;
    if (map.getSource(SRC)) map.getSource(SRC).setData(data);
    else map.addSource(SRC, { type: 'geojson', data: data });

    const before = host.beforeId(map);
    const ink = inkColor(), casing = casingColor();
    // ⚠️ THE CASING IS THE WHOLE LEGIBILITY STORY (see the mile-grid note at the top): a fat dark line
    // under a thin light one reads over bright returns AND over pale basemap alike, which no single
    // stroke at any opacity does.
    if (!map.getLayer(CASING_LAYER)) {
        map.addLayer({
            id: CASING_LAYER, type: 'line', source: SRC,
            filter: ['==', ['geometry-type'], 'LineString'],
            paint: { 'line-color': casing, 'line-width': 4, 'line-opacity': 0.55, 'line-blur': 0.4 },
        }, before);
    }
    if (!map.getLayer(LINE_LAYER)) {
        map.addLayer({
            id: LINE_LAYER, type: 'line', source: SRC,
            filter: ['==', ['geometry-type'], 'LineString'],
            paint: {
                'line-color': ink,
                'line-width': ['case', ['==', ['get', 'k'], 'axis'], 1.6, 1.2],
                'line-opacity': ['case', ['==', ['get', 'k'], 'minor'], 0.75, 1],
            },
        }, before);
    }
    if (!map.getLayer(LABEL_LAYER)) {
        map.addLayer({
            id: LABEL_LAYER, type: 'symbol', source: SRC,
            filter: ['==', ['geometry-type'], 'Point'],
            layout: {
                // ⚠️ 'Noto Sans Medium' is a stack the bundled styles' own glyph host actually serves
                // (mapassets/fonts). A font it doesn't have renders NOTHING — not a fallback, nothing.
                'text-field': ['get', 'label'], 'text-font': ['Noto Sans Medium'], 'text-size': 11,
                // Collision thinning is MapLibre's job: as the spoke shortens on screen the labels drop
                // themselves, instead of us maintaining a second density ladder for text.
                'text-allow-overlap': false, 'text-ignore-placement': false, 'text-padding': 4,
            },
            paint: {
                'text-color': ink, 'text-halo-color': casing, 'text-halo-width': 1.2, 'text-halo-blur': 0.4,
            },
        }, before);
    }
}

function removeLayers(v) {
    const map = v && v.map;
    if (!map) return;
    [LABEL_LAYER, LINE_LAYER, CASING_LAYER].forEach(function (id) {
        if (map.getLayer(id)) map.removeLayer(id);
    });
    if (map.getSource(SRC)) map.removeSource(SRC);
}

// ---- Handles + chip (primary pane only) ------------------------------------------------------------
// Both handles bake their colours into SVG markup, so a theme or colour change can't re-cascade them —
// refresh() re-renders both, the way markers.js does for the reticle.
function knobSvg() {
    const casing = casingColor(), ink = inkColor();
    return '<svg width="24" height="24" viewBox="0 0 24 24" aria-hidden="true">' +
        '<circle cx="12" cy="12" r="7.5" fill="' + ink + '" stroke="' + casing + '" stroke-width="1.5"/>' +
        '<circle cx="12" cy="12" r="2.5" fill="' + casing + '"/></svg>';
}

function beadSvg() {
    const casing = casingColor(), ink = inkColor();
    const core = Theme.color('--anvil-ruler-bead', '#4aa8ff');
    return '<svg width="26" height="26" viewBox="0 0 26 26" aria-hidden="true">' +
        '<circle cx="13" cy="13" r="8" fill="none" stroke="' + casing + '" stroke-width="4.5"/>' +
        '<circle cx="13" cy="13" r="8" fill="none" stroke="' + ink + '" stroke-width="2"/>' +
        '<circle cx="13" cy="13" r="4" fill="' + core + '"/></svg>';
}

function ensureHandleStyle() {
    if (document.getElementById('radar-ruler-style')) return;
    const s = document.createElement('style');
    s.id = 'radar-ruler-style';
    s.textContent =
        '.radar-ruler-handle{cursor:grab;}' +
        '.radar-ruler-handle:active{cursor:grabbing;}' +
        '.radar-ruler-handle svg{display:block;filter:drop-shadow(0 1px 2px rgba(0,0,0,.55));}';
    document.head.appendChild(s);
}

function makeHandle(map, lngLat, svg, onDrag) {
    ensureHandleStyle();
    const el = document.createElement('div');
    el.className = 'radar-ruler-handle';
    el.innerHTML = svg;
    const m = new maplibregl.Marker({ element: el, draggable: true }).setLngLat(lngLat).addTo(map);
    // 'drag' rather than 'dragend': the spoke and the numbers follow the finger. Each handler ends in a
    // rebuild, which snaps the marker back onto its constraint (the ring, or the axis) — that is what
    // keeps the knob on the circle no matter where the pointer actually is.
    m.on('drag', function () { onDrag(m.getLngLat()); });
    return m;
}

function ensureChip() {
    if (chip && chip.isConnected) return chip;
    const el = document.createElement('div');
    el.id = 'radar-ruler-chip';
    el.style.cssText = 'position:absolute;z-index:20;pointer-events:none;display:none;' +
        'font:600 12px/1.35 "Segoe UI",sans-serif;color:var(--anvil-readout-text);' +
        'background:var(--anvil-readout-bg);border:1px solid var(--anvil-readout-border);' +
        'border-radius:4px;padding:4px 8px;white-space:nowrap;box-shadow:0 1px 4px rgba(0,0,0,.55);';
    document.body.appendChild(el);
    chip = el;
    return el;
}

function drawChip(v, f, sp) {
    const map = v && v.map;
    if (!map) return;
    const el = ensureChip();
    const p = map.project(along(f, sp, beadMeters));
    const rect = map.getContainer().getBoundingClientRect();
    const sub = function (t) { return '<div style="font-weight:400;opacity:.8">' + t + '</div>'; };
    const faint = function (t) { return '<div style="font-weight:400;opacity:.6">' + t + '</div>'; };

    let html = '<div>' + fmtChipDistance(beadMeters) + ' · ' + fmtAz(azRad) +
        (fromLocation() ? ' from you' : '') + '</div>';
    html += sp.hits
        ? sub(fmtChipDistance(Math.max(0, sp.len - beadMeters)) + ' to the ring')
        : sub('this bearing misses the coverage');
    if (fromLocation()) {
        // Where the radar is, from you: the site is the frame's origin, so it is simply −o.
        const d = Math.sqrt(sp.o[0] * sp.o[0] + sp.o[1] * sp.o[1]);
        html += faint('site ' + fmtChipDistance(d) + ' · ' + fmtAz(Math.atan2(-sp.o[0], -sp.o[1])) + ' from you');
    } else {
        html += faint('ring ' + fmtChipDistance(radius()));
        if (anchor.wanted) html += faint('no location set — measuring from the site');
    }
    el.innerHTML = html;
    el.style.display = 'block';
    el.style.left = (rect.left + p.x + 18) + 'px';
    el.style.top = (rect.top + p.y + 14) + 'px';
}

function hideChip() { if (chip) chip.style.display = 'none'; }

function positionHandles(f, sp) {
    const v = host.primaryView();
    if (!v || !v.map) return;
    const knobAt = along(f, sp, sp.len), beadAt = along(f, sp, beadMeters);
    if (!knob) {
        knob = makeHandle(v.map, knobAt, knobSvg(), function (ll) {
            // KNOB: azimuth only. The range it was dragged to is discarded — rebuild puts it back on the ring.
            azRad = polarFromOrigin(ll).az;
            rebuild();
        });
    } else knob.setLngLat(knobAt);
    if (!bead) {
        bead = makeHandle(v.map, beadAt, beadSvg(), function (ll) {
            // BEAD: takes BOTH; rebuild clamps it to the spoke — you cannot measure past where the data stops.
            const polar = polarFromOrigin(ll);
            azRad = polar.az;
            beadMeters = polar.range;
            rebuild();
        });
    } else bead.setLngLat(beadAt);
}

function clearHandles() {
    if (knob) { knob.remove(); knob = null; }
    if (bead) { bead.remove(); bead = null; }
    hideChip();
}

// ---- Rebuild --------------------------------------------------------------------------------------
// ONE entry point for "the picture is stale": mode, azimuth, bead, unit, anchor, radius, zoom and pan all
// land here. Coalesced to one rebuild per animation frame so a pinch-zoom doesn't queue a hundred setDatas.
function rebuild() {
    if (rebuildRaf) return;
    rebuildRaf = requestAnimationFrame(function () {
        rebuildRaf = 0;
        if (!host) return;
        if (!live()) { host.forEachView(removeLayers); clearHandles(); return; }
        const v = host.primaryView();
        if (!v || !v.map) return;
        const f = frame(), sp = spoke(f);
        // First draw → half the spoke. Afterwards CLAMP, never reset: swinging the knob from you shortens
        // and lengthens the spoke with the bearing, and the bead jumping to the middle each time would lose
        // the measurement the knob exists to keep.
        if (beadMeters < 0) beadMeters = sp.len / 2;
        else if (beadMeters > sp.len) beadMeters = sp.len;
        const data = rulerGeoJSON(f, sp, metersPerPixel(v.map));
        host.forEachView(function (view) { addLayers(view, data); });
        positionHandles(f, sp);
        drawChip(v, f, sp);
    });
}

// The primary map drives the picture: tick spacing comes off its zoom and the chip is placed in its
// screen space. Panes share one camera, so listening to the primary covers all of them.
function bindMap() {
    const v = host && host.primaryView();
    if (!v || !v.map || boundMap === v.map) return;
    unbindMap();
    boundMap = v.map;
    boundMap.on('move', rebuild);
    boundMap.on('zoom', rebuild);
}

function unbindMap() {
    if (!boundMap) return;
    boundMap.off('move', rebuild);
    boundMap.off('zoom', rebuild);
    boundMap = null;
}

// ---- Exports --------------------------------------------------------------------------------------
export function init(h) { host = h; }

export function isOn() { return on; }

// Arm or disarm the ruler. Armed with no loop it draws nothing and waits: the radius arrives with the
// first decoded frame, and rangeChanged() brings the spoke up then.
export function setEnabled(enabled) {
    on = !!enabled;
    if (on) bindMap(); else unbindMap();
    rebuild();
}

// The decode reported a new outer extent (a new site, or a tilt whose cut reaches further). The spoke
// re-extends to it; a bead now beyond the data is pulled back onto it by rebuild().
export function rangeChanged() { if (on) rebuild(); }

// Host changed the distance unit. Labels and the chip are rebuilt; nothing about the geometry moves.
export function setUnits(u) {
    units = (u && UNIT_METERS[u]) ? u : 'km';
    if (on) rebuild();
}

// Host pushed the anchor: the PREFERENCE (wantLocation) and the location to honour it with, if any. Sent
// on every move of the location marker, so a changed ORIGIN (site ↔ you) restarts the bead at half the
// spoke, but a marker merely dragged keeps the bead's range — you are refining where you are, not starting
// a new measurement.
export function setAnchor(wantLocation, hasLocation, lng, lat) {
    const has = !!hasLocation && isFinite(lng) && isFinite(lat);
    const wasFromLocation = fromLocation();
    anchor = { wanted: !!wantLocation, has: has, lng: +lng, lat: +lat };
    if (fromLocation() !== wasFromLocation) beadMeters = -1;
    if (on) rebuild();
}

// Give ONE pane its ruler layers — a pane created by a layout change, or one whose layers a basemap
// switch dropped (radar.js reAdd). Nothing here is per-pane state, so it is a plain re-add.
export function attachView(v) {
    if (!host || !live()) return;
    const p = host.primaryView();
    if (!p || !p.map) return;
    const f = frame(), sp = spoke(f);
    if (beadMeters < 0 || beadMeters > sp.len) { rebuild(); return; }   // let rebuild settle the bead first
    addLayers(v, rulerGeoJSON(f, sp, metersPerPixel(p.map)));
    bindMap();
}

// A pane is going away (before map.remove()). Its layers go with the map; the handles only ever live on
// the primary, so they are dropped if THAT is the map leaving.
export function detachView(v) {
    if (v && boundMap === v.map) { unbindMap(); clearHandles(); }
}

// Re-read the colours after a theme switch or a Settings colour change: the handles bake theirs into SVG
// markup, and the live layers hold theirs as paint properties — neither re-cascades from the CSS on its own.
export function refresh() {
    if (knob) knob.getElement().innerHTML = knobSvg();
    if (bead) bead.getElement().innerHTML = beadSvg();
    if (!host) return;
    const ink = inkColor(), casing = casingColor();
    host.forEachView(function (v) {
        const map = v.map;
        if (!map) return;
        if (map.getLayer(CASING_LAYER)) map.setPaintProperty(CASING_LAYER, 'line-color', casing);
        if (map.getLayer(LINE_LAYER)) map.setPaintProperty(LINE_LAYER, 'line-color', ink);
        if (map.getLayer(LABEL_LAYER)) {
            map.setPaintProperty(LABEL_LAYER, 'text-color', ink);
            map.setPaintProperty(LABEL_LAYER, 'text-halo-color', casing);
        }
    });
}

// The loop was cleared or the site changed: drop the picture but KEEP the mode, the bearing and the anchor,
// so a ruler you armed is still armed at the next site and still pointing the way you left it.
export function reset() {
    if (!host) return;
    host.forEachView(removeLayers);
    clearHandles();
    beadMeters = -1;
}
