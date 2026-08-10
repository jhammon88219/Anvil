// states.js — map masking: a BASE EXTENT (CONUS or full map) plus optional single-STATE isolation, all
// drawn as one inverted "mask" over the same bundled US-state polygons.
//
//   BASE EXTENT: either the full map (no mask) or CONUS — everything outside the contiguous 48 (+ DC) is
//           covered by one opaque fill the color of the map's water, leaving the lower-48 showing the real
//           basemap through the hole. CONUS is the launch default (this app is CONUS-only for now; AK/HI
//           come later). Toggled full↔CONUS by the host.
//   ARMED  (state hover mode): hovering a state paints a faint fill + a bold outline on THAT state only and
//           the cursor becomes a pointer — the affordance it's clickable. Clicking isolates it.
//   ISOLATED (single state): the mask tightens to one state (a clean thin outline traces its border). This
//           OVERRIDES the base extent; clearing the state falls back to the base (so clearing while CONUS
//           is on returns to CONUS, not the full map).
//
// WHY a bundled polygon: the Protomaps basemap ships state BORDER LINES only (the `boundaries` source-
// layer) — no fillable state polygon in the tiles, and tile line geometry can't be read back reliably. So
// masking needs real polygon geometry; we bundle a US-states GeoJSON (served same-origin from `mapassets`).
//
// WHY the "inverted mask": MapLibre can't clip the map to a polygon. The trick is one fill whose OUTER ring
// is the whole world and whose HOLES are the region's ring(s) — fill it water-color and it covers all but
// the region. No turf / boolean ops here: earcut treats the first ring as outer and the rest as holes
// regardless of winding. ⚠️ CONUS uses a PRE-DISSOLVED boundary (`conus-boundary.geojson` — the contiguous
// states UNIONED offline into disjoint exterior rings: one mainland + coastal islands, WITH the Great Lakes
// water filled in so radar returns and the lakes' labels show over them, not covered by the mask). Tiling
// the raw 49 state rings as holes was tried and FAILED — earcut bridges between the many TOUCHING holes gored
// the interior with triangles. Disjoint rings have no shared edges, so they cut clean. Regenerate the file
// with tools/make_conus_boundary.py (shapely union of the states + tools/great-lakes.geojson).
//
// Host seam: map.js exposes window.stateIso{Arm,Disarm,Select,Clear} + window.setConusIsolation, driven by
// StateIsolationViewModel through IMapService. This module posts {type:"stateIsolated", name} back so the
// VM tracks the isolated state, and calls setOnIsolationChange (wired to the radar-site coverage filter)
// with the effective rings whenever the mask changes. window.__isoTest(name) is a dev console helper.
// applyStyle calls reAdd(map) after a basemap switch (setStyle drops our layers; polygons stay in memory).

const STATES_URL = 'https://mapassets/state-boundaries.geojson';
const CONUS_URL = 'https://mapassets/conus-boundary.geojson'; // pre-dissolved lower-48 boundary (see header)

// The world rectangle used as the mask's outer ring. Y capped at MapLibre's Web-Mercator limit (~85.05°);
// X full span (renderWorldCopies is off, so one world is all there is).
const WORLD_RING = [[-180, -85.05], [180, -85.05], [180, 85.05], [-180, 85.05], [-180, -85.05]];

const SRC = 'state-iso';            // the states FeatureCollection (hover source)
const FILL = 'state-iso-fill';      // faint hover highlight fill
const LINE = 'state-iso-line';      // bold hover outline (only the hovered state draws)
const MASK_SRC = 'state-iso-mask';  // the inverted world-minus-region polygon
const MASK_FILL = 'state-iso-mask-fill';
const OUTLINE_SRC = 'state-iso-outline'; // the isolated state's geometry (for the clean border line)
const MASK_LINE = 'state-iso-mask-line';

