// outlook.js — the SPC outlook overlay: translucent probability fills + per-CIG intensity HATCHING,
// with the nested intensity groups clipped into mutually-exclusive rings (so lower hatch doesn't show
// through the higher group's gaps). Extracted from map.js. Owns the state + all helpers + the
// show/clear/setOpacity shims; map.js delegates and calls reAdd(map) after a basemap switch — setStyle
// drops custom sources/layers AND registered IMAGES, so the hatch images are re-ensured on each add.
//
// WHAT IT DRAWS — probability fills in SPC's own colors, with the intensity groups hatched on top:
//
//   ┌──────────────────────────────────┐   fills: one per category/probability, colors read
//   │▒▒▒▒▒▒▒▒ 5%  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒│   straight from the SPC GeoJSON (never hand-coded here)
//   │▒▒▒┌────────────────────┐▒▒▒▒▒▒▒▒│
//   │▒▒▒│▓▓▓▓ 10% ▓▓▓▓▓▓▓▓▓▓▓│▒▒▒▒▒▒▒▒│   hatching = the Conditional Intensity Groups:
//   │▒▒▒│▓▓┌───────────┐▓▓▓▓▓│▒▒▒▒▒▒▒▒│      CIG1  ╱ ╱ ╱   DASHED  '/'
//   │▒▒▒│▓▓│ ╱ ╱ CIG1  │▓▓▓▓▓│▒▒▒▒▒▒▒▒│      CIG2  ╲╲╲╲╲   solid   '\'
//   │▒▒▒│▓▓│ ╲╳╲ CIG2… │▓▓▓▓▓│▒▒▒▒▒▒▒▒│      CIG3  ╳╳╳╳╳   solid cross-hatch
//   │▒▒▒│▓▓└───────────┘▓▓▓▓▓│▒▒▒▒▒▒▒▒│
//   │▒▒▒└────────────────────┘▒▒▒▒▒▒▒▒│   ⚠️ the groups NEST (CIG3 ⊂ CIG2 ⊂ CIG1), so each lower
//   └──────────────────────────────────┘   group is CLIPPED to exclude the higher one before it is
//                                          drawn — otherwise the sparser hatch shows through the
//                                          denser one's gaps and both read as mush
//
// The hatch tiles are drawn at runtime into a canvas (makeHatchImage) and registered as MapLibre
// images, so tuning the look = the tile size / line width / dash there, not a bundled asset.
//
// A DAY WITH NO RISK AREAS draws SPC's own sentence across CONUS instead, as spc.noaa.gov does:
//
//   ┌──────────────────────────────────┐   the text is the feed's LABEL, VERBATIM — never ours.
//   │                                  │   SPC sends one DN 0 feature for it ("Predictability Too
//   │      Predictability Too Low      │   Low", "Potential Too Low", "Less Than 5% All Areas",
//   │                                  │   "No Thunderstorms Forecast", fire's lowercase `label`
//   └──────────────────────────────────┘   "Probability Too Low"); the forecaster picks the wording,
//                                          so it can't be mapped from the day number.
// It is a DOM marker ABOVE the radar site keys (addNote says why), click-through, 40 px.
// ⚠️ DN 0 is NEVER a risk level (no catalog level uses it), so a DN 0 feature is kept OUT of the fill /
// line layers — the fire feed ships a ~200 m placeholder square in Kansas as its "empty" geometry.
// The note shows only when EVERY feature is DN 0; one real area and it stays hidden. The IEM past
// archive carries no DN 0 feature, so a dead PastCast day just draws nothing.

// Slots beneath place names (readable through the fill) — see layers.js.
import { firstSymbolLayerId } from './layers.js';
// The hatch tiles are drawn on a 2D canvas, which can't read a CSS variable — hence theme.js.
import * as Theme from './theme.js';
// The "no risk areas" sentence is a <template> in map.html (tpl-outlook-note).
import * as Templates from './templates.js';

