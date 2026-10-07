// site-reveal.js — EXPERIMENTAL: a range ring rides the cursor, and only the radar sites inside it show.
// A bolt-on (grep SITE-REVEAL across the repo to excise it): this module, one gate in radar-sites.js, one
// map.js shim, one IMapService call, one AppSettings bool, one Settings → Radar section.
//
//                 ·  ·  ·                      the ring = REVEAL_KM round the point under the cursor,
//             ·    [KTLX]   ·                  drawn as a DOM circle sized from the map's scale there
//           ·                 ·
//          ·        ✛          ·   [KFDR]  ← outside the ring: hidden (every network's toggle still applies)
//           ·    [KINX]       ·
//             ·             ·              [KAMA]  ← the LOADED site: always shown, wherever the cursor is
//                 ·  ·  ·
//
// WHY 230 km: a WSR-88D's usable reflectivity range — the same radius state isolation uses to decide a site
// "covers" a state. So the ring answers "which radars can see the spot I'm pointing at".
// ⚠️ PRIMARY PANE ONLY, like the site keys themselves (radar-sites.js is primary-only).
// ⚠️ The cursor LEAVING the map (onto the chrome) keeps the last set — reaching for the bar must not wipe the
//    map. The ring hides; the keys stay until the cursor is back.
// ⚠️ Visibility is pushed only when the SET changes, not per mousemove: applyVisibility touches every key.

import * as Geo from './geo.js';

const REVEAL_KM = 230;
const EARTH_CIRC_M = 40075016.686; // MapLibre's 512-px tiles: m/px = this × cos(lat) / (512 × 2^zoom)

let map = null, sites = null, ring = null;
let lastKey = '', lastPoint = null, raf = 0;

function ensureRing() {
    if (ring) return;
    ring = document.createElement('div');
    // ⚠️ The ring wears the scope colour (the range rings' outline, user-pickable), dashed so it never reads
    // as a radar's own range ring.
    ring.setAttribute('style',
        'position:absolute;left:0;top:0;border-radius:50%;pointer-events:none;display:none;z-index:2;' +
        'border:1.5px dashed var(--anvil-scope-ring);box-sizing:border-box;');
    map.getContainer().appendChild(ring);
}

// Which site ids lie within REVEAL_KM of lng/lat — in the point's own frame (geo.js metres per degree), which
// is plenty at this radius.
function idsNear(lng, lat) {
    const k = Geo.metersPerDeg(lat), r = REVEAL_KM * 1000, out = [];
    const coords = sites.siteCoordinates();
    Object.keys(coords).forEach(function (id) {
        const c = coords[id];
        const dx = (c[0] - lng) * k.mPerDegLon, dy = (c[1] - lat) * k.mPerDegLat;
        if (dx * dx + dy * dy <= r * r) out.push(id);
    });
    return out;
}

function update() {
    raf = 0;
    if (!map || !lastPoint) return;
    const ll = map.unproject(lastPoint);
    const mpp = EARTH_CIRC_M * Math.cos(ll.lat * Geo.D2R) / (512 * Math.pow(2, map.getZoom()));
    const d = 2 * (REVEAL_KM * 1000) / mpp;
    ring.style.width = d + 'px';
    ring.style.height = d + 'px';
    ring.style.transform = 'translate(' + (lastPoint.x - d / 2) + 'px,' + (lastPoint.y - d / 2) + 'px)';
    ring.style.display = '';

    const ids = idsNear(ll.lng, ll.lat).sort();
    const key = ids.join(',');
    if (key !== lastKey) {
        lastKey = key;
        sites.setRevealed(ids);
    }
}
function queue() { if (!raf) raf = requestAnimationFrame(update); }

function onMove(e) { lastPoint = e.point; queue(); }
function onZoom() { if (lastPoint) queue(); }  // the wheel zooms under a still cursor — the ring rescales
function onLeave() { if (ring) ring.style.display = 'none'; }

/** Turn the reveal on or off. `radarSites` = the radar-sites.js module (it owns the keys and their coords). */
export function setEnabled(primaryMap, on, radarSites) {
    if (on && map) return;
    if (!on) {
        if (map) {
            map.off('mousemove', onMove);
            map.off('zoom', onZoom);
            map.getCanvasContainer().removeEventListener('mouseleave', onLeave);
        }
        if (raf) cancelAnimationFrame(raf);
        if (ring) ring.remove();
        if (sites) sites.setRevealed(null);
        map = null; sites = null; ring = null; raf = 0; lastKey = ''; lastPoint = null;
        return;
    }
    if (!primaryMap || !radarSites) return;
    map = primaryMap;
    sites = radarSites;
    ensureRing();
    // Until the cursor first moves over the map, only the loaded site shows (an empty set, not null).
    sites.setRevealed([]);
    map.on('mousemove', onMove);
    map.on('zoom', onZoom);
    map.getCanvasContainer().addEventListener('mouseleave', onLeave);
}
