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

// ⚠️ ENTRIES CARRY NO TRAITS TODAY — the registry is the product ID list and the place their per-product
// notes live, nothing more. It used to carry `lazy: true` for the dealiasing products (velocity/SRV), back
// when the decode built every non-lazy product eagerly and only lazy ones waited for the upgrade queue.
// The ON-DEMAND model (radar.js wantedProducts(): build reflectivity + exactly what the visible panes ask
// for) plus the ~0.2 s dealias rewrite retired that split — every product is built on demand now, so
// nothing branched on the flag any more. Add a trait back here the moment one earns a branch.
export const PRODUCTS = {
    reflectivity: {},
    velocity:     {}, // dealiases (dealiasSweep)
    srv:          {}, // storm-relative velocity: base velocity − storm-motion component; shares
                      // velocity's dealiased cut, so it dealiases too. See buildSrv.
    cc:           {},
    kdp:          {}, // derived from ΦDP (½·dΦDP/dr); cheap windowed slope, no dealias
    zdr:          {}, // direct dual-pol moment read (drop shape/size); cheap, no dealias
    sw:           {}, // direct Doppler moment read (velocity spread); cheap, no dealias
};

// Registry / iteration order. Reflectivity is first deliberately: it's the widest sweep, so the on-map
// range ring is taken from whichever built product comes first (see decodeAndBuild).
export const PRODUCT_IDS = Object.keys(PRODUCTS);
