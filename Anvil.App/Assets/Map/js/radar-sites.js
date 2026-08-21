// radar-sites.js — the on-map radar-site marker "key" buttons (extracted from map.js). Owns the
// marker DOM/state and the pushable-key CSS; map.js's window.showRadarSites / setSelectedRadarSite /
// setRadarSitesStatus / setRadarSitesVisible shims delegate here, passing the map. Posts radarSiteClick
// to the host on a key press. `maplibregl` is the global from the vendored classic script.
//
// ANATOMY OF ONE KEY — a pushable graphite button with three zones:
//
//        ┌─┬────────┬─┐        LEFT square  .radar-site-swatch  = AVAILABILITY
//        │█│  KTLX  │◗│                     green = data flowing · red (.offline) = nothing recent
//        └─┴────────┴─┘        CENTRE       .radar-site-label    = the ICAO
//         ▲     ▲    ▲         RIGHT bar    .radar-site-class    = NETWORK, and it MIRRORS the left
//         │     │    └── nexrad ◗ (graphite) · tdwr ✈ (blue) · research ⚗ (violet)
//         │     └── selected inverts the FACE to a light key; both end zones still read
//         └── availability, independent of selection
//
//   The class bar keeps its color through offline AND selection, on purpose: the two opt-in networks
//   are otherwise indistinguishable from the ~160 operational keys at a glance.
//
// COLLISION FAN-OUT — keeps the opt-in keys findable where they pile up (the OKC KTLX+TOKC+KCRI stack):
//
//        [TOKC]                     A special key that would overlap is pushed UP off the pile with a
//           ╎  ← leader line        dashed leader line + ring back to its true site, and snaps back
//           ○  ← true site          once zoom separates them. Operational NEXRAD keys NEVER move —
//        [KTLX] [KCRI]              they are the fixed obstacles the specials route around.
//
//   ⚠️ A special is de-overlapped only against its OWN pile (markers truly overlapping its spot, plus
//   specials already fanned from that pile) — never "avoid everything on screen". That bound is
//   load-bearing: avoid-everything let a key climb the dense national column and land ~380px away
//   (KCRI ended up in North Dakota at CONUS zoom). Pile-local means it rises about one key-height and
//   stops. The recompute is a cheap no-op while neither opt-in network is shown, which is the default.
//
// These are DOM-overlay markers (maplibregl.Marker), so they auto-reposition on pan/zoom and survive
// basemap switches (no style-layer re-add needed). Structure: a `.radar-site-marker` WRAPPER (which
// MapLibre positions via an inline transform) holds an inner `.radar-site-btn` (free to use its own
// transform for the press/sink effect) with THREE zones: a full-height availability SQUARE
// `.radar-site-swatch` on the LEFT + the ID text `.radar-site-label` + a full-height class bar
// `.radar-site-class` on the RIGHT. The SQUARE shows availability — green = available, red (.offline) =
// no recent data — always, independent of selection. The CLASS BAR mirrors it on the right, showing the
// radar's network: nexrad (neutral graphite, radar glyph) / tdwr (blue, plane) / research (violet, flask);
// its color survives offline + selection so class always reads. SELECTION is the inverted "light" key
// (dark text on near-white), and both end zones still show on the light face.
// (History: this was a small round dot before; the accent status halo + orange-selected + dead-key
// offline styling were removed in an earlier rework. The class bar was added when TDWR/research markers
// became visually indistinguishable from operational sites.)

import { coverageDistanceMeters } from './geo.js';

