// radar-sites.js — the on-map radar-site marker "key" buttons (extracted from map.js). Owns the
// marker DOM/state and the pushable-key CSS; map.js's window.showRadarSites / setSelectedRadarSite /
// setRadarSitesStatus / setNexradSitesVisible (+ TDWR / research) shims delegate here, passing the map. Posts
// radarSiteClick (the NAME) and radarSiteMenu (the CHEVRON) to the host. `maplibregl` is the global from the
// vendored classic script.
//
// ANATOMY OF ONE KEY — ONE colour, three seamless zones (no dividers), outline = the ink colour:
//
//        ╭──────────────╮      LEFT   .radar-site-glyph  = the NETWORK glyph: nexrad ◗ · tdwr ✈ · research ⚗
//        │ ◗  KTLX   ⌄  │      CENTRE .radar-site-label  = the ICAO — click LOADS (toggles, like before)
//        ╰──────────────╯      RIGHT  .radar-site-chev   = click OPENS the host's WinUI site menu
//                                                          (Load radar · Open in Atlas · home · favorite)
//
//   THE COLOUR IS THE STATE (DATA — literal, never themed): a deep shade + a bright ink of ONE hue, the
//   PastCast event-type pill's scheme.
//
//                   online                 offline                 not checked yet
//      NEXRAD       green                  red                     grey
//      TDWR         blue                   red                     grey
//      research     violet                 red                     grey
//
//   · online = the NETWORK's colour; offline / not checked override it (the glyph still names the network).
//   · OFFLINE is inert: no hover, no hand, the name does nothing — EXCEPT the chevron, which stays live
//     (near-white, hovers alone) so the menu can still open the Atlas, where the outage is explained.
//     The menu greys "Load radar" for it.
//   · NOT CHECKED: dark-grey glyph/name/outline, light-grey chevron; loads like an online key.
//   · HOVER lifts the WHOLE key's background one step. SELECTED inverts: dark ink on the bright colour.
//   · a pass CASCADES: each key eases grey → its colour as ITS check lands (the background transition).
//
//   SIZES: 22px tall inside the 1px outline (12px text + 5px top/bottom padding); glyph + chevron zones
//   22px wide; label 7px side padding.
//
//   ⚠️ HIT TESTING: only the KEY takes the pointer. The MapLibre wrapper and the fan's offset wrapper are
//   pointer-events:none — a fanned key used to leave its wrapper's empty box at the TRUE site, above the
//   NEXRAD key there, eating that key's clicks. And nothing MOVES on press (the old 2px "push" slid the key
//   out from under a click near its top edge, so the release landed off it and the click never fired).
//
// COLLISION FAN-OUT — keeps the opt-in keys findable where they pile up (the OKC KTLX+TOKC+KCRI stack):
//
//        [TOKC]                     A special key that would overlap is pushed UP off the pile with a
//           ╎  ← leader line        dashed leader line + ring back to its true site, and snaps back
//           ○  ← true site          once zoom separates them. Operational NEXRAD keys NEVER move —
//        [KTLX] [KCRI]              they are the fixed obstacles the specials route around.
//
//   A key MOVES only when its true spot is covered (an operational key, or a special already placed). It
//   then takes the NEAREST clear spot from a short fixed list (up, down, sides, diagonals, two/three rows
//   up), checked against EVERY nearby key — the old rule checked only its own pile, so a lifted key could
//   land on a neighbour and needed a zoom to separate. ⚠️ The list is BOUNDED (≤ 3 rows) on purpose: an
//   "avoid everything, keep climbing" rule once sent KCRI ~380px up into North Dakota at CONUS zoom. When
//   no spot is clear it takes the one with the fewest collisions. A cheap no-op while neither opt-in
//   network is shown, which is the default.
//
// WHICH KEYS SHOW — THE VISIBILITY RULES table below (network · era | declutter: isolation · reveal · focus).
//
//        rules:  network ✓  era ✓  isolation ✓  reveal ✗   → hidden        a key shows only when EVERY rule passes;
//        loaded: network ✓  era ✓  (declutter skipped)      → shown         the LOADED site skips the declutter ones
//
// These are DOM-overlay markers (maplibregl.Marker), so they auto-reposition on pan/zoom and survive
// basemap switches (no style-layer re-add needed). Structure: a `.radar-site-marker` WRAPPER (MapLibre
// positions it via an inline transform) > `.radar-site-offset` (the fan's translate target) > the
// `.radar-site-btn` key with its three zones.
// (History: a round dot → a graphite key with a left availability square + a right network bar → this
// one-colour key with a menu chevron, 2026-10-04.)

