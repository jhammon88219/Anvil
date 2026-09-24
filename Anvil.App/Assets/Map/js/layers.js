// layers.js — where an overlay slots into the basemap's layer stack. One place, because every overlay
// module had grown its own copy of "find the first symbol layer" and they had drifted apart.
//
// MapLibre's addLayer(spec, beforeId) inserts the layer immediately BENEATH `beforeId`, so a beforeId
// is really "the first basemap thing that must stay on top of me".
//
//   ── top ──   ░░░ place labels ░░░         ← firstSymbolLayerId() returns THIS
//                ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁              an overlay passed it draws just below: under names,
//               ─── state / country lines ─  ← firstBoundaryLayerId() returns THIS
//                ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁              an overlay passed it draws below the borders too,
//                                               so they stay legible through the fill
//   ── base ──  basemap fills
//
// So: pass firstSymbolLayerId to sit UNDER labels but OVER the borders (the outlook does this — its
// fills are faint and the borders would be lost beneath them anyway). Pass firstBoundaryLayerId to sit
// under the borders as well (watches, warnings). Pass nothing to sit on top of
// everything (storm-report dots). Two overlays sharing one beforeId are ordered by ADD order — which
// is why reAddAll() in map.js is an explicit ordered list, not a loop over whatever is loaded.
// ⚠️ For the overlays in GROUPS below, a beforeId only decides where a layer lands for the instant
// before restack() moves it into the USER's order. It still matters for everything else.

// First symbol (label) layer — an overlay passed this draws under place names but OVER the boundary
// lines, since those are `line` layers sitting below the labels.
export function firstSymbolLayerId(map) {
    const layers = (map.getStyle() && map.getStyle().layers) || [];
    const symbol = layers.find(function (l) { return l.type === 'symbol'; });
    return symbol ? symbol.id : undefined;
}

// First boundary (state / country line) layer — an overlay passed this draws UNDER the borders, so
// state and country lines stay legible through it. Falls back to the labels on a style without
// boundary layers. Mirrors radar.js's beforeId(), which is the same idea with radar's extra steps.
export function firstBoundaryLayerId(map) {
    if (map.getLayer('boundaries_country')) return 'boundaries_country';
    if (map.getLayer('boundaries')) return 'boundaries';
    return firstSymbolLayerId(map);
}

// ===== USER OVERLAY ORDER =====================================================================
// The temporal windows let the user drag their layer sections into any order, and the map follows:
// the TOP of the list draws on TOP. Every ordered overlay lives in ONE band directly beneath the
// boundary lines, so borders and place names stay legible over whatever the user stacks:
//
//   ── top ──   ░░░ place labels ░░░
//               ─── state / country lines ───   ← firstBoundaryLayerId(): the band's ceiling
//               ┌ group 1 (list top) ┐
//               │ group 2            │           ← restack() keeps these contiguous, in order
//               └ group N (list end) ┘
//   ── base ──  basemap fills
//
// ⚠️ A GROUP IS A SET OF LAYER-ID PREFIXES, not a module. Each module still adds its layers with its
// own beforeId; restack() runs after EVERY addLayer (watchStack wraps it) and moves them into place, so
// no module has to know the order and a late add (a refresh, a site click, a new pane) can't break it.
// ⚠️ GROUP IDS ARE THE HOST'S — PanelSection.LayerId in the NowCast/PastCast XAML. Change both.
// Top-first; this is also the default order (the stack before the order was the user's).
const GROUPS = [
    ['reports',  ['spc-report-']],
    ['damage',   ['dat-']],
    ['warnings', ['nws-warning-']],
    ['watches',  ['spc-watch-']],
    ['outlook',  ['spc-outlook-']],
    ['radar',    ['level2-', 'radar-ruler-']],   // the WebGL layer + its range rings, sweep and ruler
];
const DEFAULT_ORDER = GROUPS.map(function (g) { return g[0]; });
const RADAR_LAYER = 'level2-radar';              // bottom of its own group: rings + ruler draw over it

