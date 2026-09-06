// style-tune.js — the DEV style editor: a live LEVELS transform over the basemap's colours, plus a
// per-SLOT override on top of it.
//
//   pristine style JSON          transform (global)        overrides (per slot)      what the map shows
//   ┌──────────────────┐        white ────●──             ┌──────────────────┐      ┌──────────────────┐
//   │ slot 0  earth    │        black  ●────              │ slot 3 = #2b4c6f │      │ earth   #e0e0e0  │
//   │ slot 1  water    │  ───►  gamma  ──●──   ─────────► │                  │ ───► │ water   #2b4c6f  │
//   │ … 86 slots       │        tint   ●────              └──────────────────┘      │ roads   #cecece  │
//   └──────────────────┘                                    (wins per slot)         └──────────────────┘
//        snapshot                                                                     setPaintProperty
//     (fetched ONCE)                                                                 (per layer, in place)
//
// ⚠️ A SLOT IS (layer, paint property, OCCURRENCE INDEX) — not a layer, and not a distinct colour.
//   · not a layer: one property can hold several colours (landuse_park's ['case', …] holds 7);
//   · not a colour: #ffffff is the earth fill AND 12 road casings AND 8 label halos, so editing by
//     colour would move three unrelated things at once, which is the opposite of full control.
//
// ⚠️⚠️ PREVIEW USES setPaintProperty, NEVER setStyle. setStyle drops every custom source and layer, so
// map.js has to follow it with the whole reAddAll restore (radar, outlook, watches, warnings, storm
// reports, the isolation mask, the site markers). Driving that from a slider or a colour picker would run
// a full overlay rebuild on every tick. Paint properties change in place and touch none of our layers.
//
// ⚠️⚠️ THE SNAPSHOT IS THE PRISTINE STYLE JSON, FETCHED — not map.getStyle(). Two reasons, both
// load-bearing:
//   1. map.getStyle() also contains OUR layers (radar, the overlays, the mask). Those are DATA and must
//      never be tuned. Reading the style file gives exactly the basemap's layers and nothing else.
//   2. A transform must always run from the PRISTINE value. Reading back live paint would tune an
//      already-tuned colour and COMPOUND.
//
// ⚠️⚠️ SLOT ORDER IS THE EXPORT CONTRACT. Slots are enumerated in DOCUMENT order — layers in order, paint
// keys in insertion order, colours within a value in order — which is the same order the #rrggbb literals
// appear in the style FILE. That is what lets the host export by replacing the Nth literal in the original
// text: formatting survives (a JSON round-trip rewrites all ~10,000 lines) and two slots that share a
// source colour can still diverge. ⚠️ The host verifies the counts match before writing, because that
// correspondence is an assumption, not a guarantee.
//
// ⚠️ The fetch happens ONLY while something is actually applied. map.js's resolveStyle deliberately
// returns the bare URL offline so first paint costs no fetch; an untouched style must not spend one.
//
// ⚠️ HEX ONLY. The Data Viz styles are pure #rrggbb ramps. rgba()/hsl() literals (style.json, the
// polychrome "Regular" basemap) are invisible to this tool — they are neither slots nor tuned.
//
// ⚠️ THE MATHS LIVES HERE AND NOWHERE ELSE. The host never computes a colour; it sends parameters and
// overrides, and asks for the resulting slot colours. No second implementation to drift.

let pristine = null;      // [{ id, prop, value, base: [hex…] }] in document order
let slots = null;         // [{ id, prop, index, base }] flattened, document order — the export contract
let pristineUrl = null;   // which style URL the snapshot came from
let transform = null;     // the active levels transform, or null
let overrides = {};       // "layer|prop|index" -> "#rrggbb"

const HEX = /#[0-9a-fA-F]{6}/g;

function clamp(v, lo, hi) { return v < lo ? lo : v > hi ? hi : v; }
function key(id, prop, index) { return id + '|' + prop + '|' + index; }

function hexes(value) {
    const found = JSON.stringify(value).match(HEX);
    return found || [];
}

// h in [0,1]. Standard HSL to RGB, used only to build the tint's fully-saturated reference colour.
function hslToRgb(h, s, l) {
    if (s <= 0) { const v = l * 255; return [v, v, v]; }
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    const p = 2 * l - q;
    const f = function (t) {
        if (t < 0) t += 1; else if (t > 1) t -= 1;
        if (t < 1 / 6) return p + (q - p) * 6 * t;
        if (t < 1 / 2) return q;
        if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
        return p;
    };
    return [f(h + 1 / 3) * 255, f(h) * 255, f(h - 1 / 3) * 255];
}

// One channel through the levels curve: normalise, shape by gamma, then map onto [black, white].
// gamma 1 + black 0 reduces to a plain scale, which is exactly the by-hand pass this tool replaced.
function levels(v, t) {
    const n = Math.pow(clamp(v / 255, 0, 1), 1 / Math.max(0.05, t.gamma));
    return t.black + n * (t.white - t.black);
}