// Class glyphs for the RIGHT-side class bar (the mirror of the left availability square): a radar sweep
// for operational NEXRAD (neutral bar — stays quiet since it's the majority), a plane for TDWR (blue),
// a flask for research (violet). Inline SVG so they're self-contained in the WebView (no icon-font or
// emoji dependency); fill/stroke inherit `currentColor` from the bar's per-class color.
const CLASS_GLYPH = {
    nexrad: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="7" cy="17" r="1.4" fill="currentColor" stroke="none"/><path d="M7 12.5a4.5 4.5 0 0 1 4.5 4.5"/><path d="M7 7.5a9.5 9.5 0 0 1 9.5 9.5"/></svg>',
    tdwr: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 2c.6 0 1 .9 1 2v5.2l7 4v1.9l-7 -2v3.9l2 1.5v1.5l-3 -1l-3 1v-1.5l2 -1.5v-3.9l-7 2v-1.9l7 -4v-5.2c0 -1.1 .4 -2 1 -2z"/></svg>',
    research: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3h6"/><path d="M10 3v5l-4.4 8.3a1.9 1.9 0 0 0 1.7 2.7h9.4a1.9 1.9 0 0 0 1.7 -2.7l-4.4 -8.3v-5"/></svg>'
};

let radarMarkers = {};        // id -> inner button element (state ops target the button)
let radarMarkerObjs = [];     // every Marker object (for show/hide + teardown)
let selectedSiteId = null;
let radarSitesVisible = true;
let researchVisible = false;      // research/test radars (e.g. KCRI) are an opt-in extra layer
let researchIds = new Set();      // ids flagged research (site.research) in the current list
let tdwrVisible = false;          // Terminal Doppler Weather Radars (T***) are an opt-in extra layer
let tdwrIds = new Set();          // ids flagged tdwr (site.tdwr) in the current list
let radarSiteOffline = new Set(); // site ids with no recent data in the feed (red availability dot)
let siteCoords = {};              // id -> [lng, lat] (kept so the isolation filter can measure coverage)

// Collision fan-out: when the opt-in TDWR/research keys would pile onto a neighbor at low zoom (the OKC
// KTLX+TOKC+KCRI trio is the poster child), offset them off the stack — with a leader line back to the
// true site — so they stay findable/clickable at ALL zooms (never hidden). Operational NEXRAD keys never
// move: they're the fixed obstacles the special keys route around. See docs/app-notes.md (Radar).
let fanMap = null;                // the MapLibre map (project() + move/zoom events)
let fanListeners = false;         // guard: attach the move/zoom/resize handlers exactly once
let fanQueued = false;            // rAF debounce for the move handler
let fanLast = [];                 // offset wrappers displaced last pass (reset before recomputing)
let lineSvg = null;               // leader-line overlay, under the marker keys / over the map canvas
let keyW = 0, keyH = 0;           // measured key box (uniform — ICAO labels are all 4 chars); cached once

// State-isolation coverage filter: when a state is isolated, only sites whose usable range reaches the
// state stay visible (see setIsolation). Null rings = no isolation (all sites pass this gate).
let isolationRings = null;        // the isolated state's outer ring(s), or null
let coverageMeters = 0;           // a site "covers" the state if within this distance of it (radius in m)
let coveredIds = null;            // set of site ids that pass the coverage gate (null = gate off)

// Recompute which sites pass the coverage gate: within coverageMeters of the isolated state's rings. A
// site INSIDE the state is distance 0; adjacent-state radars pass when their umbrella overlaps the state.
function recomputeCoverage() {
    if (!isolationRings) { coveredIds = null; return; }
    coveredIds = new Set();
    Object.keys(siteCoords).forEach(function (id) {
        const c = siteCoords[id];
        if (coverageDistanceMeters(c[0], c[1], isolationRings) <= coverageMeters) coveredIds.add(id);
    });
}

// A marker shows only when the global sites layer is on AND each opt-in category it belongs to is on AND
// (when a state is isolated) the site's range covers that state. The currently-selected site is exempt
// from the coverage gate so it can't get stranded (its loop keeps rendering; you can still deselect it).
// So "Show Research Radars" / "Show TDWRs" reveal just those keys, and "Hide Sites" still hides everything.
function markerVisible(id) {
    return radarSitesVisible
        && (!researchIds.has(id) || researchVisible)
        && (!tdwrIds.has(id) || tdwrVisible)
        && (coveredIds === null || coveredIds.has(id) || id === selectedSiteId);
}

