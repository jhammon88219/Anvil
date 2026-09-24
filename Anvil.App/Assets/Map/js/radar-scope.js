// The radar SCOPE furniture: the range rings around the site, and the one-shot sweep pulse that rotates
// out to the reflectivity edge.
//
//                  ╱▔▔▔▔▔▔▔╲              level2-range      REFLECTIVITY OUTLINE — where the DISPLAYED
//               ┆╱  ┌┄┄┄┄┄┐  ╲┆                             frame's reflectivity stops (reach.refl)
//              ┆│  ┆╱████▏┆   │┆          level2-range-vel  VELOCITY REACH, dashed — where its velocity
//              ┆│  ┆╳───▶ ┆   │┆                            stops (reach.vel); past it no vel / SRV
//              ┆│  ┆╲▒▒▒▒ ┆   │┆          level2-range-dist DISTANCE RINGS, faint dotted, every step of
//               ┆╲  └┄┄┄┄┄┘  ╱┆           (+ -label)        the distance unit out to DIST_EXTENT_M,
//                  ╲▁▁▁▁▁▁▁╱                                labelled along ONE bearing ("100 km")
//                                          level2-sweep-*    arm + comet tail, one revolution then a
//                                                            fade, only on a genuinely NEW frame
//
//   THE LABEL HANDLE (primary pane only, DOM, like the ruler's knob):
//
//                                                 450 km     the outermost label lifts clear of the handle
//              ╳ ─ ┆ 100 km ─ ┆ 200 km ─ … ───────(⟳)       (⟳) a 30 px round grip ON the OUTERMOST distance
//            site                                            ring, at the labels' bearing. Drag it round and
//                                                            every label swings with it, live, in every pane;
//                                                            let go and the bearing is posted to the host
//                                                            (rangeRingLabelBearing) to persist.
//
//   WHICH RINGS: Settings → Radar Range Ring (setRings; default outline + velocity). HOW THEY LOOK: the same
//   tab (setStyle — per-ring opacity/width/pattern, velocity/distance/label colours, label size/halo/bearing,
//   handle on/off). SIZE: the DISPLAYED frame's reach (radar.js syncScope → setReach), never the last frame to
//   decode. The distance rings do NOT follow the data — they run to DIST_EXTENT_M so they stay put when a
//   TDWR's upper tilt shrinks the other two to 89 km; only Auto spacing reads the reach.
//   ⚠️ The OUTLINE's colour is the --anvil-scope-ring CSS variable (map.js setScopeColor), shared with the
//   ruler; the other colours come in setStyle ('' = the theme's). The ruler ends at reach.refl even when the
//   outline is off.
//   ⚠️ NOTHING IS RE-ADDED TO CHANGE A LOOK. Ring layers are created once per pane and kept; a ring switched
//   off fades to opacity 0, and every colour/opacity/width/pattern change is a setPaintProperty that MapLibre
//   animates (FADE_MS). The first build re-added layers on every change, and a re-added GeoJSON layer draws
//   NOTHING until its tiles rebuild — so rings blinked out under an opacity slider and flashed on toggles.
//   ⚠️ Moving LABELS is the other trap (a symbol re-placed by setData fades in from 0) — see "Moving labels".
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
const DIST_LABEL_SRC = 'level2-range-dist-labels', DIST_LABEL_LAYER = 'level2-range-dist-label';
const FADE_MS = 220;          // every look change (colour, opacity, width, on/off) animates over this
const LIVE_SETTLE_MS = 400;   // labels keep "live" placement this long after they stop moving
const DIST_EXTENT_M = 460000; // distance rings run to a NEXRAD's full reach whatever is on screen
const DIST_MAX_RINGS = 40;    // a cap, not a design value: 25 nm to 460 km is 10 rings
const SWEEP_SRC = 'level2-sweep', SWEEP_FILL_LAYER = 'level2-sweep-fill', SWEEP_ARM_LAYER = 'level2-sweep-arm';
const SWEEP_MS = 1300;        // duration of one revolution
const SWEEP_FADE_MS = 400;    // brief fade-out of the trail once the revolution completes
const SWEEP_TRAIL_DEG = 75;   // angular length of the trailing afterglow behind the leading arm
const SWEEP_TRAIL_N = 64;     // wedge triangles across the trail — high so the taper reads smooth (no spokes)
const SWEEP_PEAK = 0.42;      // peak fill opacity right behind the arm (the wedge is a translucent glow)
const SWEEP_GAMMA = 1.6;      // trailing-fade shape (>1 = fades to nothing faster → a comet-tail falloff)