let statesData = null;    // the fetched states FeatureCollection (for hover + single-state isolation)
let statesPromise = null;
let conusData = null;     // the fetched pre-dissolved CONUS boundary FeatureCollection (one MultiPolygon)
let conusPromise = null;
let conusRingsCache = null; // its exterior rings, extracted once (the CONUS mask holes)
let baseConus = false;    // base extent: true = mask to CONUS, false = full map. Host sets it (default on).
let armed = false;        // state hover mode on
let isolatedName = null;  // name of the isolated state, or null
let isolatedRings = null; // the isolated state's outer ring(s), or null
let hoverName = null;     // name of the state currently under the cursor, or null
let handlersBound = false;
// Notified with the EFFECTIVE mask rings (state rings, CONUS rings, or null) whenever the mask changes —
// map.js wires this to the radar-site coverage filter. A settable callback so states.js stays decoupled.
let onIsolationChange = null;
export function setOnIsolationChange(fn) { onIsolationChange = fn; }
function notifyIsolation(rings) { if (onIsolationChange) onIsolationChange(rings); }

// The style's water color, read live so the mask matches whatever basemap is loaded AND tracks a switch
// (dataVizBlack water is #1c1c1c, but each style differs). Falls back if the water layer is atypical.
function waterColor(map) {
    try {
        if (map.getLayer('water')) {
            const c = map.getPaintProperty('water', 'fill-color');
            if (typeof c === 'string') return c;
        }
    } catch (e) { /* fall through */ }
    return '#1c1c1c';
}

function ensureData() {
    if (statesData) return Promise.resolve(statesData);
    if (statesPromise) return statesPromise;
    // PROTOTYPE: 'reload' so a redeployed (higher-detail) polygon file always takes effect instead of a
    // stale cached copy. Switch to 'force-cache' when this productionizes (the bundled file is stable then).
    statesPromise = fetch(STATES_URL, { cache: 'reload' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (gj) { statesData = gj; return gj; })
        .catch(function (e) { console.error('states.js load failed: ' + e); return null; });
    return statesPromise;
}

// The CONUS boundary is a separate (pre-dissolved) file, fetched lazily the first time CONUS is masked.
function ensureConus() {
    if (conusData) return Promise.resolve(conusData);
    if (conusPromise) return conusPromise;
    conusPromise = fetch(CONUS_URL, { cache: 'reload' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (gj) { conusData = gj; return gj; })
        .catch(function (e) { console.error('conus-boundary load failed: ' + e); return null; });
    return conusPromise;
}

// The state's outer ring(s) — a MultiPolygon's parts each contribute one; a state's own interior holes
// (lakes) are ignored. Reused as BOTH the mask's holes and the coverage-test rings for the site filter.
function outerRings(feature) {
    const rings = [];
    const g = feature.geometry;
    if (g.type === 'Polygon') rings.push(g.coordinates[0]);
    else if (g.type === 'MultiPolygon') g.coordinates.forEach(function (poly) { rings.push(poly[0]); });
    return rings;
}

// The CONUS mask holes: the pre-dissolved boundary's exterior rings (one mainland + coastal islands),
// extracted once. Disjoint (no shared edges), so they cut clean — unlike tiling the raw state rings.
function conusRings() {
    if (conusRingsCache) return conusRingsCache;
    if (!conusData || !conusData.features || !conusData.features.length) return null;
    conusRingsCache = outerRings(conusData.features[0]); // the single CONUS MultiPolygon
    return conusRingsCache;
}

// The rings the mask should currently cut out: a single isolated state wins; else CONUS if the base extent
// is on; else null (full map, no mask).
function effectiveRings() {
    if (isolatedRings) return isolatedRings;
    if (baseConus) return conusRings();
    return null;
}

// Build the inverted mask: one Polygon = world outer ring + the region's ring(s) as holes. So a lake inside
// the region still shows the basemap, and everything outside the rings is covered.
function buildMask(rings) {
    return { type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [WORLD_RING].concat(rings) } };
}

function findState(name) {
    return statesData && statesData.features.find(function (f) { return f.properties && f.properties.name === name; });
}

// Add the two hover layers (idempotent). promoteId:'name' makes each feature's id its state name — a
// clean unique string, avoiding MapLibre coercing the FIPS top-level id ("01") to a number. Both layers
// draw NOTHING until a feature gets feature-state {hover:true}, so armed-but-not-hovering is invisible.
function addHoverLayers(map) {
    if (!statesData) return;
    if (!map.getSource(SRC)) map.addSource(SRC, { type: 'geojson', data: statesData, promoteId: 'name' });
    const hovered = ['boolean', ['feature-state', 'hover'], false];
    if (!map.getLayer(FILL)) {
        map.addLayer({
            id: FILL, type: 'fill', source: SRC,
            // fill-opacity 0 when not hovered still hit-tests for mousemove/click (opacity doesn't remove
            // a feature from queryRenderedFeatures) — so the whole state stays clickable while invisible.
            paint: { 'fill-color': '#4aa3ff', 'fill-opacity': ['case', hovered, 0.12, 0.0] }
        });
    }
    if (!map.getLayer(LINE)) {
        map.addLayer({
            id: LINE, type: 'line', source: SRC,
            paint: {
                'line-color': '#4aa3ff',
                'line-width': ['case', hovered, 3.0, 0.0],
                'line-opacity': ['case', hovered, 0.9, 0.0]
            }
        });
    }
}

function removeHoverLayers(map) {
    [LINE, FILL].forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
    if (map.getSource(SRC)) map.removeSource(SRC);
}

function clearHoverState(map) {
    if (hoverName !== null) { try { map.setFeatureState({ source: SRC, id: hoverName }, { hover: false }); } catch (e) {} }
    hoverName = null;
    map.getCanvas().style.cursor = '';
}

// Layer-scoped handlers survive layer remove/re-add (they dispatch by layer id at event time), so bind
// once. Each guards on armed && !isolatedName — the same layers exist while isolated, but the mask covers
// them and hover must stay inert (a state is then re-picked via the combo, not a click).
function bindHandlers(map) {
    if (handlersBound) return;
    handlersBound = true;

    map.on('mousemove', FILL, function (e) {
        if (!armed || isolatedName) return;
        const f = e.features && e.features[0];
        if (!f) return;
        map.getCanvas().style.cursor = 'pointer';
        if (hoverName !== null && hoverName !== f.id) map.setFeatureState({ source: SRC, id: hoverName }, { hover: false });
        hoverName = f.id;
        map.setFeatureState({ source: SRC, id: hoverName }, { hover: true });
    });

    map.on('mouseleave', FILL, function () {
        if (isolatedName) return;
        clearHoverState(map);
    });

    map.on('click', FILL, function (e) {
        if (!armed || isolatedName) return;
        const f = e.features && e.features[0];
        if (f) isolate(map, f.id, false); // already over the clicked state — keep the current zoom
    });
}

// Tell the host which state is isolated (name) or that isolation cleared (null), so the VM can drive the
// combo / a readout. Best-effort — no-op outside the WebView. Note: CONUS is NOT a state, so the base-
// extent toggle never posts here (the VM's SelectedState stays null when only CONUS is on).
function postIsolated(name) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'stateIsolated', name: name || null }));
    }
}

