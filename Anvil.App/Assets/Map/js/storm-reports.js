// storm-reports.js — SPC storm-report dots (the filtered Tornado / Wind / Hail reports SPC verifies its
// outlooks against). One GeoJSON point source, three circle layers (one per type, so each toggles
// independently), colored to SPC's convention: tornado = red, wind = blue, hail = green. Rendered ON TOP of
// everything (no beforeId) so the dots read against the outlook fill + radar beneath them. Loaded LAZILY —
// only fetched when a type is first shown. map.js's window.setStormReports* shims delegate here; applyStyle
// calls reAdd(map) after a basemap switch (setStyle drops the layers; the fetched data stays in memory).
//
//        ●  ● ●        ● = one report, one circle layer per type so each toggles alone:
//      ●  ●    ●            red ● tornado   ·   blue ● wind   ·   green ● hail
//        ●  ●         ┌──────────────────────────┐
//          ●  ●───────│ Hail · 2.00"             │  ← click a dot: a pure-MapLibre popup, no host
//       ●     ●       │ 3 NE Norman, OK          │    round-trip. Everything in it comes from the
//                     │ 2013-05-20 20:14 UTC     │    feature's own props (time/mag/loc/county/st/com)
//                     │ REPORTED BY TRAINED …    │
//                     └──────────────────────────┘
//
// Drawn ON TOP of everything on purpose: these are the verification dots you read AGAINST the outlook
// fill and the radar beneath them, so they must never be tinted by either.

let reportsUrl = null;
let reportsData = null;
// Per-type visibility, driven by the card's Tornado / Wind / Hail checkboxes.
let kinds = { torn: false, wind: false, hail: false };
let reportsOpacity = 0.9;
let popup = null;            // single reusable click popup (created lazily)
// Click/hover handlers are bound once per layer id (they survive re-adds, since they dispatch by
// layer id at event time). Keyed by MAP, not a single flag: in multi-pane every pane draws these
// layers, so each needs its own handlers — one shared flag meant only the first pane ever bound,
// and a dot clicked in any other pane did nothing.
const interactionsBound = new WeakSet();

// SPC storm-report colors + labels (matching the SPC storm-reports pages / verification graphics).
const KIND_LAYERS = [
    { kind: 'torn', id: 'spc-report-torn', color: '#e51919', label: 'Tornado' }, // red
    { kind: 'wind', id: 'spc-report-wind', color: '#1663d8', label: 'Wind' },    // blue
    { kind: 'hail', id: 'spc-report-hail', color: '#18a020', label: 'Hail' },    // green
];
const META = KIND_LAYERS.reduce(function (m, l) { m[l.kind] = l; return m; }, {});

function anyShown() { return kinds.torn || kinds.wind || kinds.hail; }

function removeReportLayers(map) {
    KIND_LAYERS.forEach(function (l) { if (map.getLayer(l.id)) map.removeLayer(l.id); });
    if (map.getSource('spc-reports')) map.removeSource('spc-reports');
}

function layersPresent(map) {
    return !!(map.getSource('spc-reports') && map.getLayer(KIND_LAYERS[0].id));
}

function addReportLayers(map) {
    if (!reportsData) return;
    removeReportLayers(map);
    map.addSource('spc-reports', { type: 'geojson', data: reportsData });
    // One small circle layer per type. Radius scales gently with zoom so dots stay legible zoomed way out
    // (a big outbreak covers CONUS) yet don't blob together zoomed in. A thin dark stroke keeps them crisp
    // over bright outlook fills. Each layer's visibility follows its type toggle.
    KIND_LAYERS.forEach(function (l) {
        map.addLayer({
            id: l.id,
            type: 'circle',
            source: 'spc-reports',
            filter: ['==', ['get', 'kind'], l.kind],
            layout: { visibility: kinds[l.kind] ? 'visible' : 'none' },
            paint: {
                'circle-color': l.color,
                'circle-radius': ['interpolate', ['linear'], ['zoom'], 3, 3, 7, 5, 10, 7],
                'circle-opacity': reportsOpacity,
                'circle-stroke-width': 1,
                // ⚠️ Deliberately NOT themed, unlike the popup surface below. This ring is part of the
                // DATA mark — it separates a bright dot from whatever it lands on — and a dark ring
                // does that job on a light basemap just as well as on a dark one.
                'circle-stroke-color': 'rgba(20,20,20,0.85)',
                'circle-stroke-opacity': reportsOpacity
            }
        });
    });
    bindInteractions(map);
}