// Line patterns, in LINE WIDTHS (MapLibre's unit). ⚠️ The keys MIRROR Models/Radar/RingStyle.cs RingLines.
const DASHES = { solid: null, dashed: [4, 3], dotted: [1, 3], dashdot: [6, 3, 1, 3] };

// { forEachView(fn), viewCount(), primaryView(), beforeId(map), getSite() -> {lat,lon} } — from radar.js.
let host = null;
let reflMeters = 0, velMeters = 0;                          // the DISPLAYED frame's reach (setReach)
let rings = { refl: true, vel: true, dist: false, spacing: 0 }; // setRings; spacing 0 = Auto
let units = 'km';                                            // setUnits — the distance rings' unit
// setStyle. ⚠️ These defaults MIRROR RingStyle.*Default / RingLabelStyle.Default: they are what draws before
// the host's first push (and the host pushes at map-ready, so they rarely show).
let style = {
    refl: { op: 0.55, w: 1.3, line: 'solid' },
    vel: { color: '', op: 0.6, w: 1.2, line: 'dashed' },
    dist: { color: '', op: 0.45, w: 0.8, line: 'dotted' },
    label: { color: '', op: 1, size: 10, halo: 1.2 },
    bearing: 0,     // degrees clockwise from north — where the distance labels sit
    handle: true,   // show the label handle
};
let labelHandle = null, handleMap = null;                    // the DOM handle + the map it lives on
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
function stepMeters() { return autoStep() * (Geo.UNIT_METERS[units] || 1000); }
// The radii of the distance rings, innermost first — the last one is the OUTERMOST, where the handle sits.
function distRadii() {
    const step = stepMeters(), extent = Math.max(DIST_EXTENT_M, reflMeters), out = [];
    for (let r = step; r <= extent + 1 && out.length < DIST_MAX_RINGS; r += step) out.push(r);
    return out;
}
function distRingsData() {
    return { type: 'FeatureCollection', features: distRadii().map(ringFeature) };
}
// The labels are a SEPARATE source from the rings they name: moving them (the handle, the Position slider)
// then re-tiles only a few points, never the ring lines. The outermost carries `outer` so its text lifts
// clear of the handle sitting on that ring.
function distLabelsData() {
    const per = Geo.UNIT_METERS[units] || 1000, s = host.getSite(), az = style.bearing * Geo.D2R;
    const radii = distRadii();
    return {
        type: 'FeatureCollection',
        features: radii.map(function (r, i) {
            return {
                type: 'Feature', properties: { label: Math.round(r / per) + ' ' + units, outer: i === radii.length - 1 },
                geometry: { type: 'Point', coordinates: Geo.siteToLngLat(s.lat, s.lon, r, az) },
            };
        }),
    };
}

// ---- Looks ----
// A ring's colour: the user's hex, else the theme's variable for that ring (always with a fallback — an empty
// colour throws inside MapLibre's render).
function reflColor() { return Theme.color('--anvil-scope-ring', '#9fe0ff'); }
function velColor() { return style.vel.color || Theme.color('--anvil-scope-vel', '#ffa07a'); }
function distColor() { return style.dist.color || Theme.color('--anvil-scope-dist', '#c7cdd4'); }
function labelColor() { return style.label.color || distColor(); }
function casingColor() { return Theme.color('--anvil-ruler-casing', '#000000'); }

