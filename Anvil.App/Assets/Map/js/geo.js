// geo.js — the ONE canonical definition of the radar's site-relative coordinate projection, shared by
// the decoder (radar-decode.js, static ES-module import) and the renderer (radar.js, async dynamic
// import + cache, since it's a classic-script IIFE). Equirectangular ("flat-earth") approximation
// around the radar site: at these ranges (≤ ~460 km) it matches the painted gate geometry exactly, and
// it's what every overlay MUST agree with — the range ring, the sweep arm, and the inspector all
// project through here so they line up with the gates. Pure + stateless; callers pass the site position.
//
// THE FRAME EVERY RADAR OVERLAY SHARES:
//
//              N (az 0°)                 siteToLngLat(range, az)  → where a gate is drawn
//                 │  ╱ az                lngLatToPolar(lng, lat)  → which gate the cursor is over
//                 │ ╱                    (exact inverses, which is why the Inspector names the gate
//        W ───────╳───────► E (az 90°)    that is actually painted under the pointer)
//         (az270) │╲ range
//                 │ ╲___ a gate at (range m, az°)
//                 S
//
//   Azimuth is degrees CLOCKWISE FROM NORTH (radar convention), not the math convention — mixing the
//   two is the classic way to get an overlay mirrored about the diagonal.
//
// PERF NOTE: buildGates (radar-decode) projects MILLIONS of gates per sweep in a hot loop, so it only
// borrows metersPerDeg() (computed once per sweep) and keeps its per-gate formula inline. The non-hot
// callers (ring = 128 pts, sweep = 1 line/frame, inspector = 1/mousemove) use the helpers below.

export const D2R = Math.PI / 180;

// Metres per degree at a latitude (equirectangular). Latitude is ~constant; longitude shrinks by cos.
export function metersPerDeg(lat) {
    return { mPerDegLat: 111320, mPerDegLon: 111320 * Math.cos(lat * D2R) };
}

// Site-relative polar (range in METRES, azimuth in RADIANS clockwise from north) -> [lng, lat].
export function siteToLngLat(siteLat, siteLon, rangeMeters, azRad) {
    const { mPerDegLat, mPerDegLon } = metersPerDeg(siteLat);
    return [
        siteLon + (rangeMeters * Math.sin(azRad)) / mPerDegLon,
        siteLat + (rangeMeters * Math.cos(azRad)) / mPerDegLat,
    ];
}

// [lng, lat] -> site-relative polar { rangeMeters, azDeg } (azimuth clockwise from north, 0..360).
export function lngLatToPolar(siteLat, siteLon, lng, lat) {
    const { mPerDegLat, mPerDegLon } = metersPerDeg(siteLat);
    const dx = (lng - siteLon) * mPerDegLon, dy = (lat - siteLat) * mPerDegLat;
    let azDeg = Math.atan2(dx, dy) / D2R;
    if (azDeg < 0) azDeg += 360;
    return { rangeMeters: Math.sqrt(dx * dx + dy * dy), azDeg: azDeg };
}

// Coverage distance (METRES) from a point to a set of polygon rings: 0 if the point is INSIDE any ring,
// else the shortest distance to any ring EDGE (point-to-segment, not just nearest vertex). Works in a
// local equirectangular plane centred on the point (the same flat-earth approximation as above; error is
// <1% at radar-coverage ranges). rings = array of rings, each an array of [lng, lat]; disjoint rings
// (MultiPolygon parts) are each tested independently. Used by the state-isolation site filter: a radar
// "covers" the isolated state when this distance is within its usable range.
export function coverageDistanceMeters(lng, lat, rings) {
    const { mPerDegLat, mPerDegLon } = metersPerDeg(lat);
    const px = function (v) { return (v[0] - lng) * mPerDegLon; }; // ring vertex -> local metres, point at origin
    const py = function (v) { return (v[1] - lat) * mPerDegLat; };

    // Inside any ring? even-odd ray cast in the projected plane, with the test point at (0,0).
    for (let r = 0; r < rings.length; r++) {
        const ring = rings[r];
        let odd = false;
        for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
            const yi = py(ring[i]), yj = py(ring[j]);
            if ((yi > 0) !== (yj > 0)) {
                const xi = px(ring[i]), xj = px(ring[j]);
                if (0 < (xj - xi) * (0 - yi) / (yj - yi) + xi) odd = !odd;
            }
        }
        if (odd) return 0;
    }

    // Otherwise the shortest distance from the origin to any edge.
    let best = Infinity;
    for (let r = 0; r < rings.length; r++) {
        const ring = rings[r];
        for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
            const ax = px(ring[j]), ay = py(ring[j]);
            const dx = px(ring[i]) - ax, dy = py(ring[i]) - ay;
            const len2 = dx * dx + dy * dy;
            let t = len2 > 0 ? (-ax * dx + -ay * dy) / len2 : 0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            const cx = ax + t * dx, cy = ay + t * dy;
            const d = Math.sqrt(cx * cx + cy * cy);
            if (d < best) best = d;
        }
    }
    return best;
}
