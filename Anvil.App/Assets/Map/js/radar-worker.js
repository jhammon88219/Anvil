// Off-main-thread Level II decode. Fetches the volume ITSELF (given a same-origin url) — or accepts a
// pre-fetched ArrayBuffer — then decodes + builds gate geometry via the shared radar-decode module and
// transfers the typed arrays back so the bzip2 decode never freezes the UI. Doing the ~7 MB body read
// HERE (not on the caller's thread) keeps it off the map's render thread, so a backfill of N frames no
// longer hitches pan/zoom. Classic worker using dynamic import().
//
//   MAIN THREAD (radar.js)                    WORKERS (this file)
//   ──────────────────────                    ───────────────────
//   map render, pan/zoom, GL upload   ──url──►  pool of min(4, cores−1)   ─┐ fetch → bzip2 → gates
//   (must never block — a hitch here            N fetches in flight,       │ → colors, then the
//    is visible as a stutter)        ◄─arrays─  ONE decode at a time     ─┘ typed arrays come back
//                                               ZERO-COPY (transferred)
//                                    ──urls──►  a DEDICATED vwp worker    ── the ~8-tilt storm-motion
//                                    ◄─motion─  ────────────────────────     job, so it never queues
//                                                                            behind frame decodes
//
//   The URL is posted, not the bytes: the ~7 MB body read happens HERE. Workers are pre-warmed at
//   map-ready (each eagerly imports the decoder) so the first site click doesn't pay the cold start.
//
// ⚠️ A WORKER RUNS ONE DECODE AT A TIME, AND THAT IS A CORRECTNESS REQUIREMENT, NOT A THROTTLE.
// radar.js hands work out by plain round-robin with no busy tracking (getWorker), so a 12-wide replay
// backfill over a pool of 4 puts ~3 jobs on each worker at once. radar-decode.js keeps per-decode state
// at MODULE level (the dealias memo _sharedDealiased, plus _dealiasInfo / _decodeSeedProfile /
// _decodeVadProfile / _stormMotion) and clears it SYNCHRONOUSLY at the top of decodeAndBuild while the
// work itself happens in a .then() continuation. So with two jobs interleaved the order is:
//   B clears the memo ─► A's body runs, FILLS the memo ─► B's body runs, finds it non-null
// and frame B renders frame A's dealiased Doppler field. Measured 2026-09-03 on a 26-frame El Reno
// replay: frames 6, 8 and 10 came back with byte-identical dealias signatures and velTris to frames
// 3, 5 and 2 — each pair two consecutive jobs on the SAME worker. It does not self-heal either: the
// later dual-pol re-decode reports the frame's own field, but mergeFrameResult keeps geometry that is
// already marked built, so the borrowed velocity survives for the life of the loop.
// The fix below serializes the DECODE CALL per worker (fetches still overlap, so throughput is
// unchanged — the bodies were always synchronous and already monopolized the worker thread; all that
// was missing was keeping each reset next to its own body).
// ⚠️ The deeper fix is to stop keeping per-decode state in module scope at all — thread it through as a
// context object — which touches every builder signature in radar-decode.js. Until that happens, DO NOT
// remove this queue, and DO NOT start a decode anywhere outside it.

// Resolve the volume bytes: use a pre-supplied ArrayBuffer if the caller transferred one (kept for any
// one-off caller), else fetch the same-origin url (the normal loop/upgrade path).
function loadAb(d) {
    if (d.ab) return Promise.resolve(d.ab);
    return fetch(d.url, { cache: 'no-store' }).then(function (r) {
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.arrayBuffer();
    });
}
// Same, for the VWP path's several tilt volumes: pre-supplied `buffers`, else fetch each of `urls`.
function loadBuffers(d) {
    if (d.buffers) return Promise.resolve(d.buffers);
    return Promise.all((d.urls || []).map(function (u) {
        return fetch(u, { cache: 'no-store' }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.arrayBuffer();
        });
    }));
}

// Kick the decoder module load as soon as this worker STARTS, not lazily on its first message — the vendored
// decoder is a few MB, so a cold worker otherwise pays that import on the first decode. Pre-warming the pool
// (radar.js prewarmRadarWorkers, at map-ready) creates the workers ahead of the first site click, so this
// load finishes off the first-paint critical path. Dynamic import caches, so every branch shares this promise.
var _decoder = import('./radar-decode.js');
function decoder() { return _decoder; }