// --- Click popup (report details) ------------------------------------------------------------------
// Each report feature carries its SPC fields (time, magnitude, place, comments); clicking a dot opens a
// popup showing them. Pure MapLibre — no host round-trip. The layer-scoped click/hover handlers are bound
// ONCE per layer id and survive removeLayer/addLayer (MapLibre resolves them against the live layers each
// event), so re-adding after a basemap switch doesn't double-bind.

function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
}

// SPC magnitude column → readable text (empty when unknown). Hail size is hundredths of an inch; wind is
// mph; tornado is the (E)F rating number.
function magText(kind, mag) {
    if (!mag || mag === 'UNK') return '';
    var n = parseInt(mag, 10);
    if (kind === 'hail') return isNaN(n) ? '' : (n / 100).toFixed(2) + ' in';
    if (kind === 'wind') return isNaN(n) ? String(mag) : (n + ' mph');
    if (kind === 'torn') return isNaN(n) ? String(mag) : ('EF' + n);
    return String(mag);
}

// SPC report times are UTC HHMM over the convective day — show as HH:MM UTC.
function timeText(t) {
    t = String(t == null ? '' : t);
    if (/^\d{3,4}$/.test(t)) { t = ('000' + t).slice(-4); return t.slice(0, 2) + ':' + t.slice(2) + ' UTC'; }
    return t;
}

function ensurePopupStyle() {
    if (document.getElementById('spc-report-popup-style')) return;
    // The popup SURFACE is chrome (theme.css); the three dot colors it opens from are data and stay
    // in META above. ⚠️ The tip is a BORDER-COLOR trick — MapLibre draws the little arrow as a CSS
    // triangle — so it has to repeat the background var rather than inherit it.
    var css = [
        '.spc-report-popup .maplibregl-popup-content{background:var(--anvil-popup-bg);color:var(--anvil-popup-text);',
        'font:12px/1.45 "Segoe UI",system-ui,sans-serif;border-radius:8px;padding:9px 12px;',
        'box-shadow:0 4px 16px rgba(0,0,0,0.5);max-width:280px;}',
        '.spc-report-popup .maplibregl-popup-tip{border-top-color:var(--anvil-popup-bg);',
        'border-bottom-color:var(--anvil-popup-bg);}',
        '.spc-report-popup .maplibregl-popup-close-button{color:var(--anvil-popup-close);font-size:15px;padding:0 4px;}',
        '.spc-report-title{font-weight:600;margin-bottom:3px;}',
        '.spc-report-meta{color:var(--anvil-popup-meta);margin-bottom:4px;}',
        '.spc-report-com{color:var(--anvil-popup-body);}'
    ].join('');
    var st = document.createElement('style');
    st.id = 'spc-report-popup-style';
    st.textContent = css;
    document.head.appendChild(st);
}

function popupHtml(p) {
    var meta = META[p.kind] || { label: p.kind || 'Report', color: '#888' };
    var mag = magText(p.kind, p.mag);
    var title = meta.label + (mag ? ' · ' + mag : '');
    var place = [p.loc, p.county ? p.county + ' Co.' : '', p.st].filter(Boolean).join(', ');
    var mea = [];
    if (place) mea.push(esc(place));
    if (p.time) mea.push(esc(timeText(p.time)));
    var html = '<div class="spc-report-title" style="color:' + meta.color + '">' + esc(title) + '</div>';
    if (mea.length) html += '<div class="spc-report-meta">' + mea.join(' · ') + '</div>';
    if (p.com) html += '<div class="spc-report-com">' + esc(p.com) + '</div>';
    return html;
}

