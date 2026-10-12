// radar-sites.js — the on-map radar-site marker "key" buttons (extracted from map.js). Owns the
// marker state; the key's MARKUP is <template id="tpl-site-key"> in map.html and its LOOK is
// styles/components/radar-site-keys.css — this module only clones, fills and toggles classes. map.js's
// window.showRadarSites / setSelectedRadarSite / setRadarSitesStatus / setNexradSitesVisible (+ TDWR /
// research) shims delegate here, passing the map. Posts radarSiteClick (the NAME) and radarSiteMenu (the
// CHEVRON) to the host. `maplibregl` is the global from the vendored classic script.
//
// ANATOMY OF ONE KEY — a gray face and edge, three seamless zones (no dividers), the INK is the status:
//
//        ╭──────────────╮      LEFT   .radar-site-glyph  = the NETWORK glyph: nexrad ◗ · tdwr ✈ · research ⚗
//        │ ◗  KTLX   ⌄  │      CENTRE .radar-site-label  = the ICAO — click LOADS (toggles, like before)
//        ╰──────────────╯      RIGHT  .radar-site-chev   = click OPENS the host's WinUI site menu
//                                                          (Load radar · Open in Atlas · home · favorite)
//
//   THE INK IS THE STATE (DATA — literal, never themed). The face and edge are one theme gray for every
//   key (chrome); the glyph, the ICAO and the chevron wear the status colour.
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
//   · NOT CHECKED: dark-grey glyph/name, light-grey chevron; loads like an online key.
//   · HOVER lifts the gray face one step. SELECTED lifts it too and edges the key in its status ink.
//   · a pass CASCADES: each key's ink eases grey → its colour as ITS check lands (the colour transition).
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
// WHICH KEYS SHOW — THE VISIBILITY RULES table below (network · era | declutter: isolation · reveal · favorites · focus).
//
//        rules:  network ✓  era ✓  isolation ✓  reveal ✗   → hidden        a key shows only when EVERY rule passes;
//        loaded: network ✓  era ✓  (declutter skipped)      → shown         the LOADED site skips the declutter ones
//
// These are DOM-overlay markers (maplibregl.Marker), so they auto-reposition on pan/zoom and survive
// basemap switches (no style-layer re-add needed). Structure: a `.radar-site-marker` WRAPPER (MapLibre
// positions it via an inline transform) > `.radar-site-offset` (the fan's translate target) > the
// `.radar-site-btn` key with its three zones.
// (History: a round dot → a graphite key with a left availability square + a right network bar → a
// one-colour key with a menu chevron, 2026-10-04 → this gray key whose ink is the status, 2026-10-12.)

import { coverageDistanceMeters } from './geo.js';
// The key, its three network glyphs and the leader-line parts are <template>s in map.html.
import * as Templates from './templates.js';

// The network glyph template for each key class: a radar sweep for operational NEXRAD, a plane for TDWR,
// a flask for research. The SHAPE is what names the network on an offline or unchecked key, where the ink
// says status instead.
const GLYPH_TEMPLATE = { nexrad: 'tpl-glyph-nexrad', tdwr: 'tpl-glyph-tdwr', research: 'tpl-glyph-research' };

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
//                                                 favorites only home + favorites (Settings → Radar, or the
//                                                           site picker's footer — one setting)
//
// ⚠️ ONE PAIRING, on purpose: favorites + reveal together mean "favorites always, the ring reveals the rest" —
//    each lets the other's sites through. Plain AND would show only favorites INSIDE the ring, which is useless.
//
// Rules COMPOSE BY AND, so they cannot fight: with focus on and a site loaded, only that site shows whatever
// the others say; with nothing loaded, focus passes everything and the rest decide.
// ⚠️ A NEW WAY TO HIDE KEYS = ONE entry here (+ its state and setter). Never another && in markerVisible, and
//    never a display write anywhere but applyVisibility — that is how the features used to stack up.
let revealedIds = null;  // SITE-REVEAL: ids inside the cursor's ring; null = the reveal is off
let focusOn = false;     // setFocus: hide the other sites while one is loaded (Settings → Radar)
let favoriteIds = null;  // setFavoritesOnly: home + favorite ids; null = the filter is off
function isRevealed(id) { return revealedIds !== null && revealedIds.has(id); }
function isFavorite(id) { return favoriteIds !== null && favoriteIds.has(id); }
const RULES = [
    { name: 'network',   declutter: false, test: function (id) { return researchIds.has(id) ? researchVisible : tdwrIds.has(id) ? tdwrVisible : nexradVisible; } },
    { name: 'era',       declutter: false, test: function (id) { return !outOfEraIds.has(id); } },
    { name: 'isolation', declutter: true,  test: function (id) { return coveredIds === null || coveredIds.has(id); } },
    { name: 'reveal',    declutter: true,  test: function (id) { return revealedIds === null || revealedIds.has(id) || isFavorite(id); } }, // SITE-REVEAL
    { name: 'favorites', declutter: true,  test: function (id) { return favoriteIds === null || favoriteIds.has(id) || isRevealed(id); } },
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

// FAVORITES: only home + favorites show (RadarSiteFavoritesViewModel.ShowOnlyFavoritesOnMap). The flag and the
// list arrive TOGETHER, re-pushed on every star / home change; off = null whatever the list holds.
export function setFavoritesOnly(on, json) {
    let ids = [];
    try { ids = JSON.parse(json || '[]'); } catch (e) { console.error('setFavoritesOnly: bad id list'); }
    favoriteIds = on ? new Set(ids) : null;
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

// Applies a marker's availability (the ink, via .offline / .unknown) + tooltip from the pushed status.
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
    lineSvg = Templates.clone('tpl-site-lines');
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
    fanLast.forEach(function (o) { o.style.transform = ''; if (o.parentElement) o.parentElement.classList.remove('fanned'); });
    fanLast = [];
    if (!researchVisible && !tdwrVisible) { clearLines(); return; }

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
    if (specials.length === 0) { clearLines(); return; }

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

    const lines = [];
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
            v.el.classList.add('fanned'); // the offset key rides above the leader-line overlay + operational keys
            // The leader: a ring at the true site + a dashed line to the key. Coordinates are the only thing
            // set here — the stroke is CSS (.radar-site-leader-*), so it follows a theme switch on its own.
            const origin = Templates.clone('tpl-site-leader', '.radar-site-leader-origin');
            origin.setAttribute('cx', v.x.toFixed(1));
            origin.setAttribute('cy', v.y.toFixed(1));
            const line = Templates.clone('tpl-site-leader', '.radar-site-leader-line');
            line.setAttribute('x1', v.x.toFixed(1));
            line.setAttribute('y1', v.y.toFixed(1));
            line.setAttribute('x2', x.toFixed(1));
            line.setAttribute('y2', y.toFixed(1));
            lines.push(origin, line);
        }
    });
    if (lineSvg) lineSvg.replaceChildren.apply(lineSvg, lines);
}

