// grid.js — a square DISTANCE grid anchored to the selected radar, at a chosen mile spacing. A
// hide/show reference the user can eyeball distances against (how far a storm is from the radar, how
// wide a feature is) — a persistent alternative to a point-to-point measure tool.
//
// Geometry: the grid is built in the radar's own local equirectangular plane (geo.js — the SAME
// projection the gates/range-ring use), so a cell is a true `spacing`-mile SQUARE on the ground,
// centered on the site. Because that projection is linear, every grid line is a meridian or parallel
// segment — axis-aligned + straight in Web Mercator — so two endpoints per line suffice (no subdivision).
// Cells read square in ground distance; Mercator stretches them vertically far from the site, which is
// fine for a distance reference at radar range.
//
// Host seam: map.js exposes window.showMileGrid / clearMileGrid / setMileGridOpacity, driven by
// MileGridViewModel through IMapService. applyStyle calls reAdd(map) after a basemap switch (setStyle
// drops the layer; the anchor state stays in memory here). Sits UNDER the boundary lines + labels (via
// layers.js) so borders/labels stay legible, but OVER the radar.

import { metersPerDeg } from './geo.js';
import { firstBoundaryLayerId } from './layers.js';

const SRC = 'mile-grid';
const LINE = 'mile-grid-line';
const M_PER_MILE = 1609.344;
const RADIUS_MILES = 250; // ground radius the grid covers from the site (WSR-88D range ~143 mi + margin)
const GRID_COLOR = '#8ab4d8'; // cool light blue-gray — reads on the dark basemap and over the radar fill

// Anchor state (kept so reAdd after a style switch can rebuild without the host re-driving it).
let anchor = null;         // { lat, lon, spacingMiles } or null = not shown
let gridOpacity = 0.4;

// Build the grid GeoJSON (one MultiLineString) around a site at a mile spacing. Linear projection, so
// each line is just its two endpoints.
function buildGrid(lat, lon, spacingMiles) {
    const step = spacingMiles * M_PER_MILE;
    const half = Math.ceil(RADIUS_MILES / spacingMiles) * step; // extent snapped to the spacing
    const { mPerDegLat, mPerDegLon } = metersPerDeg(lat);
    const toLL = (x, y) => [lon + x / mPerDegLon, lat + y / mPerDegLat];
    const lines = [];
    for (let x = -half; x <= half + 1; x += step) lines.push([toLL(x, -half), toLL(x, half)]); // meridians
    for (let y = -half; y <= half + 1; y += step) lines.push([toLL(-half, y), toLL(half, y)]); // parallels
    return { type: 'FeatureCollection', features: [{ type: 'Feature', geometry: { type: 'MultiLineString', coordinates: lines }, properties: {} }] };
}

function present(map) { return !!(map.getSource(SRC) && map.getLayer(LINE)); }

function addLayer(map, data) {
    if (map.getLayer(LINE)) map.removeLayer(LINE);
    if (map.getSource(SRC)) map.removeSource(SRC);
    map.addSource(SRC, { type: 'geojson', data: data });
    map.addLayer({
        id: LINE,
        type: 'line',
        source: SRC,
        paint: {
            'line-color': GRID_COLOR,
            'line-width': ['interpolate', ['linear'], ['zoom'], 3, 0.5, 8, 1, 12, 1.5],
            'line-opacity': gridOpacity,
        },
    }, firstBoundaryLayerId(map));
}

// Show/refresh the grid anchored to (lat, lon) at spacingMiles.
export function show(map, lat, lon, spacingMiles) {
    if (!(isFinite(lat) && isFinite(lon) && spacingMiles > 0)) return;
    anchor = { lat: lat, lon: lon, spacingMiles: spacingMiles };
    addLayer(map, buildGrid(lat, lon, spacingMiles));
}

export function clear(map) {
    anchor = null;
    if (map.getLayer(LINE)) map.removeLayer(LINE);
    if (map.getSource(SRC)) map.removeSource(SRC);
}

export function setOpacity(map, o) {
    gridOpacity = Math.max(0, Math.min(1, +o || 0));
    if (map.getLayer(LINE)) map.setPaintProperty(LINE, 'line-opacity', gridOpacity);
}

// Re-add after a basemap switch (setStyle drops the layer; the anchor stays in memory).
export function reAdd(map) {
    if (anchor && !present(map)) addLayer(map, buildGrid(anchor.lat, anchor.lon, anchor.spacingMiles));
}
