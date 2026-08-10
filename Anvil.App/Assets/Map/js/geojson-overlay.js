// geojson-overlay.js — shared lifecycle for a simple cached-GeoJSON map overlay drawn as a translucent
// FILL + bold OUTLINE, colored per a feature property. Factored out of watches.js and warnings.js, which
// were near-identical: each hand-rolled the same state + setSource/setVisible/setOpacity/reAdd shims with
// lazy fetch-on-first-show, keep-last-known-good on a failed fetch, in-place setData on refresh, and
// re-add ONLY when the layers are actually missing (so the periodic refresh doesn't flicker).
//
// createGeojsonOverlay(config) returns { setSource, setVisible, setOpacity, reAdd } bound to the config's
// source/layer ids, color, and base opacities. Each caller (watches.js/warnings.js) is then just that
// config plus re-exports. Overlays that don't fit this fill+line shape stay hand-written: storm-reports.js
// (per-type circle layers + click popups) and outlook.js (CIG hatching + nested-ring clipping).
//
// config:
//   sourceId, fillLayerId, lineLayerId — MapLibre source + layer ids this overlay owns
//   colorProp   — feature property to color by (e.g. 'phenom')
//   colors      — { value: cssColor, … } match table for colorProp
//   colorDefault— fallback color for any other/absent value
//   fillBase, lineBase — base fill-/line-opacity at opacity multiplier 1 (the default look)
//   lineWidth   — outline width in px
//   beforeId    — optional (map) => layer id to insert BENEATH; omitted = on top of the stack
//   logName     — label used in the console.error on a failed fetch

export function createGeojsonOverlay(config) {
    const {
        sourceId, fillLayerId, lineLayerId,
        colorProp, colors, colorDefault,
        fillBase, lineBase, lineWidth,
        beforeId, logName,
    } = config;

    let url = null;
    let data = null;
    let visible = false;
    // Overall opacity multiplier (0..1) — scales BOTH the faint fill and the bold outline from their base
    // values, so the slider fades the whole overlay together (1 = the default look).
    let opacity = 1;

    // ['match', ['to-string', ['get', colorProp]], k1, c1, k2, c2, …, colorDefault]
    function colorExpr() {
        const expr = ['match', ['to-string', ['get', colorProp]]];
        Object.keys(colors).forEach(function (k) { expr.push(k, colors[k]); });
        expr.push(colorDefault);
        return expr;
    }

    function removeLayers(map) {
        [fillLayerId, lineLayerId].forEach(function (id) { if (map.getLayer(id)) map.removeLayer(id); });
        if (map.getSource(sourceId)) map.removeSource(sourceId);
    }

    function layersPresent(map) {
        return !!(map.getLayer(fillLayerId) && map.getLayer(lineLayerId) && map.getSource(sourceId));
    }

    function addLayers(map) {
        if (!data) return;
        removeLayers(map);
        map.addSource(sourceId, { type: 'geojson', data: data });
        // Insert beneath the caller's chosen layer (e.g. the state/country lines, so borders read through
        // the fill). When two overlays share the same beforeId, add order decides which sits higher.
        const before = beforeId ? beforeId(map) : undefined;
        map.addLayer({
            id: fillLayerId, type: 'fill', source: sourceId,
            paint: { 'fill-color': colorExpr(), 'fill-opacity': fillBase * opacity }
        }, before);
        map.addLayer({
            id: lineLayerId, type: 'line', source: sourceId,
            paint: { 'line-color': colorExpr(), 'line-width': lineWidth, 'line-opacity': lineBase * opacity }
        }, before);
    }

    // Bring the layers in line with the current state. ⚠️ Add ONLY when they're actually missing (first
    // show / basemap swap): the periodic refresh calls through here, and an unconditional remove-then-add
    // showed up as a flicker. New data reaches live layers via setData in load() instead.
    function refreshLayers(map) {
        if (!visible || !data) { removeLayers(map); return; }
        if (!layersPresent(map)) addLayers(map);
    }

    // Fetch the cached GeoJSON (no-store: the file is overwritten in place each refresh). A failed fetch
    // keeps the last known good data on screen rather than blanking the overlay.
    function load(map) {
        if (!url) return;
        fetch(url, { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : null; }).then(function (gj) {
            if (gj) data = gj;
            if (gj && map.getSource(sourceId)) map.getSource(sourceId).setData(gj);
            refreshLayers(map);
        }).catch(function (e) { console.error(logName + ' load failed: ' + e); });
    }

    return {
        setSource: function (map, u) {
            url = u;
            if (visible) load(map); // lazy: only fetch when the layer is shown
        },
        setVisible: function (map, on) {
            visible = !!on;
            if (on && !data) load(map); // first enable → fetch, then refreshLayers runs in .then
            else refreshLayers(map);
        },
        // Set the overall opacity multiplier (0..1). Updates the live layers in place if present; otherwise
        // it's picked up the next time the layers are added (basemap switch / first show).
        setOpacity: function (map, o) {
            opacity = Math.max(0, Math.min(1, +o || 0));
            if (map.getLayer(fillLayerId)) map.setPaintProperty(fillLayerId, 'fill-opacity', fillBase * opacity);
            if (map.getLayer(lineLayerId)) map.setPaintProperty(lineLayerId, 'line-opacity', lineBase * opacity);
        },
        // Re-add after a basemap switch (setStyle drops the layers; data is still in memory).
        reAdd: function (map) { refreshLayers(map); },
    };
}