// Re-apply the visibility rule to every marker (after any toggle changes).
function applyVisibility() {
    radarMarkerObjs.forEach(function (m) {
        const id = m.getElement().dataset.siteId;
        m.getElement().style.display = markerVisible(id) ? '' : 'none';
    });
    updateFan(); // visibility changed which keys are on-screen — re-evaluate the collision fan
}

function ensureStyle() {
    if (document.getElementById('radar-site-style')) return;
    const siteStyle = document.createElement('style');
    siteStyle.id = 'radar-site-style';
    siteStyle.textContent = `
        .radar-site-marker { line-height: 0; }

        /* Pushable graphite "key": a full-height status SQUARE on the left + the ID on the face. */
        .radar-site-btn {
            display: inline-flex;
            align-items: stretch;              /* the status square fills the full key height */
            font: 700 12px/1 "Segoe UI", sans-serif;
            letter-spacing: .3px;
            color: #f3f3f3;
            background: linear-gradient(#3b3b3e, #2c2c2f);
            border: 1px solid #5a5a5e;
            border-radius: 6px;
            overflow: hidden;                  /* clip the square's corners to the key radius */
            cursor: pointer;
            white-space: nowrap;
            user-select: none;
            box-shadow: 0 3px 0 #161618, 0 4px 6px rgba(0, 0, 0, .45);
            transition: transform .05s ease, box-shadow .05s ease, filter .1s ease;
        }
        .radar-site-btn:hover { filter: brightness(1.18); }
        .radar-site-btn:active {
            transform: translateY(2px);
            box-shadow: 0 1px 0 #161618, 0 1px 2px rgba(0, 0, 0, .4);
        }

        /* Status square: green = available, red = offline (the staleness-ramp endpoint colors, so the
           palette matches the freshness readout). A full-height block filling the LEFT of the key; always
           shows availability, independent of selection (still reads on the light selected face). */
        .radar-site-swatch {
            flex: 0 0 auto;
            align-self: stretch;
            width: 22px;
            background: #3fb950;
        }
        .radar-site-btn.offline .radar-site-swatch { background: #f85149; }

        /* The ID text sits on the key face to the right of the square. */
        .radar-site-label { padding: 5px 9px; }

        /* Selected = inverted "light" key (dark text on a near-white face). Distinct from BOTH the dark
           unselected keys and the red/green square (orange sat too close to the offline red). Latches down
           onto its edge like a pressed key; the status square still shows availability on the light face.
           The active site's "radar" is also the big geographic range ring + sweep drawn on the MAP (radar.js). */
        .radar-site-btn.selected {
            color: #1a1a1a;
            background: linear-gradient(#ffffff, #e8e8e8);
            border-color: #b9b9b9;
            transform: translateY(2px);
            box-shadow: 0 1px 0 #9a9a9a, 0 1px 3px rgba(0, 0, 0, .4);
        }
        .radar-site-btn.selected:hover { filter: brightness(1.03); }

        /* Class bar: the RIGHT-side mirror of the availability square, showing the radar's CLASS. Operational
           NEXRAD = a neutral graphite bar with a radar glyph (stays quiet — it's the majority); TDWR = blue
           (plane); research = violet (flask). COLOR flags the special networks; the bar keeps its color
           through offline (left square turns red) and selection (face turns white), so class identity always
           reads. Full-height + clipped to the key radius by the button's overflow:hidden, like the square. */
        .radar-site-class {
            flex: 0 0 auto;
            align-self: stretch;
            width: 22px;
            display: flex;
            align-items: center;
            justify-content: center;
        }
        .radar-site-class svg { width: 13px; height: 13px; display: block; }
        .radar-site-class.nexrad { background: #44444a; color: #cfcfd3; border-left: 1px solid #5a5a5e; }
        .radar-site-class.tdwr { background: #2f6fb0; color: #ffffff; }
        .radar-site-class.research { background: #6b4bd6; color: #ffffff; }

        /* Fan-out offset wrapper: MapLibre positions the OUTER .radar-site-marker at the true lng/lat, so
           the collision fan translates this inner wrapper instead — keeping the marker's true anchor (and
           the leader-line origin) put while the visible key slides off the pile. Inline-flex so it hugs the
           key and doesn't change the marker's measured size. */
        .radar-site-offset { display: inline-flex; }`;
    document.head.appendChild(siteStyle);
}

