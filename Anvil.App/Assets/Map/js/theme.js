// ============================================================================================
// theme.js — reads a chrome color out of styles/themes/ for the consumers that CAN'T use var().
//
//     anvil-dark.css  :root { --anvil-scope-ring: #9fe0ff; }
//                          │
//        CSS ──────────────┤  uses var(--anvil-scope-ring) directly. Not this module's business.
//                          │
//        JS  ──────────────┴─►  color('--anvil-scope-ring', '#9fe0ff')
//                                 └─► MapLibre paint props, canvas strokeStyle
//
// Two things in the page cannot read a CSS variable and so come through here: a MapLibre paint
// property (states.js, radar-scope.js, radar-ruler.js) and a canvas 2D stroke (outlook.js's hatch
// tiles). SVG used to be a third; the markers, handles and leader lines are now <template>s whose
// fill/stroke are CSS (styles/components/), which reads var() directly.
//
// ⚠️ EVERY CALLER PASSES A FALLBACK, and it is not defensive noise. CSS degrades quietly — a missing
// variable just leaves an element unstyled — but an empty string handed to MapLibre as a color
// THROWS, and a throw inside a render aborts the frame and blanks every layer above it. The same
// reasoning as safeBbox in states.js: a wrong color beats a dead map.
//
// ⚠️ NOT CACHED, on purpose. Every caller reads at layer-add / element-build time, never per frame,
// and a cache would have to be invalidated on a theme switch — a whole mechanism to save a handful
// of getComputedStyle calls that nobody has measured as a cost.
// ============================================================================================

/**
 * The computed value of a CSS custom property on the document root.
 * @param {string} name  the property, including the leading dashes ('--anvil-ground').
 * @param {string} fallback  used when the variable is missing or empty — always pass a real color.
 */
export function color(name, fallback) {
    try {
        const v = getComputedStyle(document.documentElement).getPropertyValue(name);
        if (v) {
            const trimmed = v.trim();
            if (trimmed) return trimmed;
        }
    } catch (e) { /* fall through to the fallback */ }
    return fallback;
}
