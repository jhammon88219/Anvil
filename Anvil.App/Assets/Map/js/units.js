// units.js — Settings → Units on the PAGE side: the clock / zone / speed / height / hail choices, and the
// formatters every popup and readout the page draws goes through. A MIRROR of Anvil.Core Models/Map/UnitFormat
// (same tokens, same wording) — change both. Ground distance rides setDistanceUnits: the ruler + rings keep their
// own copy (RadarLayer); this module gets one too (setDistance) for popups that print a length (damage paths).
//
//   host ── window.setDisplayUnits({clock, zone, speed, height, hail}) ──▶ set(u)
//                                                                            │
//        storm-reports.js / storm-cells.js / damage-surveys.js / radar-inspect.js ◀── clock(ms) · speedFromMs(…) · …
//
//   clock(ms)        "5:04 PM" · "17:04" · "22:04Z"      (UTC is ALWAYS 24-hour Z)
//   speedFromMs(ms)  "28 mph" · "24 kt" · "45 km/h" · "12.3 m/s"
//   heightFromKft(k) "22.4 kft" · "6.8 km" · "22,400 ft"
//   hailFromInches(i) "1.75 in" · "4.4 cm"
//
// ⚠️ Read at FORMAT time (a popup is built on click, the readout on every move), never cached into a module
// const — so a change in Settings shows on the next popup with nothing re-added.
// ⚠️ Only tokens arrive here (the host normalizes them); an unknown one falls back to the default.

let u = { clock: '12h', zone: 'local', speed: 'mph', height: 'kft', hail: 'in' };

const OK = {
    clock: ['12h', '24h'], zone: ['local', 'utc'], speed: ['kt', 'mph', 'kmh', 'ms'],
    height: ['km', 'kft', 'ft'], hail: ['in', 'cm']
};

export function set(next) {
    const n = Object.assign({}, u);
    Object.keys(OK).forEach(function (k) { if (next && OK[k].indexOf(next[k]) >= 0) n[k] = next[k]; });
    u = n;
}

function pad2(n) { return (n < 10 ? '0' : '') + n; }

// "5:04 PM" · "17:04" · "22:04Z".
export function clock(ms) {
    const d = new Date(ms);
    if (u.zone === 'utc') return pad2(d.getUTCHours()) + ':' + pad2(d.getUTCMinutes()) + 'Z';
    const h = d.getHours(), m = pad2(d.getMinutes());
    if (u.clock === '24h') return pad2(h) + ':' + m;
    return ((h % 12) || 12) + ':' + m + (h < 12 ? ' AM' : ' PM');
}

// "May 20, 2013" in the display zone (with UTC chosen, the DATE is the UTC date too).
export function date(ms) {
    const d = new Date(ms);
    const opts = { month: 'short', day: 'numeric', year: 'numeric' };
    if (u.zone === 'utc') opts.timeZone = 'UTC';
    return d.toLocaleDateString('en-US', opts);
}

// "5:04–6:10 PM" style spans are not mirrored — the page shows "clock – clock".
export function span(ms0, ms1) {
    if (!ms1 || ms1 <= ms0) return clock(ms0);
    return clock(ms0) + '–' + clock(ms1);
}

const MS_PER_KT = 0.514444, MS_PER_MPH = 0.44704;

export function speedFromMs(ms) {
    switch (u.speed) {
        case 'kt': return Math.round(ms / MS_PER_KT) + ' kt';
        case 'kmh': return Math.round(ms * 3.6) + ' km/h';
        case 'ms': return ms.toFixed(1) + ' m/s';
        default: return Math.round(ms / MS_PER_MPH) + ' mph';
    }
}
export function speedFromKt(kt) { return speedFromMs(kt * MS_PER_KT); }
export function speedFromMph(mph) { return speedFromMs(mph * MS_PER_MPH); }
export function speedIsNative() { return u.speed === 'ms'; }

export function heightFromKft(kft) {
    switch (u.height) {
        case 'km': return (kft * 0.3048).toFixed(1) + ' km';
        case 'ft': return (Math.round(kft * 10) * 100).toLocaleString('en-US') + ' ft';
        default: return Number(kft).toFixed(1) + ' kft';
    }
}

export function hailFromInches(inches) {
    return u.hail === 'cm' ? (inches * 2.54).toFixed(1) + ' cm' : Number(inches).toFixed(2) + ' in';
}

// ── Ground distance: the host's DistanceUnits token, handed over by map.js setDistanceUnits (the ruler and rings
// keep their own copy through RadarLayer) — so a damage path speaks the same unit as the ruler.
let distance = 'km';
export function setDistance(token) { distance = token === 'mi' || token === 'nm' ? token : 'km'; }

// A path length published in miles → "12.3 mi" · "19.8 km" · "10.7 nm" (one decimal below 10, like the ruler).
export function lengthFromMiles(mi) {
    const v = distance === 'mi' ? mi : distance === 'nm' ? mi * 1609.344 / 1852 : mi * 1.609344;
    return (Math.abs(v) < 10 ? v.toFixed(1) : String(Math.round(v))) + ' ' + distance;
}

// A path WIDTH published in yards → "880 yd" with miles, else metres ("805 m").
export function widthFromYards(yd) {
    return distance === 'mi'
        ? Math.round(yd).toLocaleString('en-US') + ' yd'
        : Math.round(yd * 0.9144).toLocaleString('en-US') + ' m';
}

// The same, as a MapLibre 'text-field' over an inches expression — the compact map label: 1.75″ · 4.4 cm.
// ⚠️ A layer holds the expression it was built with: its owner re-applies this when the units change.
export function hailLabelExpr(inchesExpr) {
    return u.hail === 'cm'
        ? ['concat', ['number-format', ['*', inchesExpr, 2.54], { 'min-fraction-digits': 1, 'max-fraction-digits': 1 }], ' cm']
        : ['concat', ['number-format', inchesExpr, { 'min-fraction-digits': 2, 'max-fraction-digits': 2 }], '″'];
}
