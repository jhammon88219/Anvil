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
//
// ── THE SEARCH PIN (showPlace / clearPlace) ── the place the map-tools tier's search box found.
//
//                ╭───╮
//               │  ○  │ ╭ Moore, OK ╮      .place-pin        28x38, anchored at its BOTTOM tip
//                ╲   ╱  ╰───────────╯      .place-pin-label  the name chip, right of the head
//                  ▼   ← the coordinate
//
// ⚠️ A TEARDROP, NOT A RING: the reticle means "you are here", the pin means "this named place" — a
// different shape so the two never read as one another, and neither as a rectangular site key. It wears
// the reticle's two colors (same casing trick) so the app's own marks share one palette.
// ⚠️ NOT DRAGGABLE — a named place doesn't move. It is removed by clearing the search box (host side).

// The reticle and the pin are <template>s in map.html (tpl-user-loc, tpl-place-pin); their look — the
// casing and ink strokes included — is styles/components/markers.css. CSS `stroke`/`fill` read the theme
// variables directly, so a theme switch restyles both marks with no rebuild.
import * as Templates from './templates.js';

let userLocationMarker = null;
let placeMarker = null;
const USER_MARKER_ID = 'user'; // singleton; the host correlates drag/click by this fixed id
const PLACE_MARKER_ID = 'place'; // singleton search pin; MarkersViewModel.PlaceMarkerId

function postMarker(type, id, extra) {
    if (!(window.chrome && window.chrome.webview)) return;
    const msg = { type: type, id: id };
    if (extra) { for (const k in extra) msg[k] = extra[k]; }
    window.chrome.webview.postMessage(JSON.stringify(msg));
}

// Place (or replace) the user-location marker at [lng, lat]. Draggable to refine: a dragend reports the new
// position (host flags it "manual"); a click selects it (re-opens its editor if deselected).
export function show(map, lng, lat, label) {
    if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
    const el = Templates.clone('tpl-user-loc');
    el.title = label || 'Your location';
    userLocationMarker = new maplibregl.Marker({ element: el, draggable: true }).setLngLat([lng, lat]).addTo(map);
    userLocationMarker.on('dragend', function () {
        const p = userLocationMarker.getLngLat();
        postMarker('markerMoved', USER_MARKER_ID, { lng: p.lng, lat: p.lat });
    });
    el.addEventListener('click', function (ev) {
        ev.stopPropagation();
        postMarker('markerClick', USER_MARKER_ID);
    });
}

export function clear() {
    if (userLocationMarker) { userLocationMarker.remove(); userLocationMarker = null; }
}

// Theme change. Nothing to redraw any more: both marks take their colours from CSS (markers.css), which
// re-cascades on its own. Kept so map.js's theme pass (Markers.refresh) stays valid.
export function refresh() { }

// ── Search pin ──────────────────────────────────────────────────────────────────────────────────────

// Place (or replace) the search pin at [lng, lat] with its name chip. A click selects it host-side.
export function showPlace(map, lng, lat, label) {
    clearPlace();
    const el = Templates.clone('tpl-place-pin');
    el.title = label || '';
    const chip = el.querySelector('.place-pin-label');
    // textContent, never innerHTML: the label is a place name from a geocoder. No label = no chip.
    if (label) chip.textContent = label; else chip.remove();
    placeMarker = new maplibregl.Marker({ element: el, anchor: 'bottom' }).setLngLat([lng, lat]).addTo(map);
    el.addEventListener('click', function (ev) {
        ev.stopPropagation();
        postMarker('markerClick', PLACE_MARKER_ID);
    });
}

export function clearPlace() {
    if (placeMarker) { placeMarker.remove(); placeMarker = null; }
}