import { coverageDistanceMeters } from './geo.js';
// The leader lines are SVG presentation attributes, which can't read a CSS variable — so this one
// color comes through theme.js. Everything else the keys draw is CSS and uses var() directly.
import * as Theme from './theme.js';

// Network glyphs for the key's LEFT zone: a radar sweep for operational NEXRAD, a plane for TDWR, a flask
// for research. Inline SVG so they're self-contained in the WebView (no icon-font or emoji dependency);
// fill/stroke inherit `currentColor` — the key's ink. The SHAPE is what names the network on an offline or
// unchecked key, where the colour says status instead.
// The nexrad viewBox is CROPPED to its drawing (which sits in the lower-left of a 24 box) so it centres in
// the square and reads the same size as the plane and flask.
const CLASS_GLYPH = {
    nexrad: '<svg viewBox="2.5 3.5 18 18"fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="7" cy="17" r="1.4" fill="currentColor" stroke="none"/><path d="M7 12.5a4.5 4.5 0 0 1 4.5 4.5"/><path d="M7 7.5a9.5 9.5 0 0 1 9.5 9.5"/></svg>',
    tdwr: '<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 2c.6 0 1 .9 1 2v5.2l7 4v1.9l-7 -2v3.9l2 1.5v1.5l-3 -1l-3 1v-1.5l2 -1.5v-3.9l-7 2v-1.9l7 -4v-5.2c0 -1.1 .4 -2 1 -2z"/></svg>',
    research: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3h6"/><path d="M10 3v5l-4.4 8.3a1.9 1.9 0 0 0 1.7 2.7h9.4a1.9 1.9 0 0 0 1.7 -2.7l-4.4 -8.3v-5"/></svg>'
};

// The menu chevron (the key's RIGHT zone).
const CHEVRON_GLYPH = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"><path d="M6 9l6 6l6 -6"/></svg>';

let radarMarkers = {};        // id -> inner button element (state ops target the button)
let radarMarkerObjs = [];     // every Marker object (for show/hide + teardown)
let selectedSiteId = null;
let nexradVisible = true;         // operational NEXRAD keys — one of THREE per-network toggles (no master)
let researchVisible = false;      // research/test radars (e.g. KCRI) are an opt-in extra layer
let researchIds = new Set();      // ids flagged research (site.research) in the current list
let tdwrVisible = false;          // Terminal Doppler Weather Radars (T***) are an opt-in extra layer
let tdwrIds = new Set();          // ids flagged tdwr (site.tdwr) in the current list
let outOfEraIds = new Set();      // retired ids (KLIX, TPBI) outside the era being viewed — C# decides, pushes the set
let radarSiteOffline = new Set(); // site ids KNOWN to have no recent data (red availability square)
let radarSiteUnknown = null;      // site ids not checked yet; null = no status push yet, so EVERY site is unknown (grey)
let radarStatusReplayDay = false; // true while the status describes the PastCast TIMEFRAME, not the live feed
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