// Applies a marker's availability (dot color via the .offline class) + tooltip from the offline set.
function applySiteStatus(el, id) {
    const off = radarSiteOffline.has(id);
    el.classList.toggle('offline', off);
    const name = el.dataset.siteName || '';
    el.title = name + (off ? ' · offline (no recent data)' : '');
}

// ── Collision fan-out ───────────────────────────────────────────────────────────────────────────────

// The overlay <svg> holding the leader lines. Inserted right AFTER the map's canvas container so it paints
// ABOVE the basemap but BELOW the marker keys (pure DOM paint order — no z-index juggling needed).
function ensureLineOverlay(map) {
    if (lineSvg && lineSvg.isConnected) return;
    const container = map.getContainer();
    lineSvg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    lineSvg.id = 'radar-site-lines';
    lineSvg.setAttribute('style', 'position:absolute;inset:0;width:100%;height:100%;pointer-events:none;overflow:visible');
    const canvas = container.querySelector('.maplibregl-canvas-container');
    container.insertBefore(lineSvg, canvas ? canvas.nextSibling : container.firstChild);
}

// Attach the recompute triggers (once): map move/zoom/resize, rAF-debounced so a burst of move events
// collapses to one recompute per frame.
function attachFanListeners(map) {
    fanMap = map;
    ensureLineOverlay(map);
    if (fanListeners) return;
    fanListeners = true;
    const onMove = function () {
        if (fanQueued) return;
        fanQueued = true;
        requestAnimationFrame(function () { fanQueued = false; updateFan(); });
    };
    map.on('move', onMove);
    map.on('zoom', onMove);
    map.on('resize', onMove);
}

