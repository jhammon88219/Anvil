// tropical.js — tropical-cyclone watches and warnings on the map: hurricane / tropical storm / storm surge /
// extreme wind, as the forecast ZONES they are issued for, in the NWS's own hazard colours. The host writes one
// file (TropicalService) and names the MOMENT (setTime): PastCast's displayed radar frame, or null = live (every
// zone in the file is in effect). map.js's window.setTropical* shims delegate here; reAddAll calls reAdd(map).
//
//        ┌─────────────┬─────────────┐
//        │▒▒▒▒▒▒▒▒▒▒▒▒▒│░░░░░░░░░░░░░│   fill: the zone's `fill` (the NWS colour, written by the SERVICE — this
//        │▒ Hurricane ▒│░ Tropical  ░│   module has no colour table), faint; outline: the same colour, 1.5 px
//        │▒  Watch    ▒│░ Storm Wtch░│
//        ├▓▓▓▓▓▓▓▓▓▓▓▓▓┴▓▓▓▓▓▓▓▓▓▓▓▓▓┤   storm SURGE (priority 53) draws OVER hurricane (54) / TS (57) where they
//        └▓▓▓ surge ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓┘   overlap: `prio` = the NWS priority, LOWER = more important = ON TOP
//
//   click → a popup listing EVERY product at that point, most important first, the first in a band:
//        ┌▓▓ Hurricane Watch ▓▓▓▓▓▓▓▓┐   band = a DEEP shade of the colour (38% — the warning tiles' and the
//        │ Tropical Storm Isaias     │   Outlook risk cards' rule), the name in the bright colour
//        │ + Storm Surge Watch       │   storm words (TropicalService, from NHC's ATCF list), then the rest
//        │ NWS Mobile                │
//        └───────────────────────────┘
//
// ⚠️ COLOURS ARE DATA, never themed — and they come FROM THE FILE (TropicalProducts.cs is the one table).
// ⚠️ A CLICK YIELDS to every smaller clickable mark (report dots, storm cells, surveys, discussions): a tropical
// zone is big and would swallow every click inside it.

import { firstBoundaryLayerId } from './layers.js';
// The popup is a <template> in map.html; its look is styles/components/map-popups.css.
import * as Templates from './templates.js';

const SOURCE = 'tropical-src';
const LAYERS = ['tropical-fill', 'tropical-line'];
const FILL_BASE = 0.35;
const YIELD_TO = ['spc-report-torn', 'spc-report-wind', 'spc-report-hail',
    'storm-cell-tvs', 'storm-cell-hail', 'storm-cell-meso', 'storm-cell-dot',
    'dat-point', 'dat-track-start', 'dat-track', 'dat-area-fill', 'md-fill'];

let fileUrl = null;
let data = null;
let kinds = [];          // shown product ids ("HU.A")
let timeMs = null;       // the moment to show; null = live (all in effect)
let opacity = 1;
let popup = null;
const bound = new WeakSet();

// Shown products AND (live, or in effect at the moment). No products = nothing.
function baseFilter() {
    if (!kinds.length) return false;
    const shown = ['match', ['get', 'pid'], kinds, true, false];
    if (timeMs == null) return shown;
    return ['all', shown,
        ['<=', ['to-number', ['get', 't0']], timeMs],
        ['>', ['to-number', ['get', 't1']], timeMs]];
}

function removeLayers(map) {
    LAYERS.slice().reverse().forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
    if (map.getSource(SOURCE)) map.removeSource(SOURCE);
}

function addLayers(map) {
    removeLayers(map);
    map.addSource(SOURCE, { type: 'geojson', data: data });
    const before = firstBoundaryLayerId(map);
    // sort-key: a HIGHER key draws on top, so the NWS priority (lower = more important) is negated.
    map.addLayer({ id: 'tropical-fill', type: 'fill', source: SOURCE, filter: baseFilter(),
        layout: { 'fill-sort-key': ['-', 0, ['to-number', ['get', 'prio']]] },
        paint: { 'fill-color': ['get', 'fill'], 'fill-opacity': FILL_BASE * opacity } }, before);
    map.addLayer({ id: 'tropical-line', type: 'line', source: SOURCE, filter: baseFilter(),
        layout: { 'line-join': 'round', 'line-sort-key': ['-', 0, ['to-number', ['get', 'prio']]] },
        paint: { 'line-color': ['get', 'fill'], 'line-width': 1.5, 'line-opacity': opacity } }, before);
    bind(map);
}

function refresh(map) {
    if (!data) { removeLayers(map); return; }
    if (!map.getSource(SOURCE)) addLayers(map);
    else LAYERS.forEach(function (id) { if (map.getLayer(id)) map.setFilter(id, baseFilter()); });
}

// --- Click → the popup ----------------------------------------------------------------------------

