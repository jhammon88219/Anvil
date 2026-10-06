// preview.js — the Settings window's MAP PREVIEW (preview.html, hosted by Controls/Composites/MapPreview).
//
//   host (C#)                                      this page
//   ─────────                                      ─────────
//   URL ?style&tiles&tilesUrl&theme&lat&lon   ──▶  ONE map, framed on the rings
//   'previewReady'                            ◀──  posted once the map's 'load' fires
//   IMapService mirror: the LATEST of each    ──▶  window.applyTheme / applyStyle / setTileSource /
//   look command, then every live one              setBasemap / setDistanceUnits / setScopeColor /
//                                                  setRangeRings / setRangeRingStyle   (map.js's names)
//   window.previewFit()                       ──▶  re-frame on the rings (the XAML Fit key)
//   'rangeRingLabelBearing'                   ◀──  the label handle was dragged (radar-scope.js posts it)
//
// ⚠️ THE SHIM NAMES AND ARGUMENTS ARE map.js's, on purpose: the host sends this page the very script string it
// sends the main map (MapService.RunMirrored), so a look command has ONE formatter. Change a shim's signature in
// map.js and this page must follow.
// ⚠️ THE RINGS ARE radar-scope.js ITSELF, driven through its `host` seam with one view — never a re-drawing.
// That is what makes this a TRUE preview: the same module can't draw a look the main map would not.
// ⚠️ Every look shim is IDEMPOTENT here: the replay re-sends the theme + style the page was launched on, and
// a needless setStyle would blink the whole preview.

import * as Geo from './geo.js';
import * as Scope from './radar-scope.js';
import * as Basemap from './basemap.js';

const params = new URLSearchParams(location.search);
function num(v, d) { const n = parseFloat(v); return isFinite(n) ? n : d; }

// The site the rings are drawn round (the host passes the loaded site, else home; KTLX if neither).
const site = { lat: num(params.get('lat'), 35.3331), lon: num(params.get('lon'), -97.2778) };
// ⚠️ A NEXRAD's base-tilt reach, not a measured one: there is no frame here to measure. The main map sizes
// these from the DISPLAYED frame (radar.js syncScope), so a TDWR or an upper tilt draws smaller there.
const REFL_M = 460000, VEL_M = 300000;

// The look the page SHOULD wear, and the one its map was last built on. sync() closes the gap.
const want = {
    style: params.get('style') || 'https://mapassets/style.json',
    tiles: params.get('tiles') === 'online' ? 'online' : 'offline',
    tilesUrl: params.get('tilesUrl') || '',
    theme: params.get('theme') || '',
};
let built = null;      // key of the look the map was built/restyled on
let styleGen = 0;      // a later sync supersedes an in-flight online style fetch
let map = null;
const view = { map: null, index: 0 };

function key() { return want.style + '|' + want.tiles + '|' + want.tilesUrl + '|' + want.theme; }

// ⚠️ MIRRORS map.js tileSourceFor + resolveStyle (same three online forms, same offline no-fetch path). The
// boot there is a classic script that needs them synchronously, so they are copied rather than shared —
// change both or neither.
function tileSourceFor(base) {
    if (want.tiles !== 'online' || !want.tilesUrl) return null;
    const src = { type: 'vector' };
    if (base && base.attribution) src.attribution = base.attribution;
    if (/^pmtiles:\/\//i.test(want.tilesUrl) || /\.json($|\?)/i.test(want.tilesUrl)) {
        src.url = want.tilesUrl;
    } else {
        src.tiles = [want.tilesUrl];
        src.minzoom = 0;
        src.maxzoom = 15;
    }
    return src;
}
function resolveStyle(url) {
    if (want.tiles !== 'online' || !want.tilesUrl) return Promise.resolve(url);
    return fetch(url)
        .then(function (r) { return r.json(); })
        .then(function (style) {
            const id = style.sources && Object.keys(style.sources)[0];
            const src = id ? tileSourceFor(style.sources[id]) : null;
            if (id && src) style.sources[id] = src;
            return style;
        })
        .catch(function () { return url; });
}

// ⚠️ MIRRORS radar.js beforeId, minus the overlays this page never has: rings sit under the boundary lines
// and the labels, as on the main map.
function beforeId(m) {
    if (m.getLayer('boundaries_country')) return 'boundaries_country';
    if (m.getLayer('boundaries')) return 'boundaries';
    const layers = (m.getStyle() && m.getStyle().layers) || [];
    const sym = layers.find(function (l) { return l.type === 'symbol'; });
    return sym ? sym.id : undefined;
}

