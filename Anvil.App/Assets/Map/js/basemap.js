// ============================================================================================
// basemap.js — what of the BASEMAP draws: the Map key (hide it all), the layer flyout (which groups),
// and the dimmer. It only ever touches the style's own layers; every overlay is someone else's.
//
//   ── top ──   ░░░ place names ░░░          ┐ ABOVE the overlay band — dimmed by their own opacity
//               ─── borders / counties ───   ┘ (a veil here would dim the storm too)
//               ┌ overlay band (layers.js) ┐   radar, warnings, … — NEVER touched by this module
//               └──────────────────────────┘
//               ▒▒▒ VEIL ▒▒▒                    ← ours: a background layer in the BLANK colour at
//               roads · buildings · water ·       opacity = dim, so everything under it fades toward
//               land                               blank in one stroke
//   ── base ──  background                      ← repainted to BLANK while the map is hidden
//
// GROUPS are the protomaps SOURCE-LAYERS, not layer ids, so one rule covers all five styles:
//   land (earth, landcover, landuse) · water · roads (+ their names/shields) · buildings (+ addresses)
//   · borders (boundaries) · counties (boundaries_county — only Data Viz Black has it) · names (every
//   other symbol: places, POIs, water + island labels).
// ⚠️ The ids are the HOST's too — Models/Map/BasemapGroups.cs. Change both.
//
// ⚠️ HIDDEN OVERRIDES THE GROUPS WITHOUT CHANGING THEM: hide, then show, and exactly the set you had
// ticked comes back. Blank = the theme's --anvil-map-blank, NOT the style's background (that is the
// OCEAN colour in every bundled style).
//
// ⚠️ A STYLE SWAP ERASES ALL OF THIS (setStyle restores the file's values), so map.js hands setStyle
// transformFor(map): the NEXT style arrives already hidden/dimmed, with no flash of basemap while the
// new tiles load, and its pristine paint values become this map's ORIGINALS. A pane created later
// records its originals on its first apply(), which is before anything here has touched it.
// ============================================================================================

import * as Theme from './theme.js';

const VEIL = 'anvil-basemap-veil';
const ALL_GROUPS = ['land', 'water', 'roads', 'buildings', 'borders', 'counties', 'names'];

let hidden = false;
let off = new Set();   // groups the user unticked
let dim = 0;           // 0 = full basemap … 0.9 = nearly blank

// Per map: layer id → { prop: pristine value } for every paint value this module rewrites. Recorded from
// a pristine style only (see the header), so a scaled value can never be mistaken for an original.
const originals = new WeakMap();

function blankColor() { return Theme.color('--anvil-map-blank', '#0a0a0a'); }

/** Whether the whole basemap is hidden (states.js paints the isolation mask in the blank colour then). */
export function isHidden() { return hidden; }

/** The blank ground colour — states.js's mask colour while the map is hidden. */
export function blank() { return blankColor(); }

/** Which basemap group a style layer belongs to, or null for anything that is not basemap. */
function groupOf(layer) {
    const sl = layer['source-layer'];
    if (!sl) return null; // background, or one of OUR layers (geojson / custom / the veil)
    if (sl === 'boundaries') return /county/i.test(layer.id) ? 'counties' : 'borders';
    if (sl === 'roads') return 'roads';
    if (sl === 'buildings') return 'buildings';
    if (layer.type === 'symbol') return 'names';
    if (sl === 'water') return 'water';
    return 'land'; // earth / landcover / landuse — and any new fill a style grows lands with them
}

// Above the overlay band: the veil can't reach these, so they fade through their own opacity.
function isAboveBand(layer) { return layer['source-layer'] === 'boundaries' || layer.type === 'symbol'; }

function opacityProps(layer) {
    return layer.type === 'line' ? ['line-opacity']
        : layer.type === 'symbol' ? ['text-opacity', 'icon-opacity']
            : [];
}

// Scale an opacity value by k. A number or a zoom curve (interpolate / step with numeric outputs) scales;
// anything else is left alone rather than rewritten wrongly. k = 1 hands back the original untouched.
function scaleOpacity(orig, k) {
    if (k >= 1) return orig;
    if (orig === undefined || orig === null) return k;
    if (typeof orig === 'number') return orig * k;
    if (Array.isArray(orig) && (orig[0] === 'interpolate' || orig[0] === 'step')) {
        const out = orig.slice();
        // interpolate: [op, interp, input, stop, out, stop, out…]  step: [op, input, out, stop, out…]
        for (let i = orig[0] === 'interpolate' ? 4 : 2; i < out.length; i += 2) {
            if (typeof out[i] !== 'number') return orig;
            out[i] = out[i] * k;
        }
        return out;
    }
    return orig;
}