let currentOutlookUrl = null;    // GeoJSON URL currently shown (re-add after a basemap switch)
let outlookData = null;          // fetched + CLIPPED GeoJSON (reused on re-add — no re-fetch/re-clip)
let currentOutlookOpacity = 0.05;
// Whether the CIG hatch layers are drawn (ForeCast legend's "Show" box). ⚠️ RESET to shown by every
// show(): the layer is shared with the PastCast outlook, which has no such box, so a hide must never
// leak into it. The live OutlookViewModel re-pushes its own value right after each show.
let hatchingVisible = true;

// SPC marks its "significant"/intensity areas as separate polygons (Conditional Intensity Groups):
// LABEL = "CIG1"/"CIG2"/"CIG3" (tornado & wind go to 3, hail to 2); legacy single-significant "SIGN"
// may appear on older data. They render as HATCHING over the probability color, split onto per-group
// fill-pattern layers (see ensureHatchImages + addOutlookLayers). The groups nest (CIG3 ⊂ CIG2 ⊂ CIG1),
// so before rendering we clip each lower group to exclude the higher ones (clipSigFeatures).
const SIG_FILTER = ['any', ['in', 'CIG', ['get', 'LABEL']], ['in', 'SIG', ['get', 'LABEL']]];

// The "no risk areas" placeholder: DN 0 in either spelling (convective `DN`, fire `dn`) and either JSON
// type. Everything else — including IEM past features, which carry no DN at all — is a real area.
const NOT_NOTE = ['!=', ['to-number', ['coalesce', ['get', 'DN'], ['get', 'dn'], -1], -1], 0];
function isNote(f) {
    const p = f.properties || {};
    const dn = p.DN !== undefined ? p.DN : p.dn;
    return dn !== undefined && dn !== null && Number(dn) === 0;
}
// SPC's sentence for an all-placeholder issuance, or null when there's a real area (or no wording).
function noteText(geojson) {
    const fs = geojson.features || [];
    if (fs.length === 0 || !fs.every(isNote)) return null;
    for (const f of fs) {
        const t = String(f.properties.LABEL || f.properties.label || '').trim();
        if (t) return t;
    }
    return null;
}
// Where the sentence sits — the middle of CONUS, where SPC centres it on its own maps.
const NOTE_ANCHOR = [-97.5, 38.5];

// Significance rank from a LABEL: CIG1/2/3 -> 1/2/3, legacy "SIGN" -> 1, anything else 0.
function sigRank(label) {
    if (!label) return 0;
    if (label.indexOf('CIG') === 0) { const n = parseInt(label.slice(3), 10); return isNaN(n) ? 1 : n; }
    if (label.indexOf('SIG') >= 0) return 1;
    return 0;
}

// Builds one diagonal-hatch tile (light lines on transparent). `tile` = repeat size (bigger = sparser),
// `width` = line thickness. `opts`: `slash` draws solid SW→NE '/' lines, `backslash` solid NW→SE '\'
// lines (both => cross-hatch), `slashDashed` the '/' as DASHES.
// ⚠️ SEAMS: a solid line is drawn through all THREE of its diagonals that touch the tile (the corner
// ones too), so the antialiased ends meet the neighbouring tile's and the stripe never breaks. The dashes
// are FIXED pieces, not setLineDash — a dash rule restarts per tile and leaves dot-like stubs at the
// edges. Two dashes per diagonal at u ∈ [.09,.41] and [.59,.91]: every gap is .18 of the diagonal,
// across the tile seam included.
function makeHatchImage(tile, width, opts) {
    opts = opts || {};
    // ⚠️ The INK is chrome and themed; the PATTERN (dashes / lines / cross-hatch) encodes which CIG
    // group an area is and is data — see the membership rule in styles/themes/anvil-dark.css.
    const COLOR = Theme.color('--anvil-hatch-ink', 'rgba(235,235,235,0.9)');
    const c = document.createElement('canvas');
    c.width = c.height = tile;
    const ctx = c.getContext('2d');
    ctx.strokeStyle = COLOR;
    ctx.lineWidth = width;
    ctx.beginPath();
    ctx.lineCap = 'round';
    const h = tile / 2;
    if (opts.slash) {                                             // '/'  x+y = 0, tile, 2·tile
        for (const p of [0, h, tile]) { ctx.moveTo(p - tile, p + tile); ctx.lineTo(p + tile, p - tile); }
    }
    if (opts.backslash) {                                         // '\'  x−y = −tile, 0, tile
        for (const [px, py] of [[0, tile], [h, h], [tile, 0]]) { ctx.moveTo(px - tile, py - tile); ctx.lineTo(px + tile, py + tile); }
    }
    ctx.stroke();
    if (opts.slashDashed) {                                       // '/'  along (0,tile) → (tile,0)
        ctx.beginPath();
        ctx.lineCap = 'butt';
        for (const [a, b] of [[0.09, 0.41], [0.59, 0.91]]) {
            ctx.moveTo(a * tile, tile - a * tile);
            ctx.lineTo(b * tile, tile - b * tile);
        }
        ctx.stroke();
    }
    return ctx.getImageData(0, 0, tile, tile);
}

