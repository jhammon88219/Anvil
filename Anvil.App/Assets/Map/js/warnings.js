// warnings.js — storm-based NWS warning polygons (active Tornado / Severe Thunderstorm Warnings).
// Sibling of watches.js. Source: the NWS WWA warning polygons (host-filtered to sig='W', phenom TO/SV)
// — the actual forecaster-drawn storm-based polygon (a handful of vertices), i.e. the modern warning
// shape RadarScope/NWS show, NOT the county area watches use. A bold outline + very faint fill, colored
// by the feature's `phenom` (TO = red, SV = yellow). Loaded LAZILY — only fetched when first shown.
// map.js's window.setWarningSource / setWarningsVisible / setWarningsOpacity shims delegate here;
// applyStyle calls reAdd(map) after a basemap switch (setStyle drops the layers; the data stays in memory).
//
// The whole lazy-load / refresh / opacity / re-add lifecycle is the shared fill+line overlay in
// geojson-overlay.js — this module is just its warning-polygon configuration. Warnings are the imminent-
// threat layer, so they sit ABOVE the watch boxes: both target firstBoundaryLayerId, but map.js re-adds
// watches first, so warnings land above them. Radar targets its own beforeId chain and stays beneath.

import { firstBoundaryLayerId } from './layers.js';
import { createGeojsonOverlay } from './geojson-overlay.js';

const overlay = createGeojsonOverlay({
    sourceId: 'nws-warnings',
    fillLayerId: 'nws-warning-fill',
    lineLayerId: 'nws-warning-line',
    colorProp: 'phenom',
    colors: {
        TO: '#ff2a2a',   // tornado warning — bright red
        SV: '#ffd21a',   // severe thunderstorm warning — yellow
    },
    colorDefault: '#ff8c1a', // other/unknown
    fillBase: 0.05,
    lineBase: 1.0,
    lineWidth: 2.5,
    beforeId: firstBoundaryLayerId,
    logName: 'warnings',
});

export const setSource = overlay.setSource;
export const setVisible = overlay.setVisible;
export const setOpacity = overlay.setOpacity;
export const reAdd = overlay.reAdd;
