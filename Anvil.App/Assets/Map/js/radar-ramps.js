// Radar color scales — the SINGLE SOURCE OF TRUTH for how moment values map to colors.
//
// ⚠️ THE VALUES ARE NOT AUTHORED HERE ANY MORE. They are the NWS's own operational tables, harvested
// by tools/make_radar_ramp_catalog.py into ./radar-ramp-tables.js (generated, do not hand-edit) and
// assembled into ramps below. This file owns BEHAVIOUR — which variant each product uses, how a value
// becomes a color, and the one ramp we still design ourselves. Same discipline as the SPC colour
// catalog: a hex typed by hand is how a legend comes to disagree with the authority it claims.
//
// TWO OFFICIAL VARIANTS ship for every product the NWS publishes two for. Both are in the table; the
// choice is made HERE, in code, per product:
//
//   bands16    ▐██▌██▌██▌██▌██▌██▌   the classic banded scale (legacy 8/16-level products).
//              5        35       75  Coarse, instantly recognisable, read by band edge.
//
//   levels256  ▐▓▓▒▒░░░░▒▒▓▓▓▓▓▓▓▌   the 8-bit scale. 254 bands at the product's own resolution —
//              -32      30      94   matches the super-res Level II data we actually render.
//
// ⚠️ NOT RUNTIME-SWITCHABLE, BY DESIGN. Gate colors are BAKED per gate at decode time
// (radar-decode.js bakes from these tables), so changing a variant invalidates every built frame —
// the same cost as a tilt retile. Changing RAMP_STYLE is a code edit + a reload, not a setting.
//
// The two kinds of lookup, and what each looks like on the map and in the legend:
//
//   interpolate:false  ▐██▌██▌██▌██▌   DISCRETE bands — every official table. A value takes its
//                                      band's color, hard-edged, exactly as the NWS draws it.
//   interpolate:true   ▐▓▓▒▒░░ ░░▒▒▓▌  SMOOTH gradient — only the one ramp still ours (spectrum
//                                      width), because its official mapping can't be verified.
//
// The SAME table feeds the painted gates and the legend in each pane chip (map.js pushes the whole
// table to the host at startup, keyed by product id), so the two can't drift.
//
// A ramp is:
//   { id, label, unit, min, max, interpolate, stops: [{ v, color: [r,g,b] }, ...], index? }
//   - stops are ascending by v and are what the LEGEND draws from.
//   - `index` (present on the 254-band official tables) is a {scale, offset, colors} descriptor that
//     lets rampColor skip the scan — see below. It is an optimisation ONLY: it returns exactly what
//     walking `stops` would.
//   - min/max are the legend's display bounds and are OURS, not the product's: the official tables
//     span the full product range (reflectivity -32…94.5 dBZ), which would spend most of the bar on
//     values MIN_DBZ has already dropped. The COLOR at a value is official; where the bar ends is a
//     display choice.

import { TABLES, RANGE_FOLDED_HEX } from './radar-ramp-tables.js';

// ── WHICH OFFICIAL VARIANT EACH PRODUCT DRAWS WITH ──────────────────────────────────────────────
// Both variants are present in the table for every product that has two, so flipping one of these is
// a one-word edit. Reasons for the current picks:
//   reflectivity  bands16    — the banded scale is the one people read by band edge, and it is what
//                              Anvil has always drawn. Now with the EXACT OSF hexes rather than the
//                              rounded community values it used to carry (#04E9E7, not #00ECEC).
//   velocity/srv  levels256  — the 16-level scale is 14 bands over ±64 kt, far coarser than the
//                              dealiased field we compute. SRV's bands16 is the real SRM table if
//                              you want it; its levels256 is velocity's, since the NWS publishes no
//                              256-level SRM colormap.
//   cc/kdp/zdr    levels256  — the NWS publishes no banded variant for the dual-pol moments.
//   sw            custom     — ⚠️ see SPECTRUM_WIDTH_RAMP. Its official mapping is unverifiable.
export const RAMP_STYLE = {
    reflectivity: 'bands16',
    velocity: 'levels256',
    srv: 'levels256',
    cc: 'levels256',
    kdp: 'levels256',
    zdr: 'levels256',
    sw: 'custom',
};

