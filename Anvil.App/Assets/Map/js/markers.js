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

import * as Theme from './theme.js';

let userLocationMarker = null;
let placeMarker = null;
const USER_MARKER_ID = 'user'; // singleton; the host correlates drag/click by this fixed id
const PLACE_MARKER_ID = 'place'; // singleton search pin; MarkersViewModel.PlaceMarkerId

// ⚠️ The reticle is built from SVG presentation attributes, which can't read a CSS variable — so the
// two colors come through theme.js. READ PER BUILD, not once into a module const: a const would latch
// the palette that happened to be loaded at import time and never follow a theme change.

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
    const casing = Theme.color('--anvil-marker-ring', '#ffffff');
    const ink = Theme.color('--anvil-marker-reticle', '#2f8fff');
    return '<svg width="36" height="36" viewBox="0 0 36 36" aria-hidden="true">' +
        '<g fill="none" stroke="' + casing + '" stroke-width="5.5" stroke-linecap="round">' + ring + ticks + '</g>' +
        '<g fill="none" stroke="' + ink + '" stroke-width="2.5" stroke-linecap="round">' + ring + ticks + '</g>' +
        '<circle cx="18" cy="18" r="4.5" fill="' + casing + '"/>' +
        '<circle cx="18" cy="18" r="3" fill="' + ink + '"/>' +
        '</svg>';
}

function postMarker(type, id, extra) {
    if (!(window.chrome && window.chrome.webview)) return;
    const msg = { type: type, id: id };
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

// Redraw the reticle in place after a theme change, keeping the marker's position, draggability and
// handlers. ⚠️ Needed because the two colors are BAKED INTO THE SVG MARKUP at build time — an SVG
// presentation attribute can't read a CSS variable, so unlike everything styled by a rule, this one
// does not re-cascade on its own. No-op when no marker is placed.
export function refresh() {
    if (userLocationMarker) {
        const el = userLocationMarker.getElement();
        if (el) el.innerHTML = reticleSvg();
    }
    if (placeMarker) {
        const svgHost = placeMarker.getElement().querySelector('.place-pin-art');
        if (svgHost) svgHost.innerHTML = pinSvg();
    }
}

// ── Search pin ──────────────────────────────────────────────────────────────────────────────────────

function ensurePlaceStyle() {
    if (document.getElementById('place-pin-style')) return;
    const s = document.createElement('style');
    s.id = 'place-pin-style';
    // The chip is CSS, so it reads the theme variables directly and re-cascades on a theme switch; only the
    // SVG art needs the refresh() rebuild. pointer-events:none on the chip keeps it from eating map drags.
    s.textContent =
        '.place-pin{position:relative;width:28px;height:38px;cursor:pointer;}' +
        '.place-pin-art svg{display:block;filter:drop-shadow(0 1px 2px rgba(0,0,0,.55));}' +
        '.place-pin-label{position:absolute;left:30px;top:5px;white-space:nowrap;pointer-events:none;' +
        'font:500 12px/1.3 "Segoe UI",system-ui,sans-serif;padding:2px 7px;border-radius:4px;' +
        'color:var(--anvil-place-label-text);background:var(--anvil-place-label-bg);' +
        'border:1px solid var(--anvil-place-label-border);box-shadow:0 1px 3px rgba(0,0,0,.35);}';
    document.head.appendChild(s);
}

// A teardrop whose tip sits on the coordinate (the marker is bottom-anchored): ink body in a casing
// stroke, casing-colored eye. Stroke joins are round so the tip stays inside the 38px box.
function pinSvg() {
    const casing = Theme.color('--anvil-marker-ring', '#ffffff');
    const ink = Theme.color('--anvil-marker-reticle', '#2f8fff');
    return '<svg width="28" height="38" viewBox="0 0 28 38" aria-hidden="true">' +
        '<path d="M14,36 C14,36 3,22.5 3,13.5 A11,11 0 1 1 25,13.5 C25,22.5 14,36 14,36 Z" fill="' + ink +
        '" stroke="' + casing + '" stroke-width="2.5" stroke-linejoin="round"/>' +
        '<circle cx="14" cy="13.5" r="4" fill="' + casing + '"/>' +
        '</svg>';
}

// Place (or replace) the search pin at [lng, lat] with its name chip. A click selects it host-side.
export function showPlace(map, lng, lat, label) {
    ensurePlaceStyle();
    clearPlace();
    const el = document.createElement('div');
    el.className = 'place-pin';
    el.title = label || '';
    const art = document.createElement('div');
    art.className = 'place-pin-art';
    art.innerHTML = pinSvg();
    el.appendChild(art);
    if (label) {
        const chip = document.createElement('div');
        chip.className = 'place-pin-label';
        chip.textContent = label; // textContent, never innerHTML: the label is a place name from a geocoder
        el.appendChild(chip);
    }
    placeMarker = new maplibregl.Marker({ element: el, anchor: 'bottom' }).setLngLat([lng, lat]).addTo(map);
    el.addEventListener('click', function (ev) {
        ev.stopPropagation();
        postMarker('markerClick', PLACE_MARKER_ID);
    });
}

export function clearPlace() {
    if (placeMarker) { placeMarker.remove(); placeMarker = null; }
}