// ── THE VISIBILITY RULES ─────────────────────────────────────────────────────────────────────────
// ONE table decides whether a key shows: it shows only when EVERY rule passes. Each feature owns exactly ONE
// rule (its state + the setter below that changes it); markerVisible reads the table and applyVisibility is
// the one pass that writes display. Two kinds of rule:
//
//   WHAT EXISTS — binds the loaded site too:      network   its network's toggle is on (NEXRAD / TDWR /
//                                                           research, the tools tier — all off = no keys)
//                                                 era       the radar existed then (RadarViewModel.IsInEra)
//   DECLUTTER — NEVER hides the LOADED site       isolation its range reaches the isolated state
//   (it must not strand its own loop; you can     reveal    inside the cursor's ring (SITE-REVEAL)
//   still unload it by clicking it again):        focus     while a site is LOADED, only it shows
//
// Rules COMPOSE BY AND, so they cannot fight: with focus on and a site loaded, only that site shows whatever
// the others say; with nothing loaded, focus passes everything and the rest decide.
// ⚠️ A NEW WAY TO HIDE KEYS = ONE entry here (+ its state and setter). Never another && in markerVisible, and
//    never a display write anywhere but applyVisibility — that is how the features used to stack up.
let revealedIds = null;  // SITE-REVEAL: ids inside the cursor's ring; null = the reveal is off
let focusOn = false;     // setFocus: hide the other sites while one is loaded (Settings → Radar)
const RULES = [
    { name: 'network',   declutter: false, test: function (id) { return researchIds.has(id) ? researchVisible : tdwrIds.has(id) ? tdwrVisible : nexradVisible; } },
    { name: 'era',       declutter: false, test: function (id) { return !outOfEraIds.has(id); } },
    { name: 'isolation', declutter: true,  test: function (id) { return coveredIds === null || coveredIds.has(id); } },
    { name: 'reveal',    declutter: true,  test: function (id) { return revealedIds === null || revealedIds.has(id); } }, // SITE-REVEAL
    { name: 'focus',     declutter: true,  test: function () { return !focusOn || selectedSiteId === null; } },
];

function markerVisible(id) {
    for (let i = 0; i < RULES.length; i++) {
        const r = RULES[i];
        if (r.declutter && id === selectedSiteId) continue;
        if (!r.test(id)) return false;
    }
    return true;
}

// FOCUS: while a site is loaded, only it shows; clicking it again unloads it (RadarLoopEngine.OnRadarSiteClicked)
// and the rest come back. Its trigger is setSelected, which already re-applies the rules.
export function setFocus(on) {
    focusOn = !!on;
    applyVisibility();
}

// ── SITE-REVEAL (experimental bolt-on, site-reveal.js; grep SITE-REVEAL to excise) ───────────────
// site-reveal.js reads the coordinates and pushes the set inside its ring (null = the reveal is off).
export function setRevealed(ids) {
    revealedIds = ids ? new Set(ids) : null;
    applyVisibility();
}
export function siteCoordinates() { return siteCoords; }
// ── end SITE-REVEAL ──────────────────────────────────────────────────────────────────────────────