// RANGE-FOLDED gates — the "purple haze". NOT a ramp value and NOT part of any ramp's stops: it is a
// THIRD display class alongside "has a value" and "no data", flagged per gate by the decoder (raw data
// value 1 in the Message 31 moment block; raw 0 is below-threshold, which stays no-data). The returns
// at these gates are range-ambiguous — a second-trip echo overlaid on the first — so there is no
// velocity to draw and no ramp position to give it.
// ⚠️ Harvested from level 1 of the official VELOCITY table, not invented. It is level 1 on the Doppler
// products specifically: on reflectivity level 1 is a grey low-return colour and on the dual-pol
// products it is just the bottom of their own ramp, so "level 1" is only a statement about range
// folding where range folding exists. Anvil used #7A4FAD before, which came from AWIPS's Storm Clear
// Reflectivity table rather than from a Doppler product's.
// ⚠️ It is DATA, not chrome, so it does not move with the theme (same rule as the ramps and the SPC
// colors). ⚠️ Rendered on the DOPPLER products only (velocity / SRV / spectrum width) — the long-PRT
// surveillance reflectivity is not normally folded, and painting purple over it would read as a data
// class that product does not have.
export const RANGE_FOLDED_COLOR = hexToRgb(RANGE_FOLDED_HEX);

