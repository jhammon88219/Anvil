// The Inspector (RadarScope-style "read the value under the cursor").
//
//   ┌─────────────────┬─────────────────┐   The cursor is in ONE pane and shows the tooltip there;
//   │        ╎        │        ╎        │   every OTHER pane gets a mirrored CROSSHAIR at the same
//   │     ╌╌╌+╌╌╌     │     ╌╌╌+╌╌╌     │   lng/lat, so it is obvious all the readings are the
//   │        ╎        │        ╎ CC 0.94│   SAME gate — four moments of one storm feature at once.
//   ├─────────────────┼─────────────────┤
//   │        ╎        │        ╎        │   Each pane's value also ticks on its OWN chip ramp in the
//   │     ╌╌╌+╌╌╌ ┌────────────┐        │   bottom bar — that is the readout; the tooltip is just
//   │        ╎    │ 47.5 dBZ   │        │   what is under the pointer right now.
//   │             │ 38 km 214° │        │
//   └─────────────┴────────────┴────────┘
//
// The mode itself is armed from the Map Controls window, not the radar console: it is one cursor mode
// over a shared camera, so it belongs with the global map tools.
//
// In inspect mode a mousemove projects the cursor lng/lat back to the site's polar frame (the SAME
// equirectangular math buildGates uses, so the inspected gate is exactly the painted one), reads the
// value from the current frame's grid for the active product, shows a DOM tooltip next to the cursor,
// and pushes the value to the host (throttled) so the color-scale bar can mark it in real time. All
// lookups are pure main-thread array reads — no re-decode, no GL readback.
//
// ⚠️ CROSS-PANE: Inspect is ONE instrument reading N panes. The cursor sits in one pane, but the point
// it names is GEOGRAPHIC, and every pane shows the same ground at the same instant — so one hover
// yields one reading per pane, all of the same gate, each ticking on its own chip ramp. Four numbers
// for one storm feature, read in a glance at the chip cluster. The panes the cursor is NOT in get a
// mirrored crosshair at that lng/lat so it is obvious the readings are the same point.
//
// This module owns the inspect MODE flag and everything downstream of it. radar.js asks `isOn()` in
// the two places the loop's behaviour depends on it (whether a frame still needs a value-grid upgrade,
// and whether a decode should build grids at all), and drives the rest through bindView/unbindView/
// setEnabled. Per-PANE state still lives on the view object (crossEl, the three handler refs) because
// radar.js creates and destroys those views; this module only reads and writes those fields.
//
// geo.js is imported STATICALLY (as radar-decode.js does), so the projection is guaranteed present —
// that replaces the `!Geo` guard lookupValue used to carry.

import * as Geo from './geo.js';

const GRID_NODATA = -32768; // matches radar-decode.js buildGrid sentinel

// { forEachView(fn), getSite() -> {lat,lon}, getFrame() -> frames[currentFrame] | undefined, post(obj) }
let host = null;

let inspecting = false;
let inspectTip = null;          // the DOM tooltip element (lazily created; ONE, it follows the cursor)
let inspectLngLat = null;       // the geographic point under the cursor (null = not pointing at the map)
let hoveredView = null;         // the pane the cursor is actually in (it shows the tooltip, not a cross)
// Per-pane host-push throttle state: each pane reports its own value, so each needs its own
// has/has-not edge and timer (a shared one would swallow the other panes' transitions).
const lastInspectPush = {}, lastInspectHad = {};

export function init(h) { host = h; }

// Is Inspect armed? Read by radar.js's needsUpgrade (a frame wants its value grid) and decodeFrame
// (whether to build grids at all) — the only two places the loop cares about the mode.
export function isOn() { return inspecting; }

function ensureInspectTip() {
    if (inspectTip) return inspectTip;
    const el = document.createElement('div');
    el.id = 'radar-inspect-tip';
    el.style.cssText = 'position:absolute;z-index:20;pointer-events:none;display:none;' +
        'font:600 12px/1.3 "Segoe UI",sans-serif;color:var(--anvil-readout-text);' +
        'background:var(--anvil-readout-bg);' +
        'border:1px solid var(--anvil-readout-border);border-radius:4px;padding:3px 7px;white-space:nowrap;' +
        'box-shadow:0 1px 4px rgba(0,0,0,.55);';
    document.body.appendChild(el);
    inspectTip = el;
    return el;
}