// One hatch image per Conditional Intensity Group, matching SPC's own legend:
// CIG1 = DASHED '/', CIG2 = solid '\', CIG3 = solid cross.
// ⚠️ THE CATALOG'S NAMES READ BACKWARDS. SpcRiskCatalog's `hatch` is ESRI's fill style, and ESRI (after
// GDI+) calls '\' ForwardDiagonal and '/' BackwardDiagonal — so CIG1 = esriSFSBackwardDiagonal is '/'.
// 2026-09 read the names literally and swapped CIG1/CIG2 against spc.noaa.gov until 2026-10-28
// (docs/bugs.md). The pattern is the only thing distinguishing the groups (every CIG level is black),
// so getting it backwards misreports the intensity outright. The dashes are SPC's legend, not ESRI's
// (a simple fill can't dash). Mirrored in Controls/Primitives/OutlookLegend.xaml. setStyle drops
// registered images, so re-ensure on each add.
const HATCH_TILE = 28;
function ensureHatchImages(map) {
    if (!map.hasImage('sig-hatch-1')) map.addImage('sig-hatch-1', makeHatchImage(HATCH_TILE, 1.6, { slashDashed: true }));
    if (!map.hasImage('sig-hatch-2')) map.addImage('sig-hatch-2', makeHatchImage(HATCH_TILE, 1.6, { backslash: true }));
    if (!map.hasImage('sig-hatch-3')) map.addImage('sig-hatch-3', makeHatchImage(HATCH_TILE, 1.6, { slash: true, backslash: true }));
}

// --- Clipping the nested CIG areas into exclusive rings --------------------------------------------
// The groups nest (CIG3 ⊂ CIG2 ⊂ CIG1). We make each lower group exclude the next-higher one by punching
// the higher polygons in as HOLES — valid because they're strictly nested (containment + winding fix).
function signedArea(ring) {
    let s = 0;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        s += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];
    }
    return s / 2;
}
function pointInRing(pt, ring) {
    const x = pt[0], y = pt[1];
    let inside = false;
    for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
        const xi = ring[i][0], yi = ring[i][1], xj = ring[j][0], yj = ring[j][1];
        if (((yi > y) !== (yj > y)) && (x < (xj - xi) * (y - yi) / (yj - yi) + xi)) inside = !inside;
    }
    return inside;
}
function eachPolygon(geom, cb) {
    if (!geom) return;
    if (geom.type === 'Polygon') cb(geom.coordinates);
    else if (geom.type === 'MultiPolygon') geom.coordinates.forEach(cb);
}
// Append every higher-group exterior ring that falls inside a lower polygon part as a hole (reversed
// when needed so MapLibre's winding-based ring classifier treats it as a hole).
function punchHoles(lower, highers) {
    eachPolygon(lower.geometry, function (poly) {
        const ext = poly[0];
        const extSign = signedArea(ext) >= 0 ? 1 : -1;
        highers.forEach(function (h) {
            eachPolygon(h.geometry, function (hpoly) {
                const hext = hpoly[0];
                if (!pointInRing(hext[0], ext)) return;
                const hole = hext.slice();
                if ((signedArea(hole) >= 0 ? 1 : -1) === extSign) hole.reverse();
                poly.push(hole);
            });
        });
    });
}
// For each significance group, subtract the next-higher group (nested, so it contains all above it).
// Mutates the features' geometries in place.
function clipSigFeatures(geojson) {
    const sigs = (geojson.features || []).filter(function (f) {
        return sigRank(f.properties && f.properties.LABEL) > 0;
    });
    sigs.forEach(function (lower) {
        const lowerRank = sigRank(lower.properties.LABEL);
        const higherRanks = sigs.map(function (s) { return sigRank(s.properties.LABEL); })
            .filter(function (r) { return r > lowerRank; });
        if (higherRanks.length === 0) return;
        const nextRank = Math.min.apply(null, higherRanks);
        const toPunch = sigs.filter(function (s) { return sigRank(s.properties.LABEL) === nextRank; });
        punchHoles(lower, toPunch);
    });
}

