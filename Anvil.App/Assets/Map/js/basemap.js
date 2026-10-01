// ============================================================================================
// basemap.js — what of the BASEMAP draws: the Map key (hide it all) and the layer flyout (which groups;
// ROADS split by kind). It only ever touches the style's own layers; every overlay is someone else's.
//
//   ── top ──   cities · POIs · water names · borders / counties
//               ┌ overlay band (layers.js) ┐   radar, warnings, … — NEVER touched by this module
//               └──────────────────────────┘
//               roads · buildings · water · land
//   ── base ──  background                      ← repainted to BLANK while the map is hidden
//
// GROUPS are the protomaps SOURCE-LAYERS, not layer ids, so one rule covers all five styles:
//   land (earth, landcover, landuse) · water · buildings (+ addresses) · borders (boundaries) · counties
//   (boundaries_county — only Data Viz Black has it) · THREE LABEL groups by the symbol's source-layer:
//   cities (places — also state + country names) · water_names (water + earth: rivers, lakes, seas, islands)
//   · pois (pois + any other label) · and FIVE ROAD groups cut from the one `roads` source-layer by each
//   FEATURE's `kind` (road names + shields ride with their road, not with the labels):
//
//      highways ─ kind highway      major_roads ─ major_road      minor_roads ─ minor_road (+ service)
//      rail ─ kind rail             paths ─ EVERYTHING ELSE (path, other, pier, runway, ferry, …)
//
//   ⚠️ BY FEATURE, NOT BY LAYER: the styles' road layers don't split cleanly by kind (ramps are their own
//   layers; one shields / labels layer carries highway AND major), so each road layer keeps its own filter and
//   gets "and not one of the hidden kinds" ANDed on. A highway's ramps, shield and label go with it.
//   ⚠️ The styles' filters are a MIX of legacy syntax (["==","kind","x"]) and expressions — MapLibre won't
//   take the two inside one filter, so the added clause is written in WHICHEVER syntax the original uses
//   (isExpressionFilter, copied from the style spec). Every road group off = the layer is simply hidden.
// ⚠️ The ids are the HOST's too — Models/Map/BasemapGroups.cs. Change both.
//
// ⚠️ HIDDEN OVERRIDES THE GROUPS WITHOUT CHANGING THEM: hide, then show, and exactly the set you had
// ticked comes back. Blank = the theme's --anvil-map-blank, NOT the style's background (that is the
// OCEAN colour in every bundled style).
//
// ⚠️ A STYLE SWAP ERASES ALL OF THIS (setStyle restores the file's values), so map.js hands setStyle
// transformFor(map): the NEXT style arrives already hidden/filtered, with no flash of basemap while the
// new tiles load, and its pristine values become this map's ORIGINALS. A pane created later records its
// originals on its first apply(), which is before anything here has touched it.
// (The DIMMER — a veil layer + label opacity scaling — was removed, 2026-10-01.)
// ============================================================================================

import * as Theme from './theme.js';

// The road groups and the `kind` values each owns. PATHS owns no list: it is everything NOT named here.
const ROAD_KINDS = {
    highways: ['highway'],
    major_roads: ['major_road'],
    minor_roads: ['minor_road'],
    rail: ['rail'],
};
const ROAD_GROUPS = ['highways', 'major_roads', 'minor_roads', 'paths', 'rail'];
const NAMED_KINDS = [].concat(ROAD_KINDS.highways, ROAD_KINDS.major_roads, ROAD_KINDS.minor_roads, ROAD_KINDS.rail);
const ALL_GROUPS = ['land', 'water', 'buildings', 'borders', 'counties', 'cities', 'pois', 'water_names'].concat(ROAD_GROUPS);

let hidden = false;
let off = new Set();   // groups the user unticked

// Per map: layer id → { prop: pristine value } for every value this module rewrites (the background colour,
// each road layer's filter). Recorded from a pristine style only (see the header).
const originals = new WeakMap();

function blankColor() { return Theme.color('--anvil-map-blank', '#0a0a0a'); }

/** Whether the whole basemap is hidden (states.js paints the isolation mask in the blank colour then). */
export function isHidden() { return hidden; }

/** The blank ground colour — states.js's mask colour while the map is hidden. */
export function blank() { return blankColor(); }

/** Which basemap group a style layer belongs to ('roads' for every road layer), or null for non-basemap. */
function groupOf(layer) {
    const sl = layer['source-layer'];
    if (!sl) return null; // background, or one of OUR layers (geojson / custom)
    if (sl === 'boundaries') return /county/i.test(layer.id) ? 'counties' : 'borders';
    if (sl === 'roads') return 'roads';
    if (sl === 'buildings') return 'buildings';   // + address labels
    if (layer.type === 'symbol') {
        if (sl === 'places') return 'cities';      // cities, towns, neighbourhoods — and state + country names
        if (sl === 'water' || sl === 'earth') return 'water_names'; // rivers, lakes, seas + island names
        return 'pois';                             // pois, and any other label a style grows
    }
    if (sl === 'water') return 'water';
    return 'land'; // earth / landcover / landuse — and any new fill a style grows lands with them
}

// The style spec's own test (maplibre-style-spec feature_filter isExpressionFilter): is this filter written
// as an expression (true) or in the legacy syntax (false)?
function isExpressionFilter(f) {
    if (f === true || f === false) return true;
    if (!Array.isArray(f) || f.length === 0) return false;
    switch (f[0]) {
        case 'has': return f.length >= 2 && f[1] !== '$id' && f[1] !== '$type';
        case 'in': return f.length >= 3 && (typeof f[1] !== 'string' || Array.isArray(f[2]));
        case '!in': case '!has': case 'none': return false;
        case '==': case '!=': case '>': case '>=': case '<': case '<=':
            return f.length !== 3 || Array.isArray(f[1]) || Array.isArray(f[2]);
        case 'any': case 'all':
            for (let i = 1; i < f.length; i++) {
                if (!isExpressionFilter(f[i]) && typeof f[i] !== 'boolean') return false;
            }
            return true;
        default: return true;
    }
}