let userOrder = [];                              // top-first group ids from the host (one window's list)

function groupOf(layerId) {
    for (let i = 0; i < GROUPS.length; i++) {
        const prefixes = GROUPS[i][1];
        for (let j = 0; j < prefixes.length; j++) if (layerId.indexOf(prefixes[j]) === 0) return GROUPS[i][0];
    }
    return null;
}

// The host sends ONE window's list, which never names every group (NowCast has no outlook section, but
// ForeCast's outlook shares the map with it). A missing group keeps its DEFAULT neighbour: it goes
// directly beneath the nearest group that sits above it in DEFAULT_ORDER, or on top if none does.
function effectiveOrder() {
    const out = userOrder.filter(function (id) { return DEFAULT_ORDER.indexOf(id) >= 0; });
    DEFAULT_ORDER.forEach(function (id, di) {
        if (out.indexOf(id) >= 0) return;
        let at = 0;
        for (let k = di - 1; k >= 0; k--) {
            const above = out.indexOf(DEFAULT_ORDER[k]);
            if (above >= 0) { at = above + 1; break; }
        }
        out.splice(at, 0, id);
    });
    return out;
}

// Put every ordered layer on this map into its place. Cheap when nothing is out of place (one pass over
// the layer order), so it is safe to run after every add.
export function restack(map) {
    const all = map.getLayersOrder ? map.getLayersOrder() : ((map.getStyle() && map.getStyle().layers) || []).map(function (l) { return l.id; });
    const byGroup = {};
    all.forEach(function (id) {
        const g = groupOf(id);
        if (!g) return;
        (byGroup[g] = byGroup[g] || []).push(id);
    });
    if (byGroup.radar) {
        const r = byGroup.radar.indexOf(RADAR_LAYER);
        if (r > 0) { byGroup.radar.splice(r, 1); byGroup.radar.unshift(RADAR_LAYER); }
    }
    // Bottom-up, each group's layers in their own bottom-up order.
    const want = [];
    effectiveOrder().slice().reverse().forEach(function (g) { if (byGroup[g]) want.push.apply(want, byGroup[g]); });
    if (!want.length) return;

    // ⚠️ Not firstBoundaryLayerId() as-is: on a style with no boundary layers it falls back to the first
    // SYMBOL layer, and the ring and ruler labels are symbols of our own that sit below the basemap's.
    let anchor = (map.getLayer('boundaries_country') && 'boundaries_country') || (map.getLayer('boundaries') && 'boundaries');
    if (!anchor) anchor = all.find(function (id) { return !groupOf(id) && map.getLayer(id).type === 'symbol'; });
    const pos = {};
    all.forEach(function (id, i) { pos[id] = i; });
    let inPlace = anchor ? pos[want[want.length - 1]] + 1 === pos[anchor] : pos[want[want.length - 1]] === all.length - 1;
    for (let i = 1; inPlace && i < want.length; i++) inPlace = pos[want[i]] === pos[want[i - 1]] + 1;
    if (inPlace) return;
    // moveLayer(id, beforeId) lands id directly beneath beforeId, so walking bottom-up leaves each one
    // above the last.
    want.forEach(function (id) { map.moveLayer(id, anchor); });
}

// Restack after every addLayer on this map — coalesced to one pass per task, and before the next frame
// draws, so a freshly added layer never shows a frame in the wrong place.
export function watchStack(map) {
    if (map.__anvilStackWatched) return;
    map.__anvilStackWatched = true;
    const add = map.addLayer.bind(map);
    let queued = false;
    map.addLayer = function () {
        const r = add.apply(null, arguments);
        if (!queued) {
            queued = true;
            queueMicrotask(function () {
                queued = false;
                try { restack(map); } catch (e) { console.error('restack failed: ' + e); }
            });
        }
        return r;
    };
}

export function setOverlayOrder(ids) {
    userOrder = Array.isArray(ids) ? ids.slice() : [];
}
