// states.js — "State Isolation" prototype. Two modes layered on the same bundled US-states polygons:
//
//   ARMED  (hover mode): the basemap is untouched, but hovering a state paints a faint fill + a bold
//           outline on THAT state only, and the cursor becomes a pointer — the affordance that a state
//           is clickable. Clicking isolates it.
//   ISOLATED: everything outside the chosen state is covered by one opaque fill the color of the map's
//           water (so: no oceans, no neighbors — just a flat void), leaving the state showing the real
//           basemap + radar + overlays through a hole. A clean thin outline traces the border.
//
// WHY a bundled polygon at all: the Protomaps basemap ships state BORDER LINES only (the `boundaries`
// source-layer) — there is no fillable state polygon in the tiles, and tile line geometry can't be read
// back reliably at runtime. So isolation needs real polygon geometry; we bundle a simplified US-states
// GeoJSON (served from the same-origin `mapassets` host) and drive everything off it.
//
// WHY the "inverted mask" instead of clipping: MapLibre can't clip the whole map to a polygon. The
// standard trick is a single fill whose OUTER ring is the whole world and whose HOLES are the state's
// rings — fill it water-color and it covers everything except the state's shape. No turf / boolean ops:
// earcut treats the first ring as outer and the rest as holes regardless of winding.
//
// Host seam: map.js exposes window.stateIso{Arm,Disarm,Select,Clear} shims, driven by
// StateIsolationViewModel through IMapService (the "Isolate" top-bar toggle). This module posts
// {type:"stateIsolated", name} back so the VM tracks the isolated state. window.__isoTest(name) is a dev
// console helper. applyStyle calls reAdd(map) after a basemap switch (setStyle drops our layers; the
// fetched polygons stay in memory).

const STATES_URL = 'https://mapassets/state-boundaries.geojson';

// The world rectangle used as the mask's outer ring. Y capped at MapLibre's Web-Mercator limit (~85.05°);
// X full span (renderWorldCopies is off, so one world is all there is).
const WORLD_RING = [[-180, -85.05], [180, -85.05], [180, 85.05], [-180, 85.05], [-180, -85.05]];

const SRC = 'state-iso';            // the states FeatureCollection (hover source)
const FILL = 'state-iso-fill';      // faint hover highlight fill
const LINE = 'state-iso-line';      // bold hover outline (only the hovered state draws)
const MASK_SRC = 'state-iso-mask';  // the inverted world-minus-state polygon
const MASK_FILL = 'state-iso-mask-fill';
const OUTLINE_SRC = 'state-iso-outline'; // the isolated state's geometry (for the clean border line)
const MASK_LINE = 'state-iso-mask-line';

let statesData = null;   // the fetched FeatureCollection (fetch-once)
let statesPromise = null;
let armed = false;       // hover mode on
let isolatedName = null; // name of the isolated state, or null
let isolatedRings = null; // the isolated state's outer ring(s), or null (handed to the site filter)
let hoverName = null;    // name of the state currently under the cursor, or null
let handlersBound = false;
// Notified when isolation changes (rings on isolate, null on clear/disarm) — map.js wires this to the
// radar-site coverage filter. Kept as a settable callback so states.js stays decoupled from radar-sites.js.
let onIsolationChange = null;
export function setOnIsolationChange(fn) { onIsolationChange = fn; }
function notifyIsolation(rings) { if (onIsolationChange) onIsolationChange(rings); }

// The style's water color, read live so isolation matches whatever basemap is loaded AND tracks a switch
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
// them and hover must stay inert.
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
        if (f) isolate(map, f.id);
    });
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

// Build the inverted mask: one Polygon = world outer ring + the state's outer ring(s) as holes. So a lake
// inside the isolated state still shows the basemap, and everything outside the rings is covered.
function buildMask(rings) {
    return { type: 'Feature', properties: {}, geometry: { type: 'Polygon', coordinates: [WORLD_RING].concat(rings) } };
}

