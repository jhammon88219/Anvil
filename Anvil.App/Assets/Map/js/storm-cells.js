// storm-cells.js — the radar's own storm-cell attributes for the LOADED site, one volume SCAN at a time:
// SCIT cell tracks (where the cell has been, where it is forecast), TDA tornado vortex signatures, MDA
// mesocyclones and HDA hail. The host writes every scan of the window into one file (StormCellService,
// tracks precomputed by StormCellTracks) and then only says WHICH scan to draw (setScan, by its time), so
// scrubbing the radar loop moves the cells with it. NowCast and PastCast. map.js's window.setStormCell*
// shims delegate here; reAddAll calls reAdd(map) after a basemap switch or on a new pane.
//
//                   ▼ TVS (red, filled; an ELEVATED TVS is the same triangle hollow)
//        ·──·──·──(●)──○────○────○────○     ● the cell now, id beneath      ○ forecast +15/30/45/60 min
//        past track   ◯  ◆ 1.75″            ◯ mesocyclone ring (rank 5+; thicker at 8+)
//                    H1                     ◆ severe hail (POSH ≥ 50%), max size beside it; bigger at 2″+
//
//                    ┌──────────────────────────────────┐
//         click ─────│ ● Cell H1 · TVS                  │  ← one popup for the cell, whichever mark
//                    │ Mesocyclone · rank 7             │    was clicked; a storm-report dot at the
//                    │ Hail 70% severe · 80% any · 1.50″│    same spot takes the click instead
//                    │ VIL 30 · 63 dBZ at 22.4 kft      │
//                    │ Top 22.4 kft · from 246° 21 kt   │
//                    │ Scan 19:51 UTC · KTLX            │
//                    └──────────────────────────────────┘
//
// ⚠️ THRESHOLDS ARE MIRRORED from C# StormCellThresholds (MESO_RANK, SEVERE_POSH) — the rows' counts are
// computed there and must describe what is drawn here. Change both.
// ⚠️ COLOURS ARE DATA, NOT THEME (TVS red = the warning red; meso yellow; hail cyan; tracks white on a dark
// casing, which reads on both basemaps). Written twice — here and the row swatches in StormCellsInput.xaml.
// ⚠️ THE ICONS ARE CANVAS-DRAWN IMAGES, and setStyle drops a map's images along with its layers, so
// addLayers re-registers them every time (ensureImages).

import { firstSymbolLayerId } from './layers.js';

const SOURCE = 'storm-cell-src';
const MESO_RANK = 5;          // ⚠️ mirrors StormCellThresholds.MesoRank
const SEVERE_POSH = 50;       // ⚠️ mirrors StormCellThresholds.SeverePosh
const BIG_HAIL_IN = 2;

const TVS_RED = '#ff2a2a';
const MESO_YELLOW = '#ffe14d';
const HAIL_CYAN = '#3fd0ff';
const TRACK_WHITE = '#ffffff';
const CASING = 'rgba(20,20,20,0.85)';

// Every layer, bottom to top, and which toggle owns it.
const LAYERS = [
    { id: 'storm-cell-fcst-casing', kind: 'tracks' },
    { id: 'storm-cell-fcst-line', kind: 'tracks' },
    { id: 'storm-cell-past-line', kind: 'tracks' },
    { id: 'storm-cell-past-dot', kind: 'tracks' },
    { id: 'storm-cell-fcst-dot', kind: 'tracks' },
    { id: 'storm-cell-dot', kind: 'tracks' },
    { id: 'storm-cell-meso', kind: 'meso' },
    { id: 'storm-cell-hail', kind: 'hail' },
    { id: 'storm-cell-tvs', kind: 'tvs' },
    { id: 'storm-cell-label', kind: 'tracks' }
];
const CLICK_LAYERS = ['storm-cell-tvs', 'storm-cell-hail', 'storm-cell-meso', 'storm-cell-dot'];
// Storm-report dots have their own popup; a click on one is theirs (the damage-survey convention).
const REPORT_LAYERS = ['spc-report-torn', 'spc-report-wind', 'spc-report-hail'];

let fileUrl = null;
let file = null;              // { site, scans: [{ t, cells: [...] }] }
let scanMs = null;            // the scan the host asked for (may arrive before the file)
let kinds = { tracks: false, tvs: false, meso: false, hail: false };
let opacity = 1;
let popup = null;
const interactionsBound = new WeakSet();

