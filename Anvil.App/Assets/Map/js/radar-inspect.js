// The Inspector (RadarScope-style "read the value under the cursor").
//
//   ┌──────────────────┬──────────────────┐   The cursor is in ONE pane; every OTHER pane gets a
//   │        +         │        +         │   mirrored CROSSHAIR at the same lng/lat. EVERY pane gets
//   │         ┌──────┐ │         ┌──────┐ │   its OWN readout box beside its point (the hovered one
//   │         │ 47.5 │ │         │ 0.52 │ │   beside the real cursor) — four moments of one gate, read
//   │         │ dBZ  │ │         │      │ │   at once. A pane with no data there shows the cross only.
//   ├──────────────────┼──────────────────┤
//   │        +         │        +         │   A box sits down-right of its point and FLIPS left / up
//   │         ┌──────┐ │  ┌──────┐        │   when it would cross its pane's edge (each box lives IN
//   │         │ 31.2 │ │  │ -28.4│        │   its pane's container, so it clips there). Each value also
//   │         │ m/s  │ │  │ m/s  │        │   ticks on the pane's notch ramp.
//   └──────────────────┴──────────────────┘
//
// The mode itself is armed from the Map Controls window, not the radar console: it is one cursor mode
// over a shared camera, so it belongs with the global map tools.
//
// In inspect mode a mousemove projects the cursor lng/lat back to the site's polar frame (the SAME
// equirectangular math buildGates uses, so the inspected gate is exactly the painted one), reads the
// value from each pane's grid for that pane's product, shows a DOM readout beside the point in each pane,
// and pushes the value to the host (throttled) so the color-scale bar can mark it in real time. All
// lookups are pure main-thread array reads — no re-decode, no GL readback.
//
// ⚠️ CROSS-PANE: Inspect is ONE instrument reading N panes. The cursor sits in one pane, but the point
// it names is GEOGRAPHIC, and every pane shows the same ground at the same instant — so one hover
// yields one reading per pane, all of the same gate, each in its own readout box. The panes the cursor
// is NOT in get a mirrored crosshair at that lng/lat so it is obvious the readings are the same point.
// (Until 2026-10-08 only the hovered pane had a box; the others showed just a tick on their ramp.)
//
// This module owns the inspect MODE flag and everything downstream of it. radar.js asks `isOn()` in
// the two places the loop's behaviour depends on it (whether a frame still needs a value-grid upgrade,
// and whether a decode should build grids at all), and drives the rest through bindView/unbindView/
// setEnabled. Per-PANE state still lives on the view object (crossEl, tipEl, the three handler refs) because
// radar.js creates and destroys those views; this module only reads and writes those fields.
//
// geo.js is imported STATICALLY (as radar-decode.js does), so the projection is guaranteed present —
// that replaces the `!Geo` guard lookupValue used to carry.

import * as Geo from './geo.js';

const GRID_NODATA = -32768; // matches radar-decode.js buildGrid sentinel

// { forEachView(fn), getSite() -> {lat,lon}, getFrame() -> frames[currentFrame] | undefined, post(obj) }
let host = null;

let inspecting = false;
let inspectLngLat = null;       // the geographic point under the cursor (null = not pointing at the map)
let hoveredView = null;         // the pane the cursor is actually in (the real cursor, not a cross)
// Per-pane host-push throttle state: each pane reports its own value, so each needs its own
// has/has-not edge and timer (a shared one would swallow the other panes' transitions).
const lastInspectPush = {}, lastInspectHad = {};

export function init(h) { host = h; }

// Is Inspect armed? Read by radar.js's needsUpgrade (a frame wants its value grid) and decodeFrame
// (whether to build grids at all) — the only two places the loop cares about the mode.
export function isOn() { return inspecting; }

// One pane's readout box, inside that pane's map container (so its coordinates are the pane's canvas
// coordinates, the same space as map.project, and it clips at the pane's edge like the cross does).
function ensureTipEl(v) {
    if (v.tipEl && v.tipEl.isConnected) return v.tipEl;
    const el = document.createElement('div');
    el.className = 'radar-inspect-tip';
    el.style.cssText = 'position:absolute;z-index:20;pointer-events:none;display:none;' +
        'font:600 12px/1.3 "Segoe UI",sans-serif;color:var(--anvil-readout-text);' +
        'background:var(--anvil-readout-bg);' +
        'border:1px solid var(--anvil-readout-border);border-radius:4px;padding:3px 7px;white-space:nowrap;' +
        'box-shadow:0 1px 4px rgba(0,0,0,.55);';
    v.map.getContainer().appendChild(el);
    v.tipEl = el;
    return el;
}
function hideTip(v) { if (v.tipEl) v.tipEl.style.display = 'none'; }
function tipShown(v) { return !!(v.tipEl && v.tipEl.style.display !== 'none'); }

