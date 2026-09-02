// markers.js — the user-location marker: a RETICLE (ring + four ticks + a solid core), draggable so the
// user can refine the inherently-approximate fix. Extracted from map.js. Self-contained ES module: it owns
// the marker DOM + state and posts drag/click back to the host; map.js's window.showUserLocation /
// clearUserLocation shims delegate here, passing the map instance. `maplibregl` is the global set by the
// vendored classic script (visible to modules via the global scope). A DOM-overlay marker auto-repositions
// on pan/zoom and survives basemap switches, so there is no style-layer re-add to do — and maps[0] is never
// torn down by setPaneLayout, so the primary-only marker survives a pane change too.
//
//                  │              .user-loc        36x36 hit area (the drag target)
//              ╭───┼───╮          ring + ticks     the RETICLE: r=9 ring, four ticks standing off it at
//         ─────┤   ●   ├─────                      N/E/S/W. Drawn twice — a fat white casing underneath,
//              ╰───┼───╯                           the blue on top — so it holds its shape over BOTH the
//                  │                               dark basemap and bright radar returns.
//                                 core             a solid blue disc in a white collar: the actual point,
//                                                  readable when the ring is over busy returns
//
// ⚠️ WHY A RETICLE, AND WHY IT IS THE ONLY ROUND MARKER: the map's other markers are the radar-site "key"
// buttons (radar-sites.js) — graphite RECTANGLES with an ICAO in them. A circle already cannot be confused
// with one at a glance, and the crosshair says "this is a point", not "this is a place you can click".
//
// ⚠️ THE PULSE IS GONE, deliberately. This was a blue dot under a translucent ring scaling .5→2.4 on a 1.8s
// infinite loop — the "we are still guessing" cue for an approximate fix. It is dropped because the marker
// now sits on the map permanently while the user reads radar, and a forever-animating element in the
// periphery competes with the data it is sitting on. The approximation is still reported: the host's
// Selected Marker readout names the source (Device GPS / IP estimate / Manually adjusted). Restoring it
// means one keyframes rule and one more child — nothing else here assumes it is absent.
//
// Draggable because the fix IS approximate (OS or IP): the user drops it where they actually are, and each
// drag posts back to the host, which re-flags the position as manual.

let userLocationMarker = null;
const USER_MARKER_ID = 'user'; // singleton; the host correlates drag/click by this fixed id

const RETICLE_BLUE = '#2f8fff';

function ensureUserLocationStyle() {
    if (document.getElementById('user-location-style')) return;
    const s = document.createElement('style');
    s.id = 'user-location-style';
    // The wrapper only sizes the hit area and centres the art; every stroke lives in the SVG below, so
    // the whole look is one geometry rather than a stack of positioned divs.
    s.textContent =
        '.user-loc{position:relative;width:36px;height:36px;cursor:grab;}' +
        '.user-loc:active{cursor:grabbing;}' +
        '.user-loc svg{display:block;filter:drop-shadow(0 1px 2px rgba(0,0,0,.55));}';
    document.head.appendChild(s);
}

// The reticle, as one SVG string. Ring r=9 and ticks spanning radius 11 → 14.5 (a deliberate gap, so the
// ticks read as standing OFF the ring rather than piercing it). Drawn twice: the white casing first at a
// fat stroke, the blue over it — the same trick map labels use to stay legible on any ground.
function reticleSvg() {
    const ring = '<circle cx="18" cy="18" r="9"/>';
    const ticks = '<path d="M18,7 V3.5 M18,29 V32.5 M7,18 H3.5 M29,18 H32.5"/>';
    return '<svg width="36" height="36" viewBox="0 0 36 36" aria-hidden="true">' +
        '<g fill="none" stroke="#fff" stroke-width="5.5" stroke-linecap="round">' + ring + ticks + '</g>' +
        '<g fill="none" stroke="' + RETICLE_BLUE + '" stroke-width="2.5" stroke-linecap="round">' + ring + ticks + '</g>' +
        '<circle cx="18" cy="18" r="4.5" fill="#fff"/>' +
        '<circle cx="18" cy="18" r="3" fill="' + RETICLE_BLUE + '"/>' +
        '</svg>';
}

function postMarker(type, extra) {
    if (!(window.chrome && window.chrome.webview)) return;
    const msg = { type: type, id: USER_MARKER_ID };
    if (extra) { for (const k in extra) msg[k] = extra[k]; }
    window.chrome.webview.postMessage(JSON.stringify(msg));
}

// Place (or replace) the user-location marker at [lng, lat]. Draggable to refine: a dragend reports the new
// position (host flags it "manual"); a click selects it (re-opens its editor if deselected).
export function show(map, lng, lat, label) {
    ensureUserLocationStyle();
    if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
    const el = document.createElement('div');
    el.className = 'user-loc';
    el.title = label || 'Your location';
    el.innerHTML = reticleSvg();
    userLocationMarker = new maplibregl.Marker({ element: el, draggable: true }).setLngLat([lng, lat]).addTo(map);
    userLocationMarker.on('dragend', function () {
        const p = userLocationMarker.getLngLat();
        postMarker('markerMoved', { lng: p.lng, lat: p.lat });
    });
    el.addEventListener('click', function (ev) {
        ev.stopPropagation();
        postMarker('markerClick');
    });
}

export function clear() {
    if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
}
