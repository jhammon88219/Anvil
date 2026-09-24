// damage-surveys.js — NWS Damage Assessment Toolkit (DAT) tornado surveys for the LOADED replay window:
// the EF-contour damage POLYGONS some offices draw, the track CENTERLINES every surveyed tornado gets, and
// the individual survey POINTS. One GeoJSON source (the host writes it per window, features tagged
// `layer` = area/track/point), one toggle per kind. PastCast only. map.js's window.setDamageSurvey* shims
// delegate here; reAddAll calls reAdd(map) after a basemap switch or on a new pane.
//
//              ░░░░░░░▒▒▒▒▒▓▓▓▒▒▒░░░░░          ░ ▒ ▓ = nested EF polygons (area), lowest EF drawn first
//     ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━     ━ = track centerline (track), EF colour on a dark casing
//              ░░░░░░░▒▒▒▒▒▓▓▓▒▒▒░░░░░          · = survey damage point (point)
//                ·   ·  ·    ·   ·
//                    ┌─────────────────────────────┐
//         click ─────│ EF4 tornado · 180 mph       │  ← one popup, top-most kind wins
//                    │ Elgin · TSA                 │    (point > track > area); a storm-report
//                    │ 2024-05-07 02:12–03:07 UTC  │    dot at the same spot takes the click
//                    │ 40.8 mi · 1,700 yd wide     │    instead (it has its own popup)
//                    │ 2 deaths · 33 injured       │
//                    │ The tornado developed …     │
//                    └─────────────────────────────┘
//
// ⚠️ COLOURS ARE DATA, NOT THEME: the EF palette is the DAT viewer's own polygon renderer (read from the
// FeatureServer's drawingInfo, 2026-09-24). Tracks and points reuse it, so one rating is one colour here.
// It is written twice — here and the swatches in PastCastTab.xaml. Change both.
// ⚠️ Z-ORDER: beneath the place labels (firstSymbolLayerId), so it lands above the outlook, the radar and
// the watch/warning fills, and below the storm-report dots (which sit above everything).

import { firstSymbolLayerId } from './layers.js';

const SOURCE = 'dat-surveys';

// DAT polygon renderer colours (FeatureServer/2 drawingInfo). EF3+ is its own class there, same colour as EF3.
const EF_COLORS = {
    'EF0': '#00ffc5',
    'EF1': '#55ff00',
    'EF2': '#ffff00',
    'EF3': '#e69800',
    'EF3+': '#e69800',
    'EF4': '#e60000',
    'EF5': '#a80084',
    'EFU': '#9c9c9c',
    'N/A': '#bee8ff'
};
const EF_COLOR_EXPR = ['match', ['get', 'ef'],
    'EF0', EF_COLORS['EF0'], 'EF1', EF_COLORS['EF1'], 'EF2', EF_COLORS['EF2'],
    'EF3', EF_COLORS['EF3'], 'EF3+', EF_COLORS['EF3+'], 'EF4', EF_COLORS['EF4'],
    'EF5', EF_COLORS['EF5'], 'EFU', EF_COLORS['EFU'],
    EF_COLORS['N/A']];
const DAT_OUTLINE = '#6e6e6e';                 // the DAT renderer's polygon outline
const CASING = 'rgba(20,20,20,0.85)';          // data mark, not chrome — same rule as the report dots

// Every layer, bottom to top, and which toggle owns it.
const LAYERS = [
    { id: 'dat-area-fill', kind: 'areas' },
    { id: 'dat-area-line', kind: 'areas' },
    { id: 'dat-track-casing', kind: 'tracks' },
    { id: 'dat-track', kind: 'tracks' },
    { id: 'dat-point', kind: 'points' }
];
// Click priority: the smallest mark wins, so a point on a track on a polygon is still reachable.
const CLICK_ORDER = ['dat-point', 'dat-track', 'dat-area-fill'];
// Storm-report dots have their own popup; a click on one is theirs.
const REPORT_LAYERS = ['spc-report-torn', 'spc-report-wind', 'spc-report-hail'];

let surveysUrl = null;
let surveysData = null;
let kinds = { areas: false, tracks: false, points: false };
let opacity = 0.7;
let popup = null;
const interactionsBound = new WeakSet();

function anyShown() { return kinds.areas || kinds.tracks || kinds.points; }