// Render (or remove) the mask FILL for a set of rings. Added with NO beforeId so it sits on top of the
// ENTIRE stack (basemap, radar, overlays, labels) — only the region's hole shows what's beneath.
function renderMaskFill(map, rings) {
    if (!rings || !rings.length) {
        if (map.getLayer(MASK_FILL)) map.removeLayer(MASK_FILL);
        if (map.getSource(MASK_SRC)) map.removeSource(MASK_SRC);
        return;
    }
    const mask = buildMask(rings);
    if (map.getSource(MASK_SRC)) map.getSource(MASK_SRC).setData(mask);
    else map.addSource(MASK_SRC, { type: 'geojson', data: mask });
    if (!map.getLayer(MASK_FILL)) {
        map.addLayer({
            id: MASK_FILL, type: 'fill', source: MASK_SRC,
            // fill-antialias off: for a single state we draw our own crisp border, and for CONUS AA on the
            // tiled hole edges would seam every shared state border. The mask edge itself reads the boundary.
            paint: { 'fill-color': waterColor(map), 'fill-antialias': false }
        });
    } else {
        map.setPaintProperty(MASK_FILL, 'fill-color', waterColor(map));
    }
}

// Render (or remove) the crisp border LINE for a single isolated state. CONUS gets no outline (the tiled
// holes would draw every internal state border); its mask edge alone traces the coastline/border.
function renderOutline(map, geometry) {
    if (!geometry) {
        if (map.getLayer(MASK_LINE)) map.removeLayer(MASK_LINE);
        if (map.getSource(OUTLINE_SRC)) map.removeSource(OUTLINE_SRC);
        return;
    }
    const outline = { type: 'Feature', properties: {}, geometry: geometry };
    if (map.getSource(OUTLINE_SRC)) map.getSource(OUTLINE_SRC).setData(outline);
    else map.addSource(OUTLINE_SRC, { type: 'geojson', data: outline });
    if (!map.getLayer(MASK_LINE)) {
        map.addLayer({
            id: MASK_LINE, type: 'line', source: OUTLINE_SRC,
            paint: { 'line-color': '#8a8a8a', 'line-width': 1.2 }
        });
    }
}