// Merge a pushed style over the current one, field by field, dropping anything malformed: every value lands
// in a MapLibre paint property, where NaN or a non-colour throws mid-render.
function num(v, lo, hi, dflt) { v = Number(v); return isFinite(v) ? Math.min(hi, Math.max(lo, v)) : dflt; }
function hex(v) { return /^#[0-9A-Fa-f]{6}$/.test(v || '') ? v : ''; }
function mergeRing(cur, o) {
    if (!o) return cur;
    return {
        color: hex(o.color), op: num(o.op, 0.05, 1, cur.op), w: num(o.w, 0.5, 6, cur.w),
        line: Object.prototype.hasOwnProperty.call(DASHES, o.line) ? o.line : cur.line,
    };
}

// What each ring layer should look like RIGHT NOW. A ring switched off is not removed — its opacity goes to
// 0, so on/off fades like every other change (see the note at the top). The velocity ring also waits for a
// velocity reach.
function ringTargets() {
    return [
        { id: RANGE_LAYER, s: style.refl, color: reflColor(), on: rings.refl },
        { id: VEL_LAYER, s: style.vel, color: velColor(), on: rings.vel && velMeters > 0 },
        { id: DIST_LAYER, s: style.dist, color: distColor(), on: rings.dist },
    ];
}
// Patch ONE map's ring + label paint to the targets. MapLibre animates each change over the layer's
// *-transition (FADE_MS). ⚠️ A solid line is `undefined` dasharray — measured on the vendored build: that
// resets it to solid, and a pattern → pattern change works in place too.
function applyPaint(map) {
    ringTargets().forEach(function (t) {
        if (!map.getLayer(t.id)) return;
        map.setPaintProperty(t.id, 'line-color', t.color);
        map.setPaintProperty(t.id, 'line-width', t.s.w);
        map.setPaintProperty(t.id, 'line-opacity', t.on ? t.s.op : 0);
        map.setPaintProperty(t.id, 'line-dasharray', DASHES[t.s.line] || undefined);
    });
    if (map.getLayer(DIST_LABEL_LAYER)) {
        map.setPaintProperty(DIST_LABEL_LAYER, 'text-color', labelColor());
        map.setPaintProperty(DIST_LABEL_LAYER, 'text-opacity', rings.dist ? style.label.op : 0);
        map.setPaintProperty(DIST_LABEL_LAYER, 'text-halo-color', casingColor());
        map.setPaintProperty(DIST_LABEL_LAYER, 'text-halo-width', style.label.halo);
        if (map.getLayoutProperty(DIST_LABEL_LAYER, 'text-size') !== style.label.size) {
            map.setLayoutProperty(DIST_LABEL_LAYER, 'text-size', style.label.size);
        }
    }
}
function applyPaintAll() { host.forEachView(function (v) { if (v.map) applyPaint(v.map); }); }

function setSource(map, id, data) {
    if (map.getSource(id)) map.getSource(id).setData(data);
    else map.addSource(id, { type: 'geojson', data: data });
}
function drop(map, layerIds, srcIds) {
    layerIds.forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
    srcIds.forEach(function (id) { if (map.getSource(id)) map.removeSource(id); });
}
// Stacking, bottom to top: distance rings (+ labels), velocity, outline — then everything the host puts above
// radar. A layer added later is slotted under the next one UP that exists, so the order holds.
function beforeFor(map, id) {
    const order = [DIST_LAYER, DIST_LABEL_LAYER, VEL_LAYER, RANGE_LAYER];
    for (let i = order.indexOf(id) + 1; i < order.length; i++) if (map.getLayer(order[i])) return order[i];
    return host.beforeId(map);
}
const FADE = { duration: FADE_MS, delay: 0 };
function addRingLayer(map, id, src) {
    map.addLayer({
        id: id, type: 'line', source: src,
        // Born invisible; applyPaint fades it to its target on the next frame.
        paint: {
            'line-opacity': 0, 'line-opacity-transition': FADE, 'line-color-transition': FADE,
            'line-width-transition': FADE, 'line-blur': id === RANGE_LAYER ? 0.3 : 0,
        },
    }, beforeFor(map, id));
}

// Make sure ONE pane has every ring layer with current geometry, then fade it to the current look. Layers
// are created once and kept (hidden = opacity 0), so a toggle or a style change never re-adds anything.
function drawRings(v) {
    const map = v && v.map;
    if (!map || !(reflMeters > 0)) return;

    setSource(map, DIST_SRC, distRingsData());
    setSource(map, DIST_LABEL_SRC, distLabelsData());
    setSource(map, VEL_SRC, velMeters > 0 ? ringFeature(velMeters) : { type: 'FeatureCollection', features: [] });
    setSource(map, RANGE_SRC, ringFeature(reflMeters));

    let added = false;
    if (!map.getLayer(DIST_LAYER)) { addRingLayer(map, DIST_LAYER, DIST_SRC); added = true; }
    if (!map.getLayer(DIST_LABEL_LAYER)) {
        map.addLayer({
            id: DIST_LABEL_LAYER, type: 'symbol', source: DIST_LABEL_SRC,
            layout: {
                // ⚠️ 'Noto Sans Medium' — the one stack the bundled glyph host serves (see radar-ruler.js).
                'text-field': ['get', 'label'], 'text-font': ['Noto Sans Medium'], 'text-size': style.label.size,
                // The outermost label lifts well clear of the handle that sits on its ring.
                'text-offset': ['case', ['boolean', ['get', 'outer'], false], ['literal', [0, -2.3]], ['literal', [0, -0.7]]],
                'text-allow-overlap': labelsLive, 'text-ignore-placement': labelsLive, 'text-padding': 4,
            },
            paint: {
                'text-opacity': 0, 'text-opacity-transition': FADE, 'text-color-transition': FADE,
                'text-halo-width-transition': FADE,
            },
        }, beforeFor(map, DIST_LABEL_LAYER));
        added = true;
    }
    if (!map.getLayer(VEL_LAYER)) { addRingLayer(map, VEL_LAYER, VEL_SRC); added = true; }
    if (!map.getLayer(RANGE_LAYER)) { addRingLayer(map, RANGE_LAYER, RANGE_SRC); added = true; }

    // A layer that was just added must see its 0 before the target, or MapLibre has nothing to fade FROM.
    if (added) requestAnimationFrame(function () { if (map.getLayer(RANGE_LAYER)) applyPaint(map); });
    else applyPaint(map);
    syncHandle();
}
function removeRings(v) {
    const map = v && v.map;
    if (!map) return;
    drop(map, [DIST_LABEL_LAYER, DIST_LAYER, VEL_LAYER, RANGE_LAYER], [DIST_LABEL_SRC, DIST_SRC, VEL_SRC, RANGE_SRC]);
}

// ---- Moving labels ----
// ⚠️ MEASURED, not assumed (vendored MapLibre, 2026-09-24): every setData on a symbol source re-places the
// labels as NEW symbols, and a new symbol fades in from 0 over the map's fadeDuration. Moving them every
// frame (a drag) therefore kept them at ~0 — they vanished for the whole drag. With text-allow-overlap +
// text-ignore-placement they skip collision placement and draw at once, frame after frame. Those two stay ON
// only while the labels are moving ("live"), then go back off after LIVE_SETTLE_MS of stillness so the labels
// thin themselves against each other again when you zoom out.
let labelsLive = false, settleTimer = 0;
function setLabelsLive(on) {
    if (labelsLive === on) return;
    labelsLive = on;
    host.forEachView(function (v) {
        const map = v.map;
        if (!map || !map.getLayer(DIST_LABEL_LAYER)) return;
        map.setLayoutProperty(DIST_LABEL_LAYER, 'text-allow-overlap', on);
        map.setLayoutProperty(DIST_LABEL_LAYER, 'text-ignore-placement', on);
    });
}
function settleLabelsSoon() {
    if (settleTimer) clearTimeout(settleTimer);
    settleTimer = setTimeout(function () { settleTimer = 0; if (host) setLabelsLive(false); }, LIVE_SETTLE_MS);
}
function moveLabels() {
    setLabelsLive(true);
    const data = distLabelsData();
    host.forEachView(function (v) {
        const src = v.map && v.map.getSource(DIST_LABEL_SRC);
        if (src) src.setData(data);
    });
}

// ---- The label handle (primary pane only) ----
// A DOM marker ON the outermost distance ring, at the labels' bearing. Dragging it takes only the BEARING
// (like the ruler's knob): the handle is snapped back onto the ring every move, and the labels in every pane
// follow live. Letting go posts the bearing to the host, which persists it and deliberately does NOT push it
// back (RangeRingsViewModel.OnLabelBearingDragged).
function handleWanted() { return !!(host && rings.dist && style.handle && reflMeters > 0); }
function handleLngLat() {
    const s = host.getSite(), radii = distRadii();
    return Geo.siteToLngLat(s.lat, s.lon, radii[radii.length - 1] || stepMeters(), style.bearing * Geo.D2R);
}
// A round grip with a circular double arrow ("swing me round"), in the labels' colour on a dark casing and a
// soft glow — it has to read over bright returns and the dark basemap alike.
function handleSvg() {
    const fill = labelColor(), casing = casingColor();
    return '<svg width="30" height="30" viewBox="0 0 30 30" aria-hidden="true">' +
        '<circle cx="15" cy="15" r="12" fill="' + fill + '" stroke="' + casing + '" stroke-width="2.5"/>' +
        '<path d="M9.2 13.2 A6 6 0 0 1 19.8 11.4" fill="none" stroke="' + casing + '" stroke-width="2" stroke-linecap="round"/>' +
        '<path d="M20.8 16.8 A6 6 0 0 1 10.2 18.6" fill="none" stroke="' + casing + '" stroke-width="2" stroke-linecap="round"/>' +
        '<path d="M20.9 8.4 L20.4 12.4 L16.6 11.3 Z" fill="' + casing + '"/>' +
        '<path d="M9.1 21.6 L9.6 17.6 L13.4 18.7 Z" fill="' + casing + '"/></svg>';
}
function ensureHandleStyle() {
    if (document.getElementById('radar-ring-handle-style')) return;
    const s = document.createElement('style');
    s.id = 'radar-ring-handle-style';
    s.textContent =
        '.radar-ring-handle{cursor:grab;transition:transform .15s ease;}' +
        '.radar-ring-handle:hover{transform:scale(1.15);}' +
        '.radar-ring-handle:active{cursor:grabbing;}' +
        '.radar-ring-handle svg{display:block;filter:drop-shadow(0 0 3px rgba(0,0,0,.9)) drop-shadow(0 1px 2px rgba(0,0,0,.7));}';
    document.head.appendChild(s);
}
// A dragged point → bearing from the site, whole degrees. In the site's own frame (geo.js metres per degree),
// with the lng unwrapped first — a drag near an Alaskan site can come back on the far side of ±180.
function bearingOf(ll) {
    const s = host.getSite(), k = Geo.metersPerDeg(s.lat);
    const lng = ll.lng - 360 * Math.round((ll.lng - s.lon) / 360);
    const deg = Math.round(Math.atan2((lng - s.lon) * k.mPerDegLon, (ll.lat - s.lat) * k.mPerDegLat) / Geo.D2R);
    return ((deg % 360) + 360) % 360;
}
function dropHandle() {
    if (labelHandle) labelHandle.remove();
    labelHandle = null;
    handleMap = null;
}
function syncHandle() {
    const v = host && host.primaryView ? host.primaryView() : null;
    if (!handleWanted() || !v || !v.map) { dropHandle(); return; }
    if (handleMap && handleMap !== v.map) dropHandle();
    if (labelHandle) { labelHandle.setLngLat(handleLngLat()); return; }

    ensureHandleStyle();
    const el = document.createElement('div');
    el.className = 'radar-ring-handle';
    el.title = 'Drag to move the distance labels';
    el.innerHTML = handleSvg();
    labelHandle = new maplibregl.Marker({ element: el, draggable: true }).setLngLat(handleLngLat()).addTo(v.map);
    handleMap = v.map;
    labelHandle.on('dragstart', function () { setLabelsLive(true); });
    labelHandle.on('drag', function () {
        const deg = bearingOf(labelHandle.getLngLat());
        if (deg !== style.bearing) { style.bearing = deg; moveLabels(); }
        labelHandle.setLngLat(handleLngLat()); // back onto the outer ring
    });
    labelHandle.on('dragend', function () {
        settleLabelsSoon();
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'rangeRingLabelBearing', deg: style.bearing }));
            }
        } catch (e) { /* host gone — the bearing still holds for this session */ }
    });
}