// "#FF00FF" → the same hue at 38% brightness (OutlookRiskCard.Deep's rule), so the bright colour reads ON it.
function deep(hex) {
    const m = /^#?([0-9a-f]{6})$/i.exec(hex || '');
    if (!m) return '#3a3a3a';
    const n = parseInt(m[1], 16);
    const c = function (v) { return ('0' + Math.round(v * 0.38).toString(16)).slice(-2); };
    return '#' + c(n >> 16 & 255) + c(n >> 8 & 255) + c(n & 255);
}

const OFFICES = { KLIX: 'New Orleans', KMOB: 'Mobile', KTAE: 'Tallahassee', KTBW: 'Tampa Bay', KMFL: 'Miami',
    KKEY: 'Key West', KMLB: 'Melbourne', KJAX: 'Jacksonville', KCHS: 'Charleston', KILM: 'Wilmington',
    KMHX: 'Newport/Morehead City', KAKQ: 'Wakefield', KLWX: 'Baltimore/Washington', KPHI: 'Mount Holly',
    KOKX: 'New York', KBOX: 'Boston', KGYX: 'Gray', KCAR: 'Caribou', KLCH: 'Lake Charles', KHGX: 'Houston',
    KCRP: 'Corpus Christi', KBRO: 'Brownsville', TJSJ: 'San Juan', PHFO: 'Honolulu', PGUM: 'Guam' };

function popupContent(hits) {
    // One line per PRODUCT at this point (several zones of one product can stack), most important first.
    const seen = {};
    const products = [];
    hits.forEach(function (h) {
        const p = h.properties;
        if (!seen[p.pid]) { seen[p.pid] = true; products.push(p); }
    });
    products.sort(function (a, b) { return (+a.prio) - (+b.prio); });
    const top = products[0];
    // <template id="tpl-tropical-popup"> + one tpl-tropical-more row per further product. The band's and the
    // chips' colours are DATA (the product's own colour), so they are the inline values.
    const el = Templates.clone('tpl-tropical-popup');
    const band = Templates.text(el, '.tropical-band', top.name);
    band.style.background = deep(top.fill);
    band.style.color = top.fill;
    Templates.text(el, '.tropical-storm', top.storm);
    const more = el.querySelector('.tropical-more-list');
    products.slice(1).forEach(function (p) {
        const row = Templates.clone('tpl-tropical-more');
        row.querySelector('.map-popup-chip').style.background = p.fill;
        row.querySelector('.tropical-more-name').textContent = p.name;
        more.appendChild(row);
    });
    const office = top.wfo ? (OFFICES[top.wfo] ? 'NWS ' + OFFICES[top.wfo] : top.wfo) : 'National Hurricane Center';
    Templates.text(el, '.map-popup-meta', office);
    return el;
}

function onClick(e) {
    const map = e.target;
    if (!map.getLayer('tropical-fill')) return;
    const theirs = YIELD_TO.filter(function (id) { return map.getLayer(id) && map.getLayoutProperty(id, 'visibility') !== 'none'; });
    if (theirs.length && map.queryRenderedFeatures(e.point, { layers: theirs }).length) return;
    const hits = map.queryRenderedFeatures(e.point, { layers: ['tropical-fill'] });
    if (!hits.length) return;
    if (!popup) popup = new maplibregl.Popup({ className: 'map-popup map-popup-banded', maxWidth: '300px' });
    popup.remove(); // one popup across every pane
    popup.setLngLat(e.lngLat).setDOMContent(popupContent(hits)).addTo(map);
}

function bind(map) {
    if (bound.has(map)) return;
    bound.add(map);
    map.on('click', onClick);
    map.on('mouseenter', 'tropical-fill', function () { map.getCanvas().style.cursor = 'pointer'; });
    map.on('mouseleave', 'tropical-fill', function () { map.getCanvas().style.cursor = ''; });
}

// --- Host commands --------------------------------------------------------------------------------

export function setSource(map, url) {
    // ⚠️ A LIVE refresh re-sends the SAME url (the file was rewritten): keep drawing until the new file lands.
    // A different url is a different window — drop the old one at once.
    if (url !== fileUrl) { data = null; removeLayers(map); }
    fileUrl = url;
    if (!url) return;
    const asked = url;
    fetch(url, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        if (fileUrl !== asked || !gj) return;
        data = gj;
        if (map.getSource(SOURCE)) map.getSource(SOURCE).setData(gj);
        refresh(map);
    }).catch(function (e) { console.error('tropical load failed: ' + e); });
}

export function setKinds(map, csv) {
    kinds = (csv || '').split(',').filter(function (s) { return s.length > 0; });
    refresh(map);
}

export function setTime(map, ms) {
    timeMs = ms == null ? null : +ms;
    refresh(map);
}

export function setOpacity(map, o) {
    opacity = Math.max(0, Math.min(1, +o || 0));
    if (map.getLayer('tropical-fill')) map.setPaintProperty('tropical-fill', 'fill-opacity', FILL_BASE * opacity);
    if (map.getLayer('tropical-line')) map.setPaintProperty('tropical-line', 'line-opacity', opacity);
}

export function clear(map) {
    fileUrl = null;
    data = null;
    if (popup) popup.remove();
    removeLayers(map);
}

export function reAdd(map) { refresh(map); }