// Framing margins as a FRACTION of the region bbox. FIT = the breathing room left around the shape when you
// Fit-to-view / isolate (a state gets a wide margin so its straight borders don't butt the straight bars;
// CONUS stays tight — it's meant to fill the view). LOCK = how far setMaxBounds lets you pan/zoom out.
// ⚠️ LOCK MUST be comfortably LOOSER than FIT *plus the fit's pixel padding* — otherwise setMaxBounds CLAMPS
// the fit and zooms small states IN (the fixed-pixel padding is a big fraction of a small state's bbox, so
// it overflows a tight lock). That clamp was why NJ "loaded zoomed in". Proportional, so it scales by size.
const CONUS_FIT_MARGIN = 0.04;
const STATE_FIT_MARGIN = 0.45;
const CONUS_LOCK_MARGIN = 0.25;
const STATE_LOCK_MARGIN = 1.00;
function fitMarginFrac() { return isolatedName ? STATE_FIT_MARGIN : CONUS_FIT_MARGIN; }
function lockMarginFrac() { return isolatedName ? STATE_LOCK_MARGIN : CONUS_LOCK_MARGIN; }

// The region's bbox expanded by a margin fraction, clamped to the world.
function framedBbox(rings, frac) {
    const b = bboxOfRings(rings);
    const dx = (b[1][0] - b[0][0]) * frac, dy = (b[1][1] - b[0][1]) * frac;
    return [
        [Math.max(-180, b[0][0] - dx), Math.max(-85, b[0][1] - dy)],
        [Math.min(180, b[1][0] + dx), Math.min(85, b[1][1] + dy)]
    ];
}

// Lock pan/zoom to the effective region: setMaxBounds constrains panning AND caps zoom-out (MapLibre won't
// zoom out past where the bounds fill the viewport), so the masked void can't be panned/zoomed into. Null
// rings (full map) releases the lock. ⚠️ maxBounds is RECTANGULAR — a state locks to its BOUNDING BOX (plus
// margin), not its polygon; the box corners (masked void) stay reachable, as MapLibre has no polygon limit.
function applyMaxBounds(map, rings) {
    if (!rings || !rings.length) { map.setMaxBounds(null); return; } // full map — free pan
    map.setMaxBounds(framedBbox(rings, lockMarginFrac()));
}

// Apply the current mask (fill only) from the effective rings, keep the radar-site coverage filter matched
// to what's masked, and lock pan/zoom to the region. The outline is managed by the isolate/clear paths.
function applyMask(map) {
    const rings = effectiveRings();
    renderMaskFill(map, rings);
    notifyIsolation(rings);
    applyMaxBounds(map, rings);
}

// Cover everything outside `name`, tightening the mask to one state + tracing its border. Overrides the
// base extent until cleared.
// `frame` = move the camera to frame the new state. ⚠️ Needed when switching states from the combo: the
// camera is over the OLD (now-masked) state, and setMaxBounds does NOT move the camera into the new bounds
// on its own — the map shows the masked void until an interaction re-applies the constraint. A fitBounds
// IS that interaction and lands the camera on the new state. A map-click (frame=false) is already over the
// state, so it keeps the user's current zoom.
function isolate(map, name, frame) {
    const feat = findState(name);
    if (!feat) return;
    isolatedName = name;
    isolatedRings = outerRings(feat);
    clearHoverState(map);
    applyMask(map);                                   // fill from the state rings (+ notify the site filter)
    renderOutline(map, feat.geometry);                // crisp state border
    postIsolated(name);
    if (frame) fitRings(map, isolatedRings);          // frame the new state (combo switch); see note above
}

// Drop the single-state isolation and fall back to the base extent (CONUS or full map).
function clearIsolation(map) {
    isolatedName = null;
    isolatedRings = null;
    renderOutline(map, null);
    applyMask(map);                                   // reverts to base rings (or removes the mask)
}