// Every pane has its ring layers — a pane added since the last frame fails this, which is what gets it rings
// without a new decode. (Layers exist whether or not a ring is shown; showing is opacity.)
function allUp() {
    let up = host.viewCount() > 0;
    host.forEachView(function (v) { if (!v.map.getLayer(RANGE_LAYER) || !v.map.getLayer(DIST_LABEL_LAYER)) up = false; });
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

// How the rings look (Settings → Radar Range Ring) — see `style` above for the shape. Anything missing or
// malformed keeps its current value. Patches paint in place (a smooth fade); only a moved bearing or a new
// label size touches the labels' geometry, and that goes through the live-label path.
export function setStyle(o) {
    if (!o) return;
    const prev = style;
    style = {
        refl: mergeRing(style.refl, o.refl),
        vel: mergeRing(style.vel, o.vel),
        dist: mergeRing(style.dist, o.dist),
        label: o.label ? {
            color: hex(o.label.color), op: num(o.label.op, 0.05, 1, style.label.op),
            size: num(o.label.size, 8, 20, style.label.size), halo: num(o.label.halo, 0, 4, style.label.halo),
        } : style.label,
        bearing: ((Math.round(num(o.bearing, -1e6, 1e6, style.bearing)) % 360) + 360) % 360,
        handle: o.handle === undefined ? style.handle : !!o.handle,
    };
    if (!host) return;
    const moved = style.bearing !== prev.bearing || style.label.size !== prev.label.size;
    if (moved) setLabelsLive(true);           // BEFORE the size/position change re-places them
    applyPaintAll();
    if (style.bearing !== prev.bearing) moveLabels();
    if (moved) settleLabelsSoon();
    syncHandle();
    if (labelHandle) labelHandle.getElement().innerHTML = handleSvg(); // its colours are baked in
}

// A pane is going away (radar.js detachView, before map.remove()). Only the handle is ours to clean up:
// the ring layers go with the map.
export function detachView(v) {
    if (v && handleMap && v.map === handleMap) dropHandle();
}

// Which rings to draw (Settings → Radar Range Ring). spacing = the distance rings' step in the unit, 0 = Auto.
// On/off is a fade; a new spacing rebuilds the distance geometry.
export function setRings(o) {
    const next = { refl: !!o.refl, vel: !!o.vel, dist: !!o.dist, spacing: Number(o.spacing) > 0 ? Number(o.spacing) : 0 };
    const respaced = next.spacing !== rings.spacing;
    rings = next;
    if (!host) return;
    if (respaced) host.forEachView(drawRings);
    else { applyPaintAll(); syncHandle(); }
}

// The distance rings' unit (Settings → Radar → Readouts). Only they (and their labels) use it.
export function setUnits(u) {
    units = Geo.UNIT_METERS[u] ? u : 'km';
    if (host) host.forEachView(drawRings);
}

// The reflectivity reach, in metres (0 = no frame yet). ⚠️ The ONE radius on the page: radar-ruler.js reads
// it through here rather than keeping its own, so the ruler's far end can never disagree with the outline it
// lands on — and it keeps ending there when the outline is switched off.
export function getRange() { return reflMeters; }

// Re-read the theme/override colours into every pane's live layers — after Settings changes the outline's
// (map.js setScopeColor writes --anvil-scope-ring inline on :root) or the theme switches. A paint property
// holds the colour it was given, so the CSS change alone would not reach a ring already on screen.
// The sweep is deliberately untouched: its warm afterglow is not the ring's colour (see theme.css).
export function refreshColors() {
    if (!host) return;
    applyPaintAll();
    if (labelHandle) labelHandle.getElement().innerHTML = handleSvg();
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
    dropHandle();
    stop();
    reflMeters = 0;
    velMeters = 0;
}