// The mirrored crosshair drawn in every pane the cursor is NOT in. Built from two child bars rather
// than a stylesheet rule, so the whole thing is inline and this module still injects no CSS (the one
// place that does — radar-sites.js — is a template-literal minefield we don't need to join).
function ensureCrossEl(v) {
    if (v.crossEl && v.crossEl.isConnected) return v.crossEl;
    const el = document.createElement('div');
    el.className = 'radar-inspect-cross';
    el.style.cssText = 'position:absolute;z-index:19;pointer-events:none;display:none;' +
        'width:17px;height:17px;margin-left:-8.5px;margin-top:-8.5px;';
    const bar = 'position:absolute;background:var(--anvil-readout-crosshair);box-shadow:0 0 2px rgba(0,0,0,.9);';
    const h = document.createElement('div');
    h.style.cssText = bar + 'left:0;top:8px;width:17px;height:1.5px;';
    const w = document.createElement('div');
    w.style.cssText = bar + 'left:8px;top:0;width:1.5px;height:17px;';
    el.appendChild(h); el.appendChild(w);
    v.map.getContainer().appendChild(el);
    v.crossEl = el;
    return el;
}
function hideCross(v) { if (v.crossEl) v.crossEl.style.display = 'none'; }
// Place (or hide) one pane's crosshair for the current inspect point. The hovered pane never gets one
// — the real cursor is already there, and it is already a crosshair.
function positionCross(v) {
    if (!inspecting || !inspectLngLat || v === hoveredView) { hideCross(v); return; }
    let pt;
    try { pt = v.map.project(inspectLngLat); } catch (e) { hideCross(v); return; }
    const el = ensureCrossEl(v);
    el.style.left = pt.x + 'px';
    el.style.top = pt.y + 'px';
    el.style.display = 'block';
}

// The value grid for the current frame + THIS PANE's product, or null if not available.
function inspectGrid(v) {
    const f = host.getFrame();
    return (f && f.grids && f.grids[v.product]) || null;
}

// Reads the moment value at a geographic point from a polar value grid, or null (no data /
// out of range). Mirrors buildGates' projection: x∝sin(az), y∝cos(az), az from north clockwise.
function lookupValue(grid, lat, lng) {
    if (!grid || !grid.values) return null; // values null = grid was built metadata-only (Inspect was off)
    const s = host.getSite();
    const polar = Geo.lngLatToPolar(s.lat, s.lon, lng, lat);
    const rangeKm = polar.rangeMeters / 1000, azDeg = polar.azDeg;
    const j = Math.floor((rangeKm - grid.firstGate) / grid.gateSize);
    if (j < 0 || j >= grid.nGates) return null;
    // Nearest radial by azimuth (unsorted, ~720 entries — trivial per move). Reject if the
    // closest beam is too far (a gap or beyond the sweep), so we don't report a bogus value.
    let best = -1, bestD = 999;
    for (let i = 0; i < grid.az.length; i++) {
        const a = grid.az[i]; if (isNaN(a)) continue;
        let dd = Math.abs(a - azDeg); if (dd > 180) dd = 360 - dd;
        if (dd < bestD) { bestD = dd; best = i; }
    }
    if (best < 0 || bestD > 2) return null;
    const q = grid.values[best * grid.nGates + j];
    if (q === GRID_NODATA) return null;
    return { value: q / grid.scale, unit: grid.unit, digits: grid.digits };
}

// Push the inspected value to the host for the color-scale marker. Throttled (~14/s) and edge-
// triggered on the has/has-not transition so leaving data hides the marker promptly.
function pushInspect(pane, has, value) {
    const now = Date.now();
    if (has === lastInspectHad[pane] && now - (lastInspectPush[pane] || 0) < 70) return;
    lastInspectPush[pane] = now; lastInspectHad[pane] = has;
    host.post({ type: 'radarInspect', pane: pane, has: has, value: has ? value : 0 });
}

// Bound per pane (see setEnabled). ONE hover, N readings: every pane reads its own product's grid at
// the SAME geographic point, so all the chips tick together. The pane under the cursor also gets the
// tooltip; the rest get the mirrored crosshair.
// Cost is one lookupValue per pane per mouse move — a nearest-azimuth scan of ~720 radials plus an
// array read, so four panes is still nothing.
function onInspectMove(v, e) {
    if (!inspecting) return;
    hoveredView = v;
    inspectLngLat = e.lngLat;
    let r = null;
    host.forEachView(function (o) {
        const hit = lookupValue(inspectGrid(o), e.lngLat.lat, e.lngLat.lng);
        if (o === v) r = hit;                       // reuse the hovered pane's own read for the tooltip
        pushInspect(o.index, !!hit, hit ? hit.value : 0);
        positionCross(o);                           // no-op for the hovered pane (hides its cross)
    });
    const el = ensureInspectTip();
    if (r) {
        // Speed products (velocity / SRV / spectrum width — unit "m/s") read as the native m/s value
        // PLUS mph, e.g. "12.3 m/s (28 mph)"; other products keep their native unit (dBZ / unitless CC).
        const main = r.unit === 'm/s'
            ? formatSpeed(r.value)
            : r.value.toFixed(r.digits) + (r.unit ? ' ' + r.unit : '');
        // On Velocity, show the SAME gate's dealiasing breakdown so the unfold can be checked
        // without re-hovering: the displayed value is the dealiased speed; the raw (folded)
        // value is what the radar measured (within ±Nyquist), recovered by removing the whole
        // 2×Nyquist folds the dealiaser added. Lets the user confirm high velocities at a glance.
        const vel = velocityFold(r.value, v.product);
        if (vel) {
            el.innerHTML = '<div>' + main + '</div>' +
                '<div style="font-size:10px;opacity:.75;font-weight:400">raw ' + vel.raw.toFixed(0) +
                ' · Nyq ' + vel.nyq.toFixed(0) + ' · ' + vel.foldLabel + '</div>';
        } else {
            el.textContent = main;
        }
        // e.point is relative to THIS pane's canvas; the tooltip is a document-level element, so
        // offset it by where that pane sits in the page.
        const box = v.map.getContainer().getBoundingClientRect();
        el.style.left = (box.left + e.point.x + 14) + 'px';
        el.style.top = (box.top + e.point.y + 14) + 'px';
        el.style.display = 'block';
    } else {
        el.style.display = 'none';
    }
    // NB: no pushInspect here — the loop above already pushed EVERY pane's value, this one included.
}