Scope.init({
    forEachView: function (fn) { if (view.map) fn(view); },
    viewCount: function () { return view.map ? 1 : 0; },
    primaryView: function () { return view.map ? view : null; },
    beforeId: beforeId,
    getSite: function () { return site; },
});

function post(msg) {
    try { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(msg)); }
    catch (e) { /* host gone */ }
}

// Restyle onto `want` if the map is on anything else. Rings come back on the new style's first idle, as
// map.js reAddAll does; the handle's colours are baked into its SVG, hence refreshColors.
function sync() {
    if (!map || built === key()) return;
    const k = key(), gen = ++styleGen;
    document.documentElement.dataset.theme = want.theme;
    resolveStyle(want.style).then(function (spec) {
        if (gen !== styleGen) return;
        built = k;
        map.setStyle(spec, { diff: true, transformStyle: Basemap.transformFor(map) });
        map.once('idle', function () {
            Scope.attachView(view);
            Scope.refreshColors();
        });
    });
}

// Frame the reflectivity ring with a little air round it.
function fit(animate) {
    if (!map) return;
    const pts = [0, 90, 180, 270].map(function (d) { return Geo.siteToLngLat(site.lat, site.lon, REFL_M, d * Geo.D2R); });
    const lngs = pts.map(function (p) { return p[0]; }), lats = pts.map(function (p) { return p[1]; });
    map.fitBounds([[Math.min.apply(null, lngs), Math.min.apply(null, lats)], [Math.max.apply(null, lngs), Math.max.apply(null, lats)]],
        { padding: 24, duration: animate ? 400 : 0 });
}

// ---- Boot ----
const protocol = new pmtiles.Protocol();
maplibregl.addProtocol('pmtiles', protocol.tile);
const bootKey = key();
resolveStyle(want.style).then(function (spec) {
    map = new maplibregl.Map({
        container: 'map', style: spec, center: [site.lon, site.lat], zoom: 5,
        attributionControl: false, renderWorldCopies: false, minZoom: 2,
        dragRotate: false, pitchWithRotate: false, touchPitch: false,
    });
    map.touchZoomRotate.disableRotation();
    view.map = map;
    built = bootKey;
    map.on('load', function () {
        Basemap.apply(map);
        fit(false);
        Scope.setReach(REFL_M, VEL_M);
        sync(); // a look that changed while the boot style was loading
        post({ type: 'previewReady' });
    });
});

// ---- Host commands (map.js's names — see the header) ----
window.applyTheme = function (themeId, url) {
    want.theme = themeId || '';
    if (url) want.style = url;
    sync();
};
window.applyStyle = function (url) { want.style = url; sync(); };
window.setTileSource = function (mode, url) {
    want.tiles = mode === 'online' ? 'online' : 'offline';
    want.tilesUrl = url || '';
    sync();
};
window.setBasemap = function (hidden, offGroupsJson) {
    let off = [];
    try { off = JSON.parse(offGroupsJson || '[]'); } catch (e) { /* bad payload = nothing unticked */ }
    Basemap.setState(hidden, off);
    if (map) Basemap.apply(map);
};
window.setDistanceUnits = function (unit) { Scope.setUnits(unit); };
// ⚠️ MIRRORS map.js setScopeColor: the host only sends a bare #RRGGBB or '' (ScopeColors.Normalize).
window.setScopeColor = function (hex) {
    const style = document.documentElement.style;
    const ok = /^#[0-9A-Fa-f]{6}$/.test(hex || '');
    ['--anvil-scope-ring', '--anvil-ruler-ink'].forEach(function (name) {
        if (ok) style.setProperty(name, hex); else style.removeProperty(name);
    });
    Scope.refreshColors();
};
window.setRangeRings = function (visible, refl, vel, dist, spacing, site) {
    Scope.setRings({
        all: !!visible, refl: !!refl, vel: !!vel, dist: !!dist, spacing: Number(spacing) || 0,
        site: site === undefined ? true : !!site,
    });
};
window.setRangeRingStyle = function (json) {
    let o = null;
    try { o = JSON.parse(json); } catch (e) { return; }
    Scope.setStyle(o);
};
window.previewFit = function () { fit(true); };