// Tracks and outlines stay stronger than the fill so a path reads through its own polygons.
function lineOpacity() { return Math.min(1, opacity + 0.3); }

function removeLayers(map) {
    LAYERS.slice().reverse().forEach(function (l) { if (map.getLayer(l.id)) map.removeLayer(l.id); });
    if (map.getSource(SOURCE)) map.removeSource(SOURCE);
}

function layersPresent(map) { return !!(map.getSource(SOURCE) && map.getLayer(LAYERS[0].id)); }

function vis(kind) { return kinds[kind] ? 'visible' : 'none'; }

function addLayers(map) {
    if (!surveysData) return;
    removeLayers(map);
    map.addSource(SOURCE, { type: 'geojson', data: surveysData });
    const before = firstSymbolLayerId(map);
    const isArea = ['==', ['get', 'layer'], 'area'];
    const isTrack = ['==', ['get', 'layer'], 'track'];
    const isPoint = ['==', ['get', 'layer'], 'point'];

    map.addLayer({
        id: 'dat-area-fill', type: 'fill', source: SOURCE, filter: isArea,
        // Higher EF on top — the host also writes them in that order; this makes it hold within a tile.
        layout: { visibility: vis('areas'), 'fill-sort-key': ['get', 'efn'] },
        paint: { 'fill-color': EF_COLOR_EXPR, 'fill-opacity': opacity }
    }, before);
    map.addLayer({
        id: 'dat-area-line', type: 'line', source: SOURCE, filter: isArea,
        layout: { visibility: vis('areas') },
        paint: { 'line-color': DAT_OUTLINE, 'line-width': 1, 'line-opacity': lineOpacity() }
    }, before);
    map.addLayer({
        id: 'dat-track-casing', type: 'line', source: SOURCE, filter: isTrack,
        layout: { visibility: vis('tracks'), 'line-cap': 'round', 'line-join': 'round' },
        paint: {
            'line-color': CASING,
            'line-width': ['interpolate', ['linear'], ['zoom'], 4, 3, 8, 4.5, 12, 7],
            'line-opacity': lineOpacity()
        }
    }, before);
    map.addLayer({
        id: 'dat-track', type: 'line', source: SOURCE, filter: isTrack,
        layout: { visibility: vis('tracks'), 'line-cap': 'round', 'line-join': 'round', 'line-sort-key': ['get', 'efn'] },
        paint: {
            'line-color': EF_COLOR_EXPR,
            'line-width': ['interpolate', ['linear'], ['zoom'], 4, 1.5, 8, 2.5, 12, 4.5],
            'line-opacity': lineOpacity()
        }
    }, before);
    map.addLayer({
        id: 'dat-point', type: 'circle', source: SOURCE, filter: isPoint,
        layout: { visibility: vis('points'), 'circle-sort-key': ['get', 'efn'] },
        paint: {
            'circle-color': EF_COLOR_EXPR,
            'circle-radius': ['interpolate', ['linear'], ['zoom'], 6, 2, 10, 4, 14, 6],
            'circle-opacity': opacity,
            'circle-stroke-width': 0.75,
            'circle-stroke-color': CASING,
            'circle-stroke-opacity': opacity
        }
    }, before);
    bindInteractions(map);
}

// --- Popup ------------------------------------------------------------------------------------------

function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
}

function pad2(n) { return (n < 10 ? '0' : '') + n; }
function utcDate(ms) { const d = new Date(ms); return d.getUTCFullYear() + '-' + pad2(d.getUTCMonth() + 1) + '-' + pad2(d.getUTCDate()); }
function utcClock(ms) { const d = new Date(ms); return pad2(d.getUTCHours()) + ':' + pad2(d.getUTCMinutes()); }

// "2024-05-07 02:12–03:07 UTC", or one instant when start = end.
function spanText(t0, t1) {
    if (!t0) return '';
    if (!t1 || t1 <= t0) return utcDate(t0) + ' ' + utcClock(t0) + ' UTC';
    const sameDay = utcDate(t0) === utcDate(t1);
    return utcDate(t0) + ' ' + utcClock(t0) + '–' + (sameDay ? '' : utcDate(t1) + ' ') + utcClock(t1) + ' UTC';
}

function num(n) { return Number(n).toLocaleString('en-US'); }