function hexToRgb(h) {
    return [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
}

// Returns [r,g,b] (0-255 ints) for `value` under `ramp`. Values past the ends clamp.
// ⚠️ The `index` fast path exists because the official tables are 254 bands and this is called ONCE
// PER GATE PER PRODUCT inside the decoder — a linear scan would walk up to 254 stops per gate, on
// sweeps of ~240k gates. Uniform band spacing makes the band number pure arithmetic, so the scan is
// skipped entirely. Both paths FLOOR to the band the value falls in, so a value in [v(i), v(i+1))
// takes band i either way; they were checked to agree over ~245k probes per product, including every
// band edge (tools/make_radar_ramp_catalog.py --check).
//
// ⚠️ LEVEL_EPS IS LOAD-BEARING, NOT A FUDGE. A stop's value is stored as (level - offset) / scale, and
// multiplying it back does not always land on the integer it came from: with CC's scale 300 and
// offset -60.5, ρHV 0.285 round-trips to level 24.999999999999986, so a bare floor() drops it a whole
// band. 12 of CC's 254 stops did that. One ULP of slack in LEVEL units fixes every case and is ~3e-9
// ρHV / 5e-7 dBZ wide — orders of magnitude below anything the data resolves.
const LEVEL_EPS = 1e-6;

export function rampColor(ramp, value) {
    const ix = ramp.index;
    if (ix) {
        let i = Math.floor(value * ix.scale + ix.offset + LEVEL_EPS) - 2;
        if (i < 0) i = 0;
        else if (i >= ix.colors.length) i = ix.colors.length - 1;
        return ix.colors[i];
    }
    const s = ramp.stops;
    if (value <= s[0].v) return s[0].color;
    if (value >= s[s.length - 1].v) return s[s.length - 1].color;
    let i = 0;
    while (i + 1 < s.length && value >= s[i + 1].v) i++; // s[i].v <= value < s[i+1].v
    if (!ramp.interpolate) return s[i].color;            // discrete: lower-bound band
    const lo = s[i], hi = s[i + 1];
    const t = (value - lo.v) / (hi.v - lo.v);
    return [
        (lo.color[0] + (hi.color[0] - lo.color[0]) * t) | 0,
        (lo.color[1] + (hi.color[1] - lo.color[1]) * t) | 0,
        (lo.color[2] + (hi.color[2] - lo.color[2]) * t) | 0,
    ];
}

// ── Assembling one product's ramp from the generated tables ─────────────────────────────────────
// Throws rather than falling back silently: a missing or unverified variant is a generation bug, and
// a ramp that quietly resolves to the wrong table is exactly the failure this whole change removes.
function officialRamp(id, style) {
    const t = TABLES[id];
    if (!t) throw new Error('radar-ramps: no table for ' + id);
    const v = t[style];
    if (!v || !v.verified) {
        throw new Error('radar-ramps: ' + id + '/' + style + ' is absent or unverified');
    }
    const ramp = {
        id: id, label: t.label, unit: t.unit, min: t.min, max: t.max,
        interpolate: false,   // every official table is banded
        stops: null,
    };
    if (style === 'bands16') {
        ramp.stops = v.stops.map(function (s) { return { v: s.v, color: hexToRgb(s.color) }; });
        return ramp;
    }
    // levels256: colors[] is level 2..255. Materialise stops for the legend AND keep the arithmetic
    // descriptor for the per-gate path. The two describe the same bands.
    const colors = v.colors.map(hexToRgb);
    ramp.stops = colors.map(function (c, i) {
        return { v: (i + 2 - v.offset) / v.scale, color: c };
    });
    ramp.index = { scale: v.scale, offset: v.offset, colors: colors };
    return ramp;
}

export const REFLECTIVITY_RAMP = officialRamp('reflectivity', RAMP_STYLE.reflectivity);
export const VELOCITY_RAMP = officialRamp('velocity', RAMP_STYLE.velocity);
export const SRV_RAMP = officialRamp('srv', RAMP_STYLE.srv);
export const CORRELATION_RAMP = officialRamp('cc', RAMP_STYLE.cc);
export const KDP_RAMP = officialRamp('kdp', RAMP_STYLE.kdp);
export const ZDR_RAMP = officialRamp('zdr', RAMP_STYLE.zdr);

// Spectrum width (m/s) — the SPREAD of velocities within a gate, i.e. turbulence / shear. Low = smooth
// laminar flow; high = turbulent air, shear boundaries (gust fronts, convergence), the chaos around
// mesocyclones/tornadoes, and ground clutter. A Doppler moment (same cut as velocity).
//
// ⚠️⚠️ THE ONE RAMP STILL OURS, AND DELIBERATELY SO. The AWIPS spectrum-width tables ARE harvested
// (TABLES.sw carries both variants) but are marked unverified and nothing selects them: the Level III
// bucket does not carry NSW, so there is no real product to measure the level→value mapping from, and
// every other mapping in this file was measured rather than assumed. Official colors pinned to a
// guessed scale would look authoritative and be wrong — worse than an honest custom ramp that claims
// nothing. To finish it: find a source for NSW's scale/offset, set RAMP_STYLE.sw, delete this.
//
// Sequential dark→blue→green→yellow→orange→red→magenta with rising width. 0…14 m/s clamps; smooth so
// the legend matches.
export const SPECTRUM_WIDTH_RAMP = {
    id: 'sw', label: 'Spectrum Width', unit: 'm/s', min: 0, max: 14, interpolate: true,
    stops: [
        { v: 0, color: [0x20, 0x20, 0x30] }, // very low — near-laminar (dark)
        { v: 2, color: [0x00, 0x50, 0xb0] }, // blue
        { v: 4, color: [0x00, 0xb0, 0x90] }, // teal-green
        { v: 6, color: [0x60, 0xd0, 0x00] }, // yellow-green
        { v: 8, color: [0xf0, 0xd0, 0x00] }, // yellow
        { v: 10, color: [0xff, 0x80, 0x00] }, // orange
        { v: 12, color: [0xff, 0x10, 0x10] }, // red — high turbulence / shear
        { v: 14, color: [0xff, 0x40, 0xff] }, // extreme — magenta
    ],
};