function allRoadsOff() { return ROAD_GROUPS.every(function (g) { return off.has(g); }); }

// The road clauses for the current ticks, in the syntax of `orig` (an original filter, possibly undefined).
// Returns the filter to set: the original untouched when no road group is off.
function roadFilter(orig) {
    const hiddenKinds = [];
    Object.keys(ROAD_KINDS).forEach(function (g) { if (off.has(g)) Array.prototype.push.apply(hiddenKinds, ROAD_KINDS[g]); });
    const pathsOff = off.has('paths');
    if (!hiddenKinds.length && !pathsOff) return orig;

    const expr = orig === undefined || orig === null || isExpressionFilter(orig);
    const clauses = [];
    if (hiddenKinds.length) {
        clauses.push(expr ? ['!', ['in', ['get', 'kind'], ['literal', hiddenKinds]]]
            : ['!in', 'kind'].concat(hiddenKinds));
    }
    if (pathsOff) { // keep ONLY the named kinds
        clauses.push(expr ? ['in', ['get', 'kind'], ['literal', NAMED_KINDS]]
            : ['in', 'kind'].concat(NAMED_KINDS));
    }
    const parts = (orig === undefined || orig === null) ? clauses : [orig].concat(clauses);
    return parts.length === 1 ? parts[0] : ['all'].concat(parts);
}

function visibleFor(layer) {
    if (hidden) return false;
    const g = groupOf(layer);
    return g === 'roads' ? !allRoadsOff() : !off.has(g);
}

// ---- Pristine style JSON (a style swap) -------------------------------------------------------------

function recordFromSpec(map, layers) {
    const rec = {};
    layers.forEach(function (l) {
        if (l.type === 'background') rec[l.id] = { 'background-color': (l.paint || {})['background-color'] };
        else if (l['source-layer'] === 'roads') rec[l.id] = { filter: l.filter };
    });
    originals.set(map, rec);
}

// Bake the current state into a style document before MapLibre applies it (mutates + returns `style`).
function bake(style) {
    (style.layers || []).forEach(function (l) {
        if (l.type === 'background') {
            if (hidden) { l.paint = Object.assign({}, l.paint, { 'background-color': blankColor() }); }
            return;
        }
        const g = groupOf(l);
        if (!g) return;
        if (!visibleFor(l)) l.layout = Object.assign({}, l.layout, { visibility: 'none' });
        if (g === 'roads') {
            const f = roadFilter(l.filter);
            if (f === undefined) delete l.filter; else l.filter = f;
        }
    });
    return style;
}

/** The setStyle transformStyle for one map: records the next style's originals, then bakes the state in. */
export function transformFor(map) {
    return function (previous, next) {
        try {
            recordFromSpec(map, next.layers || []);
            return bake(next);
        } catch (e) {
            console.error('basemap transform failed: ' + e);
            return next;
        }
    };
}

// ---- A live map ------------------------------------------------------------------------------------

function originalsOf(map, layers) {
    let rec = originals.get(map);
    if (!rec) {
        // First touch of a map built by the constructor (the launch pane, a new pane): nothing here has
        // written to it yet, so its current values ARE the style file's.
        rec = {};
        layers.forEach(function (l) {
            if (l.type === 'background') rec[l.id] = { 'background-color': map.getPaintProperty(l.id, 'background-color') };
            else if (l['source-layer'] === 'roads') rec[l.id] = { filter: map.getFilter(l.id) };
        });
        originals.set(map, rec);
    }
    return rec;
}

function setPaint(map, id, prop, value) {
    const cur = map.getPaintProperty(id, prop);
    if (JSON.stringify(cur) !== JSON.stringify(value)) map.setPaintProperty(id, prop, value);
}

/** Apply the current state to one live map. Idempotent; safe before the style is loaded (no-op). */
export function apply(map) {
    let layers;
    try { layers = map.getStyle() && map.getStyle().layers; } catch (e) { return; }
    if (!layers) return;
    try {
        const rec = originalsOf(map, layers);
        layers.forEach(function (l) {
            if (l.type === 'background') {
                const orig = rec[l.id] ? rec[l.id]['background-color'] : undefined;
                setPaint(map, l.id, 'background-color', hidden ? blankColor() : orig);
                return;
            }
            const g = groupOf(l);
            if (!g) return;
            const vis = visibleFor(l) ? 'visible' : 'none';
            if ((map.getLayoutProperty(l.id, 'visibility') || 'visible') !== vis) map.setLayoutProperty(l.id, 'visibility', vis);
            if (g === 'roads') {
                const want = roadFilter(rec[l.id] ? rec[l.id].filter : undefined);
                if (JSON.stringify(map.getFilter(l.id)) !== JSON.stringify(want)) map.setFilter(l.id, want);
            }
        });
    } catch (e) {
        console.error('basemap apply failed: ' + e);
    }
}

/**
 * Set the whole state (the host's one command). `offGroups` is the list of unticked group ids.
 * Returns nothing — map.js fans apply() out over the panes.
 */
export function setState(isHidden, offGroups) {
    hidden = !!isHidden;
    off = new Set((offGroups || []).filter(function (g) { return ALL_GROUPS.indexOf(g) >= 0; }));
}