// Path length (miles) and max width (yards), as the NWS publishes them.
function sizeText(p) {
    const parts = [];
    if (p.len) parts.push(num(p.len) + ' mi');
    if (p.wid) parts.push(num(p.wid) + ' yd wide');
    return parts.join(' · ');
}

function casualtyText(p) {
    const parts = [];
    if (p.fat) parts.push(p.fat + (p.fat === 1 ? ' death' : ' deaths'));
    if (p.inj) parts.push(p.inj + ' injured');
    return parts.join(' · ');
}

function efLabel(ef) { return ef === 'N/A' ? 'Unrated' : ef; }

function popupHtml(p) {
    const color = EF_COLORS[p.ef] || EF_COLORS['N/A'];
    let title;
    const meta = [];
    const where = [p.name, p.wfo].filter(Boolean).join(' · ');
    if (p.layer === 'point') {
        title = efLabel(p.ef) + ' damage' + (p.wind ? ' · ' + p.wind + ' mph' : '');
        if (p.dmg) meta.push(esc(p.dmg));
        if (p.dod) meta.push(esc(p.dod));
        if (where) meta.push(esc(where));
        meta.push(esc(spanText(p.t0, p.t1)));
    } else if (p.layer === 'track') {
        title = efLabel(p.ef) + ' tornado' + (p.wind ? ' · ' + p.wind + ' mph' : '');
        if (where) meta.push(esc(where));
        meta.push(esc(spanText(p.t0, p.t1)));
        if (sizeText(p)) meta.push(esc(sizeText(p)));
        if (casualtyText(p)) meta.push(esc(casualtyText(p)));
    } else {
        title = efLabel(p.ef) + ' damage area';
        // A polygon linked to its track carries the tornado's rating and numbers — say whose they are.
        if (p.tef) meta.push(esc('Part of ' + efLabel(p.tef) + ' tornado' + (p.wind ? ' · ' + p.wind + ' mph peak' : '')));
        if (where) meta.push(esc(where));
        meta.push(esc(spanText(p.t0, p.t1)));
        if (p.tef && sizeText(p)) meta.push(esc(sizeText(p)));
        if (p.tef && casualtyText(p)) meta.push(esc(casualtyText(p)));
    }
    let html = '<div class="dat-popup-title"><span class="dat-popup-chip" style="background:' + color + '"></span>' + esc(title) + '</div>';
    const lines = meta.filter(Boolean);
    if (lines.length) html += '<div class="dat-popup-meta">' + lines.join('<br>') + '</div>';
    if (p.com) html += '<div class="dat-popup-com">' + esc(p.com) + '</div>';
    html += '<div class="dat-popup-src">NWS Damage Assessment Toolkit</div>';
    return html;
}

function ensurePopupStyle() {
    if (document.getElementById('dat-popup-style')) return;
    // The popup SURFACE is chrome (theme.css vars, like the report popup); the EF chip is data.
    const css = [
        '.dat-popup .maplibregl-popup-content{background:var(--anvil-popup-bg);color:var(--anvil-popup-text);',
        'font:12px/1.45 "Segoe UI",system-ui,sans-serif;border-radius:8px;padding:9px 12px;',
        'box-shadow:0 4px 16px rgba(0,0,0,0.5);max-width:300px;}',
        '.dat-popup .maplibregl-popup-tip{border-top-color:var(--anvil-popup-bg);border-bottom-color:var(--anvil-popup-bg);}',
        '.dat-popup .maplibregl-popup-close-button{color:var(--anvil-popup-close);font-size:15px;padding:0 4px;}',
        '.dat-popup-title{font-weight:600;margin-bottom:3px;}',
        '.dat-popup-chip{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px;',
        'vertical-align:-1px;box-shadow:0 0 0 1px rgba(20,20,20,0.85);}',
        '.dat-popup-meta{color:var(--anvil-popup-meta);margin-bottom:4px;}',
        '.dat-popup-com{color:var(--anvil-popup-body);max-height:160px;overflow:auto;}',
        '.dat-popup-src{color:var(--anvil-popup-meta);font-size:10px;margin-top:5px;}'
    ].join('');
    const st = document.createElement('style');
    st.id = 'dat-popup-style';
    st.textContent = css;
    document.head.appendChild(st);
}