// ---- public API (map.js shims delegate here) ----

// Base extent toggle: mask to CONUS (on) or show the full map (off). Independent of state isolation — a
// single isolated state still overrides it until cleared. Default is driven by the host at map-ready.
// Returns a promise that resolves once the mask has been applied (CONUS needs the boundary fetched first),
// so the caller can know WHEN the mask is on screen — used at launch to hold the map cover until then.
export function setConus(map, on) {
    baseConus = !!on;
    if (baseConus) return ensureConus().then(function () { applyMask(map); }); // need the boundary loaded first
    applyMask(map);                                                            // full map (or state) needs no CONUS data
    return Promise.resolve();
}

// "Fit to view": frame the current region of interest — the isolated state if one is isolated, else CONUS
// — into view (center + zoom via fitBounds). Padding leaves room for the bars (extra at the bottom for the
// radar console); maxZoom keeps a tiny region (DC/RI) from zooming to street level. NOTE: the padding is
// static for now; when this becomes a persistent control it should account for whichever cards are open so
// the region centers in the actually-visible map area.
const FIT_PADDING = { top: 60, bottom: 110, left: 40, right: 40 };
// Generous cap so the fit never feels limited — well above any state's natural fit zoom; it only stops a
// degenerate tiny bbox from snapping to street level. (Manual zoom-in past this stays free — the global
// map maxZoom is untouched.) Raise further if needed.
const FIT_MAX_ZOOM = 16;

function bboxOfRings(rings) {
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    rings.forEach(function (ring) {
        ring.forEach(function (p) {
            if (p[0] < minX) minX = p[0];
            if (p[0] > maxX) maxX = p[0];
            if (p[1] < minY) minY = p[1];
            if (p[1] > maxY) maxY = p[1];
        });
    });
    return [[minX, minY], [maxX, maxY]];
}

function fitRings(map, rings) {
    if (!rings || !rings.length) return;
    // framedBbox carries the per-region FIT margin; FIT_PADDING is a pixel inset on top to keep the framing
    // off the bars. The looser LOCK margin (lockMarginFrac) leaves room for both, so maxBounds never clamps.
    map.fitBounds(framedBbox(rings, fitMarginFrac()), { padding: FIT_PADDING, maxZoom: FIT_MAX_ZOOM, duration: 700 });
}

export function fitToView(map) {
    if (isolatedName) {
        ensureData().then(function () {
            const f = findState(isolatedName);
            if (f) fitRings(map, outerRings(f));
        });
    } else {
        ensureConus().then(function () {
            const rings = conusRings();
            if (rings) fitRings(map, rings);
        });
    }
}

// Enter state hover mode: fetch the polygons (once), add the hover layers, wire the handlers.
export function arm(map) {
    armed = true;
    ensureData().then(function () { addHoverLayers(map); bindHandlers(map); });
}

// Leave state hover mode: clear any isolated state (back to the base extent) and drop the hover layers.
// Does NOT change the base extent — disarming returns to CONUS/full, not necessarily the full map.
export function disarm(map) {
    armed = false;
    clearIsolation(map);
    clearHoverState(map);
    removeHoverLayers(map);
    postIsolated(null);
}

// Isolate a state by name (e.g. "Texas"), from the combo or programmatically. Arms hover mode implicitly if
// it wasn't, and FRAMES the state (the camera may be over a different state / CONUS — see isolate's note).
export function isolateState(map, name) {
    armed = true;
    ensureData().then(function () { addHoverLayers(map); bindHandlers(map); isolate(map, name, true); });
}

// Exit single-state isolation but STAY armed — back to hover mode over the base extent.
export function clear(map) {
    clearIsolation(map);
    postIsolated(null);
}

// Re-add after a basemap switch (setStyle drops our layers; the polygons stay in memory). Re-reads the
// water color so the mask matches the new style, and restores the base extent + any isolated state.
export function reAdd(map) {
    if (!armed && !isolatedName && !baseConus) return; // nothing to restore
    Promise.all([ensureData(), baseConus ? ensureConus() : Promise.resolve()]).then(function () {
        if (armed) { addHoverLayers(map); bindHandlers(map); }
        applyMask(map);                                            // fill from the effective rings
        const feat = isolatedName ? findState(isolatedName) : null;
        renderOutline(map, feat ? feat.geometry : null);
    });
}