// Re-evaluate the fan. NEXRAD keys stay at their true screen position (fixed obstacles); each visible
// TDWR/research key that would overlap a neighbor is pushed UP off the pile until clear, and gets a
// dashed leader line + ring back to its true site. Cheap no-op when neither opt-in network is shown (the
// default), and when zoomed in far enough that nothing overlaps (keys snap back to true positions).
function updateFan() {
    if (!fanMap) return;
    // Reset whatever we displaced last pass so separated keys return to true positions.
    fanLast.forEach(function (o) { o.style.transform = ''; if (o.parentElement) o.parentElement.style.zIndex = ''; });
    fanLast = [];
    if (!researchVisible && !tdwrVisible) { if (lineSvg) lineSvg.innerHTML = ''; return; }

    const vis = [];
    radarMarkerObjs.forEach(function (m) {
        const el = m.getElement();
        if (el.style.display === 'none') return;
        const ll = siteCoords[el.dataset.siteId];
        if (!ll) return;
        const p = fanMap.project(ll);
        const cls = el.dataset.siteClass;
        vis.push({ el: el, x: p.x, y: p.y, special: cls === 'tdwr' || cls === 'research', cls: cls });
    });
    const specials = vis.filter(function (v) { return v.special; });
    if (specials.length === 0) { if (lineSvg) lineSvg.innerHTML = ''; return; }

    // Key box is uniform (4-char ICAOs); measure one live key once, then cache.
    if (!keyW) {
        const sample = radarMarkerObjs.length ? radarMarkerObjs[0].getElement().querySelector('.radar-site-btn') : null;
        const r = sample ? sample.getBoundingClientRect() : null;
        if (r && r.width) { keyW = r.width; keyH = r.height; }
    }
    const W = keyW || 92, H = keyH || 26, GAP = 6;
    const overlaps = function (ax, ay, bx, by) { return Math.abs(ax - bx) < W && Math.abs(ay - by) < H + GAP; };

    // Deterministic fan order: TDWR before research, then by screen y.
    const operational = vis.filter(function (v) { return !v.special; });
    specials.sort(function (a, b) { return a.cls !== b.cls ? (a.cls === 'tdwr' ? -1 : 1) : a.y - b.y; });

    // ⚠️ Each special is fanned ONLY off the markers that TRULY overlap its own spot (its immediate PILE),
    // plus any special already fanned from that same pile. The obstacle set is FIXED by true position, so a
    // key rises just clear of its pile and STOPS — it never climbs the map's dense national field (an
    // "avoid everything on screen" rule sent KCRI ~380px up into North Dakota). Net effect: just enough to
    // reveal the whole button, no more.
    const placedSpecials = []; // { tx, ty, x, y } — each fanned special's TRUE + FINAL screen position
    let lines = '';
    specials.forEach(function (v) {
        const obstacles = [];
        operational.forEach(function (o) { if (overlaps(v.x, v.y, o.x, o.y)) obstacles.push({ x: o.x, y: o.y }); });
        placedSpecials.forEach(function (p) { if (overlaps(v.x, v.y, p.tx, p.ty)) obstacles.push({ x: p.x, y: p.y }); });

        let x = v.x, y = v.y, bumped = true, guard = 0;
        while (bumped && guard < 8) {
            bumped = false; guard++;
            for (let i = 0; i < obstacles.length; i++) {
                if (overlaps(x, y, obstacles[i].x, obstacles[i].y)) { y = obstacles[i].y - (H + GAP); bumped = true; }
            }
        }
        placedSpecials.push({ tx: v.x, ty: v.y, x: x, y: y });

        const dx = x - v.x, dy = y - v.y;
        if (Math.abs(dx) > 0.5 || Math.abs(dy) > 0.5) {
            const o = v.el.querySelector('.radar-site-offset');
            if (o) { o.style.transform = 'translate(' + dx.toFixed(1) + 'px,' + dy.toFixed(1) + 'px)'; fanLast.push(o); }
            v.el.style.zIndex = '4'; // the offset key rides above the leader-line overlay + operational keys
            lines += '<circle cx="' + v.x.toFixed(1) + '" cy="' + v.y.toFixed(1) + '" r="3" fill="none" stroke="#8a8f98" stroke-width="1.5"/>'
                + '<line x1="' + v.x.toFixed(1) + '" y1="' + v.y.toFixed(1) + '" x2="' + x.toFixed(1) + '" y2="' + y.toFixed(1) + '" stroke="#8a8f98" stroke-width="1" stroke-dasharray="2 2"/>';
        }
    });
    if (lineSvg) lineSvg.innerHTML = lines;
}

