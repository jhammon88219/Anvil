// radar-products.js — the single source of truth for the radar product REGISTRY: which moments the app
// renders and their per-product traits. Both the decoder (radar-decode.js, static import) and the
// render/upgrade layer (radar.js, dynamic cached import) read this, so adding a product is ONE entry
// here (+ a build fn in radar-decode's BUILDERS + a ramp in radar-ramps.js) instead of editing the
// flat per-moment fields (velPositions/ccPositions/…) that used to be scattered across the
// decode → transfer → render path.
//
// The seven products, as the user meets them (one chip per pane, this order in the dropdown):
//
//   Ref  reflectivity  dBZ    where + how heavy the precip is          — always built
//   Vel  velocity      m/s    toward / away from the radar             — dealiases
//   SRV  srv           m/s    velocity minus the storm's own motion    — dealiases (shares Vel's cut)
//   CC   cc            0–1    is it all one kind of thing? (debris)
//   KDP  kdp           °/km   rain rate, derived from ΦDP
//   ZDR  zdr           dB     drop shape — flat rain vs round hail
//   SW   sw            m/s    velocity spread — turbulence

// `lazy: true` marks a product whose geometry is EXPENSIVE to build — today only velocity, because it's
// the one moment that must dealias (dealiasSweep, ~1.5 s/frame). Lazy products are built on demand /
// prefetched rather than eagerly (see radar.js's upgrade queue). Non-lazy products (reflectivity, CC,
// and the future ZDR / spectrum width / ΦDP) are cheap and always built.
export const PRODUCTS = {
    reflectivity: { lazy: false },
    velocity:     { lazy: true },
    srv:          { lazy: true }, // storm-relative velocity: base velocity − storm-motion component; shares
                                  // velocity's dealiased cut, so it dealiases too (lazy). See buildSrv.
    cc:           { lazy: false },
    kdp:          { lazy: false }, // derived from ΦDP (½·dΦDP/dr); cheap windowed slope, no dealias
    zdr:          { lazy: false }, // direct dual-pol moment read (drop shape/size); cheap, no dealias
    sw:           { lazy: false }, // direct Doppler moment read (velocity spread); cheap, no dealias
};

// Registry / iteration order. Reflectivity is first deliberately: it's the widest sweep, so the on-map
// range ring is taken from whichever built product comes first (see decodeAndBuild).
export const PRODUCT_IDS = Object.keys(PRODUCTS);

// Is this product's geometry expensive/lazy? Safe for unknown ids (returns false).
export function isLazy(id) { return !!(PRODUCTS[id] && PRODUCTS[id].lazy); }