function presentVisible(map, ids) {
    return ids.filter(function (id) { return map.getLayer(id) && map.getLayoutProperty(id, 'visibility') !== 'none'; });
}

// ONE map-level click (not per-layer): overlapping kinds would otherwise each fire and fight over the popup.
function onClick(e) {
    const map = e.target;
    const reports = presentVisible(map, REPORT_LAYERS);
    if (reports.length && map.queryRenderedFeatures(e.point, { layers: reports }).length) return;
    const ids = presentVisible(map, CLICK_ORDER);
    if (!ids.length) return;
    const hits = map.queryRenderedFeatures(e.point, { layers: ids });
    if (!hits.length) return;
    // Priority by layer, then highest EF within it.
    hits.sort(function (a, b) {
        const d = CLICK_ORDER.indexOf(a.layer.id) - CLICK_ORDER.indexOf(b.layer.id);
        return d !== 0 ? d : (b.properties.efn || 0) - (a.properties.efn || 0);
    });
    ensurePopupStyle();
    if (!popup) popup = new maplibregl.Popup({ className: 'dat-popup', maxWidth: '300px' });
    popup.remove(); // one popup across every pane — detach it from wherever it was
    popup.setLngLat(e.lngLat).setHTML(popupHtml(hits[0].properties)).addTo(map);
}

function bindInteractions(map) {
    if (interactionsBound.has(map)) return;
    interactionsBound.add(map);
    map.on('click', onClick);
    CLICK_ORDER.forEach(function (id) {
        map.on('mouseenter', id, function () { map.getCanvas().style.cursor = 'pointer'; });
        map.on('mouseleave', id, function () { map.getCanvas().style.cursor = ''; });
    });
}

function closePopup() { if (popup) popup.remove(); }

// --- State -> layers --------------------------------------------------------------------------------

function refreshLayers(map) {
    if (!surveysData || !anyShown()) { removeLayers(map); return; }
    if (!layersPresent(map)) addLayers(map);
    LAYERS.forEach(function (l) {
        if (map.getLayer(l.id)) map.setLayoutProperty(l.id, 'visibility', vis(l.kind));
    });
}

// The window file is written once per load, but fetch no-store anyway: a recent day's surveys are
// re-fetched and the same window's file rewritten.
function loadSurveys(map) {
    if (!surveysUrl) return;
    const url = surveysUrl;
    fetch(url, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        if (surveysUrl !== url) return; // a newer window won
        if (gj) surveysData = gj;
        if (gj && map.getSource(SOURCE)) map.getSource(SOURCE).setData(gj);
        refreshLayers(map);
    }).catch(function (e) { console.error('damage surveys load failed: ' + e); });
}

export function setSource(map, url) {
    surveysUrl = url;
    surveysData = null;
    closePopup();
    removeLayers(map); // the previous window's features must not linger while the new file loads
    if (anyShown()) loadSurveys(map);
}

export function setKinds(map, areas, tracks, points) {
    kinds = { areas: !!areas, tracks: !!tracks, points: !!points };
    if (anyShown() && !surveysData) loadSurveys(map);
    else refreshLayers(map);
}

export function setOpacity(map, o) {
    opacity = Math.max(0, Math.min(1, +o || 0));
    if (map.getLayer('dat-area-fill')) map.setPaintProperty('dat-area-fill', 'fill-opacity', opacity);
    if (map.getLayer('dat-area-line')) map.setPaintProperty('dat-area-line', 'line-opacity', lineOpacity());
    if (map.getLayer('dat-track-casing')) map.setPaintProperty('dat-track-casing', 'line-opacity', lineOpacity());
    if (map.getLayer('dat-track')) map.setPaintProperty('dat-track', 'line-opacity', lineOpacity());
    if (map.getLayer('dat-point')) {
        map.setPaintProperty('dat-point', 'circle-opacity', opacity);
        map.setPaintProperty('dat-point', 'circle-stroke-opacity', opacity);
    }
}

export function clear(map) {
    surveysUrl = null;
    surveysData = null;
    kinds = { areas: false, tracks: false, points: false };
    closePopup();
    removeLayers(map);
}

// Re-add after a basemap switch / onto a new pane (the data is still in memory).
export function reAdd(map) {
    refreshLayers(map);
}
