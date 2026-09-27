// discussions.js — mesoscale discussions on the map: SPC MDs and WPC MPDs (every C# DiscussionKinds kind)
// as the areas they were drawn for, labelled, clickable. The host writes the window's discussions into one
// file (MesoDiscussionService) and then names the MOMENT to show (setTime): PastCast's displayed radar
// frame, NowCast's now. A click posts the discussion's key back so the window's reader opens it.
// map.js's window.setDiscussion* shims delegate here; reAddAll calls reAdd(map).
//
//        ┌──────────────────────────────┐
//        │░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│   fill: the kind's colour, faint (NWS's own MD purple; WPC teal)
//        │░░░░░░░░ MD 0647 · 95% ░░░░░░░│   label: product + number, and SPC's watch probability
//        │░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░│
//        └──────────────────────────────┘   outline: the kind's outline colour, 2 px
//        ╔══════════════════════════════╗
//        ║  the SELECTED one (the one   ║   selected: a dark casing under a 4 px outline, so the one you
//        ║  the reader shows)           ║   are reading stands out of an overlapping stack
//        ╚══════════════════════════════╝
//
// ⚠️ KIND_COLORS MIRRORS C# DiscussionKinds (Fill / Outline) — change both. Data colours, never themed.
// ⚠️ A CLICK YIELDS to every smaller mark with its own popup (report dots, storm cells, survey marks):
// a discussion is a big area, and it would otherwise swallow every click inside it. Among overlapping
// discussions the most recently issued wins — the one a forecaster would read first.

import { firstBoundaryLayerId } from './layers.js';

const SOURCE = 'md-src';
const KIND_COLORS = {
    mcd: { fill: '#A900E6', outline: '#8400A8' },   // ⚠️ mirrors DiscussionKinds.Spc
    mpd: { fill: '#00C7A8', outline: '#008F78' },   // ⚠️ mirrors DiscussionKinds.Wpc
};
const DEFAULT_COLOR = { fill: '#9a9a9a', outline: '#6e6e6e' };
const CASING = 'rgba(20,20,20,0.85)';
const FILL_BASE = 0.12;
const LAYERS = ['md-fill', 'md-line', 'md-selected-casing', 'md-selected', 'md-label'];
// Other overlays' clickable marks: a click on one of these is theirs.
const YIELD_TO = ['spc-report-torn', 'spc-report-wind', 'spc-report-hail',
    'storm-cell-tvs', 'storm-cell-hail', 'storm-cell-meso', 'storm-cell-dot',
    'dat-point', 'dat-track-start', 'dat-track', 'dat-area-fill'];

let fileUrl = null;
let data = null;
let kinds = [];          // shown kind ids
let timeMs = null;       // the moment to show
let selected = '';       // key of the discussion being read
let opacity = 1;
const bound = new WeakSet();

function colorExpr(part) {
    const expr = ['match', ['get', 'kind']];
    Object.keys(KIND_COLORS).forEach(function (k) { expr.push(k, KIND_COLORS[k][part]); });
    expr.push(DEFAULT_COLOR[part]);
    return expr;
}

// Shown kinds AND in effect at the moment. No moment or no kinds = nothing.
function baseFilter() {
    if (timeMs == null || !kinds.length) return false;
    return ['all',
        ['match', ['get', 'kind'], kinds, true, false],
        ['<=', ['to-number', ['get', 't0']], timeMs],
        ['>', ['to-number', ['get', 't1']], timeMs]];
}

function selectedFilter() {
    const base = baseFilter();
    return base === false || !selected ? false : ['all', base, ['==', ['get', 'key'], selected]];
}

function removeLayers(map) {
    LAYERS.slice().reverse().forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
    if (map.getSource(SOURCE)) map.removeSource(SOURCE);
}

function addLayers(map) {
    removeLayers(map);
    map.addSource(SOURCE, { type: 'geojson', data: data });
    const before = firstBoundaryLayerId(map);
    map.addLayer({ id: 'md-fill', type: 'fill', source: SOURCE, filter: baseFilter(),
        paint: { 'fill-color': colorExpr('fill'), 'fill-opacity': FILL_BASE * opacity } }, before);
    map.addLayer({ id: 'md-line', type: 'line', source: SOURCE, filter: baseFilter(),
        layout: { 'line-join': 'round' },
        paint: { 'line-color': colorExpr('outline'), 'line-width': 2, 'line-opacity': opacity } }, before);
    map.addLayer({ id: 'md-selected-casing', type: 'line', source: SOURCE, filter: selectedFilter(),
        layout: { 'line-join': 'round' },
        paint: { 'line-color': CASING, 'line-width': 7, 'line-opacity': opacity } }, before);
    map.addLayer({ id: 'md-selected', type: 'line', source: SOURCE, filter: selectedFilter(),
        layout: { 'line-join': 'round' },
        paint: { 'line-color': colorExpr('fill'), 'line-width': 4, 'line-opacity': opacity } }, before);
    map.addLayer({ id: 'md-label', type: 'symbol', source: SOURCE, filter: baseFilter(),
        layout: {
            'text-field': ['case', ['>=', ['to-number', ['get', 'prob']], 0],
                ['concat', ['get', 'label'], ' · ', ['to-string', ['get', 'prob']], '%'],
                ['get', 'label']],
            'text-font': ['Noto Sans Medium'], 'text-size': 11,
            'text-allow-overlap': false
        },
        paint: { 'text-color': colorExpr('fill'), 'text-halo-color': CASING, 'text-halo-width': 1.5, 'text-opacity': opacity }
    }, before);
    bind(map);
}