function bindInteractions(map) {
    if (interactionsBound.has(map)) return;
    interactionsBound.add(map);
    KIND_LAYERS.forEach(function (l) {
        map.on('click', l.id, function (e) {
            var f = e.features && e.features[0];
            if (!f) return;
            ensurePopupStyle();
            if (!popup) popup = new maplibregl.Popup({ className: 'spc-report-popup', maxWidth: '280px' });
            // ONE popup across every pane — clicking a dot in another pane moves it there. Detach it
            // from wherever it was first, or the previous pane is left holding an orphaned node.
            popup.remove();
            popup.setLngLat(f.geometry.coordinates.slice()).setHTML(popupHtml(f.properties)).addTo(map);
        });
        map.on('mouseenter', l.id, function () { map.getCanvas().style.cursor = 'pointer'; });
        map.on('mouseleave', l.id, function () { map.getCanvas().style.cursor = ''; });
    });
}

function closePopup() { if (popup) popup.remove(); }

// Bring the layers in line with the current state: drop them entirely when nothing is shown; otherwise add
// them if missing and set each type's visibility.
function refreshReportLayers(map) {
    if (!reportsData || !anyShown()) { removeReportLayers(map); return; }
    if (!layersPresent(map)) addReportLayers(map);
    KIND_LAYERS.forEach(function (l) {
        if (map.getLayer(l.id)) map.setLayoutProperty(l.id, 'visibility', kinds[l.kind] ? 'visible' : 'none');
    });
}

// Fetch the cached report GeoJSON (no-store: today's file is overwritten in place as reports come in). A
// failed fetch keeps the last known good data on screen rather than blanking the overlay.
function loadReports(map) {
    if (!reportsUrl) return;
    var url = reportsUrl;
    fetch(url, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
        if (reportsUrl !== url) return; // a newer day/selection won
        if (gj) reportsData = gj;
        if (gj && map.getSource('spc-reports')) map.getSource('spc-reports').setData(gj);
        refreshReportLayers(map);
    }).catch(function (e) { console.error('storm reports load failed: ' + e); });
}

export function setSource(map, url) {
    reportsUrl = url;
    reportsData = null; // a new day → drop the old points until the new file loads
    closePopup();       // a popup from the previous day would be stranded
    if (anyShown()) loadReports(map); // lazy: only fetch when a type is shown
}

export function setKinds(map, torn, wind, hail) {
    kinds = { torn: !!torn, wind: !!wind, hail: !!hail };
    if (anyShown() && !reportsData) loadReports(map); // first enable → fetch, then refresh runs in .then
    else refreshReportLayers(map);
}

export function setOpacity(map, o) {
    reportsOpacity = Math.max(0, Math.min(1, +o || 0));
    KIND_LAYERS.forEach(function (l) {
        if (map.getLayer(l.id)) {
            map.setPaintProperty(l.id, 'circle-opacity', reportsOpacity);
            map.setPaintProperty(l.id, 'circle-stroke-opacity', reportsOpacity);
        }
    });
}

export function clear(map) {
    reportsUrl = null;
    reportsData = null;
    kinds = { torn: false, wind: false, hail: false };
    closePopup();
    removeReportLayers(map);
}

// Re-add after a basemap switch (setStyle drops the layers; data is still in memory).
export function reAdd(map) {
    refreshReportLayers(map);
}

// What is this module ACTUALLY drawing right now? The host logs this beside its own view of the same
// state, so "the dots are still there" can be diagnosed without guessing which side is wrong: a source
// the page never re-pointed, data it failed to fetch, or layers left visible.
export function describe(map) {
    var src = map.getSource('spc-reports');
    var n = reportsData && reportsData.features ? reportsData.features.length : -1;
    var vis = KIND_LAYERS.map(function (l) {
        return l.kind + ':' + (map.getLayer(l.id) ? (map.getLayoutProperty(l.id, 'visibility') || 'visible') : 'none-layer');
    }).join(',');
    return 'url=' + (reportsUrl || 'null') + ' feats=' + n + ' src=' + (src ? 'yes' : 'no') +
        ' kinds=' + kinds.torn + '/' + kinds.wind + '/' + kinds.hail + ' layers=' + vis;
}