// Re-apply THE VISIBILITY RULES to every marker — the ONE place a key's display is written.
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
        /* ⚠️ Only the KEY takes the pointer — see "HIT TESTING" in the header. */
        .radar-site-marker { line-height: 0; pointer-events: none; }

        /* ⚠️ DATA, NOT CHROME — every colour here is literal and never themed: the colour IS the site's
           status (and, online, its network). A theme that could restyle it would be able to lie. Each
           state sets the same six variables; the rules below only read them.
             --k-bg   resting face (a deep shade)      --k-hv    hover face (one step up)
             --k-ink  glyph, name, outline, chevron    --k-chev  chevron ink, where it differs
             --k-sbg  selected face (the bright ink)   --k-shv   selected hover   --k-sink selected ink
           Online = the NETWORK's hue (the PastCast type pill's pairs); offline + unknown come LATER in the
           sheet so they win over the network at equal specificity.
           The three --k-ink hues are MIRRORED as the lit glyphs of MapControlsStrip.xaml's site toggles — change both. */
        .radar-site-btn.nexrad   { --k-bg: #163f1e; --k-hv: #22582b; --k-ink: #6fdb7f; --k-sbg: #6fdb7f; --k-shv: #8ae698; --k-sink: #0f2c15; }
        .radar-site-btn.tdwr     { --k-bg: #17315e; --k-hv: #22457f; --k-ink: #7aabfa; --k-sbg: #7aabfa; --k-shv: #97befc; --k-sink: #0e2142; }
        .radar-site-btn.research { --k-bg: #2a1e5a; --k-hv: #3a2a7a; --k-ink: #a893f5; --k-sbg: #a893f5; --k-shv: #bdadf8; --k-sink: #1f1545; }
        /* Offline: red throughout, the chevron near-white — it is the one live part. */
        .radar-site-btn.offline  { --k-bg: #57181a; --k-hv: #732226; --k-ink: #f07070; --k-chev: #f2f2f2; --k-sbg: #f07070; --k-shv: #f48b8b; --k-sink: #3d0f10; }
        /* Not checked yet: grey, so a site is never green merely because nobody has looked; light chevron. */
        .radar-site-btn.unknown  { --k-bg: #33363a; --k-hv: #44484d; --k-ink: #7a7f86; --k-chev: #b0b6be; --k-sbg: #b0b6be; --k-shv: #c4c9d0; --k-sink: #26292c; }

        .radar-site-btn {
            display: inline-flex;
            align-items: stretch;
            font: 700 12px/1 "Segoe UI", sans-serif;
            letter-spacing: .3px;
            color: var(--k-ink);
            background: var(--k-bg);
            border: 1px solid var(--k-ink);
            border-radius: 6px;
            overflow: hidden;
            cursor: pointer;
            white-space: nowrap;
            user-select: none;
            pointer-events: auto;
            box-shadow: 0 2px 5px rgba(0, 0, 0, .45);
            /* Eases a key grey → its colour as its site's check lands (the cascade). Short, because the
               same transition carries the hover. */
            transition: background-color .15s ease, color .15s ease, border-color .15s ease;
        }
        .radar-site-btn:not(.offline):hover { background: var(--k-hv); }

        /* LEFT: the network glyph. */
        .radar-site-glyph { flex: 0 0 22px; display: flex; align-items: center; justify-content: center; }
        .radar-site-glyph svg { width: 15px; height: 15px; display: block; }

        /* CENTRE: the ICAO. The VERTICAL padding sets the key's height (12px text + 5+5 = 22px). */
        .radar-site-label { padding: 5px 7px 5px 3px; }

        /* RIGHT: the menu chevron. */
        .radar-site-chev {
            flex: 0 0 22px;
            display: flex;
            align-items: center;
            justify-content: center;
            color: var(--k-chev, var(--k-ink));
            transition: background-color .15s ease;
        }
        .radar-site-chev svg { width: 13px; height: 13px; display: block; }

        /* OFFLINE is inert except its chevron: no hand on the body, and the chevron hovers ALONE. */
        .radar-site-btn.offline { cursor: default; }
        .radar-site-btn.offline .radar-site-chev { cursor: pointer; }
        .radar-site-btn.offline .radar-site-chev:hover { background: var(--k-hv); }

        /* Selected = INVERTED: dark ink on the bright colour. The active site's "radar" is also the big
           geographic range ring drawn on the MAP (radar.js). */
        .radar-site-btn.selected { background: var(--k-sbg); color: var(--k-sink); border-color: var(--k-sbg); }
        .radar-site-btn.selected .radar-site-chev { color: var(--k-sink); }
        .radar-site-btn.selected:not(.offline):hover { background: var(--k-shv); border-color: var(--k-shv); }
        .radar-site-btn.selected.offline .radar-site-chev:hover { background: var(--k-shv); }

        /* Fan-out offset wrapper: MapLibre positions the OUTER .radar-site-marker at the true lng/lat, so
           the collision fan translates this inner wrapper instead — keeping the marker's true anchor (and
           the leader-line origin) put while the visible key slides off the pile. Inline-flex so it hugs the
           key and doesn't change the marker's measured size. Takes no pointer (see HIT TESTING). */
        .radar-site-offset { display: inline-flex; pointer-events: none; }`;
    document.head.appendChild(siteStyle);
}

// Applies a marker's availability (square color via .offline / .unknown) + tooltip from the pushed status.
// ⚠️ The host (RadarViewModel's SITE AVAILABILITY block) pushes the SAME state the Atlas rows hold, so the
// tooltip words match the Atlas's status pill.
function applySiteStatus(el, id) {
    const unknown = radarSiteUnknown === null || radarSiteUnknown.has(id);
    const off = !unknown && radarSiteOffline.has(id);
    el.classList.toggle('unknown', unknown);
    el.classList.toggle('offline', off);
    const name = el.dataset.siteName || '';
    // PastCast (radarStatusReplayDay): only the sites you've loaded are checked, for the loaded timeframe.
    el.title = name + (unknown ? (radarStatusReplayDay ? ' · not checked for this timeframe' : ' · checking availability')
        : off ? (radarStatusReplayDay ? ' · no data for this timeframe' : ' · offline (no recent data)')
        : '');
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
// TDWR/research key whose true spot is covered moves to the nearest clear slot (see the header's
// COLLISION FAN-OUT) and gets a dashed leader line + ring back to its true site. Cheap no-op when neither opt-in network is shown (the
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
    // A step to the next slot: one key plus a gap, on each axis.
    const SX = W + GAP, SY = H + GAP;
    // TRIGGER: does a key at (ax,ay) cover (bx,by)? (Vertical gap included — a key sitting flush on another
    // still reads as one pile.)
    const covers = function (ax, ay, bx, by) { return Math.abs(ax - bx) < W && Math.abs(ay - by) < H + GAP; };
    // LANDING: a candidate spot must clear its neighbours by a gap on BOTH axes.
    const crowds = function (ax, ay, bx, by) { return Math.abs(ax - bx) < SX && Math.abs(ay - by) < SY; };
    // The candidate spots, NEAREST first, in steps of (SX, SY). ⚠️ Bounded at three rows — see the header.
    const SLOTS = [[0, -1], [0, 1], [-1, 0], [1, 0], [-1, -1], [1, -1], [-1, 1], [1, 1],
        [0, -2], [0, 2], [-1, -2], [1, -2], [0, -3]];

    // Deterministic fan order: TDWR before research, then by screen y.
    const operational = vis.filter(function (v) { return !v.special; });
    specials.sort(function (a, b) { return a.cls !== b.cls ? (a.cls === 'tdwr' ? -1 : 1) : a.y - b.y; });

    // What a key must avoid: every operational key (fixed, at its true spot) + every special ALREADY placed
    // (at its final spot). Specials not placed yet are skipped — they avoid this one when their turn comes.
    const placed = []; // { x, y } — each placed special's FINAL screen position
    const nearby = function (v) {
        const out = [];
        const rx = 3 * SX, ry = 5 * SY; // a slot is ≤ 1 col / 3 rows out; a placed special ≤ 3 rows from ITS spot
        operational.forEach(function (o) { if (Math.abs(o.x - v.x) < rx && Math.abs(o.y - v.y) < ry) out.push(o); });
        placed.forEach(function (p) { if (Math.abs(p.x - v.x) < rx && Math.abs(p.y - v.y) < ry) out.push(p); });
        return out;
    };

    let lines = '';
    specials.forEach(function (v) {
        const around = nearby(v);
        let x = v.x, y = v.y;
        if (around.some(function (o) { return covers(v.x, v.y, o.x, o.y); })) {
            // Covered — take the nearest slot that clears every neighbour, else the least-crowded one.
            let best = null, bestHits = Infinity;
            for (let i = 0; i < SLOTS.length && bestHits > 0; i++) {
                const cx = v.x + SLOTS[i][0] * SX, cy = v.y + SLOTS[i][1] * SY;
                let hits = 0;
                around.forEach(function (o) { if (crowds(cx, cy, o.x, o.y)) hits++; });
                if (hits < bestHits) { bestHits = hits; best = [cx, cy]; }
            }
            x = best[0]; y = best[1];
        }
        placed.push({ x: x, y: y });

        const dx = x - v.x, dy = y - v.y;
        if (Math.abs(dx) > 0.5 || Math.abs(dy) > 0.5) {
            const o = v.el.querySelector('.radar-site-offset');
            if (o) { o.style.transform = 'translate(' + dx.toFixed(1) + 'px,' + dy.toFixed(1) + 'px)'; fanLast.push(o); }
            v.el.style.zIndex = '4'; // the offset key rides above the leader-line overlay + operational keys
            const leader = Theme.color('--anvil-leader', '#8a8f98');
            lines += '<circle cx="' + v.x.toFixed(1) + '" cy="' + v.y.toFixed(1) + '" r="3" fill="none" stroke="' + leader + '" stroke-width="1.5"/>'
                + '<line x1="' + v.x.toFixed(1) + '" y1="' + v.y.toFixed(1) + '" x2="' + x.toFixed(1) + '" y2="' + y.toFixed(1) + '" stroke="' + leader + '" stroke-width="1" stroke-dasharray="2 2"/>';
        }
    });
    if (lineSvg) lineSvg.innerHTML = lines;
}

// Provide the site list (as buttons). Each wrapper is the marker MapLibre positions; the inner
// button is the styled key. The name posts radarSiteClick, the chevron radarSiteMenu.
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
        // The network is a class on the key (it picks the ONLINE colour); .offline / .unknown override it.
        const klass = s.tdwr ? 'tdwr' : (s.research ? 'research' : 'nexrad');
        const btn = document.createElement('div');
        btn.className = 'radar-site-btn ' + klass;
        const glyph = document.createElement('span');
        glyph.className = 'radar-site-glyph';
        glyph.innerHTML = CLASS_GLYPH[klass];
        const label = document.createElement('span');
        label.className = 'radar-site-label';
        label.textContent = s.id;
        const chev = document.createElement('span');
        chev.className = 'radar-site-chev';
        chev.innerHTML = CHEVRON_GLYPH;
        btn.appendChild(glyph);
        btn.appendChild(label);
        btn.appendChild(chev);
        btn.dataset.siteName = s.name || '';
        el.dataset.siteClass = klass; // for the collision fan-out
        // The offset wrapper is the fan's translate target — see .radar-site-offset / updateFan.
        const offset = document.createElement('div');
        offset.className = 'radar-site-offset';
        offset.appendChild(btn);
        el.appendChild(offset);
        if (selectedSiteId === s.id) btn.classList.add('selected');
        applySiteStatus(btn, s.id); // sets .offline / .unknown + the tooltip from the current status
        // ONE listener for the key; the zone under the click picks the action. The CHEVRON asks the host for
        // its WinUI site menu, anchored under the key (client px = WebView DIPs at zoom 1). The NAME/glyph
        // loads — except on an OFFLINE key, whose body is inert (the menu says why).
        btn.addEventListener('click', function (ev) {
            ev.stopPropagation();
            if (!(window.chrome && window.chrome.webview)) return;
            if (ev.target.closest('.radar-site-chev')) {
                const r = btn.getBoundingClientRect();
                window.chrome.webview.postMessage(JSON.stringify({ type: 'radarSiteMenu', id: s.id, x: r.left, y: r.bottom + 4 }));
                return;
            }
            if (btn.classList.contains('offline')) return;
            window.chrome.webview.postMessage(JSON.stringify({ type: 'radarSiteClick', id: s.id }));
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

// Site availability from the host: { offline: [ids], unknown: [ids], replayDay: bool }. Re-styles existing
// markers. A payload that can't be read leaves every site UNKNOWN (grey) — never green by default.
export function setStatus(json) {
    try {
        const s = (typeof json === 'string') ? JSON.parse(json) : json;
        radarSiteOffline = new Set((s && s.offline) || []);
        radarSiteUnknown = new Set((s && s.unknown) || []);
        radarStatusReplayDay = !!(s && s.replayDay);
    } catch (e) {
        radarSiteOffline = new Set();
        radarSiteUnknown = null;
        radarStatusReplayDay = false;
    }
    Object.keys(radarMarkers).forEach(function (k) { applySiteStatus(radarMarkers[k], k); });
}

// ONE site's live result, the moment its check lands — a pass CASCADES across the map (grey keys easing to
// green / red one by one; the key's colour transition does the easing). The pass's closing setStatus
// reconciles everything, including sites the pass never probed individually.
export function setOneStatus(id, state) {
    if (radarSiteUnknown === null) radarSiteUnknown = new Set(Object.keys(radarMarkers)); // first result of a fresh page
    radarSiteUnknown.delete(id);
    if (state === 'offline') radarSiteOffline.add(id); else radarSiteOffline.delete(id);
    radarStatusReplayDay = false; // per-site results only come from LIVE passes
    const btn = radarMarkers[id];
    if (btn) applySiteStatus(btn, id);
}

// No-op: the on-map markers no longer use the OS accent (the halo was removed — availability is a fixed
// data colour and selection inverts the key). Kept so the host's setRadarSitesAccent shim
// (MapService.SetRadarSiteAccentAsync → map.js) stays valid; the OverlayBar still uses the accent itself.
export function setAccent(border, glow) { /* markers no longer use an accent halo */ }

// Redraw the collision fan's leader lines after a theme change. ⚠️ The KEYS need nothing — they are
// styled by the injected rules above, so they re-cascade the moment data-theme flips — but the leader
// lines are an SVG string whose stroke is baked in at build time, and they would otherwise keep the old
// color until the next pan or zoom happened to rebuild them.
export function refresh() { updateFan(); }

// Show/hide just the operational NEXRAD keys. Independent of the radar layer — an active loop keeps
// rendering while its marker is hidden. TDWR / research keys have their own toggles below.
// ⚠️ NEXRAD keys are the fan-out's fixed obstacles; hidden ones drop out of it on their own (updateFan
// only considers displayed keys).
export function setNexradVisible(visible) {
    nexradVisible = !!visible;
    applyVisibility();
}

// Show/hide just the research/test radar markers. Off by default; other networks are unaffected. An
// active research loop keeps rendering while hidden.
export function setResearchVisible(visible) {
    researchVisible = !!visible;
    applyVisibility();
}

// Show/hide just the TDWR markers. Off by default; other networks are unaffected. An active TDWR loop
// keeps rendering while hidden.
export function setTdwrVisible(visible) {
    tdwrVisible = !!visible;
    applyVisibility();
}

// The retired ids to hide (a JSON array). Survives show() — it's C#'s era state, not part of the site list.
export function setOutOfEra(json) {
    const ids = (typeof json === 'string') ? JSON.parse(json) : json;
    outOfEraIds = new Set(ids || []);
    applyVisibility();
}

// ── DEV MEASUREMENT SEAM (perf-probe.js) ──────────────────────────────────────────────────────────
// How many marker objects this module is holding, and how many of those are actually shown. Read only
// by the dev pan probe, to stamp each frame-time sample with what was drawing during the gesture.
//
// ⚠️ `attached` vs `shown` is THE number the marker work is about: today every site is attached to the
// map whether or not it is visible (hiding is a CSS display flip), so MapLibre reprojects all of them on
// every move — attached == total, always, and turning markers off buys nothing. If hidden markers are
// ever detached instead, `attached` starts tracking `shown`, and that gap closing IS the fix landing.
export function stats() {
    let shown = 0;
    radarMarkerObjs.forEach(function (m) {
        if (m.getElement().style.display !== 'none') shown++;
    });
    return { attached: radarMarkerObjs.length, shown: shown };
}