function visibleFor(group) { return !hidden && !off.has(group); }

function veilSpec() {
    return {
        id: VEIL, type: 'background',
        layout: { visibility: !hidden && dim > 0 ? 'visible' : 'none' },
        paint: { 'background-color': blankColor(), 'background-opacity': dim },
    };
}

// ---- Pristine style JSON (a style swap) -------------------------------------------------------------

function recordFromSpec(map, layers) {
    const rec = {};
    layers.forEach(function (l) {
        const paint = l.paint || {};
        if (l.type === 'background') rec[l.id] = { 'background-color': paint['background-color'] };
        else if (isAboveBand(l)) {
            const r = {};
            opacityProps(l).forEach(function (p) { r[p] = paint[p]; });
            rec[l.id] = r;
        }
    });
    originals.set(map, rec);
}

// Bake the current state into a style document before MapLibre applies it (mutates + returns `style`).
function bake(style) {
    const layers = style.layers || [];
    let veilAt = -1;
    layers.forEach(function (l, i) {
        if (l.type === 'background') {
            if (hidden) { l.paint = Object.assign({}, l.paint, { 'background-color': blankColor() }); }
            return;
        }
        const g = groupOf(l);
        if (!g) return;
        if (!visibleFor(g)) l.layout = Object.assign({}, l.layout, { visibility: 'none' });
        if (isAboveBand(l)) {
            if (veilAt < 0) veilAt = i;
            if (dim > 0) {
                const paint = Object.assign({}, l.paint);
                opacityProps(l).forEach(function (p) {
                    const v = scaleOpacity(paint[p], 1 - dim);
                    if (v !== undefined) paint[p] = v;
                });
                l.paint = paint;
            }
        }
    });
    if (!layers.some(function (l) { return l.id === VEIL; })) {
        layers.splice(veilAt < 0 ? layers.length : veilAt, 0, veilSpec());
    }
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
            else if (isAboveBand(l)) {
                const r = {};
                opacityProps(l).forEach(function (p) { r[p] = map.getPaintProperty(l.id, p); });
                rec[l.id] = r;
            }
        });
        originals.set(map, rec);
    }
    return rec;
}

// The veil sits directly beneath the first layer that must stay above it: an overlay, or a border/label.
// Walking bottom-up, that is the first layer that is neither the background nor below-band basemap.
function veilBeforeId(layers) {
    for (let i = 0; i < layers.length; i++) {
        const l = layers[i];
        if (l.id === VEIL || l.type === 'background') continue;
        const g = groupOf(l);
        if (!g || isAboveBand(l)) return l.id;
    }
    return undefined;
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
            if (l.id === VEIL) return;
            if (l.type === 'background') {
                const orig = rec[l.id] ? rec[l.id]['background-color'] : undefined;
                setPaint(map, l.id, 'background-color', hidden ? blankColor() : orig);
                return;
            }
            const g = groupOf(l);
            if (!g) return;
            const vis = visibleFor(g) ? 'visible' : 'none';
            if ((map.getLayoutProperty(l.id, 'visibility') || 'visible') !== vis) map.setLayoutProperty(l.id, 'visibility', vis);
            if (isAboveBand(l)) {
                const r = rec[l.id] || {};
                opacityProps(l).forEach(function (p) { setPaint(map, l.id, p, scaleOpacity(r[p], 1 - dim)); });
            }
        });

        const spec = veilSpec();
        if (!map.getLayer(VEIL)) {
            map.addLayer(spec, veilBeforeId(layers));
        } else {
            map.setLayoutProperty(VEIL, 'visibility', spec.layout.visibility);
            setPaint(map, VEIL, 'background-color', spec.paint['background-color']);
            setPaint(map, VEIL, 'background-opacity', spec.paint['background-opacity']);
        }
    } catch (e) {
        console.error('basemap apply failed: ' + e);
    }
}

/**
 * Set the whole state (the host's one command). `offGroups` is the list of unticked group ids; `dimLevel`
 * 0…0.9. Returns nothing — map.js fans apply() out over the panes.
 */
export function setState(isHidden, offGroups, dimLevel) {
    hidden = !!isHidden;
    off = new Set((offGroups || []).filter(function (g) { return ALL_GROUPS.indexOf(g) >= 0; }));
    const d = Number(dimLevel);
    dim = isFinite(d) ? Math.max(0, Math.min(0.9, d)) : 0;
}