function applyFilters(map) {
    ['md-fill', 'md-line', 'md-label'].forEach(function (id) { if (map.getLayer(id)) map.setFilter(id, baseFilter()); });
    ['md-selected-casing', 'md-selected'].forEach(function (id) { if (map.getLayer(id)) map.setFilter(id, selectedFilter()); });
}

function refresh(map) {
    if (!data) { removeLayers(map); return; }
    if (!map.getSource(SOURCE)) addLayers(map); else applyFilters(map);
}

// --- Click → the reader ---------------------------------------------------------------------------

function onClick(e) {
    const map = e.target;
    if (!map.getLayer('md-fill')) return;
    const theirs = YIELD_TO.filter(function (id) { return map.getLayer(id) && map.getLayoutProperty(id, 'visibility') !== 'none'; });
    if (theirs.length && map.queryRenderedFeatures(e.point, { layers: theirs }).length) return;
    const hits = map.queryRenderedFeatures(e.point, { layers: ['md-fill'] });
    if (!hits.length) return;
    hits.sort(function (a, b) { return (b.properties.t0 || 0) - (a.properties.t0 || 0); });
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(JSON.stringify({ type: 'discussionPicked', key: hits[0].properties.key }));
    }
}

function bind(map) {
    if (bound.has(map)) return;
    bound.add(map);
    map.on('click', onClick);
    map.on('mouseenter', 'md-fill', function () { map.getCanvas().style.cursor = 'pointer'; });
    map.on('mouseleave', 'md-fill', function () { map.getCanvas().style.cursor = ''; });
}

// --- Host commands --------------------------------------------------------------------------------

export function setSource(map, url) {
    // ⚠️ A LIVE refresh re-sends the SAME url (the file was rewritten): keep drawing until the new file
    // lands. A different url is a different window — drop the old one at once.
    if (url !== fileUrl) { data = null; removeLayers(map); }
    fileUrl = url;
    if (!url) return;
    const asked = url;
    fetch(url, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        if (fileUrl !== asked || !gj) return;
        data = gj;
        if (map.getSource(SOURCE)) map.getSource(SOURCE).setData(gj);
        refresh(map);
    }).catch(function (e) { console.error('discussions load failed: ' + e); });
}

export function setKinds(map, csv) {
    kinds = (csv || '').split(',').filter(function (s) { return s.length > 0; });
    refresh(map);
}

export function setTime(map, ms) {
    timeMs = ms == null ? null : +ms;
    refresh(map);
}

export function setSelected(map, key) {
    selected = key || '';
    refresh(map);
}

export function setOpacity(map, o) {
    opacity = Math.max(0, Math.min(1, +o || 0));
    if (map.getLayer('md-fill')) map.setPaintProperty('md-fill', 'fill-opacity', FILL_BASE * opacity);
    ['md-line', 'md-selected-casing', 'md-selected'].forEach(function (id) { if (map.getLayer(id)) map.setPaintProperty(id, 'line-opacity', opacity); });
    if (map.getLayer('md-label')) map.setPaintProperty('md-label', 'text-opacity', opacity);
}

// Frame one discussion (its whole area, padded). PRIMARY pane only — the others follow the camera.
export function focus(map, key) {
    if (!data || !data.features) return;
    const f = data.features.find(function (x) { return x.properties && x.properties.key === key; });
    if (!f || !f.geometry) return;
    let w = 180, s = 90, e = -180, n = -90;
    (function walk(c) {
        if (typeof c[0] === 'number') {
            w = Math.min(w, c[0]); e = Math.max(e, c[0]); s = Math.min(s, c[1]); n = Math.max(n, c[1]);
        } else c.forEach(walk);
    })(f.geometry.coordinates);
    if (w > e) return;
    map.fitBounds([[w, s], [e, n]], { padding: 80, duration: 700, maxZoom: 8 });
}

export function clear(map) {
    fileUrl = null;
    data = null;
    selected = '';
    removeLayers(map);
}

export function reAdd(map) { refresh(map); }