// The one-decode-at-a-time queue (see the ⚠️ note in the header for what breaks without it). Every call
// into radar-decode.js goes through here, so a decode's module-level state can never be clobbered by the
// next job's setup. Only the DECODE is queued — loadAb/loadBuffers run before it, so the fetches of the
// jobs waiting behind this one are already in flight by the time their turn comes.
var _decodeQueue = Promise.resolve();
function queueDecode(run) {
    const started = _decodeQueue.then(run);
    // ⚠️ The chain must never carry a rejection forward, or one failed volume wedges every later decode in
    // this worker. The tail swallows it; the caller still sees the real result via `started`.
    _decodeQueue = started.then(function () { }, function () { });
    return started;
}

self.onmessage = function (e) {
    const d = e.data;
    // Full-volume VWP storm motion (radar.js computeStormMotionForVolume): decode a volume's bottom velocity
    // tilts, build a merged VAD wind profile, and reduce to a Bunkers storm motion — the tiny result comes
    // back (no geometry). See radar-decode decodeVwp; runs off-thread because the per-cut dealias is the cost.
    if (d.vwp) {
        loadBuffers(d).then(function (buffers) {
            return decoder().then(function (m) {
                return queueDecode(function () { return m.decodeVwp(buffers); });
            });
        }).then(function (motion) {
            self.postMessage({ vwp: true, reqId: d.reqId, motion: motion });
        }).catch(function (err) {
            self.postMessage({ vwp: true, reqId: d.reqId, error: String(err && err.message ? err.message : err) });
        });
        return;
    }
    // Grids-only inspector build (radar.js decodeGridForFrame): decode just ONE product's value grid and
    // transfer it back for the host to merge into the existing frame — no full re-decode. See decodeGridOnly.
    if (d.gridOnly) {
        loadAb(d).then(function (ab) {
            return decoder().then(function (m) {
                return queueDecode(function () {
                    return m.decodeGridOnly(ab, d.siteLat, d.siteLon, d.minDbz, d.product, d.stormMotion, d.seedProfile);
                });
            });
        }).then(function (res) {
            const msg = { token: d.token, index: d.index, url: d.url, gridsOnly: true, gridProduct: d.product, grids: {} };
            const transfer = [];
            const gr = res.grids[d.product];
            if (gr && gr.az && gr.values) { msg.grids[d.product] = gr; transfer.push(gr.az.buffer, gr.values.buffer); }
            else { msg.grids[d.product] = null; }
            self.postMessage(msg, transfer);
        }).catch(function (err) {
            self.postMessage({ token: d.token, index: d.index, url: d.url, gridsOnly: true, error: String(err && err.message ? err.message : err) });
        });
        return;
    }
    loadAb(d).then(function (ab) {
        return decoder().then(function (m) {
            return queueDecode(function () {
                return m.decodeAndBuild(ab, d.siteLat, d.siteLon, d.minDbz, d.buildProducts, d.buildGrids, d.stormMotion, d.seedProfile);
            });
        });
    }).then(function (res) {
        // Product geometry + inspector grids are keyed by product id (radar-products.js); we forward them
        // as maps, transferring each product's typed arrays zero-copy. Adding a product needs no change here.
        const msg = {
            token: d.token, index: d.index, url: d.url, built: res.built, gridsBuilt: res.gridsBuilt,
            decodeMs: res.decodeMs, buildMs: res.buildMs,
            radials: res.radials, gates: res.gates, bytes: res.bytes, rangeMeters: res.rangeMeters,
            elevList: res.elevList, velElev: res.velElev, reflStats: res.reflStats, velStats: res.velStats,
            velNyq: res.velNyq, dealias: res.dealias, seedProfile: res.seedProfile,
            moments: {}, grids: {},
        };
        const transfer = [];
        let any = false;
        Object.keys(res.moments).forEach(function (id) {
            const g = res.moments[id];
            if (g) { msg.moments[id] = { positions: g.positions, colors: g.colors, count: g.count }; transfer.push(g.positions.buffer, g.colors.buffer); any = true; }
            else { msg.moments[id] = null; }
        });
        if (!any) msg.empty = true;
        // Inspector value grids (radar-decode buildGrid): each carries az + an Int16 value array; only
        // present when Inspect was on. Forward zero-copy so the host reads values without re-decoding.
        Object.keys(res.grids).forEach(function (id) {
            const gr = res.grids[id];
            if (gr && gr.az && gr.values) { msg.grids[id] = gr; transfer.push(gr.az.buffer, gr.values.buffer); }
            else { msg.grids[id] = null; }
        });
        self.postMessage(msg, transfer); // zero-copy transfer of whichever geometries exist
    }).catch(function (err) {
        self.postMessage({ token: d.token, index: d.index, url: d.url, error: String(err && err.message ? err.message : err) });
    });
};