function tuneHex(hex, t) {
    if (!t) return hex;
    let r = levels(parseInt(hex.slice(1, 3), 16), t);
    let g = levels(parseInt(hex.slice(3, 5), 16), t);
    let b = levels(parseInt(hex.slice(5, 7), 16), t);

    if (t.tintStrength > 0.001) {
        // Blend toward a saturated colour of the chosen hue at this colour's OWN lightness, so the ramp's
        // shape survives and only its cast changes. Capped well below 1: full travel should read as a warm
        // or cool grey, not as a colour wash.
        const lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
        const tint = hslToRgb(clamp(t.tintHue, 0, 360) / 360, 1, clamp(lum, 0, 1));
        const k = clamp(t.tintStrength, 0, 1) * 0.35;
        r += (tint[0] - r) * k;
        g += (tint[1] - g) * k;
        b += (tint[2] - b) * k;
    }

    const hx = function (c) { return clamp(Math.round(c), 0, 255).toString(16).padStart(2, '0'); };
    return '#' + hx(r) + hx(g) + hx(b);
}

// THE resolution rule, in one place: an override WINS outright, otherwise the pristine colour goes through
// the transform. So the transform is a global grade you can dim everything with, and an override is the
// escape hatch for the handful of things the grade gets wrong.
function resolve(slot) {
    const o = overrides[key(slot.id, slot.prop, slot.index)];
    return o || tuneHex(slot.base, transform);
}

// Fetch the style FILE and enumerate its colour slots in document order. Cached per URL; a basemap change
// calls reset(), so the next apply re-snapshots the new style.
function ensurePristine(styleUrl) {
    if (pristine && pristineUrl === styleUrl) return Promise.resolve(pristine);
    return fetch(styleUrl)
        .then(function (r) { return r.json(); })
        .then(function (style) {
            const entries = [];
            const flat = [];
            (style.layers || []).forEach(function (layer) {
                const paint = layer.paint;
                if (!paint) return;
                Object.keys(paint).forEach(function (prop) {
                    const base = hexes(paint[prop]).map(function (h) { return h.toLowerCase(); });
                    if (!base.length) return;
                    entries.push({ id: layer.id, prop: prop, value: paint[prop], base: base });
                    base.forEach(function (h, i) {
                        flat.push({ id: layer.id, prop: prop, index: i, base: h });
                    });
                });
            });
            pristine = entries;
            slots = flat;
            pristineUrl = styleUrl;
            return entries;
        });
}

// Rewrite one paint value with its slots' resolved colours. ⚠️ POSITIONAL, not a lookup by colour: the Nth
// #rrggbb in the serialised value is slot N of this property, which is what lets two slots that started
// the same colour end up different.
function rewrite(entry) {
    let i = 0;
    const json = JSON.stringify(entry.value).replace(HEX, function () {
        const n = i++;
        return resolve({ id: entry.id, prop: entry.prop, index: n, base: entry.base[n] });
    });
    return JSON.parse(json);
}

function applyTo(map) {
    pristine.forEach(function (e) {
        if (!map.getLayer(e.id)) return;   // a layer the style has but this map does not
        try {
            map.setPaintProperty(e.id, e.prop, rewrite(e));
        } catch (err) { /* one bad property must not abandon the rest of the style */ }
    });
}

function isTouched() {
    return !!transform || Object.keys(overrides).length > 0;
}

/**
 * Apply the current transform + overrides to every map. `maps` is map.js's pane array, `styleUrl` the
 * basemap it loaded. With nothing applied this repaints the PRISTINE colours, which is how a clear works.
 */
export function apply(maps, styleUrl) {
    // ⚠️ Nothing applied and nothing to undo — return WITHOUT touching the style. This guard is what keeps
    // the untouched case free: no fetch, which is the whole reason map.js can avoid one offline.
    if (!isTouched() && !pristine) return Promise.resolve();
    return ensurePristine(styleUrl).then(function () {
        maps.forEach(function (m) { if (m) applyTo(m); });
    });
}

/** Set the global levels transform (null clears it) and repaint. */
export function setTransform(maps, styleUrl, t) {
    transform = t || null;
    return apply(maps, styleUrl);
}

/** Replace the whole override table (a {slotKey: "#rrggbb"} map) and repaint. */
export function setOverrides(maps, styleUrl, table) {
    overrides = table || {};
    return apply(maps, styleUrl);
}

/** Re-apply to ONE map — after a style switch, or to a pane that has just been created. */
export function reAdd(map, styleUrl) {
    if (!isTouched()) return Promise.resolve();
    return ensurePristine(styleUrl).then(function () { applyTo(map); });
}

/** Drop the snapshot. The host calls this on a basemap change: the next apply re-reads the new style. */
export function reset() { pristine = null; slots = null; pristineUrl = null; }

/**
 * The slot LIST for the host's editor: every colour slot in document order with its pristine colour.
 * Requires a snapshot, so the host asks for it via loadSlots below rather than calling this cold.
 */
export function slotList() {
    return slots ? slots.map(function (s) {
        return { id: s.id, prop: s.prop, index: s.index, base: s.base };
    }) : [];
}

/** Force the snapshot (without applying anything) so the host can populate its editor. */
export function loadSlots(styleUrl) {
    return ensurePristine(styleUrl).then(slotList);
}

/**
 * The RESOLVED colour of every slot, in document order — the export payload. The host replaces the Nth
 * #rrggbb literal in the pristine file with the Nth entry here, so the file keeps its formatting.
 * ⚠️ The host must check this length against the file's own literal count before writing.
 */
export function exportSlotColors() {
    return slots ? slots.map(resolve) : [];
}