const TIP_OFFSET = 14;          // px from the point to the box's near corner (the old tooltip's offset)

// Put a SHOWN box beside canvas point pt: down-right, flipped left / up where it would cross the pane's
// right / bottom edge. Measures the box, so it must already be display:block with its text set.
function placeTip(v, pt) {
    const el = v.tipEl, box = v.map.getContainer();
    const w = el.offsetWidth, h = el.offsetHeight;
    let x = pt.x + TIP_OFFSET, y = pt.y + TIP_OFFSET;
    if (x + w > box.clientWidth) x = pt.x - TIP_OFFSET - w;
    if (y + h > box.clientHeight) y = pt.y - TIP_OFFSET - h;
    el.style.left = x + 'px';
    el.style.top = y + 'px';
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
function hideMarks(v) { hideCross(v); hideTip(v); }

// The current inspect point in pane v's canvas coordinates, or null (not inspecting / projection threw).
function projectPoint(v) {
    if (!inspecting || !inspectLngLat) return null;
    try { return v.map.project(inspectLngLat); } catch (e) { return null; }
}

// Re-place one pane's cross + box at point pt (null hides both). The hovered pane never gets a cross —
// the real cursor is already there, and it is already a crosshair. The box is placed only if a read
// left it shown (readAt decides WHETHER it shows; this decides WHERE).
function placeMarks(v, pt) {
    if (!pt) { hideMarks(v); return; }
    if (v === hoveredView) hideCross(v);
    else {
        const el = ensureCrossEl(v);
        el.style.left = pt.x + 'px';
        el.style.top = pt.y + 'px';
        el.style.display = 'block';
    }
    if (tipShown(v)) placeTip(v, pt);
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
// the SAME geographic point and shows it in its own box. The rest get the mirrored crosshair too.
// Cost is one lookupValue per pane per mouse move — a nearest-azimuth scan of ~720 radials plus an
// array read, so four panes is still nothing.
function onInspectMove(v, e) {
    if (!inspecting) return;
    hoveredView = v;
    inspectLngLat = e.lngLat;
    readAll();
}

// Re-read the point the cursor is RESTING on. Reads otherwise only happen on mousemove, so a cursor held still
// while the value grids were still building (arming Inspect queues them) never showed a value until it was
// nudged (2026-10-04) — and a playing loop's tooltip kept the frame it was read on. radar.js calls this when the
// frame on screen changes or the shown frame's grids land.
export function refresh() {
    if (!inspecting || !inspectLngLat || !hoveredView) return;
    readAll();
}

// One read at the inspect point, in EVERY pane: its ramp tick, its box's text + spot, and its cross.
function readAll() {
    const lngLat = inspectLngLat;
    host.forEachView(function (o) {
        const hit = lookupValue(inspectGrid(o), lngLat.lat, lngLat.lng);
        pushInspect(o.index, !!hit, hit ? hit.value : 0);
        if (hit) fillTip(o, hit);
        else hideTip(o);                            // no data here in THIS pane's product: cross only
        placeMarks(o, projectPoint(o));
    });
}

// Write one pane's reading into its box and show it (placeMarks then positions it).
function fillTip(v, r) {
    const el = ensureTipEl(v);
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
    el.style.display = 'block';
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
    inspectLngLat = null;
    hoveredView = null;
    host.forEachView(function (o) { hideMarks(o); pushInspect(o.index, false, 0); });
}

// Attach / detach the inspect handlers for ONE pane. Called per view when the mode toggles and when
// a pane is created while Inspect is already on.
export function bindView(v) {
    if (v.inspectMove) return;
    v.inspectMove = function (e) { onInspectMove(v, e); };
    v.inspectOut = function () { onInspectOut(v); };
    // The cross + box mark a GEOGRAPHIC point, so they have to be re-projected whenever the camera moves.
    // A drag already fires mousemove, but a wheel zoom with a stationary pointer does not — without
    // this the mirrored crosses and the boxes would drift off the gate they are marking.
    v.inspectCamera = function () { if (inspecting && inspectLngLat) placeMarks(v, projectPoint(v)); };
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
    hideMarks(v);
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
        inspectLngLat = null;
        hoveredView = null;
    }
}