// Provide the site list (as buttons). Each wrapper is the marker MapLibre positions; the inner
// button is the styled key. A press posts radarSiteClick to the host.
export function show(map, json) {
    ensureStyle();
    const sites = (typeof json === 'string') ? JSON.parse(json) : json;
    radarMarkerObjs.forEach(function (m) { m.remove(); });
    radarMarkerObjs = [];
    radarMarkers = {};
    researchIds = new Set();
    tdwrIds = new Set();
    siteCoords = {};
    fanLast = [];                          // old offset wrappers are gone with the removed markers
    if (lineSvg) lineSvg.innerHTML = '';   // drop any leader lines from the previous list
    sites.forEach(function (s) {
        if (s.research) researchIds.add(s.id);
        if (s.tdwr) tdwrIds.add(s.id);
        siteCoords[s.id] = [s.lng, s.lat];
        const el = document.createElement('div');
        el.className = 'radar-site-marker';
        el.dataset.siteId = s.id; // used by applyVisibility to re-evaluate the per-marker rule
        const btn = document.createElement('div');
        btn.className = 'radar-site-btn';
        const swatch = document.createElement('span');
        swatch.className = 'radar-site-swatch'; // availability: green (available) / red (.offline)
        const label = document.createElement('span');
        label.className = 'radar-site-label';
        label.textContent = s.id;
        // Right-side class bar: nexrad (neutral) / tdwr (blue) / research (violet), each with its glyph.
        const klass = s.tdwr ? 'tdwr' : (s.research ? 'research' : 'nexrad');
        const clsBar = document.createElement('span');
        clsBar.className = 'radar-site-class ' + klass;
        clsBar.innerHTML = CLASS_GLYPH[klass];
        btn.appendChild(swatch);
        btn.appendChild(label);
        btn.appendChild(clsBar);
        btn.dataset.siteName = s.name || '';
        el.dataset.siteClass = klass; // for the collision fan-out + any future per-class styling
        // The offset wrapper is the fan's translate target — see .radar-site-offset / updateFan.
        const offset = document.createElement('div');
        offset.className = 'radar-site-offset';
        offset.appendChild(btn);
        el.appendChild(offset);
        if (!markerVisible(s.id)) el.style.display = 'none';
        if (selectedSiteId === s.id) btn.classList.add('selected');
        applySiteStatus(btn, s.id); // sets .down class + tooltip from the current offline set
        btn.addEventListener('click', function (ev) {
            ev.stopPropagation();
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'radarSiteClick', id: s.id }));
            }
        });
        const marker = new maplibregl.Marker({ element: el }).setLngLat([s.lng, s.lat]).addTo(map);
        radarMarkerObjs.push(marker);
        radarMarkers[s.id] = btn; // state ops (selected/down/tooltip) target the inner button
    });
    recomputeCoverage(); // if a state is already isolated, apply the coverage gate to the fresh markers
    attachFanListeners(map); // wire move/zoom recompute + the leader-line overlay (once)
    applyVisibility();       // ends with updateFan(), so the initial fan is applied here
}

export function setSelected(id) {
    selectedSiteId = id || null;
    Object.keys(radarMarkers).forEach(function (k) {
        radarMarkers[k].classList.toggle('selected', k === selectedSiteId);
    });
    applyVisibility(); // the selected site is exempt from the coverage gate — re-evaluate on change
}

// State-isolation coverage filter. rings = the isolated state's outer ring(s) (null clears the filter);
// radiusKm = a radar's usable range (~230 km for WSR-88D reflectivity). Sites whose range doesn't reach
// the state are hidden so the isolated view isn't cluttered with markers floating over the masked void —
// but neighbors whose umbrella overlaps the state stay, so coverage holes are still reachable. Driven by
// states.js (via map.js) on every isolation change.
export function setIsolation(rings, radiusKm) {
    isolationRings = (rings && rings.length) ? rings : null;
    coverageMeters = (radiusKm || 0) * 1000;
    recomputeCoverage();
    applyVisibility();
}

// Which sites are offline (array of ids). Re-styles existing markers.
export function setStatus(json) {
    try { radarSiteOffline = new Set((typeof json === 'string') ? JSON.parse(json) : json); }
    catch (e) { radarSiteOffline = new Set(); }
    Object.keys(radarMarkers).forEach(function (k) { applySiteStatus(radarMarkers[k], k); });
}

// No-op: the on-map markers no longer use the OS accent (the halo was removed — availability is a fixed
// green/red DOT and selection is the inverted-light key). Kept so the host's setRadarSitesAccent shim
// (MapService.SetRadarSiteAccentAsync → map.js) stays valid; the OverlayBar still uses the accent itself.
export function setAccent(border, glow) { /* markers no longer use an accent halo */ }

// Show/hide all site buttons. Independent of the radar layer — an active loop keeps rendering while
// the markers are hidden. Research markers stay subject to their own toggle via markerVisible().
export function setVisible(visible) {
    radarSitesVisible = !!visible;
    applyVisibility();
}

// Show/hide just the research/test radar markers (the "Show Research Radars" toggle). Off by
// default; operational markers are unaffected. An active research loop keeps rendering while hidden.
export function setResearchVisible(visible) {
    researchVisible = !!visible;
    applyVisibility();
}

// Show/hide just the TDWR markers (the "Show TDWRs" toggle). Off by default; operational markers are
// unaffected. An active TDWR loop keeps rendering while hidden.
export function setTdwrVisible(visible) {
    tdwrVisible = !!visible;
    applyVisibility();
}
