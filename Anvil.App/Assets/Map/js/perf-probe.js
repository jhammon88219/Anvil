// perf-probe.js — DEV-ONLY pan/zoom frame-time sampler. Measures how smooth a camera move actually is,
// so a rendering change can be shown to have helped instead of argued about.
//
// Why it exists: nothing in the app measured SMOOTHNESS. Retained memory, decode times and load timings
// are all in the diagnostics JSONL already; the cost of per-frame work during a drag was the one number
// with no source. It was added to give the "detach hidden radar-site markers" change a before/after.
//
//   movestart ──────────────── drag ──────────────── moveend
//        │                                               │
//        ▼                                               ▼
//    sampler ON    ▏▏▏▎▏▏█▏▏▏▏▎▏▏▏▏█▏▏▏▎▏▏          emit one perfPan sample
//                  └─ each bar = one requestAnimationFrame interval, in ms
//                     █ = a LONG frame (> LONG_MS): a visible hitch
//
// What lands in the log is p50 / p95 / max / long-frame count for that one gesture — p95 and `long` are
// the interesting pair, because a drag that is smooth apart from four 90 ms stalls has a fine average
// and feels broken.
//
// ⚠️ OFF UNLESS `?perf=1` IS IN THE PAGE URL, and MainWindow only appends that in a DEBUG build. Release
// never imports this file. Keep it that way: this is a measuring tool, not a feature.
// ⚠️ IT MUST NOT PERTURB WHAT IT MEASURES — one timestamp and one array push per frame, and the maths
// runs on moveend, never mid-gesture. Do not add per-frame work here.
// ⚠️ ONE SAMPLER FOR THE WHOLE PAGE, refcounted across panes. requestAnimationFrame is per-DOCUMENT, not
// per-map, so four panes moving under the camera sync are still one stream of frames — attaching a
// sampler per map would multiply-count the same frames and report a quarter of the true interval.
// ⚠️ EXCISABLE BY DESIGN: this file plus its `perf` URL param, its map.js import block and the router's
// "perfPan" handler are the whole feature. Grep `perfPan` to find every piece.

const LONG_MS = 32;      // ~2 dropped frames at 60 Hz — the threshold where a hitch becomes visible
const MIN_SAMPLES = 8;   // ignore a flick too short to say anything about; avoids noise lines in the log
const MAX_SAMPLES = 3000; // a ~50 s continuous drag; bounds the array on a pathological gesture
// ⚠️ DEADMAN. The refcount below assumes every movestart/zoomstart is answered by its end event, and
// MapLibre does guarantee that — but not if the pane is DESTROYED mid-gesture (a layout change while
// dragging calls map.remove(), and the pending moveend never arrives). A leaked refcount leaves the
// sampler running forever, and every later gesture then reports one giant sample full of idle frames.
// A measuring tool that fails silently and wrong is worse than one that fails loudly, so cap it.
const MAX_GESTURE_MS = 30000;

let active = 0;          // panes currently mid-move (refcount — the camera sync moves them together)
let rafHandle = 0;
let last = 0;
let samples = [];
let startedAt = 0;
let context = null;      // () => extra fields describing WHAT was on screen for this gesture

// One rAF hop: record the interval since the previous frame, then queue the next.
function tick(now) {
    if (last > 0 && samples.length < MAX_SAMPLES) samples.push(now - last);
    last = now;
    // Deadman (see MAX_GESTURE_MS): a gesture this long means an end event was lost, not that someone
    // dragged for half a minute. Emit what we have and stand down rather than sampling forever.
    if (now - startedAt > MAX_GESTURE_MS) { active = 0; finish(); return; }
    rafHandle = requestAnimationFrame(tick);
}

function quantile(sorted, q) {
    if (!sorted.length) return 0;
    const i = Math.min(sorted.length - 1, Math.max(0, Math.round((sorted.length - 1) * q)));
    return sorted[i];
}

function post(msg) {
    try {
        if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(msg));
    } catch (e) { /* a measurement must never be able to break the map */ }
}

function begin() {
    if (active++ > 0) return;   // another pane is already driving the sampler
    samples = [];
    last = 0;
    startedAt = performance.now();
    rafHandle = requestAnimationFrame(tick);
}

function end() {
    if (active > 0) active--;
    if (active > 0) return;     // a sibling pane is still moving — keep sampling
    finish();
}

// Stop sampling and emit, if the gesture was long enough to mean anything. Two callers: the last
// moveend/zoomend, and the deadman in tick().
function finish() {
    if (!rafHandle) return;     // already finished (both an end event and the deadman can land here)
    cancelAnimationFrame(rafHandle);
    rafHandle = 0;

    const n = samples.length;
    if (n < MIN_SAMPLES) { samples = []; return; }

    const sorted = samples.slice().sort(function (a, b) { return a - b; });
    let long = 0;
    for (let i = 0; i < n; i++) { if (samples[i] > LONG_MS) long++; }

    const round = function (v) { return Math.round(v * 10) / 10; };
    const msg = {
        type: 'perfPan',
        n: n,
        durMs: Math.round(performance.now() - startedAt),
        p50: round(quantile(sorted, 0.50)),
        p95: round(quantile(sorted, 0.95)),
        max: round(sorted[n - 1]),
        long: long,
        longPct: round((long / n) * 100),
    };
    // Whatever the host can tell us about what was on screen — pane count, marker counts. Without this a
    // sample says "the drag was rough" but not what was drawing, which is exactly the comparison wanted.
    try { if (context) Object.assign(msg, context()); } catch (e) { /* context is best-effort */ }
    post(msg);
    samples = [];
}

/// Wire one map's move events into the shared sampler. Safe to call for every pane; the refcount above
/// is what keeps them from multiply-counting the same frames.
export function attach(map) {
    map.on('movestart', begin);
    map.on('moveend', end);
    map.on('zoomstart', begin);
    map.on('zoomend', end);
}

/// Supply a function returning extra fields to stamp onto each sample (pane count, marker counts).
/// Called at emit time, so it always reports the state the gesture actually ran under.
export function setContext(fn) {
    context = fn;
}