function removeOutlookLayers(map) {
    ['spc-outlook-sig-line', 'spc-outlook-line', 'spc-outlook-sig1', 'spc-outlook-sig2', 'spc-outlook-sig3', 'spc-outlook-fill'].forEach(function (id) {
        if (map.getLayer(id)) map.removeLayer(id);
    });
    if (map.getSource('spc-outlook')) map.removeSource('spc-outlook');
    removeNote(map);
}

// SPC's "no risk areas" sentence (see the header), ONE per map (every pane gets the outlook).
// ⚠️ A DOM MARKER, NOT A SYMBOL LAYER — on purpose: the radar site keys are DOM markers, which float over
// the whole canvas, so no map layer can ever draw above them; this has to be a marker too, with a
// z-index over theirs (they set none). Consequences: it sits OUTSIDE the user's layer order (it isn't
// a layer), and it must never take a click — pointer-events:none, so the keys under it stay live.
// Colours are the theme's tokens read as CSS vars, so a theme switch restyles it with no rebuild.
const notes = new WeakMap(); // map -> maplibregl.Marker
function removeNote(map) {
    const m = notes.get(map);
    if (m) { m.remove(); notes.delete(map); }
}
function addNote(map) {
    removeNote(map);
    const text = outlookData && noteText(outlookData);
    if (!text) return;
    // <template id="tpl-outlook-note"> in map.html; its look (and halo) is styles/components/outlook.css.
    const el = Templates.clone('tpl-outlook-note');
    el.textContent = text;
    notes.set(map, new maplibregl.Marker({ element: el }).setLngLat(NOTE_ANCHOR).addTo(map));
}