function findState(name) {
    return statesData && statesData.features.find(function (f) { return f.properties && f.properties.name === name; });
}

// Tell the host which state is isolated (name) or that isolation cleared (null), so the VM can drive a
// readout / future stream-mode UI. Best-effort — no-op outside the WebView.
function postIsolated(name) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'stateIsolated', name: name || null }));
    }
}

// Cover everything outside `name` with a water-colored fill, and trace the state's border. The mask + its
// outline are added with NO beforeId, so they sit on top of the ENTIRE stack (basemap, radar, overlays,
// labels) — only the state's hole shows what's beneath. Labels outside the state are hidden by design
// (clean look); in-state labels are hidden too for now (a later pass can re-show them).
function isolate(map, name) {
    const feat = findState(name);
    if (!feat) return;
    isolatedName = name;
    clearHoverState(map);

    const rings = outerRings(feat);
    isolatedRings = rings;
    const mask = buildMask(rings);
    const outline = { type: 'Feature', properties: {}, geometry: feat.geometry };

    if (map.getSource(MASK_SRC)) map.getSource(MASK_SRC).setData(mask);
    else map.addSource(MASK_SRC, { type: 'geojson', data: mask });
    if (map.getSource(OUTLINE_SRC)) map.getSource(OUTLINE_SRC).setData(outline);
    else map.addSource(OUTLINE_SRC, { type: 'geojson', data: outline });

    if (!map.getLayer(MASK_FILL)) {
        map.addLayer({
            id: MASK_FILL, type: 'fill', source: MASK_SRC,
            // fill-antialias off: we draw our own crisp border; AA on the mask's hole edge would double it.
            paint: { 'fill-color': waterColor(map), 'fill-antialias': false }
        });
    } else {
        map.setPaintProperty(MASK_FILL, 'fill-color', waterColor(map));
    }
    if (!map.getLayer(MASK_LINE)) {
        map.addLayer({
            id: MASK_LINE, type: 'line', source: OUTLINE_SRC,
            paint: { 'line-color': '#8a8a8a', 'line-width': 1.2 }
        });
    }
    postIsolated(name);
    notifyIsolation(rings);
}

function clearIsolation(map) {
    isolatedName = null;
    isolatedRings = null;
    [MASK_LINE, MASK_FILL].forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
    if (map.getSource(MASK_SRC)) map.removeSource(MASK_SRC);
    if (map.getSource(OUTLINE_SRC)) map.removeSource(OUTLINE_SRC);
}

// ---- public API (map.js shims delegate here) ----

// Enter hover mode: fetch the polygons (once), add the hover layers, wire the handlers.
export function arm(map) {
    armed = true;
    ensureData().then(function () { addHoverLayers(map); bindHandlers(map); });
}

// Leave state-isolation entirely: drop the mask AND the hover layers, back to the untouched full map.
export function disarm(map) {
    armed = false;
    clearIsolation(map);
    clearHoverState(map);
    removeHoverLayers(map);
    postIsolated(null);
    notifyIsolation(null);
}

// Isolate a state by name (e.g. "Texas"). Arms hover mode implicitly if it wasn't.
export function isolateState(map, name) {
    armed = true;
    ensureData().then(function () { addHoverLayers(map); bindHandlers(map); isolate(map, name); });
}

// Exit isolation but STAY armed — back to hover mode so another state can be picked.
export function clear(map) {
    clearIsolation(map);
    postIsolated(null);
    notifyIsolation(null);
}

// Re-add after a basemap switch (setStyle drops our layers; the polygons stay in memory). Re-reads the
// water color so isolation matches the new style.
export function reAdd(map) {
    if (!armed && !isolatedName) return;
    ensureData().then(function () {
        if (armed) { addHoverLayers(map); bindHandlers(map); }
        if (isolatedName) { const n = isolatedName; isolatedName = null; isolate(map, n); }
    });
}
