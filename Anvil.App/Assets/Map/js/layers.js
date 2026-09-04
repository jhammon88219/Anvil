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
