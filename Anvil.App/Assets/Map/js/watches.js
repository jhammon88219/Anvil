// watches.js — SPC watch boxes (Tornado / Severe Thunderstorm Watch areas). Extracted from map.js.
// Source: the NWS WWA county-aggregated active TO/SV watch polygons (host-filtered; they follow
// county lines, like RadarScope). A faint fill + bold outline colored by the feature's `phenom`
// (TO = red, SV = yellow). Loaded LAZILY — only fetched when first shown. map.js's window.setWatchSource
// / setWatchesVisible shims delegate here; applyStyle calls reAdd(map) after a basemap switch (setStyle
// drops the layers, but the fetched data is still in memory).
//
//        ┌──┐ ┌───────┐            a watch area follows COUNTY LINES (the official aggregation),
//        │  └─┘       └──┐          so its edge is stepped, not the SPC parallelogram — this is
//        │  ░░░░░░░░░░░  │          what RadarScope shows and what people compare against
//        └──┐ ░░░░░░░ ┌──┘
//           └─────────┘             red = TO (tornado watch) · yellow = SV (severe t-storm watch)
//
// The whole lazy-load / refresh / opacity / re-add lifecycle is the shared fill+line overlay in
// geojson-overlay.js — this module is just its watch-box configuration. Sits BENEATH the state/country
// lines (firstBoundaryLayerId) so borders read through the fill; radar targets the fill layer in its own
// beforeId chain, so it stays under the watch boxes.

import { firstBoundaryLayerId } from './layers.js';
import { createGeojsonOverlay } from './geojson-overlay.js';

// ⚠️ THE LOOK is exported: past-alerts.js draws PastCast's copy with exactly these values (on layers of its
// own), so the two modes cannot drift apart. Ids and z-placement stay below, per overlay.
export const STYLE = {
    colorProp: 'phenom',
    colors: {
        TO: '#ff3b30',   // tornado watch — red
        SV: '#ffd21a',   // severe thunderstorm watch — yellow
    },
    colorDefault: '#cccc40', // other/unknown
    fillBase: 0.08,
    lineBase: 0.9,
    lineWidth: 2,
};

const overlay = createGeojsonOverlay(Object.assign({
    sourceId: 'spc-watches',
    fillLayerId: 'spc-watch-fill',
    lineLayerId: 'spc-watch-line',
    beforeId: firstBoundaryLayerId,
    logName: 'watches',
}, STYLE));

export const setSource = overlay.setSource;
export const setVisible = overlay.setVisible;
export const setKinds = overlay.setKinds;
export const setOpacity = overlay.setOpacity;
export const reAdd = overlay.reAdd;
