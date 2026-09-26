// past-alerts.js — PastCast's warnings and watches: what NWS/SPC had in effect at the DISPLAYED radar frame
// of the loaded replay, from IEM's VTEC archive (PastAlertService writes one file per window per kind).
// Two overlays from the shared fill+outline factory, drawn with the NowCast modules' own STYLE, on layers
// of their own. map.js's window.setPastAlert* shims delegate here; reAddAll calls reAdd(map).
//
//     frame 7:51 PM ──►  ┌───────────────┐       every feature carries t0 / t1 (Unix ms) and draws only
//                        │ ░ TO.W v2 ░░  │       while t0 ≤ frame < t1, so as the loop plays a warning
//          ┌─────────────┴─┐ (its v1 hid │       appears at issuance, RESHAPES at each follow-up
//          │ county-filled │  at 7:48)   │       statement (one feature per polygon version, each with
//          │ tornado watch │─────────────┘       its own tier → outline weight), and is gone at expiry;
//          └───────────────┘                     a watch's counties drop out as they were cancelled.
//
// ⚠️ SEPARATE LAYERS FROM NOWCAST, not the live ones handed over: the live warnings refresh re-points its
// own source every 15–60 s, which would drag today's polygons over a replay. Ids 'past-warning-*' /
// 'past-watch-*' — layers.js GROUPS files them under the SAME 'warnings' / 'watches' groups, so PastCast's
// layer order places them exactly as NowCast's order places the live ones.

import { firstBoundaryLayerId } from './layers.js';
import { createGeojsonOverlay } from './geojson-overlay.js';
import { STYLE as WARNING_STYLE } from './warnings.js';
import { STYLE as WATCH_STYLE } from './watches.js';

const overlays = {
    warnings: createGeojsonOverlay(Object.assign({
        sourceId: 'past-warnings',
        fillLayerId: 'past-warning-fill',
        lineLayerId: 'past-warning-line',
        beforeId: firstBoundaryLayerId,
        logName: 'past warnings',
        timed: true,
    }, WARNING_STYLE)),
    watches: createGeojsonOverlay(Object.assign({
        sourceId: 'past-watches',
        fillLayerId: 'past-watch-fill',
        lineLayerId: 'past-watch-line',
        beforeId: firstBoundaryLayerId,
        logName: 'past watches',
        timed: true,
    }, WATCH_STYLE)),
};

function of(kind) { return overlays[kind] || null; }

// An empty url = the replay was unloaded: forget the window entirely.
export function setSource(map, kind, url) {
    const o = of(kind);
    if (!o) return;
    if (url) o.setSource(map, url); else o.clear(map);
}

export function setVisible(map, kind, on) { const o = of(kind); if (o) o.setVisible(map, on); }
export function setKinds(map, kind, list) { const o = of(kind); if (o) o.setKinds(map, list); }
export function setOpacity(map, kind, v) { const o = of(kind); if (o) o.setOpacity(map, v); }

// One moment for both — the displayed radar frame.
export function setTime(map, ms) {
    overlays.watches.setTime(map, ms);
    overlays.warnings.setTime(map, ms);
}

// Watches first so warnings land above them, as reAddAll orders the live pair.
export function reAdd(map) {
    overlays.watches.reAdd(map);
    overlays.warnings.reAdd(map);
}