// A speed in m/s → "12.3 m/s (28 mph)" (sign preserved: inbound negative, outbound positive).
function formatSpeed(ms) {
    return ms.toFixed(1) + ' m/s (' + (ms * 2.23694).toFixed(1) + ' mph)';
}

// For a dealiased velocity value, recover the raw (folded) measurement + the fold count from the
// current frame's Nyquist. Returns null when not on Velocity / no Nyquist (so other products show
// just their value). Dealiasing only ever adds whole multiples of 2×Nyquist, so this is exact.
function velocityFold(dealiased, product) {
    if (product !== 'velocity') return null;
    const f = host.getFrame();
    const nyq = f && f.velNyq;
    if (!(nyq > 0)) return null;
    const folds = Math.round(dealiased / (2 * nyq));
    const raw = dealiased - folds * 2 * nyq;
    const foldLabel = folds === 0 ? 'no fold'
        : (folds > 0 ? '+' : '') + folds + ' fold' + (Math.abs(folds) === 1 ? '' : 's');
    return { raw: raw, nyq: nyq, folds: folds, foldLabel: foldLabel };
}
function onInspectOut(v) {
    // ⚠️ Moving from one pane to another fires this pane's mouseout AND the next pane's mousemove, and
    // the order is not guaranteed. Only clear if the cursor genuinely left — i.e. we are still the
    // pane it was last in — or crossing a groove would blank the readings that just arrived.
    if (hoveredView && hoveredView !== v) return;
    if (inspectTip) inspectTip.style.display = 'none';
    inspectLngLat = null;
    hoveredView = null;
    host.forEachView(function (o) { hideCross(o); pushInspect(o.index, false, 0); });
}

// Attach / detach the inspect handlers for ONE pane. Called per view when the mode toggles and when
// a pane is created while Inspect is already on.
export function bindView(v) {
    if (v.inspectMove) return;
    v.inspectMove = function (e) { onInspectMove(v, e); };
    v.inspectOut = function () { onInspectOut(v); };
    // The crosshair marks a GEOGRAPHIC point, so it has to be re-projected whenever the camera moves.
    // A drag already fires mousemove, but a wheel zoom with a stationary pointer does not — without
    // this the mirrored crosses would drift off the gate they are marking.
    v.inspectCamera = function () { if (inspecting && inspectLngLat) positionCross(v); };
    v.map.on('mousemove', v.inspectMove);
    v.map.on('mouseout', v.inspectOut);
    v.map.on('move', v.inspectCamera);
    const canvas = v.map.getCanvas && v.map.getCanvas();
    if (canvas) canvas.style.cursor = 'crosshair';
}
export function unbindView(v) {
    if (v.inspectMove) { v.map.off('mousemove', v.inspectMove); v.inspectMove = null; }
    if (v.inspectOut) { v.map.off('mouseout', v.inspectOut); v.inspectOut = null; }
    if (v.inspectCamera) { v.map.off('move', v.inspectCamera); v.inspectCamera = null; }
    hideCross(v);
    const canvas = v.map.getCanvas && v.map.getCanvas();
    if (canvas) canvas.style.cursor = '';
    pushInspect(v.index, false, 0);
}

// Arm / disarm the mode across every pane. Inspect is GLOBAL (one armed cursor mode over the map), so
// it binds in EVERY pane: point at one, read all N.
export function setEnabled(on) {
    inspecting = !!on;
    if (inspecting) {
        host.forEachView(bindView);
    } else {
        host.forEachView(unbindView);
        if (inspectTip) inspectTip.style.display = 'none';
        inspectLngLat = null;
        hoveredView = null;
    }
}
