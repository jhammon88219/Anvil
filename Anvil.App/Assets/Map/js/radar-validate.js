// Dev-only velocity-dealias validation (fixed-corpus regression scorer).
//
//   corpus .V06 ──► the REAL decode/dealias path ──► over-unfold ratio ──► vs the committed baseline
//   (committed,      (decodeAndBuild → dealiasSweep,   hi/total, i.e.        Worse / Same / Better
//    fixed bytes)     nothing stubbed)                 |v| > 55 m/s gates    → ValidationReportDialog
//
//   Same bytes in, same ratio out — dealiasSweepCore is deterministic, so the ONLY variable between
//   two runs is the dealias code itself. That is the whole point of the tool.
//
//   ⚠️ For a data-structure change to this gate-for-gate-validated code, "not worse" is NOT the bar:
//   every Δ must be 0.0. A LOWER ratio hides a behavior change just as effectively as a higher one.
//
// Replays each committed corpus .V06 through the REAL decode/dealias path (decodeAndBuild ->
// dealiasSweep) and records its over-unfold ratio (hi/total = |v|>55 m/s gates, the same field the
// diagnostics call "dealias hi"), so a dealias change can be regressed offline against a fixed
// baseline — same bytes in, same ratio out (dealiasSweepCore is deterministic).
//
// Fully ISOLATED from the live loop: it touches no frames[]/layer/token state, so a run is safe over
// a live map and can't perturb what's on screen. That isolation is why it lives here rather than in
// radar.js — its only ties to the loop are the two values radar.js passes in as `ctx`. It is also
// off the startup path: radar.js imports this module the first time the host actually starts a run,
// which in a Release build is never.
//
// See RadarValidationViewModel / docs/radar-validation.md.

// Decoded value range of one product's inspector grid: dequantize the Int16 values (÷scale),
// skip GRID_NODATA (-32768), return {n,min,max,mean}. null if the product wasn't built/decoded.
function gridStats(gr) {
    if (!gr || !gr.values) return null;
    var v = gr.values, sc = gr.scale || 1, n = 0, mn = Infinity, mx = -Infinity, sum = 0;
    for (var k = 0; k < v.length; k++) {
        if (v[k] === -32768) continue;
        var x = v[k] / sc;
        n++; sum += x; if (x < mn) mn = x; if (x > mx) mx = x;
    }
    return n ? { n: n, min: mn, max: mx, mean: sum / n } : null;
}

// Score one corpus run.
//
// `state` is created by the CALLER and mutated in place — never reassigned. That is load-bearing:
// the host publishes it as window.__anvilValidation the moment it starts a run and then both polls
// it AND cancels through it (CancelRadarValidationAsync sets state.cancel), so swapping in a fresh
// object here would silently drop a cancel that arrived while this module was still loading.
//
// entries = [{ id, url, lat, lon }]. ctx = { hostLog, minDbz }.
export function runValidation(entriesJson, state, ctx) {
    var hostLog = ctx.hostLog, minDbz = ctx.minDbz;

    var entries;
    try { entries = JSON.parse(entriesJson); } catch (e) { entries = []; }
    if (!Array.isArray(entries)) entries = [];
    state.total = entries.length;
    hostLog('validate start n=' + entries.length);

    return import('./radar-decode.js').then(function (m) {
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
                // TWO decodes of the same bytes, SEQUENTIAL (never overlapping — _dealiasInfo is a decoder
                // global): (1) UNSEEDED — the baseline-scored over-unfold ratio + the per-product grids
                // for the dual-pol decoder check; (2) SELF-SEEDED — re-decode using this volume's OWN VAD
                // profile (res1.seedProfile, returned by the first decode) as the temporal first guess,
                // proving the seed fixes the over-unfold WITHOUT needing a live loop. The unseeded run
                // stays the regression guard; the seeded run is the new "did the fix work?" metric.
                return m.decodeAndBuild(ab, e.lat || 0, e.lon || 0, minDbz, true, true).then(function (res1) {
                    return m.decodeAndBuild(ab, e.lat || 0, e.lon || 0, minDbz, true, true, undefined, res1.seedProfile)
                        .then(function (res2) { return { res1: res1, res2: res2 }; });
                });
            }).then(function (pair) {
                var res = pair.res1, res2 = pair.res2;
                var hi = 0, tot = 0;
                var mm = /hi=(\d+)\/(\d+)/.exec((res && res.dealias) || '');
                if (mm) { hi = +mm[1]; tot = +mm[2]; }
                var ratio = tot > 0 ? hi / tot : 0;
                // Seeded pass: over-unfold ratio + the global fold shift the seed chose (seed=N marker).
                var hi2 = 0, tot2 = 0, seedShift = null;
                var m2 = /hi=(\d+)\/(\d+)/.exec((res2 && res2.dealias) || '');
                if (m2) { hi2 = +m2[1]; tot2 = +m2[2]; }
                var sm = /seed=(-?\d+)/.exec((res2 && res2.dealias) || '');
                if (sm) seedShift = +sm[1];
                var seededRatio = tot2 > 0 ? hi2 / tot2 : 0;
                // Per-product decoded stats (dequantized grid: n / min..max / mean, GRID_NODATA skipped):
                // the human log line AND the machine `dp` means the dual-pol regression guard scores. The
                // guard (C# RadarValidationReport.DualPolDrift) checks cc/zdr/sw MEAN vs the manifest
                // baseline — a decoder scale/offset regression shifts the mean (docs/radar-validation.md).
                var order = ['reflectivity', 'velocity', 'srv', 'cc', 'zdr', 'kdp', 'sw'];
                var parts = [], dp = {};
                for (var pi = 0; pi < order.length; pi++) {
                    var id = order[pi];
                    var st = gridStats(res && res.grids && res.grids[id]);
                    if (!st) continue;
                    parts.push(id + '[n=' + st.n + ' ' + st.min.toFixed(2) + '..' + st.max.toFixed(2) + ' m=' + st.mean.toFixed(2) + ']');
                    if (id === 'cc' || id === 'zdr' || id === 'sw') dp[id] = st.mean;
                }
                state.results.push({
                    id: e.id, gatesOver: hi, gatesTotal: tot, ratio: ratio,
                    seededGatesOver: hi2, seededGatesTotal: tot2, seededRatio: seededRatio, seedShift: seedShift,
                    error: (res && res.dealias) ? null : 'no velocity', dp: dp,
                });
                // Dealias detail (numReg + v-range) — see the KBUF chase (docs/radar-validation.md).
                hostLog('validate ' + e.id + ' (' + (ratio * 100).toFixed(1) + '% → seed ' +
                    (seededRatio * 100).toFixed(1) + '% shift=' + seedShift + ') ' + ((res && res.dealias) || ''));
                if (parts.length) hostLog('validate ' + e.id + ' dp ' + parts.join(' '));
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
    });
}