function clearLines() { if (lineSvg) lineSvg.replaceChildren(); }

// Provide the site list (as buttons). Each wrapper is the marker MapLibre positions; the inner
// button is the styled key. The name posts radarSiteClick, the chevron radarSiteMenu.
export function show(map, json) {
    const sites = (typeof json === 'string') ? JSON.parse(json) : json;
    radarMarkerObjs.forEach(function (m) { m.remove(); });
    radarMarkerObjs = [];
    radarMarkers = {};
    researchIds = new Set();
    tdwrIds = new Set();
    siteCoords = {};
    fanLast = [];                          // old offset wrappers are gone with the removed markers
    clearLines();                          // drop any leader lines from the previous list
    sites.forEach(function (s) {
        if (s.research) researchIds.add(s.id);
        if (s.tdwr) tdwrIds.add(s.id);
        siteCoords[s.id] = [s.lng, s.lat];
        // The marker > offset > key structure is the template; the offset wrapper is the fan's translate
        // target (see .radar-site-offset / updateFan).
        const el = Templates.clone('tpl-site-key');
        el.dataset.siteId = s.id; // used by applyVisibility to re-evaluate the per-marker rule
        // The network is a class on the key (it picks the ONLINE ink); .offline / .unknown override it.
        const klass = s.tdwr ? 'tdwr' : (s.research ? 'research' : 'nexrad');
        el.dataset.siteClass = klass; // for the collision fan-out
        const btn = el.querySelector('.radar-site-btn');
        btn.classList.add(klass);
        btn.dataset.siteName = s.name || '';
        btn.querySelector('.radar-site-glyph').appendChild(Templates.clone(GLYPH_TEMPLATE[klass]));
        btn.querySelector('.radar-site-label').textContent = s.id;
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
        const marker = new maplibregl.Marker({ element: el }).setLngLat([s.lng, s.lat]);
        timeMarkerUpdates(marker); // DEV (?perf=1 only) — see the measurement seam at the bottom
        marker.addTo(map);
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
// data ink and selection edges the key in it). Kept so the host's setRadarSitesAccent shim
// (MapService.SetRadarSiteAccentAsync → map.js) stays valid; the OverlayBar still uses the accent itself.
export function setAccent(border, glow) { /* markers no longer use an accent halo */ }

// Theme change. Nothing to redraw any more: the keys AND the leader lines are styled by
// styles/components/radar-site-keys.css, so they re-cascade the moment data-theme flips. Kept so map.js's
// theme pass (RadarSites.refresh) stays valid; re-running the fan is harmless.
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

// What those attached markers COST during a gesture: every attached marker runs MapLibre's `_update` on every
// map `move` (project + transform write), shown or not. Counted here, read + reset per gesture by the probe.
// ⚠️ `_update` is an instance arrow field that addTo() subscribes to `move` (and remove() unsubscribes), so it
// is replaced on the instance BEFORE addTo — patching the prototype would time nothing. Off unless the page URL
// has ?perf=1 (DEBUG builds only), so Release markers are untouched. Excisable with the rest of `perfPan`.
const PERF_TIMED = new URLSearchParams(location.search).get('perf') === '1';
let mkUpd = 0;
let mkUpdMs = 0;

function timeMarkerUpdates(marker) {
    if (!PERF_TIMED || typeof marker._update !== 'function') return;
    const inner = marker._update;
    marker._update = function (e) {
        const t = performance.now();
        try { return inner(e); } finally { mkUpd++; mkUpdMs += performance.now() - t; }
    };
}

/// Marker-update calls and summed ms since the last call (the probe resets it at each gesture's start and
/// reads it at the end).
export function takeUpdateStats() {
    const r = { n: mkUpd, ms: mkUpdMs };
    mkUpd = 0;
    mkUpdMs = 0;
    return r;
}
