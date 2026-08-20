// NEXRAD Level II radar rendering for the single MapLibre map.
//
// Holds a loop of decoded frames (one per volume) and renders the current frame via a
// MapLibre WebGL custom layer beneath the boundary lines / outlook / labels. Heavy work
// (bzip2 decode + gate geometry) runs off the UI thread in radar-worker.js -> radar-decode.js;
// this file owns the GL layer, the frame store, and the host command shims. The host (C#)
// fetches each volume to the "radarlevel2" virtual host and drives the loop:
//   radarBeginLoop(lat,lon) -> radarAddFrame(url,index) xN -> radarShowFrame(index)
// Each built frame posts {type:'radarFrameReady', index, hasData} back to the host.
(function () {
    'use strict';

    // radar.js's own URL, captured at load time (document.currentScript is valid during this
    // synchronous IIFE). The worker is resolved relative to THIS file rather than the page, so it
    // keeps working when radar.js lives in a subfolder — a Worker URL otherwise resolves against the
    // document's base URL (the page), not the calling script, which would 404 → silent main-thread fallback.
    const SELF_SCRIPT = (document.currentScript && document.currentScript.src) || location.href;

    const LAYER_ID = 'level2-radar';
    const MIN_DBZ = 10;
    const GRID_NODATA = -32768; // matches radar-decode.js buildGrid sentinel

    // ---- Panes: N VIEWS over ONE loop ----------------------------------------------------------
    // A pane is a PRODUCT VIEW of one site. Every pane draws the SAME loop at the SAME frame from the
    // SAME decoded geometry — it differs only in which moment it renders — so everything expensive
    // stays module-level and singular: frames[], the decoded cache, the worker pool, the upgrade queue,
    // the storm motion, the dealias seed. A view owns only what genuinely CANNOT be shared: its map,
    // its chosen product, and its GL objects, which belong to that canvas's context and can't be handed
    // to another. Adding a pane therefore costs an upload of geometry already decoded, not a decode.
    // views[0] is the PRIMARY pane (same ordering as map.js's maps[]).
    let views = [];
    function forEachView(fn) { for (let i = 0; i < views.length; i++) fn(views[i], i); }
    function viewFor(map) { for (let i = 0; i < views.length; i++) { if (views[i].map === map) return views[i]; } return null; }
    function primaryView() { return views.length ? views[0] : null; }
    // The primary pane's product. Used ONLY where a single product still genuinely means something (the
    // legend push, the build-progress message's label). Everything that gates WORK uses viewProducts().
    function activeProduct() { const v = primaryView(); return v ? v.product : 'reflectivity'; }
    // The DISTINCT products on screen right now — the basis of every "what has to be built" answer in
    // multi-pane. Deduped, because four panes may well share a product.
    function viewProducts() {
        const out = [];
        for (let i = 0; i < views.length; i++) {
            if (out.indexOf(views[i].product) < 0) out.push(views[i].product);
        }
        return out.length ? out : ['reflectivity'];
    }
    function makeView(map, index) {
        return {
            map: map,
            index: index,
            product: 'reflectivity',   // the host assigns each pane's product via setProduct(paneIndex, id)
            // GL objects — per CONTEXT, (re)created in the custom layer's onAdd; null when not attached.
            program: null, posBuf: null, colorBuf: null,
            aPos: -1, aColor: -1, uMatrix: null, uOpacity: null,
            uploadedFrame: -1,         // which frame's geometry is in THIS view's buffers
            uploadedProduct: '',       // which product's geometry is uploaded (re-upload on a switch)
            ctxBound: false,           // webglcontextlost/restored listeners attached to this canvas
            inspectMove: null, inspectOut: null, inspectCamera: null, // bound inspect handlers (to off() them)
            crossEl: null,             // this pane's mirrored inspect crosshair (created on first use)
            rangeAdded: false,         // this view's range-ring layer is up
        };
    }
    // Force every view to re-upload on its next render (a frame or product change invalidates buffers).
    function invalidateUploads() { forEachView(function (v) { v.uploadedFrame = -1; }); }

    // Shared site projection (geo.js — the SAME math radar-decode's buildGates uses, so overlays line
    // up with the painted gates). radar.js is a classic-script IIFE so it can't statically import; load
    // the module once at startup and cache it in `Geo`. Until it resolves, the geo-dependent overlays
    // (range ring, sweep, inspector) skip drawing and re-draw on the next frame/tick/mousemove — geo.js
    // is a tiny same-origin file, loaded long before any radar frame can decode.
    let Geo = null;
    import('./geo.js').then(function (m) { Geo = m; }).catch(function (e) { hostLog('geo.js load failed: ' + (e && e.message ? e.message : e)); });

    // Product registry (radar-products.js — the single source of truth shared with radar-decode.js).
    // Same tiny-module dynamic-import pattern as geo.js: loaded once at startup, cached in `Products`,
    // resolved long before the user can switch products / the first frame upgrades. productLazy() tells
    // the render/upgrade paths whether the active product is built lazily (velocity today); it defaults
    // to non-lazy for an unknown/not-yet-loaded id, which is safe (the default reflectivity isn't lazy).
    let Products = null;
    import('./radar-products.js').then(function (m) { Products = m.PRODUCTS; }).catch(function (e) { hostLog('radar-products.js load failed: ' + (e && e.message ? e.message : e)); });
    function productLazy(p) { return !!(Products && Products[p] && Products[p].lazy); }
    function productKnown(p) { return !Products || !!Products[p]; } // permissive until the registry loads
    // The non-reflectivity products we want built RIGHT NOW (reflectivity is always built by the decoder).
    // ON-DEMAND model: build ONLY the active product — the decode no longer builds all seven every time. The
    // dealias is cheap now (~0.2 s), so velocity/SRV don't need to be coupled; each builds on demand when
    // selected. Velocity is still PREFETCHED (warmed in the background) but only while the user is on
    // reflectivity, so switching to it is instant without taxing other views. Empty when on reflectivity with
    // no prefetch. Returns only registered products (a bad id would loop forever in needsBuild).
    // Velocity + SRV are COMPANIONS: SRV = velocity − storm motion, sharing the (expensive) dealiased cut, so
    // once one is built the other is nearly free (an azimuth transform + a geometry pass). We build BOTH
    // whenever a Doppler product is active OR velocity is prefetching, so switching Reflectivity↔Velocity↔SRV
    // is instant instead of re-decoding the whole loop on the switch. SRV waits until the storm motion has
    // resolved (srvMotionReady) — otherwise it would build at base velocity and need a rebuild when the auto
    // motion lands. With the eager per-loop motion compute, that's ready before prefetch does much.
    function srvMotionReady() { return !stormMotion.auto || (!!_autoMotion && !_autoMotion.insufficient); }
    // ⚠️ MULTI-PANE: this is the UNION over every visible pane's product. It is the one change the radar
    // engine needed for multi-pane — everything else is view plumbing. Each pane contributes under exactly
    // the rules that used to apply to the single active product, so a one-pane union reduces to the old
    // behaviour byte for byte. Consequence, knowingly accepted: a quad of four different products builds
    // four products per frame, so a quad backfills slower per frame than a single pane. That is inherent —
    // four products are on screen. First paint is untouched (Rule 1 still paints the visible frame first).
    function wantedProducts() {
        if (!Products) return [];
        var out = [];
        var shown = viewProducts();
        // Each pane's own product — EXCEPT SRV waits for the storm motion (srvMotionReady): we never build
        // SRV at the wrong/base motion (that caused a rebuild when the real motion landed). Until it's ready we
        // build base VELOCITY in SRV's place and render that as the stand-in (see render's SRV fallback).
        for (var si = 0; si < shown.length; si++) {
            var pid = shown[si];
            if (pid === 'reflectivity' || !Products[pid]) continue; // reflectivity is always built
            if (pid === 'srv' && !srvMotionReady()) {
                if (Products.velocity && out.indexOf('velocity') < 0) out.push('velocity');
            } else if (out.indexOf(pid) < 0) {
                out.push(pid);
            }
        }
        // Velocity + SRV are COMPANIONS (SRV = velocity − storm motion, sharing the expensive dealiased
        // cut), so a Doppler product ANYWHERE on screen — or the velocity prefetch — pulls both in.
        // ⚠️ velPrefetch counts UNCONDITIONALLY here. The single-pane original also required the active
        // product to be reflectivity, which quietly contradicted Rule 3 ("reflectivity, velocity and SRV
        // are built together, per frame, REGARDLESS of what product the user is viewing"): a frame
        // appended while the user sat on CC built refl+cc only, so its velocity never arrived and its
        // scrubber cell — gated on the duo — could never light. It also does not generalise to N panes,
        // where "the active product" is a set. Unconditional matches the law and costs nothing extra:
        // decodeFrame narrows each build to what a frame is actually MISSING (Rule 6).
        var doppler = (shown.indexOf('velocity') >= 0 || shown.indexOf('srv') >= 0) || velPrefetch;
        if (doppler) {
            if (Products.velocity && out.indexOf('velocity') < 0) out.push('velocity');
            if (Products.srv && out.indexOf('srv') < 0 && srvMotionReady()) out.push('srv');
        }
        // Dual-pol second wave: once the trio has settled we also want CC/KDP/ZDR/SW on every frame, so a
        // later switch to any of them is instant. fullPrefetch only turns on AFTER the backfill + motion are
        // done (maybeArmFullPrefetch), so this never delays first paint or the trio. decodeFrame narrows each
        // frame's build to the ones it's actually missing (Rule 6), so already-built frames aren't re-decoded.
        if (fullPrefetch) {
            var extra = dualPolIds();
            for (var i = 0; i < extra.length; i++) if (out.indexOf(extra[i]) < 0) out.push(extra[i]);
        }
        return out;
    }
    // Whether the products we want (wantedProducts) were already built in a decode result — used to reject a
    // cache hit / decide upgrades. True when nothing extra is wanted (empty set) or the registry hasn't loaded.
    function wantedBuiltIn(r) {
        return wantedProducts().every(function (id) { return !!(r && r.built && r.built[id]); });
    }

    // frames[index] = { moments: { id: { positions, colors, count } | null }, grids, built, ... }:
    // per-product gate geometry (baked colors) keyed by product id (radar-products.js), so switching
    // product is a map lookup + upload — instant for eagerly-built products (no re-decode). A null/absent
    // moment means that product has nothing to draw on this frame. currentFrame is the index rendered.
    let frames = [];
    let currentFrame = -1;

    // ---- Decoded-frame cache (instant site revisits / replay toggles) ----
    // Decoding a volume (bzip2 + gate geometry + dealias) is the expensive part of a site load, and
    // beginLoop() wipes frames[] on every (re)selection — so revisiting a site, or toggling replay,
    // used to re-fetch + re-decode volumes we'd just built. This keeps the decoded result keyed by its
    // stable volume URL (radarlevel2/{site}_{yyyyMMdd_HHmmss}.V06 — deterministic per volume), so a
    // revisit reuses the geometry SYNCHRONOUSLY on the main thread (no fetch, no worker decode). The
    // geometry is immutable, so sharing the typed arrays between the cache and frames[] is safe; LRU-
    // capped to bound memory (the cached res also carries the inspector value-grids, so inspect stays
    // instant on revisit too). Survives beginLoop/clear on purpose — only the cap evicts.
    const decodedCache = new Map(); // url -> stored applyFrameResult-shaped result (arrays shared with frames[])
    const DECODE_CACHE_MAX = 96;    // ~a handful of sites' worth of loop frames; tune down if memory bites
    function cacheGet(url) {
        if (!url) return null;
        const v = decodedCache.get(url);
        if (v) { decodedCache.delete(url); decodedCache.set(url, v); } // re-insert = move to most-recently-used
        return v || null;
    }
    function cachePut(url, res) {
        if (!url || res.empty || res.error) return; // don't cache empties/failures — let them re-fetch
        decodedCache.delete(url);
        decodedCache.set(url, res);
        while (decodedCache.size > DECODE_CACHE_MAX) decodedCache.delete(decodedCache.keys().next().value);
    }

    // ---- Lazy-upgrade queue (bounded, current-frame-first) ----
    // Switching to Velocity (or turning Inspect on) needs the loaded frames re-decoded to add the
    // geometry they were built without (velocity/dealias, or the inspector grids). Firing all of them
    // at once floods the decode pool — on big dual-pol volumes (10-44 MB) the re-decodes can't keep up
    // and frames flash blank as playback runs over them. Instead we QUEUE the upgrades and run at most
    // UPGRADE_CONCURRENCY at a time, always picking the frame nearest the one on screen (preferring the
    // forward/playback direction), so velocity/grids fill in around what the user is watching. Re-pumped
    // as each upgrade finishes; the queue re-checks need at pump time, so a product switch mid-flight
    // just drains harmlessly. State is reset whenever the loop generation changes (beginLoop/remap/clear).
    var upgradeQueue = [];        // frame indices wanting an upgrade decode, not yet started
    var upgradeInFlight = {};     // idx -> true while its upgrade decode is outstanding
    var upgradeInFlightN = 0;
    var upgradeReason = {};       // idx -> why it was queued (for the decode-cause trace / diagnosis)
    var pumpingUpgrades = false;  // re-entrancy guard (a cache-hit upgrade completes synchronously)
    // Concurrent upgrade decodes. The dealias is CPU-bound, so this must NOT exceed physical cores —
    // navigator.hardwareConcurrency reports LOGICAL (SMT-doubled), and running more heavy dealias tasks than
    // physical cores just thrashes them (measured: bumping this on a 4-core/8-thread box did nothing). Keep
    // it modest and leave a worker free for the current frame / a new load.
    var UPGRADE_CONCURRENCY = 3;
    function resetUpgrades() { upgradeQueue = []; upgradeInFlight = {}; upgradeInFlightN = 0; upgradeReason = {}; }
    // A frame needs (re)building when it lacks the geometry for any product we currently want (the active
    // product, + velocity while prefetching). built[id] tracks whether the build RAN, so a frame with
    // genuinely no data for a product (built[id]=true, geometry null) won't re-decode forever. This decides
    // full-decode vs grids-only.
    function needsBuild(f) {
        return wantedProducts().some(function (id) { return !(f.built && f.built[id]); });
    }
    // Whether the ACTIVE product's inspector value grid has been BUILT for a frame — either by a full decode
    // (frame-level gridsBuilt, which built every product's grid) or the grids-only fast path (gridsExtra[id],
    // recorded per product). "Built" ≠ "has data": a product with no data yields a null grid, but it's still
    // marked, so a no-data frame isn't re-queued forever (what the old frame-level gridsBuilt guaranteed).
    // ⚠️ MULTI-PANE: Inspect is a cross-pane instrument — one cursor, every pane reporting its own value —
    // so a frame is "grid ready" only once EVERY visible pane's product has its value grid. A full decode
    // builds them all (frame-level gridsBuilt); the grids-only fast path marks them one at a time
    // (gridsExtra[id]). "Built" ≠ "has data": a product with no data yields a null grid but is still marked,
    // so a no-data frame isn't re-queued forever.
    function activeGridReady(f) {
        if (!f) return false;
        if (f.gridsBuilt) return true;
        var shown = viewProducts();
        for (var i = 0; i < shown.length; i++) {
            if (!(f.gridsExtra && f.gridsExtra[shown[i]])) return false;
        }
        return true;
    }
    // The first visible product still missing its value grid, or null. The grids-only fast path builds ONE
    // product's grid per pass, so with several panes the upgrade queue simply comes back for the next —
    // needsUpgrade stays true until every pane's grid is in.
    function missingGridProduct(f) {
        if (!f || f.gridsBuilt) return null;
        var shown = viewProducts();
        for (var i = 0; i < shown.length; i++) {
            if (!(f.gridsExtra && f.gridsExtra[shown[i]])) return shown[i];
        }
        return null;
    }
    function needsUpgrade(idx) {
        var f = frames[idx];
        if (!f || !f.url) return false;
        if (inspecting && !activeGridReady(f)) return true;       // active product's value grid not built yet, Inspect on
        return needsBuild(f);
    }
    function upgradePriority(idx) {
        if (currentFrame < 0) return idx;
        if (idx >= currentFrame) return idx - currentFrame;       // current (0) + ahead, in play order
        return (currentFrame - idx) + frames.length;              // behind the playhead: lowest priority
    }
    function queueUpgrade(idx, reason) {
        if (!needsUpgrade(idx) || upgradeInFlight[idx]) return;
        if (upgradeQueue.indexOf(idx) < 0) upgradeQueue.push(idx);
        upgradeReason[idx] = reason || 'upgrade';
        pumpUpgrades();
    }
    function queueAllUpgrades(reason) { for (var i = 0; i < frames.length; i++) queueUpgrade(i, reason); }
    function pumpUpgrades() {
        if (pumpingUpgrades) return; // a sync completion re-entered us; the outer loop keeps draining
        pumpingUpgrades = true;
        try {
            while (upgradeInFlightN < UPGRADE_CONCURRENCY && upgradeQueue.length) {
                var bestPos = -1, bestPri = Infinity;
                for (var p = 0; p < upgradeQueue.length; p++) {
                    var idx = upgradeQueue[p];
                    if (!needsUpgrade(idx)) continue;             // stale (already built / product changed)
                    var pri = upgradePriority(idx);
                    if (pri < bestPri) { bestPri = pri; bestPos = p; }
                }
                if (bestPos < 0) { upgradeQueue = upgradeQueue.filter(needsUpgrade); break; }
                var chosen = upgradeQueue.splice(bestPos, 1)[0];
                upgradeInFlight[chosen] = true;
                upgradeInFlightN++;
                decodeFrame(frames[chosen].url, chosen, 'up:' + (upgradeReason[chosen] || '?')); // async, or sync on a cache hit
                delete upgradeReason[chosen];
            }
        } finally {
            pumpingUpgrades = false;
        }
    }
    function upgradeDone(idx) { // an upgrade decode for idx settled (ok or error) — free its slot, pump next
        if (!upgradeInFlight[idx]) return;
        delete upgradeInFlight[idx];
        upgradeInFlightN--;
        pumpUpgrades();
    }
    // Tell the host how much of the loop is ready for the ACTIVE product, so the UI can show a "Building N/M"
    // readout and playback can hold at the built frontier instead of stuttering into a frame whose active
    // product isn't built yet. Every product except reflectivity is now built ON DEMAND, so for any of them a
    // frame reads ready only once its geometry is built; reflectivity is always built, so it reads all-ready.
    function postBuildProgress() {
        var total = frames.length;
        var label = activeProduct(); // the message's product label — the primary pane's, for the readout
        if (!total) { post({ type: 'radarBuildProgress', product: label, built: 0, total: 0, ready: [], complete: [] }); return; }
        // `ready` = ACTIVE-product readiness — drives playback's built-frontier hold (don't advance onto a
        // frame whose on-screen product isn't built). While SRV is active but its motion isn't ready we render
        // the VELOCITY stand-in, so report readiness by VELOCITY (what's actually on screen) — otherwise the
        // frontier reads all-not-ready and playback stalls.
        // ⚠️ MULTI-PANE: playback must not advance onto a frame that would be blank in ANY pane, so the
        // gate is EVERY visible pane's product, each with the same SRV→velocity substitution as before
        // (while SRV's motion is pending we render the velocity stand-in, so report by what is on screen).
        var gates = [];
        var shownNow = viewProducts();
        for (var gi = 0; gi < shownNow.length; gi++) {
            var gp = (shownNow[gi] === 'srv' && !srvMotionReady()) ? 'velocity' : shownNow[gi];
            if (gp !== 'reflectivity' && gates.indexOf(gp) < 0) gates.push(gp); // reflectivity is always built
        }
        // ⚠️ MULTI-PANE: a cell must not light while a pane it feeds is still empty, so the FILL gate is the
        // duo plus every other visible product — EXCEPT SRV, which stays out of it for the same reason it
        // always has (see below). fillExtra is the visible dual-pol set: those DO build per frame during the
        // backfill, so including them keeps the fill incremental rather than batched.
        var fillExtra = [];
        for (var fi = 0; fi < shownNow.length; fi++) {
            var fp = shownNow[fi];
            if (fp !== 'reflectivity' && fp !== 'velocity' && fp !== 'srv' && fillExtra.indexOf(fp) < 0) fillExtra.push(fp);
        }
        // `complete` = per-frame SCRUBBER-fill readiness (docs/radar-loop-flow.md Rule 2: the scrubber fills
        // left-to-right as frames complete). A frame is fill-ready once its reflectivity AND velocity are built
        // — the two products that build PER FRAME during the backfill, so cells light one-by-one as the backfill
        // progresses. SRV is deliberately NOT gated here: it depends on the loop's ONE storm motion (Rule 4/5),
        // a single loop-wide event that lands last (~15 s), so gating on it would hold every cell false and then
        // flip the whole scrubber true at once (batch fill, not incremental). SRV trails loop-wide per Rule 4 —
        // a filled cell shows the velocity stand-in for SRV until the motion lands, then SRV upgrades in place
        // with no scrubber change. (Regardless of the active product, so browsing reflectivity fills the same.)
        var built = 0, ready = new Array(total), complete = new Array(total);
        for (var i = 0; i < total; i++) {
            // A STALE frame (tilt switch — see retile) still renders its old elevation so the map never blanks,
            // but it is NOT this loop's data: report it unbuilt so the scrubber re-fills left-to-right (Rule 2)
            // and playback holds rather than running through a mix of elevations. Even reflectivity — normally
            // "always built" (eager) — has to wait for the new cut.
            var stale = !!(frames[i] && frames[i].stale);
            var b = !stale && frames[i] && frames[i].built;
            var r = !stale;
            for (var ri = 0; r && ri < gates.length; ri++) r = !!(b && b[gates[ri]]);
            ready[i] = r;
            if (r) built++;
            var reflOk = !!(b && b.reflectivity); // always built by any decode
            var velOk = !!(b && b.velocity);
            var extraOk = true;
            for (var ei = 0; extraOk && ei < fillExtra.length; ei++) extraOk = !!(b && b[fillExtra[ei]]);
            complete[i] = reflOk && velOk && extraOk;
        }
        post({ type: 'radarBuildProgress', product: label, built: built, total: total, ready: ready, complete: complete });
    }

    // (The rendered moment is PER VIEW now — see makeView/viewProducts above. There is no single
    //  `product` global any more: with N panes the question is always "which pane" or "the union".)
    // Storm motion MODE + MANUAL value for Storm-Relative Velocity (SRV). speedMs = m/s, dirDeg = compass
    // bearing the storm is MOVING TOWARD. `auto` (the default) means the motion is DERIVED from a full-volume
    // VAD wind profile → Bunkers (computeStormMotionForVolume below; a single tilt is too shallow to be
    // correct); auto off uses this manual speedMs/dirDeg ({0,0} = SRV identical to base velocity).
    // Storm motion is ALWAYS auto (VAD-derived) — the manual override was removed. `auto` stays true for the
    // life of the page; resolveStormMotion/srvMotionReady read it but it never flips. Kept as an object so the
    // auto machinery below reads uniformly.
    let stormMotion = { speedMs: 0, dirDeg: 0, auto: true };
    // ONE auto storm motion for the whole loop (RadarScope-style), recomputed only when the loop's newest
    // volume changes — NOT per frame. Per-volume motion made scrubbing churn (every scrubbed frame recomputed
    // + re-decoded its SRV) and made consecutive frames look inconsistent; storm motion barely varies over a
    // replay window, so one value keeps the loop stable. `_autoMotion` = decodeVwp's result
    // ({ speedMs, dirDeg, source, layers, topM, cuts } or { insufficient, topM }); `_autoMotionKey` = the
    // volume it was computed for (a repeat request for the same volume just republishes the readout).
    let _autoMotion = null;
    let _autoMotionKey = '';
    const _vwpInFlight = {};          // volKey -> true while its VWP compute is outstanding
    const _vwpPending = {};           // worker reqId -> { volKey, gen } (correlates the async vwp reply)
    let _vwpReqId = 0;
    let vwpGen = 0;                   // bumped on every beginLoop; a VWP result from an older gen is dropped
                                     // (a slow compute for the PREVIOUS site must not set this loop's motion)
    // What buildSrv subtracts: the manual value in manual mode; the loop's auto motion (if computed and
    // sufficient) else {0,0} (base velocity) until it lands. GLOBAL — every frame uses the same motion.
    function resolveStormMotion() {
        if (!stormMotion.auto) return { speedMs: stormMotion.speedMs, dirDeg: stormMotion.dirDeg };
        if (_autoMotion && !_autoMotion.insufficient) return { speedMs: _autoMotion.speedMs, dirDeg: _autoMotion.dirDeg };
        return { speedMs: 0, dirDeg: 0 };
    }
    // Compute the auto storm motion for the loop from ONE volume's tilts (the host passes the newest volume's
    // tilt URLs when SRV/auto is active, and only re-requests when the newest volume changes). Fetch → decodeVwp
    // off-thread → set the global motion + rebuild SRV once. Guarded so the same volume isn't recomputed.
    function computeStormMotionForVolume(urls) {
        if (!stormMotion.auto || !urls || !urls.length) return;
        const volKey = urls[0];
        if (volKey === _autoMotionKey) { hostLog('vwp cached (republish) ' + shortKey(volKey)); publishAutoMotion(); return; }
        if (_vwpInFlight[volKey]) { hostLog('vwp in-flight, skip ' + shortKey(volKey)); return; }
        _vwpInFlight[volKey] = true;
        const gen = vwpGen;
        hostLog('vwp start ' + shortKey(volKey) + ' tilts=' + urls.length + ' prod=' + viewProducts().join('|'));
        const w = getVwpWorker(); // a DEDICATED worker, so the motion isn't queued behind frame decodes
        if (w) {
            // The worker fetches the tilt volumes itself — up to ~8 × ~7 MB reads kept OFF the render thread
            // (they'd otherwise burst on the main thread right in the post-first-paint backfill window, exactly
            // when the user starts panning). Stale results are dropped by gen in onVwpResult, as before.
            const id = ++_vwpReqId; _vwpPending[id] = { volKey: volKey, gen: gen };
            w.postMessage({ vwp: true, reqId: id, urls: urls });
        } else {
            // No Worker API — fetch + decode on the main thread (unchanged fallback path).
            Promise.all(urls.map(function (u) {
                return fetch(u, { cache: 'no-store' }).then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.arrayBuffer(); });
            })).then(function (abs) {
                return import('./radar-decode.js').then(function (m) { return m.decodeVwp(abs); })
                    .then(function (motion) { onVwpResult(volKey, motion, gen); });
            }).catch(function (err) { onVwpError(volKey, err, gen); });
        }
    }
    // Dedicated Worker for the storm-motion compute — separate from the frame-decode pool so a ~5 s
    // whole-volume VWP decode never sits behind a backlog of frame decodes (which delayed the motion ~16 s and
    // let SRV build at the wrong/stale motion, then rebuild). Lazily created; falls back to main-thread decodeVwp.
    var vwpWorker; // undefined = not tried, Worker = ready, null = unavailable
    function getVwpWorker() {
        if (vwpWorker === undefined) {
            try {
                vwpWorker = new Worker(new URL('radar-worker.js', SELF_SCRIPT).href);
                vwpWorker.onmessage = function (e) {
                    var m = e.data; if (!m || !m.vwp) return;
                    var p = _vwpPending[m.reqId]; delete _vwpPending[m.reqId];
                    if (p) { if (m.error) onVwpError(p.volKey, new Error(m.error), p.gen); else onVwpResult(p.volKey, m.motion, p.gen); }
                };
                vwpWorker.onerror = function (e) { hostLog('vwp worker error: ' + (e && e.message ? e.message : e)); };
            } catch (e) { vwpWorker = null; hostLog('vwp worker unavailable; main-thread: ' + (e && e.message ? e.message : e)); }
        }
        return vwpWorker;
    }
    function onVwpError(volKey, err, gen) {
        delete _vwpInFlight[volKey];
        hostLog('vwp ' + shortKey(volKey) + ' failed: ' + (err && err.message ? err.message : err));
    }
    function onVwpResult(volKey, motion, gen) {
        delete _vwpInFlight[volKey];
        if (gen !== vwpGen) { hostLog('vwp result ' + shortKey(volKey) + ' DROPPED (stale loop)'); return; }
        const before = resolveStormMotion();
        _autoMotion = motion || { insufficient: true, topM: 0 };
        _autoMotionKey = volKey;
        publishAutoMotion();
        // Rebuild SRV only if the motion SRV would subtract actually changed (e.g. don't re-decode when the
        // result is "insufficient" and SRV was already at base velocity). One global rebuild at most.
        const after = resolveStormMotion();
        const changed = (after.speedMs !== before.speedMs || after.dirDeg !== before.dirDeg);
        hostLog('vwp result ' + shortKey(volKey) + ' '
            + (_autoMotion.insufficient ? 'INSUFFICIENT top=' + _autoMotion.topM
                : Math.round(_autoMotion.dirDeg) + '°@' + Math.round(_autoMotion.speedMs / 0.514444) + 'kt cuts=' + _autoMotion.cuts + ' ' + (_autoMotion.source || ''))
            + ' rebuild=' + changed
            + (_autoMotion.detail ? ' [' + _autoMotion.detail.join(' ') + ']' : '')); // per-cut top/pts/tier for threshold tuning
        if (changed) {
            dropAllSrvAndRequeue(); // motion CHANGED → invalidate stale SRV geometry (re-queues only if product==srv)
        }
        // Top up SRV in the BACKGROUND so a later switch to SRV is instant (the velocity-prefetch bargain). This
        // sits OUTSIDE the `changed` gate on purpose and is IDEMPOTENT — queueAllUpgrades only touches frames that
        // actually lack SRV (needsUpgrade is false once built), so it's a no-op for frames already done. That
        // makes it RECOVER any SRV a queue/decode race dropped, and cover frames added since the last motion
        // result, even when this result didn't change the value. Still gated by srvMotionReady(), so it NEVER
        // builds SRV at a base/wrong motion; skipped while SRV is the active product (that path re-queues above).
        if (viewProducts().indexOf('srv') < 0 && velPrefetch && srvMotionReady()) queueAllUpgrades('srvfill');
        // The motion was the trio's long pole — its resolution (a value OR a settled "insufficient") may be
        // the last thing gating the dual-pol second wave. Arm it now if the backfill is also done.
        maybeArmFullPrefetch();
    }
    function shortKey(u) { var m = /([A-Z]{3,4}_[0-9]{8}_[0-9]{6})/.exec(u || ''); return m ? m[1] : (u || '?'); }
    // Surface the loop's auto motion to the host (App Settings readout). speed is m/s → host converts to kt.
    function publishAutoMotion() {
        const m = _autoMotion;
        if (!m) return;
        if (m.insufficient) post({ type: 'radarStormMotion', insufficient: true, topM: m.topM || 0 });
        else post({ type: 'radarStormMotion', speedMs: m.speedMs, dirDeg: m.dirDeg, source: m.source, layers: m.layers, topM: m.topM, cuts: m.cuts });
    }
    // Drop every loaded/cached frame's SRV geometry so it rebuilds with the current storm motion, and (if SRV
    // is the active product) re-queue. Shared by manual setStormMotion and the auto-motion result — a motion
    // change is loop-wide, so this runs at most ONCE per change (never per scrubbed frame).
    function dropAllSrvAndRequeue() {
        function dropSrv(r) {
            if (!r) return;
            if (r.built) r.built.srv = false;      // force a rebuild with the new motion
            // NB: keep r.moments.srv so the frame keeps rendering the OLD-motion SRV until the rebuild replaces
            // it — no blank frame during the rebuild (the geometry is swapped in place when the decode lands).
            if (r.gridsExtra) r.gridsExtra.srv = false;
        }
        var n = 0;
        for (let i = 0; i < frames.length; i++) { if (frames[i] && frames[i].built && frames[i].built.srv) n++; dropSrv(frames[i]); }
        decodedCache.forEach(dropSrv);
        var srvShown = viewProducts().indexOf('srv') >= 0;
        hostLog('dropAllSrv: dropped ' + n + ' built srv frame(s), requeue=' + srvShown);
        if (srvShown) {
            invalidateUploads();
            queueAllUpgrades('motion');
            postBuildProgress();
            repaintAll();
        }
    }
    let velPrefetch = false; // build velocity (+SRV, its companion) on every frame even when reflectivity is
                             // the active product — armed by the host (prefetchVelocity) RIGHT AFTER FIRST
                             // PAINT, so the backfill builds COMPLETE frames in one pass (Rule 3), not a
                             // second sweep. Reset per new loop.
    // ---- Temporal dealias seed (first-guess velocity unfold; see radar-decode.js) ----
    // A velocity decode returns its cut's VAD wind profile; we keep the newest loop's profile and feed it
    // into subsequent decodes as the absolute-fold first guess. Winds vary slowly, so any recent cut is a
    // fine seed; null until the first velocity cut lands (then decodes just use the default anchor — today's
    // behavior). Reset per new loop so a new site never seeds from the old one's winds.
    let _loopSeedProfile = null;
    let _seedProfileIdx = -1;  // loop index the current seed came from (prefer the newest volume's profile)
    // ---- Dual-pol SECOND WAVE (CC/KDP/ZDR/SW prefetch) ----
    // The TRIO (reflectivity + velocity + SRV) is staged eagerly (velPrefetch → Rule 3) so refl↔vel↔SRV
    // switches are instant. The remaining dual-pol products build ON DEMAND by default. This second wave
    // warms them across the whole loop AFTER the trio has SETTLED, so a switch to any of them is instant too.
    // ⚠️ Strictly idle-time work: it self-arms (maybeArmFullPrefetch) only once first paint, the backfill,
    // AND the loop's storm motion are all done — so it NEVER competes with the latencies the trio staging
    // protects. The current-frame-first upgrade queue keeps it yielding to whatever the user does next. These
    // products are OUTSIDE the scrubber-fill law (docs/radar-loop-flow.md — the fill rides the DUO); this only
    // warms geometry in the background, it never touches the fill gate. Reset per new loop, like velPrefetch.
    let fullPrefetch = false;
    // The products the second wave warms = everything registered except the trio. Reads the registry so a
    // newly added dual-pol product is picked up automatically (Object.keys preserves radar-products.js order).
    function dualPolIds() {
        if (!Products) return [];
        var trio = { reflectivity: 1, velocity: 1, srv: 1 };
        return Object.keys(Products).filter(function (id) { return !trio[id]; });
    }
    // The trio has SETTLED for the whole loop when: velocity prefetch is armed (a real trio loop), every
    // frame's DUO (refl + velocity) is built (the backfill is done — SRV rides one motion, so it's not part
    // of this gate, same reasoning as the scrubber fill), and the loop's storm motion has resolved (a value
    // or a settled "insufficient"; auto off = trivially settled). At that point the VWP worker is idle and
    // the srvfill sweep has been kicked — the pipeline's heavy lifting is over, so background work is safe.
    function vwpInFlight() { for (var k in _vwpInFlight) return true; return false; }
    function trioSettled() {
        if (!velPrefetch || !frames.length) return false;
        // The loop's motion must have SETTLED — resolved (_autoMotion set, a value or "insufficient") or given
        // up (no compute in flight; covers a VWP worker error, which clears _vwpInFlight without a result).
        // We only block while a compute is genuinely running. In the normal path the host kicks the VWP before
        // the backfill finishes, so by the time the duo completes below this is either resolved or in flight.
        if (stormMotion.auto && !_autoMotion && vwpInFlight()) return false;
        for (var i = 0; i < frames.length; i++) {
            // ⚠️ A STALE frame (mid tilt re-cut — see retile) carries the PREVIOUS elevation's build flags, so
            // it must not count as settled: treating it as built armed the dual-pol second wave the instant a
            // re-cut started, which then widened wantedProducts() to six products per frame and filled the
            // decode pool with dual-pol upgrades while the user was still waiting on the trio. The wave
            // re-arms on its own once the re-cut's duo is genuinely complete.
            var b = frames[i] && !frames[i].stale && frames[i].built;
            if (!(b && b.reflectivity && b.velocity)) return false; // duo not complete → backfill still running
        }
        return true;
    }
    // Arm the dual-pol second wave once (idempotent). Called after each frame build and after the motion
    // lands, so it fires as soon as whichever finished last settles the trio. A no-op until then, and a no-op
    // once armed. queueAllUpgrades only touches frames actually missing a dual-pol product (needsUpgrade),
    // so it's self-limiting.
    function maybeArmFullPrefetch() {
        if (fullPrefetch || !trioSettled()) return;
        var extra = dualPolIds();
        if (!extra.length) return;
        fullPrefetch = true;
        hostLog('fullPrefetch armed (' + extra.join('+') + ') across ' + frames.length + ' frame(s)');
        queueAllUpgrades('fullprefetch');
    }
    // ===== PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit) =====
    // Per-product "time to complete" for the console: elapsed ms from beginLoop (≈ the site click, so it
    // includes fetch) to when that product's geometry is built on EVERY loaded frame — i.e. when its
    // scrubber row finishes filling. Each frame-product is stamped once (its build time); a product's
    // completion is the MAX of those stamps, but only when every frame has it (else null = still filling /
    // never fills). Computed live, then FROZEN once the initial load is done (fullPrefetch armed AND the
    // upgrade queue fully drained) so a later live append can't rewrite the initial-fill number. A product
    // that never fills (e.g. SRV when the storm motion is insufficient) stays null → the console shows "—".
    // Order-independent (max over per-frame stamps), so it's correct for both fill directions. Reset per loop.
    let _loopStartT = 0;         // performance.now() captured at beginLoop
    let _builtAtMs = [];         // per frame index: { id: elapsed ms when that product FIRST built on it }
    let _prodFirstAtMs = {};     // product id -> elapsed ms when its FIRST frame built (time-to-first-paint), else null
    let _prodFullAtMs = {};      // product id -> elapsed ms when its LAST frame built (row full), else null
    let _timingFrozen = false;   // once the initial load settles, stop updating (ignore live appends)
    function updatePipelineTiming() {
        if (_timingFrozen || !Products) return;
        var ids = Object.keys(Products), n = frames.length;
        var elapsed = (typeof performance !== 'undefined' ? performance.now() : Date.now()) - _loopStartT;
        // Stamp each frame-product the first time we observe it built (this runs right after the build, so
        // elapsed ≈ its true build time). Order-independent: works for newest-first and oldest-first fills.
        for (var i = 0; i < n; i++) {
            var b = frames[i] && frames[i].built; if (!b) continue;
            var at = _builtAtMs[i] || (_builtAtMs[i] = {});
            for (var k = 0; k < ids.length; k++) { var id = ids[k]; if (b[id] && at[id] == null) at[id] = elapsed; }
        }
        // A product's time-to-complete = the LAST (max) of its per-frame stamps, but only once EVERY frame
        // has it (row full); otherwise null (still filling / never fills). Its time-to-FIRST-PAINT = the
        // FIRST (min) stamp — the earliest ANY frame got it built, i.e. loop-start → first rendered frame of
        // that product — known as soon as one frame has it (no "full" gate), null until then.
        for (var k2 = 0; k2 < ids.length; k2++) {
            var pid = ids[k2], mx = -1, mn = Infinity, full = n > 0;
            for (var j = 0; j < n; j++) {
                var atj = _builtAtMs[j];
                if (!atj || atj[pid] == null) { full = false; continue; }
                if (atj[pid] > mx) mx = atj[pid];
                if (atj[pid] < mn) mn = atj[pid];
            }
            _prodFullAtMs[pid] = full ? mx : null;
            _prodFirstAtMs[pid] = (mn === Infinity) ? null : mn;
        }
        // Freeze only when the pipeline is GENUINELY done. Three conditions beyond "queue drained":
        //  1. motionSettled — in auto mode the storm motion has a DEFINITIVE result (resolved OR insufficient),
        //     never still-pending. This is the key fix for the intermittent SRV "—": fullPrefetch can arm from
        //     just the DUO (trioSettled treats "no VWP in-flight yet" as settled), so the dual-pol wave can
        //     drain BEFORE the VWP compute even starts — a window where SRV isn't yet wanted and !vwpInFlight
        //     is (misleadingly) true. Requiring _autoMotion != null blocks the freeze until the motion lands,
        //     so SRV gets a chance to become wanted + fill and be timed. (_autoMotion is set for BOTH resolved
        //     and insufficient, so an insufficient result still lets the freeze proceed with SRV "—".)
        //  2. !vwpInFlight() — not mid-compute (covers a re-compute after an earlier resolution).
        //  3. srvOk — when SRV is wanted (motion resolved), it has actually finished filling; else a late SRV
        //     sweep would land after the freeze and its row would fill green with the time stuck at "—".
        var motionSettled = !stormMotion.auto || _autoMotion != null;
        var srvOk = wantedProducts().indexOf('srv') < 0 || _prodFullAtMs['srv'] != null;
        if (fullPrefetch && upgradeQueue.length === 0 && upgradeInFlightN === 0
            && motionSettled && !vwpInFlight() && srvOk) {
            _timingFrozen = true;
        }
    }
    // ===== END PIPELINE CONSOLE =====
    let pendingFrame = -1;  // a frame requested via showFrame before it finished decoding; the
                            // decode that satisfies it promotes it to currentFrame (so showFrame
                            // never pins currentFrame to an undecoded index and blanks the layer).
    // (uploadedFrame / uploadedProduct are PER VIEW — the GL buffers they describe belong to one
    //  canvas's context. See makeView.)
    let siteLat = 0, siteLon = 0;   // shared: every pane draws the same site
    let opacity = 0.85;             // shared: one radar opacity across the panes
    let loopToken = 0;      // bumped per loop so stale async frames are dropped
    // Range ring: a thin circle at the radar's REAL outer data extent (rangeMeters from the
    // decode), centred on the site — RadarScope-style. Its own GeoJSON line layer (survives
    // basemap switches via reAdd). currentRangeMeters = the radius currently drawn (0 = none).
    const RANGE_SRC = 'level2-range', RANGE_LAYER = 'level2-range';
    let currentRangeMeters = 0;
    // Sweep pulse: a one-shot rotating arm + trailing afterglow, drawn ON THE MAP (scaled to the real
    // coverage, RadarScope-style) — not a DOM decoration. The range ring is always shown; the arm only
    // appears to do ONE revolution when the host reports a genuinely-new frame (pulseSweep), then hides.
    // (Replaced the old continuous, phase-locked rotation.) The afterglow is a FILLED wedge (a fan of
    // abutting triangles — no gaps, so no visible spokes) that fades leading→tail, plus one crisp leading
    // arm line on top; per-feature opacity, rebuilt each animation frame while a pulse runs.
    const SWEEP_SRC = 'level2-sweep', SWEEP_FILL_LAYER = 'level2-sweep-fill', SWEEP_ARM_LAYER = 'level2-sweep-arm';
    const SWEEP_MS = 1300;        // duration of one revolution
    const SWEEP_FADE_MS = 400;    // brief fade-out of the trail once the revolution completes
    const SWEEP_TRAIL_DEG = 75;   // angular length of the trailing afterglow behind the leading arm
    const SWEEP_TRAIL_N = 64;     // wedge triangles across the trail — high so the taper reads smooth (no spokes)
    const SWEEP_PEAK = 0.42;      // peak fill opacity right behind the arm (the wedge is a translucent glow)
    const SWEEP_GAMMA = 1.6;      // trailing-fade shape (>1 = fades to nothing faster → a comet-tail falloff)
    let sweepAnimStart = 0, sweepRaf = 0;

    // Render-path diagnostics: render() runs every frame, so rate-limit its logging. We track
    // the running error/blank counts and only emit on the first occurrence + periodically, plus
    // a one-shot "recovered" line, so the debug log shows WHEN tiles blanked without flooding.
    let renderErrCount = 0, lastRenderErrAt = 0, lastRenderErr = '';
    let blankCount = 0, lastBlankAt = 0, lastBlankReason = '';
    let drewSinceIssue = false;
    function noteRenderIssue(reason, isError) {
        const now = Date.now();
        if (isError) { renderErrCount++; lastRenderErr = reason; }
        else { blankCount++; lastBlankReason = reason; }
        const last = isError ? lastRenderErrAt : lastBlankAt;
        if (last === 0 || now - last > 3000) {
            if (isError) lastRenderErrAt = now; else lastBlankAt = now;
            post({
                type: 'radarRender', kind: isError ? 'error' : 'blank', reason: reason,
                cf: currentFrame, errs: renderErrCount, blanks: blankCount,
            });
        }
        drewSinceIssue = false;
    }
    function noteRenderOk() {
        if (!drewSinceIssue && (renderErrCount > 0 || blankCount > 0)) {
            drewSinceIssue = true;
            post({ type: 'radarRender', kind: 'recovered', cf: currentFrame, errs: renderErrCount, blanks: blankCount });
        }
    }

    // WebGL context loss is permanent unless restored — a prime suspect for "tiles vanish and
    // never come back" under the heavy per-frame geometry. Log both edges once.
    // Per VIEW: each pane is its own canvas with its own context, so each needs its own listeners — and
    // the report then says WHICH pane lost it.
    function attachContextListeners(v) {
        if (!v || v.ctxBound || !v.map || !v.map.getCanvas) return;
        try {
            const c = v.map.getCanvas();
            c.addEventListener('webglcontextlost', function () { post({ type: 'radarRender', kind: 'ctxlost', pane: v.index, cf: currentFrame }); }, false);
            c.addEventListener('webglcontextrestored', function () { post({ type: 'radarRender', kind: 'ctxrestored', pane: v.index, cf: currentFrame }); }, false);
            v.ctxBound = true;
        } catch (e) { hostLog('ctx listener attach failed: ' + (e && e.message ? e.message : e)); }
    }

    // GL objects live on the VIEW (per context) — see makeView.

    function showError(msg) {
        document.body.insertAdjacentHTML('beforeend',
            '<div style="position:absolute;top:8px;left:8px;z-index:10;background:rgba(120,0,0,.85);' +
            'color:#fff;font:12px sans-serif;padding:6px 8px;border-radius:4px;max-width:60%">' +
            'Radar: ' + msg + '</div>');
    }
    function hostLog(msg) {
        try { console.log('[radar] ' + msg); } catch (e) { /* ignore */ }
        post({ type: 'radarLog', msg: String(msg) });
    }
    function post(obj) {
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify(obj));
            }
        } catch (e) { /* ignore */ }
    }

    // ---- Off-thread decode via a Web Worker POOL ----
    // A single worker decodes its message queue serially, so the backfill was decode-bound (one frame
    // at a time). A pool of N workers decodes N frames in parallel across cores; round-robin dispatch.
    // Results carry {token,index} and applyFrameResult runs serially on the main thread, so out-of-order
    // completions across workers are safe. Pool persists for the app lifetime (creating workers is
    // expensive); each loads radar-decode.js + the vendored decoder independently (~a few MB each).
    // Pool cap 4 (one more than UPGRADE_CONCURRENCY, so the current frame / a new load grabs a free worker
    // while upgrades run). Capped low on purpose: the decode is CPU-bound (dealias), so more workers than
    // physical cores just thrash — the win comes from a FASTER dealias, not more parallelism.
    const DECODE_POOL_SIZE = Math.max(1, Math.min(4,
        (typeof navigator !== 'undefined' && navigator.hardwareConcurrency) ? navigator.hardwareConcurrency - 1 : 3));
    let workerPool; // undefined = not tried, array = ready, null = Worker API unavailable
    let workerRR = 0;
    function getWorker() {
        if (workerPool === undefined) {
            try {
                workerPool = [];
                for (let i = 0; i < DECODE_POOL_SIZE; i++) {
                    const w = new Worker(new URL('radar-worker.js', SELF_SCRIPT).href);
                    w.onmessage = function (e) { const m = e.data; if (m && m.gridsOnly) applyGridResult(m); else applyFrameResult(m); };
                    w.onerror = function (e) { hostLog('worker error: ' + (e && e.message ? e.message : e)); };
                    workerPool.push(w);
                }
                hostLog('decode pool size=' + workerPool.length);
            } catch (e) {
                workerPool = null; // fall back to main-thread decode
                hostLog('worker unavailable; main-thread decode: ' + (e && e.message ? e.message : e));
            }
        }
        if (!workerPool || !workerPool.length) return null;
        const w = workerPool[workerRR % workerPool.length]; // round-robin next worker
        workerRR++;
        return w;
    }

    // Pre-warm the decode + VWP workers so the FIRST site click doesn't pay their cold start. Creating a
    // worker is expensive and each then imports the vendored decoder (~a few MB, eagerly on startup — see
    // radar-worker.js); doing that ahead of time moves it off the first-paint critical path (the diagnostics
    // showed the pool being built ~1.4 s INTO the first load). The host calls this at map-ready, before any
    // loop. Idempotent (getWorker/getVwpWorker create their pools once) and best-effort.
    window.prewarmRadarWorkers = function () {
        try { getWorker(); getVwpWorker(); } catch (e) { hostLog('prewarm failed: ' + (e && e.message ? e.message : e)); }
    };

    // Wraps a decode result (r2 from decodeAndBuild / decodeDowFrame — already the keyed
    // { moments, grids, built, gridsBuilt, ... } shape) into what applyFrameResult consumes, just
    // stamping this load's token/index/url. Used by the main-thread decode fallback and the DOW path.
    // NOTE: the Worker (radar-worker.js) builds the message itself because it must pass the typed-array
    // buffers as postMessage transferables — a worker-only concern it can't reach this IIFE-private helper for.
    function frameResultFrom(r2, token, index, url) {
        return Object.assign({}, r2, { token: token, index: index, url: url });
    }

    // Accumulate a decode's products onto whatever this frame already had (docs/radar-loop-flow.md
    // Rule 6 for products). A decode only builds the products it was asked for (reflectivity + the
    // buildProducts ids); every OTHER product comes back null/false. So on an additive upgrade —
    // velocity prefetch, a product switch, the dual-pol second wave — we keep the previously-built
    // geometry and overlay only what THIS decode actually built, so switching to a dual-pol product
    // and back never drops the trio (the old REPLACE-not-MERGE gap #2).
    //
    // PURE: reads only its two arguments, mutates neither, returns a NEW result object. That is why it
    // sits out here rather than inline in applyFrameResult, which is otherwise a fixed-order sequence of
    // side effects — this is the one genuinely algorithmic step in that sequence, and keeping it
    // separable is what makes Rule 6 checkable on its own. (The returned object SHARES the geometry /
    // typed arrays of both inputs by reference; that is safe because decoded geometry is read-only.)
    //
    // The CALLER decides mergeability (same index AND same volume url) — a fresh frame never reaches
    // here, because it has nothing to keep and the plain decode result already is the answer.
    function mergeFrameResult(prev, res) {
        var mo = res.moments || {};
        var mMo = {}, mBuilt = {}, mGr = {}, mGridsExtra = Object.assign({}, prev.gridsExtra);
        var rGr = res.grids || {}, pMo = prev.moments || {}, pBuilt = prev.built || {}, pGr = prev.grids || {};
        // res.built carries EVERY product id (the decode loop sets true/false for all), so iterating it
        // covers the whole registry. Authoritative = whatever this decode built; keep prev's otherwise.
        var bkeys = Object.keys(res.built || {});
        for (var bi = 0; bi < bkeys.length; bi++) {
            var pid = bkeys[bi];
            if (res.built[pid]) {                          // built now → take it (geometry may be null = no data)
                mMo[pid] = mo[pid]; mBuilt[pid] = true;
                if (res.gridsBuilt) { mGr[pid] = rGr[pid]; mGridsExtra[pid] = true; }
                else if (pid in pGr) mGr[pid] = pGr[pid];  // this decode skipped grids → keep prior grid
            } else if (pBuilt[pid]) {                      // skipped now, but we already had it → keep it
                mMo[pid] = pMo[pid]; mBuilt[pid] = true;
                if (pid in pGr) mGr[pid] = pGr[pid];
            } else {                                       // never built → carry the null placeholder
                mMo[pid] = (pid in mo) ? mo[pid] : (pid in pMo ? pMo[pid] : null); mBuilt[pid] = false;
            }
        }
        // The MERGED view = this decode's fresh metadata + the accumulated geometry/grids, so both
        // frames[] and the decoded-cache entry (cachePut) carry every product built so far.
        return Object.assign({}, res, {
            moments: mMo, built: mBuilt, grids: mGr,
            gridsBuilt: !!prev.gridsBuilt || !!res.gridsBuilt, gridsExtra: mGridsExtra,
        });
    }

    // One decoded frame has arrived (worker, main-thread fallback, or a synchronous cache hit).
    // ORDER IS PART OF THE CONTRACT — the phases below run exactly as written:
    //   guard → seed → MERGE → commit frames[] → cache + free upgrade slot → re-queue → notify host
    //   → reconcile what's on screen → fire downstream triggers.
    // The couplings that are easy to break by reordering: res.empty must be computed from the MERGED
    // moments (cachePut must not cache a no-geometry frame); upgradeDone must precede queueUpgrade
    // (it frees the slot the pump then takes); setRangeRing must follow the layer (re)add, or the ring
    // draws under the fill. Each is restated at its line — keep it that way.
    function applyFrameResult(res) {
        if (!res || res.token !== loopToken) return; // stale (loop changed)
        if (res.error) {
            upgradeDone(res.index); // free the upgrade slot (if this was one); don't retry a failing frame
            hostLog('frame ' + res.index + ' decode failed: ' + res.error);
            post({ type: 'radarFrameReady', index: res.index, hasData: false });
            return;
        }
        // Temporal dealias seed: a velocity decode returns its cut's VAD wind profile. Keep the NEWEST loop
        // frame's profile (index ~ volume time) as the first guess for subsequent decodes — backfill runs
        // newest-first so this settles on the freshest cut; winds vary slowly, so any recent one is fine.
        if (res.seedProfile && res.seedProfile.length && res.index >= _seedProfileIdx) {
            _loopSeedProfile = res.seedProfile;
            _seedProfileIdx = res.index;
        }
        var mo = res.moments || {};
        // MERGE, not replace (docs/radar-loop-flow.md Rule 6 for products) — see mergeFrameResult above.
        // Mergeable = the SAME volume already sits at this index (url match), i.e. this decode is an
        // additive upgrade. A fresh frame (initial load, or a NEW volume here after a remap/live append)
        // has nothing to keep, so it reduces to the plain decode result. NB prev/mergeable/prevVelNyq are
        // read again further down (the frames[] commit), so they stay in this scope.
        var prev = frames[res.index];
        var mergeable = prev && prev.url && res.url && prev.url === res.url;
        var prevVelNyq = mergeable ? (prev.velNyq || 0) : 0;
        if (mergeable) {
            res = mergeFrameResult(prev, res);
            mo = res.moments;
        }
        // Compute empty authoritatively from the (merged) moments map, so cachePut below skips caching a
        // no-geometry frame regardless of which path decoded it.
        res.empty = !Object.keys(mo).some(function (id) { return mo[id]; });
        frames[res.index] = {
            // Geometry + inspector grids keyed by product id (radar-products.js) — see render / lookupValue.
            moments: mo,                    // { id: { positions, colors, count } | null }
            grids: res.grids || {},         // { id: value-grid | null } (present only when Inspect was on)
            built: res.built || {},         // { id: bool } — whether that product's build ran (lazy bookkeeping)
            gridsExtra: res.gridsExtra || (mergeable ? prev.gridsExtra : undefined), // per-product grid bookkeeping (merge/grids-only)
            velNyq: res.velNyq || prevVelNyq || 0, // Nyquist (m/s) — lets the inspector show the raw fold of a dealiased gate
            // url = this frame's stable volume URL (so a product/inspect switch can re-decode it),
            // gridsBuilt = whether the inspector value grids were built (skipped by default; built on
            // demand — see setProduct / setInspect).
            url: res.url || null, gridsBuilt: !!res.gridsBuilt,
        };
        // Post the per-frame decode metrics as a STRUCTURED message (the C# RadarDiagnostics
        // service records them, evaluates the suspect heuristics, and quarantines a bad frame's
        // .V06). The metrics are already computed by the decoder; we just forward them losslessly.
        // Retain the decoded result for instant reuse on a site revisit / replay toggle (keyed by its
        // stable volume URL). Shares the typed arrays with frames[res.index] — safe, geometry is read-only.
        cachePut(res.url, res);
        upgradeDone(res.index); // if this arrival was a queued upgrade, free its slot + pump the next
        // Reconcile this just-arrived frame with the ACTIVE product/inspect state. It may have been
        // decoded WITHOUT the geometry the current view needs — refl-only while the user is on Velocity,
        // or without inspector grids while Inspect is on — because the product/Inspect was switched
        // mid-load, AFTER this frame's decode was posted but BEFORE it arrived, so the switch's queue
        // sweep couldn't see it yet (it wasn't in frames[] then). That race left a scattered set of
        // frames stuck refl-only on a slow past-event load (the "switch to Velocity shows nothing until I
        // reload" bug). Queue it for a bounded upgrade; needsUpgrade returns false once built, so there's
        // no decode loop and no cost when the product was already active at decode time.
        queueUpgrade(res.index, 'arrive');
        var reflCount = (mo.reflectivity && mo.reflectivity.count) || 0;
        var velCount = (mo.velocity && mo.velocity.count) || 0;
        post({
            type: 'radarFrame', index: res.index, empty: !!res.empty, cached: !!res.cached,
            tris: reflCount, velTris: velCount,
            decodeMs: res.decodeMs, buildMs: res.buildMs, bytes: res.bytes,
            elevList: res.elevList, velElev: res.velElev, velNyq: res.velNyq,
            velNyqSrc: res.velNyqSrc, velNyqRad: res.velNyqRad, velNyqVol: res.velNyqVol,
            reflStats: res.reflStats, velStats: res.velStats, dealias: res.dealias,
            decoded: frames.filter(Boolean).length, total: frames.length, cf: currentFrame,
        });
        try { console.log('[radar] decoded idx=' + res.index + (res.empty ? ' EMPTY' : ' tris=' + reflCount + ' velTris=' + velCount)); } catch (e) { /* ignore */ }

        // Decide what to show now that this frame is available. Crucially, ANY of these paths
        // re-adds the layer if it's missing (e.g. after a reload removed it) — so the radar can
        // never get stuck blank with a decoded current frame.
        if (views.length) {
            if (res.index === pendingFrame) {
                // A showFrame() requested this index before it had decoded — promote it now.
                pendingFrame = -1;
                currentFrame = res.index;
                invalidateUploads();
                showCurrentAll('pending');
            } else if (currentFrame < 0) {
                // Nothing shown yet — adopt the first frame to arrive (host pushes newest first).
                currentFrame = res.index;
                invalidateUploads();
                showCurrentAll('first');
            } else if (res.index === currentFrame) {
                // The on-screen frame was re-decoded (live in-place update, or another pane's product
                // arriving) — repaint / re-add. Every pane renders this index, so every pane is stale.
                invalidateUploads();
                showCurrentAll('re-add');
            }
            // Draw the real outer-extent range ring (RadarScope-style) from this frame's decoded
            // range — AFTER the radar layer (re)add above, so the ring sits on top of the fill.
            if (res.rangeMeters > 0) setRangeRing(res.rangeMeters);
        }
        post({ type: 'radarFrameReady', index: res.index, hasData: !res.empty });
        // The AUTO (VAD-derived) storm motion is NO LONGER computed per frame — a single tilt is too shallow
        // for a correct VWP. It's computed once per loop from the newest volume's bottom velocity tilts
        // (computeStormMotionForVolume, triggered by the host) and surfaced via publishAutoMotion.
        postBuildProgress(); // this frame's build state may have changed the ready count
        maybeArmFullPrefetch(); // this frame may have been the last of the DUO → arm the dual-pol second wave
        updatePipelineTiming(); // PIPELINE CONSOLE: track per-product time-to-fill (remove with the feature)
    }

    // ---- GL custom layer ----
    function compile(glc, type, src) {
        const s = glc.createShader(type);
        glc.shaderSource(s, src);
        glc.compileShader(s);
        if (!glc.getShaderParameter(s, glc.COMPILE_STATUS)) {
            throw new Error(glc.getShaderInfoLog(s) || 'shader compile failed');
        }
        return s;
    }

    // One custom layer per VIEW. The layer id is the same in every pane — layer ids are scoped to a map
    // — but the GL program and buffers it creates belong to that pane's context and can't be handed to
    // another, so they live on the view. Capturing `v` here is the whole mechanism.
    function makeCustomLayer(v) {
        return {
            id: LAYER_ID,
            type: 'custom',
            onAdd: function (map, glc) {
                const vs = compile(glc, glc.VERTEX_SHADER,
                    'uniform mat4 u_matrix;' +
                    'attribute vec2 a_pos;' +
                    'attribute vec4 a_color;' +
                    'varying vec4 v_color;' +
                    'void main(){ v_color=a_color; gl_Position=u_matrix*vec4(a_pos,0.0,1.0); }');
                const fs = compile(glc, glc.FRAGMENT_SHADER,
                    'precision mediump float;' +
                    'uniform float u_opacity;' +
                    'varying vec4 v_color;' +
                    'void main(){ gl_FragColor=vec4(v_color.rgb, v_color.a*u_opacity); }');
                v.program = glc.createProgram();
                glc.attachShader(v.program, vs);
                glc.attachShader(v.program, fs);
                glc.linkProgram(v.program);
                if (!glc.getProgramParameter(v.program, glc.LINK_STATUS)) {
                    throw new Error(glc.getProgramInfoLog(v.program) || 'program link failed');
                }
                v.aPos = glc.getAttribLocation(v.program, 'a_pos');
                v.aColor = glc.getAttribLocation(v.program, 'a_color');
                v.uMatrix = glc.getUniformLocation(v.program, 'u_matrix');
                v.uOpacity = glc.getUniformLocation(v.program, 'u_opacity');
                v.posBuf = glc.createBuffer();
                v.colorBuf = glc.createBuffer();
                v.uploadedFrame = -1; // force a re-upload on first render
            },
            render: function (glc, args) {
                if (!v.program || currentFrame < 0) return;
                const f = frames[currentFrame];
                if (!f) { noteRenderIssue('no frame at cf=' + currentFrame, false); return; }
                // Pick the geometry for THIS PANE's product — one keyed lookup (radar-products.js).
                // Every pane renders the same frame index; this lookup is the only thing that differs.
                const product = v.product;
                let pos, col, cnt;
                var effProduct = product;
                let g = f.moments && f.moments[product];
                // SRV stand-in: whenever this frame's SRV geometry isn't built yet, render base VELOCITY in its
                // place — SRV ≈ velocity for weak motion — so the field never goes blank. Two windows need this:
                // (1) while the auto storm motion is still computing (we don't build SRV at the wrong motion),
                // and (2) the ~5 s AFTER the motion lands, when dropAllSrv has requeued every frame and the
                // current frame's SRV is mid-redecode. The outer `!g` already means "SRV not built for this
                // frame", so we do NOT also gate on srvMotionReady() — gating on it re-blanked the displayed
                // frame for that second window (the momentary disappear-then-reappear on switching to SRV).
                // Latch on effProduct so the real SRV re-uploads when it arrives (uploadedProduct flips
                // velocity→srv).
                if (!g && product === 'srv' && f.moments && f.moments.velocity) {
                    g = f.moments.velocity; effProduct = 'velocity';
                }
                if (g) { pos = g.positions; col = g.colors; cnt = g.count; }
                try {
                    // Re-upload when the frame OR the (effective) product changed. Only latch the buffers as
                    // current once an upload actually happened: a frame that lacks this product's
                    // geometry (e.g. an archive frame in Velocity mode, or a live volume whose Doppler
                    // companion hadn't finished scanning) must NOT mark uploadedFrame, or the buffers
                    // stay stale-but-marked and a later frame that DOES carry the geometry is skipped.
                    if ((v.uploadedFrame !== currentFrame || v.uploadedProduct !== effProduct) && pos && col) {
                        glc.bindBuffer(glc.ARRAY_BUFFER, v.posBuf);
                        glc.bufferData(glc.ARRAY_BUFFER, pos, glc.STATIC_DRAW);
                        glc.bindBuffer(glc.ARRAY_BUFFER, v.colorBuf);
                        glc.bufferData(glc.ARRAY_BUFFER, col, glc.STATIC_DRAW);
                        v.uploadedFrame = currentFrame;
                        v.uploadedProduct = effProduct;
                    }
                    if (!cnt) return; // this product has nothing to draw on this frame

                    const matrix = (args && args.defaultProjectionData && args.defaultProjectionData.mainMatrix)
                        || (args && args.modelViewProjectionMatrix) || args;
                    glc.useProgram(v.program);
                    glc.uniformMatrix4fv(v.uMatrix, false, matrix);
                    glc.uniform1f(v.uOpacity, opacity);
                    glc.bindBuffer(glc.ARRAY_BUFFER, v.posBuf);
                    glc.enableVertexAttribArray(v.aPos);
                    glc.vertexAttribPointer(v.aPos, 2, glc.FLOAT, false, 0, 0);
                    glc.bindBuffer(glc.ARRAY_BUFFER, v.colorBuf);
                    glc.enableVertexAttribArray(v.aColor);
                    glc.vertexAttribPointer(v.aColor, 4, glc.UNSIGNED_BYTE, true, 0, 0);
                    glc.enable(glc.BLEND);
                    glc.blendFunc(glc.SRC_ALPHA, glc.ONE_MINUS_SRC_ALPHA);
                    glc.drawArrays(glc.TRIANGLES, 0, cnt);
                    glc.disableVertexAttribArray(v.aPos);
                    glc.disableVertexAttribArray(v.aColor);
                    glc.bindBuffer(glc.ARRAY_BUFFER, null);
                    noteRenderOk();
                } catch (e) {
                    noteRenderIssue((e && e.message ? e.message : String(e)), true);
                }
            },
            onRemove: function (map, glc) {
                if (v.posBuf) glc.deleteBuffer(v.posBuf);
                if (v.colorBuf) glc.deleteBuffer(v.colorBuf);
                if (v.program) glc.deleteProgram(v.program);
                v.posBuf = v.colorBuf = v.program = null;
                v.uploadedFrame = -1;
            },
        };
    }

    // Beneath the watch boxes, the warning polygons, the boundary lines (so borders draw over radar),
    // the outlook, and the labels. Watches are the LOWEST of those overlays (warnings sit above them),
    // so target watches FIRST, then warnings — otherwise whichever was added last (a site click vs the
    // periodic watch/warning refresh) would land on top of radar.
    function beforeId(map) {
        if (map.getLayer('spc-watch-fill')) return 'spc-watch-fill';
        if (map.getLayer('nws-warning-fill')) return 'nws-warning-fill';
        if (map.getLayer('boundaries_country')) return 'boundaries_country';
        if (map.getLayer('boundaries')) return 'boundaries';
        if (map.getLayer('spc-outlook-fill')) return 'spc-outlook-fill';
        const layers = (map.getStyle() && map.getStyle().layers) || [];
        const sym = layers.find(function (l) { return l.type === 'symbol'; });
        return sym ? sym.id : undefined;
    }
    function removeLayer(v) {
        if (v.map.getLayer(LAYER_ID)) { v.map.removeLayer(LAYER_ID); hostLog('layer removed pane=' + v.index); }
    }
    function removeLayerAll() { forEachView(removeLayer); }
    function addLayer(v) {
        if (currentFrame < 0) return;
        removeLayer(v);
        v.uploadedFrame = -1;
        v.map.addLayer(makeCustomLayer(v), beforeId(v.map));
        hostLog('layer added pane=' + v.index + ' before=' + beforeId(v.map) + ' cf=' + currentFrame);
    }
    // Ensures the current frame is on screen IN EVERY PANE: repaint where the layer is up, (re)add it
    // where it isn't. This is the single place that guarantees a decoded current frame is never left
    // blank — and in multi-pane that guarantee has to hold pane by pane, since a pane created mid-loop
    // starts with no layer at all.
    function showCurrent(v, reason) {
        if (currentFrame < 0 || !frames[currentFrame]) return;
        if (v.map.getLayer(LAYER_ID)) {
            v.map.triggerRepaint();
        } else {
            hostLog('showCurrent(' + reason + ') pane=' + v.index + ' idx=' + currentFrame + ' -> re-add layer');
            addLayer(v);
        }
    }
    function showCurrentAll(reason) { forEachView(function (v) { showCurrent(v, reason); }); }
    function repaintAll() {
        forEachView(function (v) { if (v.map.getLayer(LAYER_ID)) v.map.triggerRepaint(); });
    }

    // ---- Range ring (real outer data extent) ----
    // A 128-point circle at currentRangeMeters around the site, using the same equirectangular
    // metres-per-degree approximation as the gate geometry (radar-decode buildGates) so the ring
    // lines up exactly with the data's edge.
    function ringGeoJSON() {
        const N = 128, R = currentRangeMeters;
        const coords = [];
        for (let k = 0; k <= N; k++) {
            coords.push(Geo.siteToLngLat(siteLat, siteLon, R, (k / N) * 2 * Math.PI));
        }
        return { type: 'Feature', geometry: { type: 'LineString', coordinates: coords } };
    }
    // The RADIUS is shared (one site, one range); the SOURCE + LAYER are per map, so each pane draws
    // its own ring from the same number.
    function addRangeRing(v) {
        const map = v && v.map;
        if (!map || !(currentRangeMeters > 0) || !Geo) return; // geo.js not loaded yet -> redraws on next setRangeRing
        if (map.getSource(RANGE_SRC)) map.getSource(RANGE_SRC).setData(ringGeoJSON());
        else map.addSource(RANGE_SRC, { type: 'geojson', data: ringGeoJSON() });
        if (!map.getLayer(RANGE_LAYER)) {
            map.addLayer({
                id: RANGE_LAYER, type: 'line', source: RANGE_SRC,
                paint: { 'line-color': '#9fe0ff', 'line-width': 1.3, 'line-opacity': 0.55, 'line-blur': 0.3 },
            }, beforeId(map));
        }
        v.rangeAdded = true;
    }
    function removeRangeRing(v) {
        const map = v && v.map;
        if (map && map.getLayer(RANGE_LAYER)) map.removeLayer(RANGE_LAYER);
        if (map && map.getSource(RANGE_SRC)) map.removeSource(RANGE_SRC);
        if (v) v.rangeAdded = false;
    }
    function removeRangeRingAll() { forEachView(removeRangeRing); }
    // Draw/update the ring for the freshly-decoded range. Per-frame ranges are ~identical, so
    // only rebuild when it actually changes (or a layer is missing, e.g. after a re-add). The sweep
    // pulse owns its own layer (added on demand in pulseSweep), so nothing to ensure here.
    function setRangeRing(rangeMeters) {
        if (!(rangeMeters > 0)) return;
        const same = Math.abs(rangeMeters - currentRangeMeters) < 500;
        // Unchanged radius AND every pane already has its ring up -> nothing to do. A pane added since
        // the last frame fails the second test, which is what gets it a ring without a new decode.
        let allUp = views.length > 0;
        forEachView(function (v) { if (!v.map.getLayer(RANGE_LAYER)) allUp = false; });
        if (same && allUp) {
            return;
        }
        currentRangeMeters = rangeMeters;
        forEachView(addRangeRing);
    }

    // ---- Sweep pulse ----
    // The trailing afterglow as a FILLED WEDGE: a fan of SWEEP_TRAIL_N abutting triangles from the site out
    // to the range-ring edge, spanning SWEEP_TRAIL_DEG BEHIND the leading bearing (0 = due north). Because
    // the triangles TILE (share edges, no gaps), it reads as a continuous glow that fades leading→tail —
    // unlike the old radial spokes, which diverged with range and looked ragged. Each triangle carries an
    // `o` fill-opacity; a separate LineString (the crisp leading arm) is appended and rendered by the line
    // layer. `fade` scales everything for the end fade-out. Same metres-per-degree projection as the ring.
    function sweepWedgeGeoJSON(leadRad, fade) {
        const feats = [];
        const center = [siteLon, siteLat];
        const step = (SWEEP_TRAIL_DEG * Math.PI / 180) / SWEEP_TRAIL_N;
        const tipAt = function (a) { return Geo.siteToLngLat(siteLat, siteLon, currentRangeMeters, a); };
        let prevTip = tipAt(leadRad);
        for (let i = 1; i <= SWEEP_TRAIL_N; i++) {
            const ang = leadRad - i * step;
            if (ang < 0) break; // don't draw behind the sweep's start (north) on the first revolution
            const tip = tipAt(ang);
            // Opacity for the slice between the previous (brighter) and this (dimmer) edge — use its midpoint
            // fraction down the trail, with a gamma falloff so the tail fades to nothing like phosphor decay.
            const o = fade * SWEEP_PEAK * Math.pow(1 - (i - 0.5) / SWEEP_TRAIL_N, SWEEP_GAMMA);
            if (o > 0.004) {
                feats.push({ type: 'Feature', properties: { o: o },
                    geometry: { type: 'Polygon', coordinates: [[center, prevTip, tip, center]] } });
            }
            prevTip = tip;
        }
        // Crisp bright leading arm (a LineString → the line layer draws it; the fill layer ignores it).
        feats.push({ type: 'Feature', properties: { o: fade },
            geometry: { type: 'LineString', coordinates: [center, tipAt(leadRad)] } });
        return { type: 'FeatureCollection', features: feats };
    }
    // One animation drives every pane's sweep: a sweep in only one pane of four would read as broken.
    // The per-frame cost is a setData of ~64 small polygons per pane, which is nothing next to the
    // basemap they are drawn over.
    function ensureSweepLayer(v) {
        const map = v && v.map;
        if (!map || !(currentRangeMeters > 0) || !Geo) return; // geo.js not loaded yet
        if (!map.getSource(SWEEP_SRC)) map.addSource(SWEEP_SRC, { type: 'geojson', data: { type: 'FeatureCollection', features: [] } });
        const before = beforeId(map);
        // Fill = the fading wedge (renders only the Polygon features); line = the crisp arm (only the
        // LineString). Both on top of the ring + radar fill. Per-feature `o` opacity for the animated fade.
        if (!map.getLayer(SWEEP_FILL_LAYER)) {
            map.addLayer({
                id: SWEEP_FILL_LAYER, type: 'fill', source: SWEEP_SRC,
                // ⚠️ antialias MUST be off: it outlines every polygon, so the 64 abutting triangles' shared
                // radial edges would each draw a 1px seam — reading as a fan of faint lines, exactly the
                // raggedness we're removing. Off, the triangles blend seamlessly (opacity steps are ~1%).
                paint: { 'fill-color': '#ffe6a0', 'fill-opacity': ['get', 'o'], 'fill-antialias': false },
            }, before);
        }
        if (!map.getLayer(SWEEP_ARM_LAYER)) {
            map.addLayer({
                id: SWEEP_ARM_LAYER, type: 'line', source: SWEEP_SRC,
                paint: { 'line-color': '#fff4c8', 'line-width': 2, 'line-blur': 1.2, 'line-opacity': ['get', 'o'] },
            }, before);
        }
    }
    function clearSweepData() {
        forEachView(function (v) {
            const src = v.map.getSource(SWEEP_SRC);
            if (src) src.setData({ type: 'FeatureCollection', features: [] });
        });
    }
    // Stop any in-flight pulse and drop its layers in every pane (site change / clear / DOW / turn-off).
    function stopSweep() {
        if (sweepRaf) { cancelAnimationFrame(sweepRaf); sweepRaf = 0; }
        forEachView(function (v) {
            const map = v.map;
            if (map.getLayer(SWEEP_ARM_LAYER)) map.removeLayer(SWEEP_ARM_LAYER);
            if (map.getLayer(SWEEP_FILL_LAYER)) map.removeLayer(SWEEP_FILL_LAYER);
            if (map.getSource(SWEEP_SRC)) map.removeSource(SWEEP_SRC);
        });
    }
    function sweepPulseFrame() {
        sweepRaf = 0;
        if (!views.length || !(currentRangeMeters > 0) || !Geo) return; // nothing to draw (e.g. layer dropped)
        const el = performance.now() - sweepAnimStart;
        if (el >= SWEEP_MS + SWEEP_FADE_MS) { clearSweepData(); return; }            // revolution done → hide arm
        let lead, fade;
        if (el < SWEEP_MS) { lead = (el / SWEEP_MS) * 2 * Math.PI; fade = 1; }       // sweeping 0→360°
        else { lead = 2 * Math.PI; fade = 1 - (el - SWEEP_MS) / SWEEP_FADE_MS; }     // hold at north, fade the trail out
        // Build the wedge ONCE and hand the same GeoJSON to every pane — the geometry is geographic, so
        // it is identical in all of them.
        const data = sweepWedgeGeoJSON(lead, fade);
        forEachView(function (v) {
            const src = v.map.getSource(SWEEP_SRC);
            if (src) src.setData(data);
        });
        sweepRaf = requestAnimationFrame(sweepPulseFrame);
    }
    // Fire ONE sweep pulse (host calls this when a genuinely-new frame lands). Restarts if one is
    // already mid-flight. No-op until a frame has decoded (no radius to sweep yet).
    function startSweepPulse() {
        if (!(currentRangeMeters > 0) || !Geo) return;
        forEachView(ensureSweepLayer);
        sweepAnimStart = performance.now();
        if (!sweepRaf) sweepRaf = requestAnimationFrame(sweepPulseFrame);
    }

    // Decodes one volume into frames[index] (off-thread, with a main-thread fallback).
    // Structured decode-cause trace (rides the diagnostics JSONL as a radarLog line, so it needs no C# change).
    // Per decode it records WHY it ran (reason: load / up:<trigger> / …), which PATH was taken (cache-hit /
    // grid-only / decode), the products REQUESTED, and — when a full decode ran despite a cache entry — WHY the
    // cache couldn't serve it (miss). Grep `why=`/`miss=` to trace an unexpected re-load to its cause without
    // reproducing it. `dt=` timestamps let re-loads be correlated with scrub/switch actions.
    function decodeTrace(index, reason, path, wantedIds, miss) {
        hostLog('decode idx=' + index + ' why=' + (reason || '?') + ' path=' + path
            + ' prod=' + viewProducts().join('|') + ' want=' + (wantedIds && wantedIds.length ? wantedIds.join('+') : '-')
            + (miss ? ' miss=' + miss : '') + ' dt=' + Math.round(performance.now()));
    }

    function decodeFrame(url, index, reason) {
        const myToken = loopToken;
        // Velocity is the only product that must dealias (expensive), so build it when it's the active
        // product OR while speculatively prefetching it (velPrefetch — armed by the host once the
        // reflectivity loop has rendered, so a later switch to Velocity is instant/near-instant). On
        // reflectivity/CC with prefetch off we skip it and re-decode on demand (setProduct).
        const wantedIds = wantedProducts(); // extra products to build this decode (active + velocity prefetch)
        const wantGrids = inspecting; // inspector value grids are only needed while Inspect is on
        // Grids-only fast path (turning Inspect on): the frame already has the active product's GEOMETRY
        // (and nothing lazy is pending for it) and only its inspector VALUE GRID is missing — so build just
        // that one grid and merge it, instead of a full re-decode of every product's geometry + a redundant
        // velocity dealias. This is what makes Inspect show values fast; the loop's other frames fill in the
        // same way through the upgrade queue. Only for the current loaded frames (not the initial decode).
        var f0 = frames[index];
        // ⚠️ Never take the grids-only path for a STALE frame (mid tilt re-cut): its built[] flags describe the
        // PREVIOUS elevation, so this would build a value grid from the new cut, merge it into the old
        // geometry (applyGridResult leaves geometry alone), and leave the frame stale forever — with Inspect
        // on, a tilt switch would wedge every frame at the old elevation. It needs the full decode below.
        var gridProduct = wantGrids ? missingGridProduct(f0) : null;
        if (gridProduct && f0 && !f0.stale && f0.built && f0.built[gridProduct] && !needsBuild(f0)) {
            decodeTrace(index, reason, 'grid-only:' + gridProduct, wantedIds, null);
            decodeGridForFrame(url, index, gridProduct);
            return;
        }
        // Build only what THIS frame is actually MISSING (Rule 6): an additive upgrade on a frame that
        // already has the trio should build just the dual-pol product(s), not redo the (expensive) velocity
        // dealias for products it already has. A fresh frame (no prior build for this url) builds the full
        // wanted set. applyFrameResult MERGES the result, so the kept products are left untouched.
        var buildIds = (f0 && f0.url === url && f0.built)
            ? wantedIds.filter(function (id) { return !f0.built[id]; })
            : wantedIds;
        // Cache hit → reuse the decoded geometry synchronously (no fetch, no worker). This is what
        // makes a site revisit / replay toggle instant. Reject a hit that lacks a piece we need now —
        // the lazy product's geometry (a refl-only decode from a prior view) or the ACTIVE product's
        // inspector grid (decoded with Inspect off) — so we fall through and build it this time. Clone
        // with THIS load's token+index; arrays shared.
        const hit = cacheGet(url);
        if (hit && wantedBuiltIn(hit) && (!wantGrids || activeGridReady(hit))) {
            decodeTrace(index, reason, 'cache-hit', wantedIds, null);
            applyFrameResult(Object.assign({}, hit, { token: myToken, index: index, cached: true }));
            return;
        }
        // A full decode is about to run. If there WAS a cache entry, say why it couldn't serve us — the key
        // signal for "why did a frame we already loaded re-decode?" (unbuilt product vs missing inspector grid).
        var miss = !hit ? 'no-entry'
            : !wantedBuiltIn(hit) ? ('unbuilt:' + wantedIds.filter(function (id) { return !(hit.built && hit.built[id]); }).join(','))
                : ('no-grid:' + (missingGridProduct(hit) || '?'));
        decodeTrace(index, reason, 'decode', buildIds, miss);
        const w = getWorker();
        if (w) {
            // The WORKER fetches the .V06 itself (same-origin radarlevel2 host), so the ~7 MB body read stays
            // OFF the map's render thread — a backfill of N frames otherwise does N such reads on the main
            // thread, hitching pan/zoom. A loop that changed while the fetch was in flight is still dropped by
            // token in applyFrameResult; a fetch/decode failure comes back as {token,index,url,error}, which
            // applyFrameResult already turns into upgradeDone + radarFrameReady(hasData:false) — same as before.
            w.postMessage({ url: url, siteLat: siteLat, siteLon: siteLon, minDbz: MIN_DBZ, token: myToken, index: index, buildProducts: buildIds, buildGrids: wantGrids, stormMotion: resolveStormMotion(), seedProfile: _loopSeedProfile });
        } else {
            // No Worker API — fetch + decode on the main thread (unchanged fallback path).
            fetch(url, { cache: 'no-store' }).then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.arrayBuffer();
            }).then(function (ab) {
                if (myToken !== loopToken) return;
                return import('./radar-decode.js').then(function (m) {
                    return m.decodeAndBuild(ab, siteLat, siteLon, MIN_DBZ, buildIds, wantGrids, resolveStormMotion(), _loopSeedProfile);
                }).then(function (r2) {
                    applyFrameResult(frameResultFrom(r2, myToken, index, url));
                });
            }).catch(function (err) {
                upgradeDone(index); // free the upgrade slot so the pump doesn't stall
                hostLog('frame ' + index + ' decode failed: ' + (err && err.message ? err.message : err));
                post({ type: 'radarFrameReady', index: index, hasData: false });
            });
        }
    }

    // Builds ONLY the active product's inspector value grid for a frame and merges it in (geometry left
    // alone) — the fast path behind turning Inspect on. Off-thread with a main-thread fallback, same as
    // decodeFrame. Runs under the upgrade queue's slot accounting (upgradeDone frees the slot).
    function decodeGridForFrame(url, index, prod) {
        const myToken = loopToken;
        const w = getWorker();
        if (w) {
            // As with decodeFrame: the worker fetches the .V06 so the body read stays off the render thread.
            // A stale loop / error is handled by applyGridResult (it frees the upgrade slot on both).
            w.postMessage({ gridOnly: true, url: url, siteLat: siteLat, siteLon: siteLon, minDbz: MIN_DBZ, token: myToken, index: index, product: prod, stormMotion: resolveStormMotion(), seedProfile: _loopSeedProfile });
        } else {
            fetch(url, { cache: 'no-store' }).then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.arrayBuffer();
            }).then(function (ab) {
                if (myToken !== loopToken) { upgradeDone(index); return; }
                return import('./radar-decode.js').then(function (m) {
                    return m.decodeGridOnly(ab, siteLat, siteLon, MIN_DBZ, prod, resolveStormMotion(), _loopSeedProfile);
                }).then(function (r2) {
                    applyGridResult({ token: myToken, index: index, url: url, gridsOnly: true, gridProduct: prod, grids: r2.grids });
                });
            }).catch(function (err) {
                upgradeDone(index);
                hostLog('grid-only ' + index + ' decode failed: ' + (err && err.message ? err.message : err));
            });
        }
    }

    // Merges a grids-only decode into the existing frame (and its decoded-cache entry), leaving the
    // geometry untouched. The inspector reads the new grid on the next mousemove, so no re-render is needed.
    function applyGridResult(res) {
        if (res.token !== loopToken) { upgradeDone(res.index); return; } // stale loop
        if (res.error) {
            hostLog('grid-only ' + res.index + ' error: ' + res.error);
            upgradeDone(res.index);
            return;
        }
        var g = (res.grids && (res.gridProduct in res.grids)) ? res.grids[res.gridProduct] : null;
        var f = frames[res.index];
        if (f) {
            f.grids = f.grids || {}; f.grids[res.gridProduct] = g || null;
            f.gridsExtra = f.gridsExtra || {}; f.gridsExtra[res.gridProduct] = true; // built (even if no data) → don't re-queue
        }
        var c = decodedCache.get(res.url); // mirror into the cache so a revisit keeps the grid
        if (c) {
            c.grids = c.grids || {}; c.grids[res.gridProduct] = g || null;
            c.gridsExtra = c.gridsExtra || {}; c.gridsExtra[res.gridProduct] = true;
        }
        upgradeDone(res.index); // free the slot + pump the next queued frame
    }

    // ---- Inspector (RadarScope-style "read the value under the cursor") ----
    // In inspect mode a mousemove projects the cursor lng/lat back to the site's polar frame
    // (the SAME equirectangular math buildGates uses, so the inspected gate is exactly the painted
    // one), reads the value from the current frame's grid for the active product, shows a DOM tooltip
    // next to the cursor, and pushes the value to the host (throttled) so the color-scale bar can mark
    // it in real time. All lookups are pure main-thread array reads — no re-decode, no GL readback.
    let inspecting = false;
    let inspectTip = null;          // the DOM tooltip element (lazily created; ONE, it follows the cursor)
    // ⚠️ CROSS-PANE: Inspect is ONE instrument reading N panes. The cursor sits in one pane, but the point
    // it names is GEOGRAPHIC, and every pane shows the same ground at the same instant — so one hover
    // yields one reading per pane, all of the same gate, each ticking on its own chip ramp. Four numbers
    // for one storm feature, read in a glance at the chip cluster. The panes the cursor is NOT in get a
    // mirrored crosshair at that lng/lat so it is obvious the readings are the same point.
    let inspectLngLat = null;       // the geographic point under the cursor (null = not pointing at the map)
    let hoveredView = null;         // the pane the cursor is actually in (it shows the tooltip, not a cross)
    // Per-pane host-push throttle state: each pane reports its own value, so each needs its own
    // has/has-not edge and timer (a shared one would swallow the other panes' transitions).
    const lastInspectPush = {}, lastInspectHad = {};

    function ensureInspectTip() {
        if (inspectTip) return inspectTip;
        const el = document.createElement('div');
        el.id = 'radar-inspect-tip';
        el.style.cssText = 'position:absolute;z-index:20;pointer-events:none;display:none;' +
            'font:600 12px/1.3 "Segoe UI",sans-serif;color:#fff;background:rgba(10,12,16,.85);' +
            'border:1px solid rgba(255,255,255,.25);border-radius:4px;padding:3px 7px;white-space:nowrap;' +
            'box-shadow:0 1px 4px rgba(0,0,0,.55);';
        document.body.appendChild(el);
        inspectTip = el;
        return el;
    }

    // The mirrored crosshair drawn in every pane the cursor is NOT in. Built from two child bars rather
    // than a stylesheet rule, so the whole thing is inline and this module still injects no CSS (the one
    // place that does — radar-sites.js — is a template-literal minefield we don't need to join).
    function ensureCrossEl(v) {
        if (v.crossEl && v.crossEl.isConnected) return v.crossEl;
        const el = document.createElement('div');
        el.className = 'radar-inspect-cross';
        el.style.cssText = 'position:absolute;z-index:19;pointer-events:none;display:none;' +
            'width:17px;height:17px;margin-left:-8.5px;margin-top:-8.5px;';
        const bar = 'position:absolute;background:rgba(255,255,255,.9);box-shadow:0 0 2px rgba(0,0,0,.9);';
        const h = document.createElement('div');
        h.style.cssText = bar + 'left:0;top:8px;width:17px;height:1.5px;';
        const w = document.createElement('div');
        w.style.cssText = bar + 'left:8px;top:0;width:1.5px;height:17px;';
        el.appendChild(h); el.appendChild(w);
        v.map.getContainer().appendChild(el);
        v.crossEl = el;
        return el;
    }
    function hideCross(v) { if (v.crossEl) v.crossEl.style.display = 'none'; }
    // Place (or hide) one pane's crosshair for the current inspect point. The hovered pane never gets one
    // — the real cursor is already there, and it is already a crosshair.
    function positionCross(v) {
        if (!inspecting || !inspectLngLat || v === hoveredView) { hideCross(v); return; }
        let pt;
        try { pt = v.map.project(inspectLngLat); } catch (e) { hideCross(v); return; }
        const el = ensureCrossEl(v);
        el.style.left = pt.x + 'px';
        el.style.top = pt.y + 'px';
        el.style.display = 'block';
    }

    // The value grid for the current frame + THIS PANE's product, or null if not available.
    function inspectGrid(v) {
        const f = frames[currentFrame];
        return (f && f.grids && f.grids[v.product]) || null;
    }

    // Reads the moment value at a geographic point from a polar value grid, or null (no data /
    // out of range). Mirrors buildGates' projection: x∝sin(az), y∝cos(az), az from north clockwise.
    function lookupValue(grid, lat, lng) {
        if (!grid || !grid.values || !Geo) return null; // values null = grid was built metadata-only (Inspect was off)
        const polar = Geo.lngLatToPolar(siteLat, siteLon, lng, lat);
        const rangeKm = polar.rangeMeters / 1000, azDeg = polar.azDeg;
        const j = Math.floor((rangeKm - grid.firstGate) / grid.gateSize);
        if (j < 0 || j >= grid.nGates) return null;
        // Nearest radial by azimuth (unsorted, ~720 entries — trivial per move). Reject if the
        // closest beam is too far (a gap or beyond the sweep), so we don't report a bogus value.
        let best = -1, bestD = 999;
        for (let i = 0; i < grid.az.length; i++) {
            const a = grid.az[i]; if (isNaN(a)) continue;
            let dd = Math.abs(a - azDeg); if (dd > 180) dd = 360 - dd;
            if (dd < bestD) { bestD = dd; best = i; }
        }
        if (best < 0 || bestD > 2) return null;
        const q = grid.values[best * grid.nGates + j];
        if (q === GRID_NODATA) return null;
        return { value: q / grid.scale, unit: grid.unit, digits: grid.digits };
    }

    // Push the inspected value to the host for the color-scale marker. Throttled (~14/s) and edge-
    // triggered on the has/has-not transition so leaving data hides the marker promptly.
    function pushInspect(pane, has, value) {
        const now = Date.now();
        if (has === lastInspectHad[pane] && now - (lastInspectPush[pane] || 0) < 70) return;
        lastInspectPush[pane] = now; lastInspectHad[pane] = has;
        post({ type: 'radarInspect', pane: pane, has: has, value: has ? value : 0 });
    }

    // Bound per pane (see setInspect). ONE hover, N readings: every pane reads its own product's grid at
    // the SAME geographic point, so all the chips tick together. The pane under the cursor also gets the
    // tooltip; the rest get the mirrored crosshair.
    // Cost is one lookupValue per pane per mouse move — a nearest-azimuth scan of ~720 radials plus an
    // array read, so four panes is still nothing.
    function onInspectMove(v, e) {
        if (!inspecting) return;
        hoveredView = v;
        inspectLngLat = e.lngLat;
        let r = null;
        forEachView(function (o) {
            const hit = lookupValue(inspectGrid(o), e.lngLat.lat, e.lngLat.lng);
            if (o === v) r = hit;                       // reuse the hovered pane's own read for the tooltip
            pushInspect(o.index, !!hit, hit ? hit.value : 0);
            positionCross(o);                           // no-op for the hovered pane (hides its cross)
        });
        const el = ensureInspectTip();
        if (r) {
            // Speed products (velocity / SRV / spectrum width — unit "m/s") read as the native m/s value
            // PLUS mph, e.g. "12.3 m/s (28 mph)"; other products keep their native unit (dBZ / unitless CC).
            const main = r.unit === 'm/s'
                ? formatSpeed(r.value)
                : r.value.toFixed(r.digits) + (r.unit ? ' ' + r.unit : '');
            // On Velocity, show the SAME gate's dealiasing breakdown so the unfold can be checked
            // without re-hovering: the displayed value is the dealiased speed; the raw (folded)
            // value is what the radar measured (within ±Nyquist), recovered by removing the whole
            // 2×Nyquist folds the dealiaser added. Lets the user confirm high velocities at a glance.
            const vel = velocityFold(r.value, v.product);
            if (vel) {
                el.innerHTML = '<div>' + main + '</div>' +
                    '<div style="font-size:10px;opacity:.75;font-weight:400">raw ' + vel.raw.toFixed(0) +
                    ' · Nyq ' + vel.nyq.toFixed(0) + ' · ' + vel.foldLabel + '</div>';
            } else {
                el.textContent = main;
            }
            // e.point is relative to THIS pane's canvas; the tooltip is a document-level element, so
            // offset it by where that pane sits in the page.
            const box = v.map.getContainer().getBoundingClientRect();
            el.style.left = (box.left + e.point.x + 14) + 'px';
            el.style.top = (box.top + e.point.y + 14) + 'px';
            el.style.display = 'block';
        } else {
            el.style.display = 'none';
        }
        // NB: no pushInspect here — the loop above already pushed EVERY pane's value, this one included.
    }

    // A speed in m/s → "12.3 m/s (28 mph)" (sign preserved: inbound negative, outbound positive).
    function formatSpeed(ms) {
        return ms.toFixed(1) + ' m/s (' + (ms * 2.23694).toFixed(1) + ' mph)';
    }

    // For a dealiased velocity value, recover the raw (folded) measurement + the fold count from the
    // current frame's Nyquist. Returns null when not on Velocity / no Nyquist (so other products show
    // just their value). Dealiasing only ever adds whole multiples of 2×Nyquist, so this is exact.
    function velocityFold(dealiased, product) {
        if (product !== 'velocity') return null;
        const f = frames[currentFrame];
        const nyq = f && f.velNyq;
        if (!(nyq > 0)) return null;
        const folds = Math.round(dealiased / (2 * nyq));
        const raw = dealiased - folds * 2 * nyq;
        const foldLabel = folds === 0 ? 'no fold'
            : (folds > 0 ? '+' : '') + folds + ' fold' + (Math.abs(folds) === 1 ? '' : 's');
        return { raw: raw, nyq: nyq, folds: folds, foldLabel: foldLabel };
    }
    function onInspectOut(v) {
        // ⚠️ Moving from one pane to another fires this pane's mouseout AND the next pane's mousemove, and
        // the order is not guaranteed. Only clear if the cursor genuinely left — i.e. we are still the
        // pane it was last in — or crossing a groove would blank the readings that just arrived.
        if (hoveredView && hoveredView !== v) return;
        if (inspectTip) inspectTip.style.display = 'none';
        inspectLngLat = null;
        hoveredView = null;
        forEachView(function (o) { hideCross(o); pushInspect(o.index, false, 0); });
    }
    // Attach / detach the inspect handlers for ONE pane. Called per view when the mode toggles and when
    // a pane is created while Inspect is already on.
    function bindInspect(v) {
        if (v.inspectMove) return;
        v.inspectMove = function (e) { onInspectMove(v, e); };
        v.inspectOut = function () { onInspectOut(v); };
        // The crosshair marks a GEOGRAPHIC point, so it has to be re-projected whenever the camera moves.
        // A drag already fires mousemove, but a wheel zoom with a stationary pointer does not — without
        // this the mirrored crosses would drift off the gate they are marking.
        v.inspectCamera = function () { if (inspecting && inspectLngLat) positionCross(v); };
        v.map.on('mousemove', v.inspectMove);
        v.map.on('mouseout', v.inspectOut);
        v.map.on('move', v.inspectCamera);
        const canvas = v.map.getCanvas && v.map.getCanvas();
        if (canvas) canvas.style.cursor = 'crosshair';
    }
    function unbindInspect(v) {
        if (v.inspectMove) { v.map.off('mousemove', v.inspectMove); v.inspectMove = null; }
        if (v.inspectOut) { v.map.off('mouseout', v.inspectOut); v.inspectOut = null; }
        if (v.inspectCamera) { v.map.off('move', v.inspectCamera); v.inspectCamera = null; }
        hideCross(v);
        const canvas = v.map.getCanvas && v.map.getCanvas();
        if (canvas) canvas.style.cursor = '';
        pushInspect(v.index, false, 0);
    }

    window.RadarLayer = {
        // ===== PANES =====
        // map.js owns the maps; it hands the list here whenever the pane layout changes. Views are
        // reconciled IN PLACE: a surviving map keeps its view (and therefore its product and its GL
        // objects), a new map gets a fresh view with the radar layer added if a loop is already up, and
        // a departing map is detached separately (detachView, called BEFORE map.remove() so the layer's
        // onRemove can free its buffers while the context is still alive).
        // The loop itself is untouched by a layout change — same frames, same decode cache, same
        // scrubber position — which is why entering multi-pane is instant.
        setViews: function (maps) {
            const next = [];
            for (let i = 0; i < maps.length; i++) {
                const existing = viewFor(maps[i]);
                const v = existing || makeView(maps[i], i);
                v.index = i;
                next.push(v);
            }
            // Any view whose map is gone from the list loses its layer (it may already be removed).
            forEachView(function (old) {
                if (next.indexOf(old) < 0) { try { removeLayer(old); } catch (e) { /* map gone */ } }
            });
            views = next;
            forEachView(function (v) {
                attachContextListeners(v);               // canvas listeners — safe before the style loads
                if (inspecting) bindInspect(v);          // a pane created while Inspect is on joins it
                // ⚠️ Everything below ADDS sources/layers, and MapLibre throws "Style is not done loading"
                // on a map whose style hasn't arrived — which is exactly the state a pane is in here, since
                // map.js calls us the instant it constructs the new maps. A pane in that state gets its
                // layers from reAddAll on its own 'load' instead (which does this same work), so skipping
                // is not a loss. Without this, entering a layout WITH A LOOP UP threw here and aborted the
                // rest of setViews — including the queueAllUpgrades + postBuildProgress below.
                if (v.map.isStyleLoaded && !v.map.isStyleLoaded()) return;
                try {
                    if (currentFrame >= 0) showCurrent(v, 'setViews');
                    if (currentRangeMeters > 0) addRangeRing(v);
                    if (sweepRaf) ensureSweepLayer(v);
                } catch (e) {
                    // One bad pane must not cost the others their setup, or the loop its progress post.
                    hostLog('setViews pane=' + v.index + ' deferred: ' + (e && e.message ? e.message : e));
                }
            });
            // A new pane's product may widen the wanted set (Rule 6 keeps this to only what's missing).
            queueAllUpgrades('panes');
            postBuildProgress();
            hostLog('setViews n=' + views.length + ' prods=' + viewProducts().join('|'));
        },
        // Release one pane's GL objects while its context is still live. map.js calls this immediately
        // before map.remove().
        detachView: function (map) {
            const v = viewFor(map);
            if (!v) return;
            if (inspecting) unbindInspect(v);
            try { removeLayer(v); } catch (e) { /* already torn down */ }
            views = views.filter(function (o) { return o !== v; });
            forEachView(function (o, i) { o.index = i; });
            hostLog('detachView -> n=' + views.length);
        },
        // ===== PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit) =====
        // Read-only snapshot of the loop's inner build state for the Pipeline Console card. The host
        // polls this ONLY while the console is open. Pure reader: mutates nothing, touches no hot path.
        // Per-frame per-product code: 0 = not built, 1 = built but no data, 2 = built with data.
        // q/f/r are frame-level (the upgrade queue decodes a whole frame): queued / in-flight / reason.
        pipelineSnapshot: function () {
            if (!frames.length) return null;
            var ids = Products ? Object.keys(Products) : [];
            var out = [];
            for (var i = 0; i < frames.length; i++) {
                // A stale frame (mid tilt re-cut) still renders, but its products belong to the PREVIOUS
                // elevation — report it unbuilt so the console shows the re-cut filling, not a full loop.
                var f = frames[i], b = (f && !f.stale && f.built) || {}, mo = (f && f.moments) || {};
                var s = new Array(ids.length);
                for (var p = 0; p < ids.length; p++) {
                    var id = ids[p];
                    if (!b[id]) s[p] = 0;
                    else if (mo[id] != null) s[p] = 2;
                    else s[p] = 1;
                }
                out.push({ s: s, q: upgradeQueue.indexOf(i) >= 0, f: !!upgradeInFlight[i], r: upgradeReason[i] || '' });
            }
            var m = _autoMotion;
            var vwp = {
                inFlight: vwpInFlight(),
                hasMotion: !!(m && !m.insufficient),
                insufficient: !!(m && m.insufficient),
                speedMs: (m && !m.insufficient) ? m.speedMs : 0,
                dirDeg: (m && !m.insufficient) ? m.dirDeg : 0,
                source: (m && m.source) || '',
                cuts: (m && m.cuts) || 0,
                topM: (m && m.topM) || 0
            };
            var done = new Array(ids.length);
            var first = new Array(ids.length);
            for (var q = 0; q < ids.length; q++) {
                var dm = _prodFullAtMs[ids[q]];
                done[q] = (dm == null) ? null : Math.round(dm);
                var fm = _prodFirstAtMs[ids[q]];
                first[q] = (fm == null) ? null : Math.round(fm);
            }
            return {
                n: frames.length, cf: currentFrame, active: viewProducts().join('|'), panes: viewProducts(),
                ids: ids, velPrefetch: velPrefetch, fullPrefetch: fullPrefetch,
                wanted: wantedProducts(), vwp: vwp, frames: out,
                done: done, first: first, timingFrozen: _timingFrozen
            };
        },
        // ===== END PIPELINE CONSOLE =====
        beginLoop: function (lat, lon) {
            forEachView(attachContextListeners);
            // New site → drop the old range ring + sweep (the first decoded frame redraws them at
            // the new site's range); same site (a reload) → keep them up, no flicker.
            if (lat !== siteLat || lon !== siteLon) {
                removeRangeRingAll(); stopSweep(); currentRangeMeters = 0;
            }
            siteLat = lat; siteLon = lon;
            loopToken++;            // invalidate any in-flight frames from a previous loop
            resetUpgrades();        // and drop any pending/in-flight lazy-upgrade decodes from it
            velPrefetch = false;    // new site: build reflectivity first; the host re-arms velocity prefetch once it's ready
            fullPrefetch = false;   // …and the dual-pol second wave re-arms itself once THIS loop's trio settles
            // ⚠️ Forget the previous loop's storm motion: a NEW site must NOT prefetch SRV with the old site's
            // motion (that built SRV wrong, then rebuilt the whole loop when the real motion landed). srvMotionReady
            // reads false until THIS loop's motion is computed; vwpGen++ drops any still-in-flight compute for the old loop.
            _autoMotion = null; _autoMotionKey = ''; vwpGen++;
            for (var _vk in _vwpInFlight) delete _vwpInFlight[_vk];
            _loopSeedProfile = null; _seedProfileIdx = -1; // new site → forget the old loop's wind profile

            // PIPELINE CONSOLE: reset per-product fill timing for the new loop (remove with the feature).
            _loopStartT = (typeof performance !== 'undefined' ? performance.now() : Date.now());
            _builtAtMs = []; _prodFirstAtMs = {}; _prodFullAtMs = {}; _timingFrozen = false;
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            invalidateUploads();
            renderErrCount = blankCount = 0; lastRenderErrAt = lastBlankAt = 0;
            removeLayerAll();
            hostLog('beginLoop token=' + loopToken + ' @ ' + lat.toFixed(3) + ',' + lon.toFixed(3)
                + ' panes=' + views.length);
        },
        addFrame: function (url, index) {
            hostLog('addFrame idx=' + index);
            decodeFrame(url, index, 'load');
        },
        showFrame: function (index) {
            if (frames[index]) {
                // Decoded: switch to it now (and (re)add the layer if needed).
                pendingFrame = -1;
                if (index !== currentFrame) { currentFrame = index; invalidateUploads(); }
                // Flag a scrub onto a frame whose ACTIVE product isn't built — it renders blank/stale until an
                // upgrade fills it (the "missing frame while scrubbing" symptom). Reflectivity is always built.
                forEachView(function (v) {
                    if (v.product !== 'reflectivity' && !(frames[index].built && frames[index].built[v.product]))
                        hostLog('showFrame idx=' + index + ' pane=' + v.index + ' NOT-BUILT prod=' + v.product + ' (blank until upgrade)');
                });
                showCurrentAll('showFrame');
            } else {
                // Not decoded yet: remember the intent but keep the current frame on screen, so
                // we don't blank the layer. applyFrameResult promotes it once it decodes.
                pendingFrame = index;
                hostLog('showFrame idx=' + index + ' pending (not decoded; keeping cf=' + currentFrame + ')');
            }
        },
        // Incremental loop refresh: reindex the existing decoded frames to a new ordering instead
        // of tearing the loop down. `mappingJson` is an array of [fromIndex, toIndex] pairs; each
        // reused frame's geometry object is carried over (NOT re-decoded), the live frame included.
        // The host then decodes only the genuinely-new volume(s) into the unfilled slots. Crucially
        // this NEVER removes the layer, so an archive reload no longer blanks the radar — the frame
        // already on screen stays up (same geometry, no re-upload) while the new frames stream in.
        remap: function (newCount, mappingJson) {
            var mapping;
            try { mapping = JSON.parse(mappingJson); } catch (e) { hostLog('remap parse failed: ' + (e && e.message ? e.message : e)); return; }
            // New loop generation: drop any in-flight decode still targeting an OLD index.
            loopToken++;
            resetUpgrades(); // stale upgrades target old indices; re-queued below against the new frames[]
            var oldCurrent = currentFrame;
            var nf = new Array(newCount);
            var newCurrent = -1;
            for (var k = 0; k < mapping.length; k++) {
                var from = mapping[k][0], to = mapping[k][1];
                if (to >= 0 && to < newCount && frames[from]) nf[to] = frames[from];
                if (from === oldCurrent && to >= 0) newCurrent = to;
            }
            frames = nf;
            pendingFrame = -1;
            if (newCurrent >= 0 && frames[newCurrent]) {
                // The displayed frame survived: keep its image up. The GL buffers already hold this
                // geometry (same object), so skip the re-upload when uploadedFrame tracked it.
                currentFrame = newCurrent;
                // Per pane: a view whose buffers already held the displayed frame just renumbers them
                // (same geometry object, no re-upload); any other view re-uploads on its next render.
                forEachView(function (v) { v.uploadedFrame = (v.uploadedFrame === oldCurrent) ? newCurrent : -1; });
                forEachView(function (v) { if (v.map.getLayer(LAYER_ID)) v.map.triggerRepaint(); else addLayer(v); });
            } else {
                // The displayed frame fell out of the window: fall back to the newest decoded frame.
                var nn = -1;
                for (var j = newCount - 1; j >= 0; j--) { if (frames[j]) { nn = j; break; } }
                currentFrame = nn;
                invalidateUploads();
                showCurrentAll('remap-fallback');
            }
            queueAllUpgrades('remap'); // a reused refl-only frame still needs velocity if Velocity is active
            postBuildProgress(); // re-report readiness/completeness against the NEW indexing (host arrays reindex)
            hostLog('remap newCount=' + newCount + ' cf=' + currentFrame + ' reused=' + frames.filter(Boolean).length + ' token=' + loopToken);
        },
        // TILT SWITCH: same site, same volumes, DIFFERENT elevation bytes — so every frame genuinely has to
        // re-decode (each cached .V06 holds exactly one cut), but per docs/radar-loop-flow.md Rule 7 the loop
        // must NOT be torn down to do it. Unlike beginLoop this keeps frames[] and the LAYER up and marks each
        // frame STALE: a stale frame still RENDERS (the previous elevation stays on screen, so the map never
        // blanks) while reporting not-ready to the host, so the scrubber empties and re-fills left-to-right as
        // the new cut lands (Rule 2) and playback holds at that frontier instead of playing a mix of tilts.
        // The host then re-issues addFrame for every index against the new tilt's URLs, CURRENT FRAME FIRST
        // (Rule 1); applyFrameResult replaces each slot as it arrives (the url differs, so it can't merge).
        //
        // Deliberately KEPT across a retile, unlike beginLoop:
        //   • the storm motion (_autoMotion) — it's a property of the VOLUME, not of the displayed cut (the VAD
        //     reads the bottom several cuts either way), so SRV is ready for the new tilt immediately (Rules 4/5:
        //     one motion per loop, and this is the same loop). Re-deriving it would strand SRV on the stand-in
        //     for ~15 s for no gain.
        //   • velPrefetch — the duo must keep building on every frame (Rule 3), and a tilt switch isn't a site
        //     switch (Rule 8 exempts it), so we don't drop back to reflectivity-only.
        //   • _loopSeedProfile — a wind profile is (u,v) vs HEIGHT; seedExpectedVr re-projects it through the
        //     decoding cut's own elevation, so it's a valid dealias first guess at any tilt.
        //   • the range ring + sweep — same site, same range.
        retile: function (count) {
            loopToken++;        // drop any in-flight decode still carrying the OLD tilt
            resetUpgrades();    // and its pending upgrades; the host's addFrame sweep re-drives the fill
            fullPrefetch = false; // the dual-pol second wave re-arms once THIS tilt's trio settles
            var n = Math.max(0, count | 0);
            for (var i = 0; i < n; i++) { if (frames[i]) frames[i].stale = true; }
            // PIPELINE CONSOLE: a retile is a fresh fill, so time it as one (remove with the feature).
            _loopStartT = (typeof performance !== 'undefined' ? performance.now() : Date.now());
            _builtAtMs = []; _prodFirstAtMs = {}; _prodFullAtMs = {}; _timingFrozen = false;
            postBuildProgress(); // report the emptied scrubber now, before the first new frame lands
            hostLog('retile count=' + n + ' cf=' + currentFrame + ' token=' + loopToken);
        },
        clear: function () {
            loopToken++;
            resetUpgrades();
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            removeLayerAll();
            removeRangeRingAll();
            stopSweep();
            currentRangeMeters = 0;
            postBuildProgress(); // frames=[] -> 0/0 clears the "building" readout
            hostLog('clear token=' + loopToken);
        },
        // DOW Event Viewer: show a single curated mobile-radar frame (the "dow-frame/1" JSON from
        // tools/dow_import.py, served from the dowevents host). It takes over the radar layer as a
        // one-frame loop centred on the TRUCK's position — reusing the whole render path (WebGL fill,
        // the real-extent range ring, product toggle, Inspect, legend). No loop/live/sweep machinery.
        showDow: function (url) {
            forEachView(attachContextListeners);
            loopToken++;
            resetUpgrades();
            const myToken = loopToken;
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            invalidateUploads();
            renderErrCount = blankCount = 0; lastRenderErrAt = lastBlankAt = 0;
            removeLayerAll();
            removeRangeRingAll();
            stopSweep();
            currentRangeMeters = 0; // a DOW frame is a single sweep — no rotating arm
            hostLog('showDow ' + url);
            fetch(url, { cache: 'no-store' }).then(function (r) {
                if (!r.ok) throw new Error('HTTP ' + r.status);
                return r.json();
            }).then(function (json) {
                if (myToken !== loopToken) return null;
                siteLat = (typeof json.lat === 'number') ? json.lat : 0;
                siteLon = (typeof json.lon === 'number') ? json.lon : 0;
                // Recenter on the truck — a DOW frame is a specific (often far-off) deployment, so
                // unlike the NEXRAD loop we DO fly there, or it would render off-screen.
                // Fly the PRIMARY only — map.js mirrors the camera to the other panes as it plays.
                try { if (primaryView()) primaryView().map.flyTo({ center: [siteLon, siteLat], zoom: 9, duration: 800 }); } catch (e) { /* non-fatal */ }
                return import('./radar-decode.js').then(function (m) {
                    return m.decodeDowFrame(json, MIN_DBZ);
                });
            }).then(function (r2) {
                if (!r2 || myToken !== loopToken) return;
                // Feed it as frame 0 — applyFrameResult adopts the first frame (currentFrame<0),
                // (re)adds the layer, and draws the range ring from r2.rangeMeters. DOW always builds
                // velocity (pre-dealiased, cheap) + grids so built.velocity/gridsBuilt=true; url undefined = not cached.
                applyFrameResult(frameResultFrom(r2, myToken, 0, undefined));
            }).catch(function (err) {
                hostLog('showDow failed: ' + (err && err.message ? err.message : err));
            });
        },
        setOpacity: function (op) {
            opacity = op; // shared: one radar opacity across every pane
            repaintAll();
        },
        // Fire ONE sweep pulse (arm + trailing afterglow, one revolution then hides). The host calls
        // this when a genuinely-new frame lands. No-op until a frame has decoded (no radius yet).
        pulseSweep: function () {
            startSweepPulse();
        },
        // Stop + remove the sweep (host calls with period <= 0 on clear / entering replay). Kept the
        // name for the existing host shim; the arm is one-shot now, so a positive period just re-pulses.
        setSweep: function (periodSeconds) {
            if (Number(periodSeconds) > 0) startSweepPulse();
            else stopSweep();
        },
        // Switch rendered moment ('reflectivity' | 'velocity' | 'cc'). Reflectivity + CC geometry is
        // always built, so those switch instantly. Velocity is built lazily (it's the one product that
        // must dealias), so switching TO Velocity queues the loaded refl-only frames for a BOUNDED,
        // current-frame-first re-decode (see the upgrade queue up top) — velocity fills in around the
        // frame on screen instead of flooding the decode pool and flashing blanks during playback.
        // Set ONE pane's product. Only that pane re-uploads; the others are untouched, which is what
        // makes a chip change in a quad cost nothing for the other three.
        setProduct: function (paneIndex, p) {
            const v = views[paneIndex | 0];
            if (!v || !productKnown(p) || p === v.product) return;
            var from = v.product;
            v.product = p;
            v.uploadedFrame = -1; // force the new product's geometry to upload on this pane's next render
            // How many frames already have the new product built — i.e. how much of this switch is INSTANT vs
            // needs a re-decode. A switch that should be instant (all built) but still decodes is a bug signal.
            var have = 0, n = frames.length;
            for (var i = 0; i < n; i++) if (p === 'reflectivity' || (frames[i] && frames[i].built && frames[i].built[p])) have++;
            hostLog('product pane=' + v.index + ' ' + from + '->' + p + ' built=' + have + '/' + n
                + (have < n ? ' (will decode ' + (n - have) + ')' : ' (instant)'));
            queueAllUpgrades('switch>' + p); // no-op unless Velocity/SRV (or Inspect) needs geometry these frames lack
            postBuildProgress(); // switching to Velocity: report the (mostly not-yet-built) ready set now
            if (v.map.getLayer(LAYER_ID)) v.map.triggerRepaint();
        },
        // Arm the loop to build Velocity (+ SRV, its companion) on every frame — the host calls this RIGHT
        // AFTER FIRST PAINT so the backfill builds COMPLETE frames in one pass (docs/radar-loop-flow.md
        // Rule 3), not a second sweep after reflectivity. The already-painted first frame gets its velocity/SRV
        // via one upgrade here; every backfill frame after gets it in its first decode. velPrefetch persists so
        // frames added later (live poll, incremental reload) build complete too. Idempotent; a no-op once every
        // frame is built (needsUpgrade returns false).
        prefetchVelocity: function () {
            if (velPrefetch) return;
            velPrefetch = true;
            queueAllUpgrades('velprefetch');
        },
        // Compute the loop's AUTO storm motion from the newest volume's tilt URLs (the host provides them when
        // SRV/auto is active, and only re-requests when the newest volume changes). Off-thread; on success it
        // pushes the readout and rebuilds SRV once. No-op in manual mode. See computeStormMotionForVolume.
        computeStormMotion: function (urls) {
            if (typeof urls === 'string') { try { urls = JSON.parse(urls); } catch (e) { urls = []; } }
            if (Array.isArray(urls)) computeStormMotionForVolume(urls);
        },
        // Re-add after a basemap switch (setStyle drops custom layers + sources); frames + the range
        // ring are retained, so restore them. If a sweep pulse is mid-flight, restore its layer too so
        // the in-progress revolution keeps drawing.
        reAdd: function (map) {
            const v = viewFor(map);
            if (!v) return;
            if (currentFrame >= 0) addLayer(v);
            addRangeRing(v);
            if (sweepRaf) ensureSweepLayer(v);
        },
        // Toggle inspect mode (read the value under the cursor). Attaches/detaches the mousemove
        // handlers + crosshair cursor and hides the tooltip / clears the host marker when off.
        // Inspect is GLOBAL (one armed cursor mode over the map), so it binds in EVERY pane: point at
        // any pane and that pane reads its own product's value grid under the cursor.
        setInspect: function (on) {
            inspecting = !!on;
            if (inspecting) {
                forEachView(bindInspect);
                // Value grids are skipped by default (memory). Turning Inspect ON now builds them on
                // demand for the loaded frames via the bounded, current-frame-first upgrade queue — so
                // lookups become available around the frame on screen first, without flooding the pool.
                queueAllUpgrades('inspect');
            } else {
                forEachView(unbindInspect);
                if (inspectTip) inspectTip.style.display = 'none';
                inspectLngLat = null;
                hoveredView = null;
            }
            hostLog('inspect=' + inspecting + ' panes=' + views.length);
        },
    };

    // ---- Dev-only velocity-dealias validation (fixed-corpus regression scorer) ----
    // The scorer itself lives in radar-validate.js: it shares NO loop state (only the two values
    // passed in below), so it stays out of this file and off the startup path — the module is
    // fetched the first time the host actually starts a run, which in a Release build is never.
    // Excise the feature by deleting this block + that file. See docs/radar-validation.md.
    window.radarValidate = function (entriesJson) {
        // Publish the progress global SYNCHRONOUSLY, before the dynamic import: the host starts a run
        // and immediately begins polling, so it must never observe the PREVIOUS run's finished state
        // in the window before the module lands. The scorer MUTATES this object rather than replacing
        // it, so a cancel arriving mid-import (the host sets .cancel here) still takes effect.
        var state = { total: 0, done: 0, finished: false, cancel: false, results: [] };
        window.__anvilValidation = state;
        import('./radar-validate.js').then(function (m) {
            m.runValidation(entriesJson, state, { hostLog: hostLog, minDbz: MIN_DBZ });
        }).catch(function (err) {
            state.finished = true;
            hostLog('validate load failed: ' + ((err && err.message) ? err.message : err));
        });
    };
})();