function anyShown() { return kinds.tracks || kinds.tvs || kinds.meso || kinds.hail; }
function vis(kind) { return kinds[kind] ? 'visible' : 'none'; }

function currentScan() {
    if (!file || scanMs == null) return null;
    for (let i = 0; i < file.scans.length; i++) if (file.scans[i].t === scanMs) return file.scans[i];
    return null;
}

// One scan → the GeoJSON the layers draw. `k` tags each feature for its layer's filter.
function scanGeojson(scan) {
    const features = [];
    (scan ? scan.cells : []).forEach(function (c) {
        const here = [c.lon, c.lat];
        if (c.past && c.past.length) {
            features.push({ type: 'Feature', properties: { k: 'past' }, geometry: { type: 'LineString', coordinates: c.past.concat([here]) } });
            c.past.forEach(function (p) { features.push({ type: 'Feature', properties: { k: 'pastpt' }, geometry: { type: 'Point', coordinates: p } }); });
        }
        if (c.fcst && c.fcst.length) {
            features.push({ type: 'Feature', properties: { k: 'fcst' }, geometry: { type: 'LineString', coordinates: [here].concat(c.fcst) } });
            c.fcst.forEach(function (p) { features.push({ type: 'Feature', properties: { k: 'fcstpt' }, geometry: { type: 'Point', coordinates: p } }); });
        }
        const props = Object.assign({ k: 'cell', t: scan.t, site: file.site }, c);
        delete props.past;
        delete props.fcst;
        features.push({ type: 'Feature', properties: props, geometry: { type: 'Point', coordinates: here } });
    });
    return { type: 'FeatureCollection', features: features };
}

// --- Icons ------------------------------------------------------------------------------------------

function icon(size, draw) {
    const ratio = 2;
    const cv = document.createElement('canvas');
    cv.width = cv.height = size * ratio;
    const g = cv.getContext('2d');
    g.scale(ratio, ratio);
    draw(g, size);
    return { image: g.getImageData(0, 0, size * ratio, size * ratio), ratio: ratio };
}

function triangleDown(g, s, fill) {
    g.beginPath();
    g.moveTo(2, 3); g.lineTo(s - 2, 3); g.lineTo(s / 2, s - 2); g.closePath();
    g.lineJoin = 'round';
    g.lineWidth = 3; g.strokeStyle = CASING; g.stroke();
    if (fill) { g.fillStyle = TVS_RED; g.fill(); }
    g.lineWidth = fill ? 1 : 2; g.strokeStyle = TVS_RED; g.stroke();
}

function diamond(g, s) {
    g.beginPath();
    g.moveTo(s / 2, 1.5); g.lineTo(s - 1.5, s / 2); g.lineTo(s / 2, s - 1.5); g.lineTo(1.5, s / 2); g.closePath();
    g.fillStyle = HAIL_CYAN; g.fill();
    g.lineWidth = 1.5; g.strokeStyle = CASING; g.stroke();
}

const ICONS = {
    'storm-cell-icon-tvs': function () { return icon(20, function (g, s) { triangleDown(g, s, true); }); },
    'storm-cell-icon-etvs': function () { return icon(20, function (g, s) { triangleDown(g, s, false); }); },
    'storm-cell-icon-hail': function () { return icon(13, diamond); },
    'storm-cell-icon-hail-big': function () { return icon(18, diamond); }
};

function ensureImages(map) {
    Object.keys(ICONS).forEach(function (id) {
        if (map.hasImage(id)) return;
        const made = ICONS[id]();
        map.addImage(id, made.image, { pixelRatio: made.ratio });
    });
}

// --- Layers -----------------------------------------------------------------------------------------

function removeLayers(map) {
    LAYERS.slice().reverse().forEach(function (l) { if (map.getLayer(l.id)) map.removeLayer(l.id); });
    if (map.getSource(SOURCE)) map.removeSource(SOURCE);
}

function layersPresent(map) { return !!(map.getSource(SOURCE) && map.getLayer(LAYERS[0].id)); }

function isK(k) { return ['==', ['get', 'k'], k]; }

