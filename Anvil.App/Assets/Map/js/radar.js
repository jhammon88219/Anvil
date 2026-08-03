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
    function wantedProducts() {
        if (!Products) return [];
        var out = [];
        // The active product itself — EXCEPT SRV waits for the storm motion (srvMotionReady): we never build
        // SRV at the wrong/base motion (that caused a rebuild when the real motion landed). Until it's ready we
        // build base VELOCITY in SRV's place and render that as the stand-in (see render's SRV fallback).
        if (product !== 'reflectivity' && Products[product]) {
            if (product === 'srv' && !srvMotionReady()) { if (Products.velocity) out.push('velocity'); }
            else out.push(product);
        }
        var doppler = (product === 'velocity' || product === 'srv') || (velPrefetch && product === 'reflectivity');
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
    function activeGridReady(f) { return !!(f && (f.gridsBuilt || (f.gridsExtra && f.gridsExtra[product]))); }
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
        if (!total) { post({ type: 'radarBuildProgress', product: product, built: 0, total: 0, ready: [], complete: [] }); return; }
        // `ready` = ACTIVE-product readiness — drives playback's built-frontier hold (don't advance onto a
        // frame whose on-screen product isn't built). While SRV is active but its motion isn't ready we render
        // the VELOCITY stand-in, so report readiness by VELOCITY (what's actually on screen) — otherwise the
        // frontier reads all-not-ready and playback stalls.
        var gate = (product === 'srv' && !srvMotionReady()) ? 'velocity' : product;
        var eager = (gate === 'reflectivity'); // reflectivity is always built; everything else on demand
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
            var b = frames[i] && frames[i].built;
            var r = eager || !!(b && b[gate]);
            ready[i] = r;
            if (r) built++;
            var reflOk = !!(b && b.reflectivity); // always built by any decode
            var velOk = !!(b && b.velocity);
            complete[i] = reflOk && velOk;
        }
        post({ type: 'radarBuildProgress', product: product, built: built, total: total, ready: ready, complete: complete });
    }

    let product = 'reflectivity'; // 'reflectivity' | 'velocity' | 'srv' | 'cc' | … — which moment to render
    // Storm motion MODE + MANUAL value for Storm-Relative Velocity (SRV). speedMs = m/s, dirDeg = compass
    // bearing the storm is MOVING TOWARD. `auto` (the default) means the motion is DERIVED from a full-volume
    // VAD wind profile → Bunkers (computeStormMotionForVolume below; a single tilt is too shallow to be
    // correct); auto off uses this manual speedMs/dirDeg ({0,0} = SRV identical to base velocity).
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
        hostLog('vwp start ' + shortKey(volKey) + ' tilts=' + urls.length + ' prod=' + product);
        Promise.all(urls.map(function (u) {
            return fetch(u, { cache: 'no-store' }).then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.arrayBuffer(); });
        })).then(function (abs) {
            const w = getVwpWorker(); // a DEDICATED worker, so the motion isn't queued behind frame decodes
            if (w) {
                const id = ++_vwpReqId; _vwpPending[id] = { volKey: volKey, gen: gen };
                w.postMessage({ vwp: true, reqId: id, buffers: abs }, abs);
            } else {
                import('./radar-decode.js').then(function (m) { return m.decodeVwp(abs); })
                    .then(function (motion) { onVwpResult(volKey, motion, gen); })
                    .catch(function (err) { onVwpError(volKey, err, gen); });
            }
        }).catch(function (err) { onVwpError(volKey, err, gen); });
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
                : Math.round(_autoMotion.dirDeg) + '°@' + Math.round(_autoMotion.speedMs / 0.514444) + 'kt cuts=' + _autoMotion.cuts)
            + ' rebuild=' + changed);
        if (changed) {
            dropAllSrvAndRequeue(); // motion CHANGED → invalidate stale SRV geometry (re-queues only if product==srv)
        }
        // Top up SRV in the BACKGROUND so a later switch to SRV is instant (the velocity-prefetch bargain). This
        // sits OUTSIDE the `changed` gate on purpose and is IDEMPOTENT — queueAllUpgrades only touches frames that
        // actually lack SRV (needsUpgrade is false once built), so it's a no-op for frames already done. That
        // makes it RECOVER any SRV a queue/decode race dropped, and cover frames added since the last motion
        // result, even when this result didn't change the value. Still gated by srvMotionReady(), so it NEVER
        // builds SRV at a base/wrong motion; skipped while SRV is the active product (that path re-queues above).
        if (product !== 'srv' && velPrefetch && srvMotionReady()) queueAllUpgrades('srvfill');
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
        hostLog('dropAllSrv: dropped ' + n + ' built srv frame(s), requeue=' + (product === 'srv'));
        if (product === 'srv') {
            uploadedFrame = -1;
            queueAllUpgrades('motion');
            postBuildProgress();
            if (currentMap && currentMap.getLayer(LAYER_ID)) currentMap.triggerRepaint();
        }
    }
    let velPrefetch = false; // build velocity (+SRV, its companion) on every frame even when reflectivity is
                             // the active product — armed by the host (prefetchVelocity) RIGHT AFTER FIRST
                             // PAINT, so the backfill builds COMPLETE frames in one pass (Rule 3), not a
                             // second sweep. Reset per new loop.
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
            var b = frames[i] && frames[i].built;
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
    let pendingFrame = -1;  // a frame requested via showFrame before it finished decoding; the
                            // decode that satisfies it promotes it to currentFrame (so showFrame
                            // never pins currentFrame to an undecoded index and blanks the layer).
    let uploadedFrame = -1; // which frame's geometry is currently in the GL buffers
    let uploadedProduct = ''; // which product's geometry is uploaded (re-upload on a product switch)
    let siteLat = 0, siteLon = 0;
    let opacity = 0.85;
    let loopToken = 0;      // bumped per loop so stale async frames are dropped
    let currentMap = null;
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
    let ctxListenersAttached = false;
    function attachContextListeners(map) {
        if (ctxListenersAttached || !map || !map.getCanvas) return;
        try {
            const c = map.getCanvas();
            c.addEventListener('webglcontextlost', function () { post({ type: 'radarRender', kind: 'ctxlost', cf: currentFrame }); }, false);
            c.addEventListener('webglcontextrestored', function () { post({ type: 'radarRender', kind: 'ctxrestored', cf: currentFrame }); }, false);
            ctxListenersAttached = true;
        } catch (e) { hostLog('ctx listener attach failed: ' + (e && e.message ? e.message : e)); }
    }

    // GL objects (recreated in onAdd; null when the layer isn't attached).
    let program = null, posBuf = null, colorBuf = null;
    let aPos = -1, aColor = -1, uMatrix = null, uOpacity = null;

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

    // Wraps a decode result (r2 from decodeAndBuild / decodeDowFrame — already the keyed
    // { moments, grids, built, gridsBuilt, ... } shape) into what applyFrameResult consumes, just
    // stamping this load's token/index/url. Used by the main-thread decode fallback and the DOW path.
    // NOTE: the Worker (radar-worker.js) builds the message itself because it must pass the typed-array
    // buffers as postMessage transferables — a worker-only concern it can't reach this IIFE-private helper for.
    function frameResultFrom(r2, token, index, url) {
        return Object.assign({}, r2, { token: token, index: index, url: url });
    }

    function applyFrameResult(res) {
        if (!res || res.token !== loopToken) return; // stale (loop changed)
        if (res.error) {
            upgradeDone(res.index); // free the upgrade slot (if this was one); don't retry a failing frame
            hostLog('frame ' + res.index + ' decode failed: ' + res.error);
            post({ type: 'radarFrameReady', index: res.index, hasData: false });
            return;
        }
        var mo = res.moments || {};
        // MERGE, not replace (docs/radar-loop-flow.md Rule 6 for products). A decode only builds the products
        // it was asked for (reflectivity + the buildProducts ids); every OTHER product comes back null/false.
        // If a frame for THIS volume already exists — an additive upgrade (velocity prefetch, a product
        // switch, the dual-pol second wave) — keep its previously-built geometry and overlay only what this
        // decode actually built, so switching to a dual-pol product and back never drops the trio (the old
        // REPLACE-not-MERGE gap #2). A fresh frame (initial load, or a NEW volume at this index after a
        // remap/live append — url differs) has nothing to keep, so this reduces to the old replace.
        var prev = frames[res.index];
        var mergeable = prev && prev.url && res.url && prev.url === res.url;
        var prevVelNyq = mergeable ? (prev.velNyq || 0) : 0;
        if (mergeable) {
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
            // Rebuild res as the MERGED view (this decode's fresh metadata + the accumulated geometry/grids)
            // so both frames[] and the decoded-cache entry (cachePut below) carry every product built so far.
            res = Object.assign({}, res, {
                moments: mMo, built: mBuilt, grids: mGr,
                gridsBuilt: !!prev.gridsBuilt || !!res.gridsBuilt, gridsExtra: mGridsExtra,
            });
            mo = mMo;
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
        if (currentMap) {
            if (res.index === pendingFrame) {
                // A showFrame() requested this index before it had decoded — promote it now.
                pendingFrame = -1;
                currentFrame = res.index;
                uploadedFrame = -1;
                showCurrent(currentMap, 'pending');
            } else if (currentFrame < 0) {
                // Nothing shown yet — adopt the first frame to arrive (host pushes newest first).
                currentFrame = res.index;
                uploadedFrame = -1;
                showCurrent(currentMap, 'first');
            } else if (res.index === currentFrame) {
                // The on-screen frame was re-decoded (live in-place update) — repaint / re-add.
                uploadedFrame = -1;
                showCurrent(currentMap, 're-add');
            }
            // Draw the real outer-extent range ring (RadarScope-style) from this frame's decoded
            // range — AFTER the radar layer (re)add above, so the ring sits on top of the fill.
            if (res.rangeMeters > 0) setRangeRing(currentMap, res.rangeMeters);
        }
        post({ type: 'radarFrameReady', index: res.index, hasData: !res.empty });
        // The AUTO (VAD-derived) storm motion is NO LONGER computed per frame — a single tilt is too shallow
        // for a correct VWP. It's computed once per loop from the newest volume's bottom velocity tilts
        // (computeStormMotionForVolume, triggered by the host) and surfaced via publishAutoMotion.
        postBuildProgress(); // this frame's build state may have changed the ready count
        maybeArmFullPrefetch(); // this frame may have been the last of the DUO → arm the dual-pol second wave
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

    function makeCustomLayer() {
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
                program = glc.createProgram();
                glc.attachShader(program, vs);
                glc.attachShader(program, fs);
                glc.linkProgram(program);
                if (!glc.getProgramParameter(program, glc.LINK_STATUS)) {
                    throw new Error(glc.getProgramInfoLog(program) || 'program link failed');
                }
                aPos = glc.getAttribLocation(program, 'a_pos');
                aColor = glc.getAttribLocation(program, 'a_color');
                uMatrix = glc.getUniformLocation(program, 'u_matrix');
                uOpacity = glc.getUniformLocation(program, 'u_opacity');
                posBuf = glc.createBuffer();
                colorBuf = glc.createBuffer();
                uploadedFrame = -1; // force a re-upload on first render
            },
            render: function (glc, args) {
                if (!program || currentFrame < 0) return;
                const f = frames[currentFrame];
                if (!f) { noteRenderIssue('no frame at cf=' + currentFrame, false); return; }
                // Pick the geometry for the active product — one keyed lookup (radar-products.js).
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
                    if ((uploadedFrame !== currentFrame || uploadedProduct !== effProduct) && pos && col) {
                        glc.bindBuffer(glc.ARRAY_BUFFER, posBuf);
                        glc.bufferData(glc.ARRAY_BUFFER, pos, glc.STATIC_DRAW);
                        glc.bindBuffer(glc.ARRAY_BUFFER, colorBuf);
                        glc.bufferData(glc.ARRAY_BUFFER, col, glc.STATIC_DRAW);
                        uploadedFrame = currentFrame;
                        uploadedProduct = effProduct;
                    }
                    if (!cnt) return; // this product has nothing to draw on this frame

                    const matrix = (args && args.defaultProjectionData && args.defaultProjectionData.mainMatrix)
                        || (args && args.modelViewProjectionMatrix) || args;
                    glc.useProgram(program);
                    glc.uniformMatrix4fv(uMatrix, false, matrix);
                    glc.uniform1f(uOpacity, opacity);
                    glc.bindBuffer(glc.ARRAY_BUFFER, posBuf);
                    glc.enableVertexAttribArray(aPos);
                    glc.vertexAttribPointer(aPos, 2, glc.FLOAT, false, 0, 0);
                    glc.bindBuffer(glc.ARRAY_BUFFER, colorBuf);
                    glc.enableVertexAttribArray(aColor);
                    glc.vertexAttribPointer(aColor, 4, glc.UNSIGNED_BYTE, true, 0, 0);
                    glc.enable(glc.BLEND);
                    glc.blendFunc(glc.SRC_ALPHA, glc.ONE_MINUS_SRC_ALPHA);
                    glc.drawArrays(glc.TRIANGLES, 0, cnt);
                    glc.disableVertexAttribArray(aPos);
                    glc.disableVertexAttribArray(aColor);
                    glc.bindBuffer(glc.ARRAY_BUFFER, null);
                    noteRenderOk();
                } catch (e) {
                    noteRenderIssue((e && e.message ? e.message : String(e)), true);
                }
            },
            onRemove: function (map, glc) {
                if (posBuf) glc.deleteBuffer(posBuf);
                if (colorBuf) glc.deleteBuffer(colorBuf);
                if (program) glc.deleteProgram(program);
                posBuf = colorBuf = program = null;
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
    function removeLayer(map) {
        if (map.getLayer(LAYER_ID)) { map.removeLayer(LAYER_ID); hostLog('layer removed'); }
    }
    function addLayer(map) {
        if (currentFrame < 0) return;
        removeLayer(map);
        uploadedFrame = -1;
        map.addLayer(makeCustomLayer(), beforeId(map));
        hostLog('layer added before=' + beforeId(map) + ' cf=' + currentFrame);
    }
    // Ensures the current frame is on screen: repaint if the layer is up, else (re)add it.
    // This is the single place that guarantees a decoded current frame is never left blank.
    function showCurrent(map, reason) {
        if (currentFrame < 0 || !frames[currentFrame]) return;
        if (map.getLayer(LAYER_ID)) {
            map.triggerRepaint();
        } else {
            hostLog('showCurrent(' + reason + ') idx=' + currentFrame + ' -> re-add layer');
            addLayer(map);
        }
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
    function addRangeRing(map) {
        if (!map || !(currentRangeMeters > 0) || !Geo) return; // geo.js not loaded yet -> redraws on next setRangeRing
        if (map.getSource(RANGE_SRC)) map.getSource(RANGE_SRC).setData(ringGeoJSON());
        else map.addSource(RANGE_SRC, { type: 'geojson', data: ringGeoJSON() });
        if (!map.getLayer(RANGE_LAYER)) {
            map.addLayer({
                id: RANGE_LAYER, type: 'line', source: RANGE_SRC,
                paint: { 'line-color': '#9fe0ff', 'line-width': 1.3, 'line-opacity': 0.55, 'line-blur': 0.3 },
            }, beforeId(map));
        }
    }
    function removeRangeRing(map) {
        if (map && map.getLayer(RANGE_LAYER)) map.removeLayer(RANGE_LAYER);
        if (map && map.getSource(RANGE_SRC)) map.removeSource(RANGE_SRC);
    }
    // Draw/update the ring for the freshly-decoded range. Per-frame ranges are ~identical, so
    // only rebuild when it actually changes (or a layer is missing, e.g. after a re-add). The sweep
    // pulse owns its own layer (added on demand in pulseSweep), so nothing to ensure here.
    function setRangeRing(map, rangeMeters) {
        if (!map || !(rangeMeters > 0)) return;
        if (Math.abs(rangeMeters - currentRangeMeters) < 500 && map.getLayer(RANGE_LAYER)) {
            return;
        }
        currentRangeMeters = rangeMeters;
        addRangeRing(map);
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
    function ensureSweepLayer(map) {
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
    function clearSweepData(map) {
        const src = map && map.getSource(SWEEP_SRC);
        if (src) src.setData({ type: 'FeatureCollection', features: [] });
    }
    // Stop any in-flight pulse and drop its layers (site change / clear / DOW / turn-off).
    function stopSweep(map) {
        if (sweepRaf) { cancelAnimationFrame(sweepRaf); sweepRaf = 0; }
        if (map) {
            if (map.getLayer(SWEEP_ARM_LAYER)) map.removeLayer(SWEEP_ARM_LAYER);
            if (map.getLayer(SWEEP_FILL_LAYER)) map.removeLayer(SWEEP_FILL_LAYER);
            if (map.getSource(SWEEP_SRC)) map.removeSource(SWEEP_SRC);
        }
    }
    function sweepPulseFrame() {
        sweepRaf = 0;
        if (!currentMap || !(currentRangeMeters > 0) || !Geo) return; // nothing to draw (e.g. layer dropped)
        const el = performance.now() - sweepAnimStart;
        if (el >= SWEEP_MS + SWEEP_FADE_MS) { clearSweepData(currentMap); return; } // revolution done → hide arm
        let lead, fade;
        if (el < SWEEP_MS) { lead = (el / SWEEP_MS) * 2 * Math.PI; fade = 1; }       // sweeping 0→360°
        else { lead = 2 * Math.PI; fade = 1 - (el - SWEEP_MS) / SWEEP_FADE_MS; }     // hold at north, fade the trail out
        const src = currentMap.getSource(SWEEP_SRC);
        if (src) src.setData(sweepWedgeGeoJSON(lead, fade));
        sweepRaf = requestAnimationFrame(sweepPulseFrame);
    }
    // Fire ONE sweep pulse (host calls this when a genuinely-new frame lands). Restarts if one is
    // already mid-flight. No-op until a frame has decoded (no radius to sweep yet).
    function startSweepPulse(map) {
        currentMap = map;
        if (!(currentRangeMeters > 0) || !Geo) return;
        ensureSweepLayer(map);
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
            + ' prod=' + product + ' want=' + (wantedIds && wantedIds.length ? wantedIds.join('+') : '-')
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
        if (wantGrids && f0 && f0.built && f0.built[product] && !activeGridReady(f0) && !needsBuild(f0)) {
            decodeTrace(index, reason, 'grid-only', wantedIds, null);
            decodeGridForFrame(url, index, product);
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
        if (hit && wantedBuiltIn(hit) && (!wantGrids || hit.gridsBuilt || (hit.gridsExtra && hit.gridsExtra[product]))) {
            decodeTrace(index, reason, 'cache-hit', wantedIds, null);
            applyFrameResult(Object.assign({}, hit, { token: myToken, index: index, cached: true }));
            return;
        }
        // A full decode is about to run. If there WAS a cache entry, say why it couldn't serve us — the key
        // signal for "why did a frame we already loaded re-decode?" (unbuilt product vs missing inspector grid).
        var miss = !hit ? 'no-entry'
            : !wantedBuiltIn(hit) ? ('unbuilt:' + wantedIds.filter(function (id) { return !(hit.built && hit.built[id]); }).join(','))
                : 'no-grid';
        decodeTrace(index, reason, 'decode', buildIds, miss);
        fetch(url, { cache: 'no-store' }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.arrayBuffer();
        }).then(function (ab) {
            if (myToken !== loopToken) return;
            const w = getWorker();
            if (w) {
                w.postMessage({ ab: ab, siteLat: siteLat, siteLon: siteLon, minDbz: MIN_DBZ, token: myToken, index: index, url: url, buildProducts: buildIds, buildGrids: wantGrids, stormMotion: resolveStormMotion() }, [ab]);
            } else {
                import('./radar-decode.js').then(function (m) {
                    return m.decodeAndBuild(ab, siteLat, siteLon, MIN_DBZ, buildIds, wantGrids, resolveStormMotion());
                }).then(function (r2) {
                    applyFrameResult(frameResultFrom(r2, myToken, index, url));
                }).catch(function (err) {
                    applyFrameResult({ token: myToken, index: index, error: String(err && err.message ? err.message : err) });
                });
            }
        }).catch(function (err) {
            upgradeDone(index); // this path skips applyFrameResult — free the upgrade slot so the pump doesn't stall
            hostLog('frame ' + index + ' fetch failed: ' + (err && err.message ? err.message : err));
            post({ type: 'radarFrameReady', index: index, hasData: false });
        });
    }

    // Builds ONLY the active product's inspector value grid for a frame and merges it in (geometry left
    // alone) — the fast path behind turning Inspect on. Off-thread with a main-thread fallback, same as
    // decodeFrame. Runs under the upgrade queue's slot accounting (upgradeDone frees the slot).
    function decodeGridForFrame(url, index, prod) {
        const myToken = loopToken;
        fetch(url, { cache: 'no-store' }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.arrayBuffer();
        }).then(function (ab) {
            if (myToken !== loopToken) { upgradeDone(index); return; }
            const w = getWorker();
            if (w) {
                w.postMessage({ gridOnly: true, ab: ab, siteLat: siteLat, siteLon: siteLon, minDbz: MIN_DBZ, token: myToken, index: index, url: url, product: prod, stormMotion: resolveStormMotion() }, [ab]);
            } else {
                import('./radar-decode.js').then(function (m) {
                    return m.decodeGridOnly(ab, siteLat, siteLon, MIN_DBZ, prod, resolveStormMotion());
                }).then(function (r2) {
                    applyGridResult({ token: myToken, index: index, url: url, gridsOnly: true, gridProduct: prod, grids: r2.grids });
                }).catch(function (err) {
                    applyGridResult({ token: myToken, index: index, url: url, gridsOnly: true, error: String(err && err.message ? err.message : err) });
                });
            }
        }).catch(function (err) {
            upgradeDone(index);
            hostLog('grid-only ' + index + ' fetch failed: ' + (err && err.message ? err.message : err));
        });
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
    let inspectTip = null;          // the DOM tooltip element (lazily created)
    let inspectMove = null, inspectOut = null; // bound MapLibre handlers (so we can off() them)
    let lastInspectPush = 0, lastInspectHad = false; // host-push throttle state

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

    // The value grid for the current frame + active product, or null if not available.
    function inspectGrid() {
        const f = frames[currentFrame];
        return (f && f.grids && f.grids[product]) || null;
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
    function pushInspect(has, value) {
        const now = Date.now();
        if (has === lastInspectHad && now - lastInspectPush < 70) return;
        lastInspectPush = now; lastInspectHad = has;
        post({ type: 'radarInspect', has: has, value: has ? value : 0 });
    }

    function onInspectMove(e) {
        if (!inspecting) return;
        const r = lookupValue(inspectGrid(), e.lngLat.lat, e.lngLat.lng);
        const el = ensureInspectTip();
        if (r) {
            // Velocity reads in familiar speed units (mph · km/h) rather than the raw m/s; other
            // products keep their native unit (dBZ / unitless CC).
            const main = product === 'velocity'
                ? formatSpeed(r.value)
                : r.value.toFixed(r.digits) + (r.unit ? ' ' + r.unit : '');
            // On Velocity, show the SAME gate's dealiasing breakdown so the unfold can be checked
            // without re-hovering: the displayed value is the dealiased speed; the raw (folded)
            // value is what the radar measured (within ±Nyquist), recovered by removing the whole
            // 2×Nyquist folds the dealiaser added. Lets the user confirm high velocities at a glance.
            const vel = velocityFold(r.value);
            if (vel) {
                el.innerHTML = '<div>' + main + '</div>' +
                    '<div style="font-size:10px;opacity:.75;font-weight:400">raw ' + vel.raw.toFixed(0) +
                    ' · Nyq ' + vel.nyq.toFixed(0) + ' · ' + vel.foldLabel + '</div>';
            } else {
                el.textContent = main;
            }
            el.style.left = (e.point.x + 14) + 'px';
            el.style.top = (e.point.y + 14) + 'px';
            el.style.display = 'block';
            pushInspect(true, r.value);
        } else {
            el.style.display = 'none';
            pushInspect(false, 0);
        }
    }

    // Velocity in m/s → "±47 mph · ±76 km/h" (sign preserved: inbound negative, outbound positive).
    function formatSpeed(ms) {
        return (ms * 2.23694).toFixed(0) + ' mph · ' + (ms * 3.6).toFixed(0) + ' km/h';
    }

    // For a dealiased velocity value, recover the raw (folded) measurement + the fold count from the
    // current frame's Nyquist. Returns null when not on Velocity / no Nyquist (so other products show
    // just their value). Dealiasing only ever adds whole multiples of 2×Nyquist, so this is exact.
    function velocityFold(dealiased) {
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
    function onInspectOut() {
        if (inspectTip) inspectTip.style.display = 'none';
        pushInspect(false, 0);
    }

    window.RadarLayer = {
        beginLoop: function (map, lat, lon) {
            currentMap = map;
            attachContextListeners(map);
            // New site → drop the old range ring + sweep (the first decoded frame redraws them at
            // the new site's range); same site (a reload) → keep them up, no flicker.
            if (lat !== siteLat || lon !== siteLon) {
                removeRangeRing(map); stopSweep(map); currentRangeMeters = 0;
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
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            uploadedFrame = -1;
            renderErrCount = blankCount = 0; lastRenderErrAt = lastBlankAt = 0;
            removeLayer(map);
            hostLog('beginLoop token=' + loopToken + ' @ ' + lat.toFixed(3) + ',' + lon.toFixed(3));
        },
        addFrame: function (map, url, index) {
            currentMap = map;
            hostLog('addFrame idx=' + index);
            decodeFrame(url, index, 'load');
        },
        showFrame: function (map, index) {
            currentMap = map;
            if (frames[index]) {
                // Decoded: switch to it now (and (re)add the layer if needed).
                pendingFrame = -1;
                if (index !== currentFrame) { currentFrame = index; uploadedFrame = -1; }
                // Flag a scrub onto a frame whose ACTIVE product isn't built — it renders blank/stale until an
                // upgrade fills it (the "missing frame while scrubbing" symptom). Reflectivity is always built.
                if (product !== 'reflectivity' && !(frames[index].built && frames[index].built[product]))
                    hostLog('showFrame idx=' + index + ' NOT-BUILT prod=' + product + ' (blank until upgrade)');
                showCurrent(map, 'showFrame');
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
        remap: function (map, newCount, mappingJson) {
            currentMap = map;
            var mapping;
            try { mapping = JSON.parse(mappingJson); } catch (e) { hostLog('remap parse failed: ' + (e && e.message ? e.message : e)); return; }
            // New loop generation: drop any in-flight decode still targeting an OLD index.
            loopToken++;
            resetUpgrades(); // stale upgrades target old indices; re-queued below against the new frames[]
            var oldCurrent = currentFrame, oldUploaded = uploadedFrame;
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
                uploadedFrame = (oldUploaded === oldCurrent) ? newCurrent : -1;
                if (map.getLayer(LAYER_ID)) map.triggerRepaint(); else addLayer(map);
            } else {
                // The displayed frame fell out of the window: fall back to the newest decoded frame.
                var nn = -1;
                for (var j = newCount - 1; j >= 0; j--) { if (frames[j]) { nn = j; break; } }
                currentFrame = nn;
                uploadedFrame = -1;
                showCurrent(map, 'remap-fallback');
            }
            queueAllUpgrades('remap'); // a reused refl-only frame still needs velocity if Velocity is active
            postBuildProgress(); // re-report readiness/completeness against the NEW indexing (host arrays reindex)
            hostLog('remap newCount=' + newCount + ' cf=' + currentFrame + ' reused=' + frames.filter(Boolean).length + ' token=' + loopToken);
        },
        clear: function (map) {
            loopToken++;
            resetUpgrades();
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            removeLayer(map);
            removeRangeRing(map);
            stopSweep(map);
            currentRangeMeters = 0;
            postBuildProgress(); // frames=[] -> 0/0 clears the "building" readout
            hostLog('clear token=' + loopToken);
        },
        // DOW Event Viewer: show a single curated mobile-radar frame (the "dow-frame/1" JSON from
        // tools/dow_import.py, served from the dowevents host). It takes over the radar layer as a
        // one-frame loop centred on the TRUCK's position — reusing the whole render path (WebGL fill,
        // the real-extent range ring, product toggle, Inspect, legend). No loop/live/sweep machinery.
        showDow: function (map, url) {
            currentMap = map;
            attachContextListeners(map);
            loopToken++;
            resetUpgrades();
            const myToken = loopToken;
            frames = [];
            currentFrame = -1;
            pendingFrame = -1;
            uploadedFrame = -1;
            renderErrCount = blankCount = 0; lastRenderErrAt = lastBlankAt = 0;
            removeLayer(map);
            removeRangeRing(map);
            stopSweep(map);
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
                try { map.flyTo({ center: [siteLon, siteLat], zoom: 9, duration: 800 }); } catch (e) { /* non-fatal */ }
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
        setOpacity: function (map, op) {
            opacity = op;
            if (map.getLayer(LAYER_ID)) map.triggerRepaint();
        },
        // Fire ONE sweep pulse (arm + trailing afterglow, one revolution then hides). The host calls
        // this when a genuinely-new frame lands. No-op until a frame has decoded (no radius yet).
        pulseSweep: function (map) {
            startSweepPulse(map);
        },
        // Stop + remove the sweep (host calls with period <= 0 on clear / entering replay). Kept the
        // name for the existing host shim; the arm is one-shot now, so a positive period just re-pulses.
        setSweep: function (map, periodSeconds) {
            currentMap = map;
            if (Number(periodSeconds) > 0) startSweepPulse(map);
            else stopSweep(map);
        },
        // Switch rendered moment ('reflectivity' | 'velocity' | 'cc'). Reflectivity + CC geometry is
        // always built, so those switch instantly. Velocity is built lazily (it's the one product that
        // must dealias), so switching TO Velocity queues the loaded refl-only frames for a BOUNDED,
        // current-frame-first re-decode (see the upgrade queue up top) — velocity fills in around the
        // frame on screen instead of flooding the decode pool and flashing blanks during playback.
        setProduct: function (map, p) {
            if (!productKnown(p) || p === product) return;
            var from = product;
            product = p;
            uploadedFrame = -1; // force the new product's geometry to upload on the next render
            // How many frames already have the new product built — i.e. how much of this switch is INSTANT vs
            // needs a re-decode. A switch that should be instant (all built) but still decodes is a bug signal.
            var have = 0, n = frames.length;
            for (var i = 0; i < n; i++) if (p === 'reflectivity' || (frames[i] && frames[i].built && frames[i].built[p])) have++;
            hostLog('product ' + from + '->' + p + ' built=' + have + '/' + n + (have < n ? ' (will decode ' + (n - have) + ')' : ' (instant)'));
            queueAllUpgrades('switch>' + p); // no-op unless Velocity/SRV (or Inspect) needs geometry these frames lack
            postBuildProgress(); // switching to Velocity: report the (mostly not-yet-built) ready set now
            if (map && map.getLayer(LAYER_ID)) map.triggerRepaint();
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
        // Set the storm motion MODE for Storm-Relative Velocity: `auto` (default true) means each volume's
        // motion is derived from its full-volume VAD wind profile (computeStormMotion below, driven by the
        // host); when false, speedKt/dirDeg (KNOTS, direction the storm is MOVING TOWARD) are used verbatim.
        // SRV = base velocity − the resolved motion's component along each beam, applied per frame in the
        // worker (buildSrv). Changing the mode or manual value invalidates every loaded/cached frame's SRV
        // geometry so it rebuilds (a full re-decode via the upgrade queue — SRV rides velocity's dealiased
        // cut); other products are untouched. Only re-queues while SRV is the active product; otherwise the new
        // motion applies the next time SRV is built. Both the live frames and the decoded-frame cache are
        // invalidated so a cache hit can't serve stale-motion SRV.
        setStormMotion: function (map, speedKt, dirDeg, auto) {
            stormMotion = { speedMs: (+speedKt || 0) * 0.514444, dirDeg: (+dirDeg || 0), auto: auto !== false };
            if (auto === false) { _autoMotion = null; _autoMotionKey = ''; } // leaving auto: forget the loop motion
            dropAllSrvAndRequeue();
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
            currentMap = map;
            if (currentFrame >= 0) addLayer(map);
            addRangeRing(map);
            if (sweepRaf) ensureSweepLayer(map);
        },
        // Toggle inspect mode (read the value under the cursor). Attaches/detaches the mousemove
        // handlers + crosshair cursor and hides the tooltip / clears the host marker when off.
        setInspect: function (map, on) {
            currentMap = map;
            inspecting = !!on;
            const canvas = map.getCanvas && map.getCanvas();
            if (inspecting) {
                if (!inspectMove) { inspectMove = onInspectMove; map.on('mousemove', inspectMove); }
                if (!inspectOut) { inspectOut = onInspectOut; map.on('mouseout', inspectOut); }
                if (canvas) canvas.style.cursor = 'crosshair';
                // Value grids are skipped by default (memory). Turning Inspect ON now builds them on
                // demand for the loaded frames via the bounded, current-frame-first upgrade queue — so
                // lookups become available around the frame on screen first, without flooding the pool.
                queueAllUpgrades('inspect');
            } else {
                if (inspectMove) { map.off('mousemove', inspectMove); inspectMove = null; }
                if (inspectOut) { map.off('mouseout', inspectOut); inspectOut = null; }
                if (inspectTip) inspectTip.style.display = 'none';
                if (canvas) canvas.style.cursor = '';
                pushInspect(false, 0);
            }
            hostLog('inspect=' + inspecting);
        },
    };

    // ---- Dev-only velocity-dealias validation (fixed-corpus regression scorer) ----
    // Replays each committed corpus .V06 through the REAL decode/dealias path (decodeAndBuild ->
    // dealiasSweep) and records its over-unfold ratio (hi/total = |v|>55 m/s gates, the same field
    // the diagnostics call "dealias hi"), so a dealias change can be regressed offline against a
    // fixed baseline — same bytes in, same ratio out (dealiasSweepCore is deterministic). Fully
    // ISOLATED from the live loop: it touches no frames[]/layer/token state, so a run is safe over a
    // live map and can't perturb what's on screen. The host starts it, then polls
    // window.__anvilValidation until `finished` (the async decode's Promise can't be awaited through
    // ExecuteScriptAsync). entries = [{ id, url, lat, lon }]. See RadarValidationViewModel /
    // docs/radar-validation.md.
    window.radarValidate = function (entriesJson) {
        var entries;
        try { entries = JSON.parse(entriesJson); } catch (e) { entries = []; }
        if (!Array.isArray(entries)) entries = [];
        // Progress global the host polls; reset on every run.
        var state = { total: entries.length, done: 0, finished: false, cancel: false, results: [] };
        window.__anvilValidation = state;
        hostLog('validate start n=' + entries.length);

        import('./radar-decode.js').then(function (m) {
            // Volumes are scored one at a time: _dealiasInfo (the source of hi/total) is a decoder
            // global set during each build, so decodes must NOT overlap. Yielding between volumes lets
            // the host poll see progress and keeps the UI from wedging through the whole corpus.
            function step(i) {
                if (i >= entries.length || state.cancel) {
                    state.finished = true;
                    hostLog('validate done ' + state.done + '/' + state.total + (state.cancel ? ' (cancelled)' : ''));
                    return;
                }
                var e = entries[i] || {};
                fetch(e.url, { cache: 'no-store' }).then(function (r) {
                    if (!r.ok) throw new Error('HTTP ' + r.status);
                    return r.arrayBuffer();
                }).then(function (ab) {
                    // Force the velocity build so dealias runs; grids off (only the ratio matters). Pass the
                    // velocity id explicitly (not `true`) so SRV isn't also built — the scorer only reads
                    // velocity's dealias ratio.
                    return m.decodeAndBuild(ab, e.lat || 0, e.lon || 0, MIN_DBZ, ['velocity'], false);
                }).then(function (res) {
                    var hi = 0, tot = 0;
                    var mm = /hi=(\d+)\/(\d+)/.exec((res && res.dealias) || '');
                    if (mm) { hi = +mm[1]; tot = +mm[2]; }
                    var ratio = tot > 0 ? hi / tot : 0;
                    state.results.push({
                        id: e.id, gatesOver: hi, gatesTotal: tot, ratio: ratio,
                        error: (res && res.dealias) ? null : 'no velocity',
                    });
                    // Log the FULL dealias detail (numReg + dealiased v-range), not just hi/tot: comparing
                    // the JS numReg + range to the offline Py-ART mirror (tools/dealias_check.py) isolates
                    // whether an over-unfold anomaly is in the DECODE (raw field differs) or the REDUCE (a
                    // region lands on a wrong fold) — how the KBUF 31% seed was chased (docs/radar-validation.md).
                    hostLog('validate ' + e.id + ' (' + (ratio * 100).toFixed(1) + '%) ' + ((res && res.dealias) || ''));
                }).catch(function (err) {
                    var msg = String((err && err.message) ? err.message : err);
                    state.results.push({ id: e.id, gatesOver: 0, gatesTotal: 0, ratio: 0, error: msg });
                    hostLog('validate ' + e.id + ' error: ' + msg);
                }).then(function () {
                    state.done++;
                    setTimeout(function () { step(i + 1); }, 0);
                });
            }
            step(0);
        }).catch(function (err) {
            state.finished = true;
            hostLog('validate load failed: ' + ((err && err.message) ? err.message : err));
        });
    };
})();
