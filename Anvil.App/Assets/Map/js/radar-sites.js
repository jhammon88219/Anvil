// radar-sites.js — the on-map radar-site marker "key" buttons (extracted from map.js). Owns the
// marker DOM/state and the pushable-key CSS; map.js's window.showRadarSites / setSelectedRadarSite /
// setRadarSitesStatus / setRadarSitesVisible shims delegate here, passing the map. Posts radarSiteClick
// to the host on a key press. `maplibregl` is the global from the vendored classic script.
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
        .radar-site-class.research { background: #6b4bd6; color: #ffffff; }`;
    document.head.appendChild(siteStyle);
}

// Applies a marker's availability (dot color via the .offline class) + tooltip from the offline set.
function applySiteStatus(el, id) {
    const off = radarSiteOffline.has(id);
    el.classList.toggle('offline', off);
    const name = el.dataset.siteName || '';
    el.title = name + (off ? ' · offline (no recent data)' : '');
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
        el.dataset.siteClass = klass; // for the collision fan-out (step 2) + any future per-class styling
        el.appendChild(btn);
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
    applyVisibility();
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