function addLayers(map) {
    removeLayers(map);
    ensureImages(map);
    map.addSource(SOURCE, { type: 'geojson', data: scanGeojson(currentScan()) });
    const before = firstSymbolLayerId(map);
    const isCell = isK('cell');

    map.addLayer({
        id: 'storm-cell-fcst-casing', type: 'line', source: SOURCE, filter: isK('fcst'),
        layout: { visibility: vis('tracks'), 'line-cap': 'round' },
        paint: { 'line-color': CASING, 'line-width': 3.5, 'line-opacity': opacity }
    }, before);
    map.addLayer({
        id: 'storm-cell-fcst-line', type: 'line', source: SOURCE, filter: isK('fcst'),
        layout: { visibility: vis('tracks'), 'line-cap': 'round' },
        paint: { 'line-color': TRACK_WHITE, 'line-width': 1.5, 'line-opacity': opacity }
    }, before);
    map.addLayer({
        id: 'storm-cell-past-line', type: 'line', source: SOURCE, filter: isK('past'),
        layout: { visibility: vis('tracks'), 'line-cap': 'round', 'line-join': 'round' },
        paint: { 'line-color': TRACK_WHITE, 'line-width': 1.25, 'line-opacity': 0.6 * opacity }
    }, before);
    map.addLayer({
        id: 'storm-cell-past-dot', type: 'circle', source: SOURCE, filter: isK('pastpt'),
        layout: { visibility: vis('tracks') },
        paint: { 'circle-color': TRACK_WHITE, 'circle-radius': 1.75, 'circle-opacity': 0.7 * opacity }
    }, before);
    map.addLayer({
        id: 'storm-cell-fcst-dot', type: 'circle', source: SOURCE, filter: isK('fcstpt'),
        layout: { visibility: vis('tracks') },
        paint: {
            'circle-color': CASING, 'circle-radius': 2.75, 'circle-opacity': opacity,
            'circle-stroke-color': TRACK_WHITE, 'circle-stroke-width': 1.25, 'circle-stroke-opacity': opacity
        }
    }, before);
    map.addLayer({
        id: 'storm-cell-dot', type: 'circle', source: SOURCE, filter: isCell,
        layout: { visibility: vis('tracks') },
        paint: {
            'circle-color': TRACK_WHITE, 'circle-radius': 3.5, 'circle-opacity': opacity,
            'circle-stroke-color': CASING, 'circle-stroke-width': 1.5, 'circle-stroke-opacity': opacity
        }
    }, before);
    map.addLayer({
        id: 'storm-cell-meso', type: 'circle', source: SOURCE,
        filter: ['all', isCell, ['>=', ['get', 'meso'], MESO_RANK]],
        layout: { visibility: vis('meso') },
        paint: {
            'circle-color': 'rgba(0,0,0,0)', 'circle-radius': 10,
            'circle-stroke-color': MESO_YELLOW,
            'circle-stroke-width': ['case', ['>=', ['get', 'meso'], 8], 3.5, 2],
            'circle-stroke-opacity': opacity
        }
    }, before);
    map.addLayer({
        id: 'storm-cell-hail', type: 'symbol', source: SOURCE,
        filter: ['all', isCell, ['>=', ['get', 'posh'], SEVERE_POSH]],
        layout: {
            visibility: vis('hail'),
            'icon-image': ['case', ['>=', ['get', 'size'], BIG_HAIL_IN], 'storm-cell-icon-hail-big', 'storm-cell-icon-hail'],
            'icon-offset': [15, 0], 'icon-allow-overlap': true, 'icon-ignore-placement': true,
            'text-field': ['concat', ['number-format', ['get', 'size'], { 'min-fraction-digits': 2, 'max-fraction-digits': 2 }], '″'],
            'text-font': ['Noto Sans Medium'], 'text-size': 10, 'text-anchor': 'left', 'text-offset': [2.3, 0],
            'text-allow-overlap': true, 'text-ignore-placement': true
        },
        paint: {
            'icon-opacity': opacity, 'text-opacity': opacity,
            'text-color': HAIL_CYAN, 'text-halo-color': CASING, 'text-halo-width': 1.25
        }
    }, before);
    map.addLayer({
        id: 'storm-cell-tvs', type: 'symbol', source: SOURCE,
        filter: ['all', isCell, ['!=', ['get', 'tvs'], '']],
        layout: {
            visibility: vis('tvs'),
            'icon-image': ['case', ['==', ['get', 'tvs'], 'ETVS'], 'storm-cell-icon-etvs', 'storm-cell-icon-tvs'],
            'icon-offset': [0, -15], 'icon-allow-overlap': true, 'icon-ignore-placement': true
        },
        paint: { 'icon-opacity': opacity }
    }, before);
    map.addLayer({
        id: 'storm-cell-label', type: 'symbol', source: SOURCE, filter: isCell, minzoom: 6.5,
        layout: {
            visibility: vis('tracks'),
            'text-field': ['get', 'id'], 'text-font': ['Noto Sans Medium'], 'text-size': 10,
            'text-anchor': 'top', 'text-offset': [0, 0.9],
            'text-allow-overlap': true, 'text-ignore-placement': true
        },
        paint: { 'text-color': TRACK_WHITE, 'text-halo-color': CASING, 'text-halo-width': 1.25, 'text-opacity': opacity }
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
function utcClock(ms) { const d = new Date(ms); return pad2(d.getUTCHours()) + ':' + pad2(d.getUTCMinutes()); }

function popupHtml(p) {
    const tvs = p.tvs ? (p.tvs === 'ETVS' ? 'Elevated TVS' : 'TVS') : '';
    const chip = p.tvs ? TVS_RED : p.meso >= MESO_RANK ? MESO_YELLOW : p.posh >= SEVERE_POSH ? HAIL_CYAN : TRACK_WHITE;
    const lines = [];
    if (p.meso > 0) lines.push((p.meso >= MESO_RANK ? 'Mesocyclone' : 'Weak circulation') + ' · rank ' + p.meso);
    if (p.poh > 0) lines.push('Hail ' + p.posh + '% severe · ' + p.poh + '% any' + (p.size > 0 ? ' · ' + Number(p.size).toFixed(2) + '″' : ''));
    const core = [];
    if (p.vil) core.push('VIL ' + p.vil);
    if (p.dbz) core.push(p.dbz + ' dBZ' + (p.dbzh ? ' at ' + p.dbzh + ' kft' : ''));
    if (core.length) lines.push(core.join(' · '));
    const motion = p.kt > 0 ? 'from ' + p.dir + '° at ' + p.kt + ' kt' : 'new cell, no motion yet';
    lines.push((p.top ? 'Top ' + p.top + ' kft · ' : '') + motion);
    let html = '<div class="cell-popup-title"><span class="cell-popup-chip" style="background:' + chip + '"></span>'
        + esc('Cell ' + p.id + (tvs ? ' · ' + tvs : '')) + '</div>';
    html += '<div class="cell-popup-meta">' + lines.map(esc).join('<br>') + '</div>';
    html += '<div class="cell-popup-src">Scan ' + esc(utcClock(p.t)) + ' UTC · ' + esc(p.site) + ' · NWS storm algorithms via IEM</div>';
    return html;
}

function ensurePopupStyle() {
    if (document.getElementById('cell-popup-style')) return;
    // The popup SURFACE is chrome (theme.css vars, like the report and survey popups); the chip is data.
    const css = [
        '.cell-popup .maplibregl-popup-content{background:var(--anvil-popup-bg);color:var(--anvil-popup-text);',
        'font:12px/1.45 "Segoe UI",system-ui,sans-serif;border-radius:8px;padding:9px 12px;',
        'box-shadow:0 4px 16px rgba(0,0,0,0.5);max-width:300px;}',
        '.cell-popup .maplibregl-popup-tip{border-top-color:var(--anvil-popup-bg);border-bottom-color:var(--anvil-popup-bg);}',
        '.cell-popup .maplibregl-popup-close-button{color:var(--anvil-popup-close);font-size:15px;padding:0 4px;}',
        '.cell-popup-title{font-weight:600;margin-bottom:3px;}',
        '.cell-popup-chip{display:inline-block;width:10px;height:10px;border-radius:5px;margin-right:6px;',
        'vertical-align:-1px;box-shadow:0 0 0 1px rgba(20,20,20,0.85);}',
        '.cell-popup-meta{color:var(--anvil-popup-meta);}',
        '.cell-popup-src{color:var(--anvil-popup-meta);font-size:10px;margin-top:5px;}'
    ].join('');
    const st = document.createElement('style');
    st.id = 'cell-popup-style';
    st.textContent = css;
    document.head.appendChild(st);
}

function presentVisible(map, ids) {
    return ids.filter(function (id) { return map.getLayer(id) && map.getLayoutProperty(id, 'visibility') !== 'none'; });
}

// ⚠️ damage-surveys.js yields its click to these ids (a copy of CLICK_LAYERS there) — change both.
function onClick(e) {
    const map = e.target;
    const reports = presentVisible(map, REPORT_LAYERS);
    if (reports.length && map.queryRenderedFeatures(e.point, { layers: reports }).length) return;
    const ids = presentVisible(map, CLICK_LAYERS);
    if (!ids.length) return;
    const hits = map.queryRenderedFeatures(e.point, { layers: ids });
    if (!hits.length) return;
    ensurePopupStyle();
    if (!popup) popup = new maplibregl.Popup({ className: 'cell-popup', maxWidth: '300px' });
    popup.remove(); // one popup across every pane
    popup.setLngLat(hits[0].geometry.coordinates).setHTML(popupHtml(hits[0].properties)).addTo(map);
}

function bindInteractions(map) {
    if (interactionsBound.has(map)) return;
    interactionsBound.add(map);
    map.on('click', onClick);
    CLICK_LAYERS.forEach(function (id) {
        map.on('mouseenter', id, function () { map.getCanvas().style.cursor = 'pointer'; });
        map.on('mouseleave', id, function () { map.getCanvas().style.cursor = ''; });
    });
}

function closePopup() { if (popup) popup.remove(); }

// --- State -> layers --------------------------------------------------------------------------------

function refreshLayers(map) {
    if (!file || !anyShown()) { removeLayers(map); return; }
    if (!layersPresent(map)) { addLayers(map); return; }
    LAYERS.forEach(function (l) {
        if (map.getLayer(l.id)) map.setLayoutProperty(l.id, 'visibility', vis(l.kind));
    });
}

function redrawScan(map) {
    if (map.getSource(SOURCE)) map.getSource(SOURCE).setData(scanGeojson(currentScan()));
}

function load(map) {
    if (!fileUrl) return;
    const url = fileUrl;
    fetch(url, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (j) {
        if (fileUrl !== url) return; // a newer window won
        if (j && j.scans) file = j;
        refreshLayers(map);
        redrawScan(map);
    }).catch(function (e) { console.error('storm cells load failed: ' + e); });
}

export function setSource(map, url) {
    // ⚠️ A LIVE refresh re-sends the SAME url (the file was rewritten), so keep the old scans drawn until
    // the new file lands — clearing here would blink the cells every two minutes.
    const sameUrl = url === fileUrl;
    fileUrl = url;
    if (!sameUrl) { file = null; closePopup(); removeLayers(map); }
    load(map);
}

export function setScan(map, ms) {
    scanMs = ms == null ? null : +ms;
    // A cell's popup describes ONE scan; stepping the loop makes it stale.
    closePopup();
    redrawScan(map);
}

export function setKinds(map, tracks, tvs, meso, hail) {
    kinds = { tracks: !!tracks, tvs: !!tvs, meso: !!meso, hail: !!hail };
    refreshLayers(map);
}

export function setOpacity(map, o) {
    opacity = Math.max(0, Math.min(1, +o || 0));
    function paint(id, prop, v) { if (map.getLayer(id)) map.setPaintProperty(id, prop, v); }
    paint('storm-cell-fcst-casing', 'line-opacity', opacity);
    paint('storm-cell-fcst-line', 'line-opacity', opacity);
    paint('storm-cell-past-line', 'line-opacity', 0.6 * opacity);
    paint('storm-cell-past-dot', 'circle-opacity', 0.7 * opacity);
    ['storm-cell-fcst-dot', 'storm-cell-dot'].forEach(function (id) {
        paint(id, 'circle-opacity', opacity);
        paint(id, 'circle-stroke-opacity', opacity);
    });
    paint('storm-cell-meso', 'circle-stroke-opacity', opacity);
    paint('storm-cell-hail', 'icon-opacity', opacity);
    paint('storm-cell-hail', 'text-opacity', opacity);
    paint('storm-cell-tvs', 'icon-opacity', opacity);
    paint('storm-cell-label', 'text-opacity', opacity);
}

export function clear(map) {
    fileUrl = null;
    file = null;
    scanMs = null;
    closePopup();
    removeLayers(map);
}

// Re-add after a basemap switch / onto a new pane (the file is still in memory).
export function reAdd(map) {
    refreshLayers(map);
}