function addOutlookLayers(map) {
    if (!outlookData) return;
    removeOutlookLayers(map);
    ensureHatchImages(map);
    map.addSource('spc-outlook', { type: 'geojson', data: outlookData });

    const before = firstSymbolLayerId(map);
    // Solid fill for the probability areas, excluding the significant areas — so the hatch shows the
    // probability color underneath through its gaps. EVERY feature reaching here now carries its own
    // fill: the convective "cake layers" ship it, and the two feeds that don't (fire weather, keyed on
    // `dn`, and the IEM past-outlook archive, keyed on a threshold string) are coloured host-side from
    // SpcRiskCatalog before the page sees them. The coalesce is a genuine last resort now, not the fire
    // path's normal behaviour — fire weather used to render every risk level as this flat gray.
    map.addLayer({
        id: 'spc-outlook-fill',
        type: 'fill',
        source: 'spc-outlook',
        filter: ['all', ['!', SIG_FILTER], NOT_NOTE],
        paint: {
            'fill-color': ['coalesce', ['get', 'fill'], '#888888'],
            'fill-opacity': currentOutlookOpacity
        }
    }, before);
    // One hatch layer per Conditional Intensity Group, each a CONSTANT fill-pattern with a LABEL filter.
    // Geometries are already clipped mutually exclusive (clipSigFeatures). CIG1 also catches legacy "SIGN".
    [
        { id: 'spc-outlook-sig1', img: 'sig-hatch-1', filter: ['any', ['==', ['get', 'LABEL'], 'CIG1'], ['in', 'SIG', ['get', 'LABEL']]] },
        { id: 'spc-outlook-sig2', img: 'sig-hatch-2', filter: ['==', ['get', 'LABEL'], 'CIG2'] },
        { id: 'spc-outlook-sig3', img: 'sig-hatch-3', filter: ['==', ['get', 'LABEL'], 'CIG3'] }
    ].forEach(function (s) {
        map.addLayer({
            id: s.id,
            type: 'fill',
            source: 'spc-outlook',
            filter: s.filter,
            layout: { visibility: hatchingVisible ? 'visible' : 'none' },
            paint: { 'fill-pattern': s.img }
        }, before);
    });
    // Outlines for every probability / category area, in the feed's own colour.
    map.addLayer({
        id: 'spc-outlook-line',
        type: 'line',
        source: 'spc-outlook',
        filter: ['all', ['!', SIG_FILTER], NOT_NOTE],
        paint: {
            'line-color': ['coalesce', ['get', 'stroke'], '#555555'],
            'line-width': 1.5
        }
    }, before);
    // Every hatched area is outlined, as SPC draws it — but in the HATCH INK, not the feed's black: on the
    // dark map a black edge vanished. Part of the hatching, so the legend's Show box hides it too.
    map.addLayer({
        id: 'spc-outlook-sig-line',
        type: 'line',
        source: 'spc-outlook',
        filter: SIG_FILTER,
        layout: { visibility: hatchingVisible ? 'visible' : 'none' },
        paint: {
            'line-color': Theme.color('--anvil-hatch-ink', 'rgba(235,235,235,0.9)'),
            'line-width': 1.5
        }
    }, before);
    addNote(map);
}

// Fetch the outlook GeoJSON ourselves, clip the nested CIG areas into exclusive rings, then render.
// (We don't hand the URL to the source directly because the clip has to run on the parsed geometry
// first.) Guards against an out-of-order response when the user switches products quickly.
function loadOutlook(map, url) {
    fetch(url).then(function (r) { return r.json(); }).then(function (gj) {
        if (currentOutlookUrl !== url) return; // a newer selection won
        clipSigFeatures(gj);
        outlookData = gj;
        if (map.isStyleLoaded()) addOutlookLayers(map);
        else map.once('idle', function () { addOutlookLayers(map); });
    }).catch(function (e) {
        console.error('outlook load failed: ' + e);
    });
}

export function show(map, url) {
    currentOutlookUrl = url;
    outlookData = null;
    hatchingVisible = true; // see hatchingVisible — a new outlook always starts hatched
    loadOutlook(map, url);
}

export function clear(map) {
    currentOutlookUrl = null;
    outlookData = null;
    removeOutlookLayers(map);
}

export function setOpacity(map, opacity) {
    currentOutlookOpacity = opacity;
    if (map.getLayer('spc-outlook-fill')) {
        map.setPaintProperty('spc-outlook-fill', 'fill-opacity', opacity);
    }
}

// Hides/shows ONLY the CIG hatch layers + their outline; fills and the probability outlines stay. Remembered for re-adds.
export function setHatchingVisible(map, visible) {
    hatchingVisible = !!visible;
    ['spc-outlook-sig1', 'spc-outlook-sig2', 'spc-outlook-sig3', 'spc-outlook-sig-line'].forEach(function (id) {
        if (map.getLayer(id)) map.setLayoutProperty(id, 'visibility', hatchingVisible ? 'visible' : 'none');
    });
}

// Re-add after a basemap switch: reuse the already-clipped data, or re-fetch if it isn't loaded yet.
export function reAdd(map) {
    if (outlookData) addOutlookLayers(map);
    else if (currentOutlookUrl) loadOutlook(map, currentOutlookUrl);
}
