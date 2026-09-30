// warnings.js — storm-based NWS warning polygons (active Tornado / Severe Thunderstorm / Flash Flood
// Warnings). Sibling of watches.js. Source: the NWS CAP alerts feed, transformed by WarningService into
// `phenom` TO/SV/FF + `threat_tier` — the actual forecaster-drawn storm-based polygon (a handful of
// vertices), i.e. the modern warning shape RadarScope/NWS show, NOT the county area watches use. A bold
// outline + very faint fill, colored by the feature's `phenom` (TO = red, SV = yellow, FF = green), the
// outline WIDENED by the damage-threat tier. Loaded LAZILY — only fetched when first shown.
// map.js's window.setWarningSource / setWarningsVisible / setWarningsOpacity shims delegate here;
// applyStyle calls reAdd(map) after a basemap switch (setStyle drops the layers; the data stays in memory).
//
//              ╱╲                  a storm-based warning is a forecaster-drawn polygon of a HANDFUL
//            ╱░░░░╲                of vertices, hugging one storm — nothing like the county-stepped
//          ╱░░░░░░░░╲              watch area beneath it. Thicker line (2.5), fainter fill (0.05):
//        ╱░░░░░░░░░░░░╲            it is the imminent-threat layer, so it must read as an outline
//        ╲░░░░░░░░░░╱              over live radar without hiding the storm it encloses.
//          ╲░░░░░░╱
//            ╲░░╱                  red = TO (tornado warning) · yellow = SV (severe t-storm warning)
//                                  green = FF (flash flood warning)
//     ═══  tier 2 (5 px)          outline weight = damage-threat tier: emergency / destructive
//     ══   tier 1 (3.75 px)       considerable (PDS tornado, considerable flash flood)
//     ─    tier 0 (2.5 px)        base; stacking: TO over SV over FF, higher tier on top
//
//            ╱▔▔▔▔╲                THE FLASH (flash(), the NowCast tiles' ‹ › arrows): a dashed INK line on
//          ┊╱ ┄ ┄ ╲┊               a CASING, the ruler's themed pair, over one warning for FLASH_MS, then a
//          ┊╲ ┄ ┄ ╱┊               fade. Its OWN source (the geometry comes from the host), so it draws even
//            ╲▁▁▁▁╱                while the warning layer / that type is hidden. Id prefix `nws-warning-` →
//                                  layers.js files it in the warnings group. Transient: a style switch drops it.
//
// The whole lazy-load / refresh / opacity / re-add lifecycle is the shared fill+line overlay in
// geojson-overlay.js — this module is just its warning-polygon configuration. Warnings are the imminent-
// threat layer, so they sit ABOVE the watch boxes: both target firstBoundaryLayerId, but map.js re-adds
// watches first, so warnings land above them. Radar targets its own beforeId chain and stays beneath.

import { firstBoundaryLayerId } from './layers.js';
import { createGeojsonOverlay } from './geojson-overlay.js';
import * as Theme from './theme.js';

// ⚠️ THE LOOK is exported: past-alerts.js draws PastCast's copy with exactly these values (on layers of its
// own), so the two modes cannot drift apart. Ids and z-placement stay below, per overlay.
export const STYLE = {
    colorProp: 'phenom',
    colors: {
        TO: '#ff2a2a',   // tornado warning — bright red
        SV: '#ffd21a',   // severe thunderstorm warning — yellow
        FF: '#2ee05a',   // flash flood warning — green (the radar-app convention; NWS's own dark red
                         // would sit next to TO red and vanish on the dark basemap)
    },
    colorDefault: '#ff8c1a', // other/unknown
    fillBase: 0.05,
    lineBase: 1.0,
    // Outline widens with the damage-threat TIER (WarningService.ThreatTier): 0 base, 1 considerable
    // (PDS tornado / considerable flash flood), 2 catastrophic or destructive (an EMERGENCY).
    lineWidth: ['match', ['to-number', ['get', 'threat_tier'], 0], 2, 5, 1, 3.75, 2.5],
    // TO over SV over FF, then the higher tier on top — a flash-flood polygon is often county-sized and
    // must not paint over the tornado warning inside it.
    sortKey: ['+',
        ['match', ['to-string', ['get', 'phenom']], 'TO', 20, 'SV', 10, 0],
        ['to-number', ['get', 'threat_tier'], 0]],
};

const overlay = createGeojsonOverlay(Object.assign({
    sourceId: 'nws-warnings',
    fillLayerId: 'nws-warning-fill',
    lineLayerId: 'nws-warning-line',
    beforeId: firstBoundaryLayerId,
    logName: 'warnings',
}, STYLE));

export const setSource = overlay.setSource;
export const setVisible = overlay.setVisible;
export const setKinds = overlay.setKinds;
export const setOpacity = overlay.setOpacity;
export const reAdd = overlay.reAdd;

// ---- The flash (see the sketch above) ----
const FLASH_SRC = 'nws-warning-flash';
const FLASH_CASING = 'nws-warning-flash-casing', FLASH_INK = 'nws-warning-flash-ink';
const FLASH_MS = 2000, FLASH_FADE_MS = 600;
const flashTimers = new WeakMap();   // per map: the pending fade / remove, cancelled by the next flash

function dropFlash(map) {
    [FLASH_INK, FLASH_CASING].forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
    if (map.getSource(FLASH_SRC)) map.removeSource(FLASH_SRC);
}

// Outline `geometry` (a GeoJSON geometry) on `map` for FLASH_MS, then fade it out and remove it. A new flash
// replaces one still showing. Null geometry = nothing to draw (the camera still moved).
export function flash(map, geometry) {
    const pending = flashTimers.get(map);
    if (pending) { pending.forEach(clearTimeout); flashTimers.delete(map); }
    dropFlash(map);
    if (!geometry) return;
    try {
        map.addSource(FLASH_SRC, { type: 'geojson', data: { type: 'Feature', geometry: geometry, properties: {} } });
        const fade = { duration: FLASH_FADE_MS, delay: 0 };
        // Casing then ink, both on TOP (no beforeId) — restack moves them into the warnings group, in this order.
        map.addLayer({ id: FLASH_CASING, type: 'line', source: FLASH_SRC,
            paint: { 'line-color': Theme.color('--anvil-ruler-casing', '#000000'), 'line-width': 7, 'line-opacity': 0.55, 'line-opacity-transition': fade } });
        map.addLayer({ id: FLASH_INK, type: 'line', source: FLASH_SRC,
            paint: { 'line-color': Theme.color('--anvil-ruler-ink', '#e8edf2'), 'line-width': 3, 'line-dasharray': [2, 1.5], 'line-opacity': 1, 'line-opacity-transition': fade } });
    } catch (e) {
        console.error('warning flash failed: ' + e);
        dropFlash(map);
        return;
    }
    const t1 = setTimeout(function () {
        [FLASH_CASING, FLASH_INK].forEach(function (id) { if (map.getLayer(id)) map.setPaintProperty(id, 'line-opacity', 0); });
    }, FLASH_MS);
    const t2 = setTimeout(function () { flashTimers.delete(map); dropFlash(map); }, FLASH_MS + FLASH_FADE_MS + 50);
    flashTimers.set(map, [t1, t2]);
}
