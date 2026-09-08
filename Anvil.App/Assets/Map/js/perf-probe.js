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
//                     █ = a LONG frame (> the threshold derived from `cadence` below)
//
// What lands in the log is p50 / p95 / max / long-frame count for that one gesture — p95 and `long` are
// the interesting pair, because a drag that is smooth apart from four 90 ms stalls has a fine average
// and feels broken.
//
// ⚠️⚠️ "LONG" IS RELATIVE TO THE DISPLAY'S OWN CADENCE, AND A FIXED MILLISECOND THRESHOLD IS WRONG.
// This shipped with a flat LONG_MS = 32, picked for a 60 Hz screen. The first real run was on a 30 Hz
// one, where the natural frame interval is 33.3 ms — just over that threshold — so EVERY frame counted
// as long and every gesture reported ~98%, including a control sample taken on an empty basemap with no
// radar loaded at all (radar-diag-20260908-122438, 12:24:54, site=None: p50 33.3, max 33.6, long 100%).
// A metric that reads 100% on an idle empty map measures the refresh rate, not the app. So the threshold
// is now derived from a measured `cadence`, and that cadence is REPORTED with every sample — a number
// whose meaning depends on the machine has to carry its own calibration or the log cannot be read later.
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

const MIN_SAMPLES = 8;   // ignore a flick too short to say anything about; avoids noise lines in the log
const MAX_SAMPLES = 3000; // a ~50 s continuous drag; bounds the array on a pathological gesture

// ── Cadence calibration (see the ⚠️⚠️ note above) ──────────────────────────────────────────────────
const CALIBRATE_FRAMES = 90;   // ~1.5 s at 60 Hz, ~3 s at 30 Hz — enough for a stable median
const LONG_FACTOR = 1.5;       // a frame is "long" past 1.5x the cadence...
const LONG_MIN_SLACK_MS = 8;   // ...or cadence + 8 ms, whichever is LARGER. At 60 Hz 1.5x is only
                               // 25 ms, close enough to the 16.7 ms floor that ordinary scheduling
                               // jitter trips it; the slack keeps a fast display from crying wolf.
const CADENCE_FLOOR_MS = 4;    // ~240 Hz. Below this we are measuring a bug, not a monitor.
const FALLBACK_CADENCE_MS = 16.7; // if calibration never completes, assume 60 Hz and say so (cal:0)

let cadenceMs = 0;       // measured display frame interval; 0 until calibration lands
let calibrated = false;
let calSamples = [];
let calHandle = 0;
let calLast = 0;         // calibration's own frame cursor — deliberately NOT the gesture sampler's
                         // `last`. The two never run at once (calTick bails while a gesture is active),
                         // but that is an accident of scheduling order, and a shared cursor between two
                         // samplers is the kind of coupling that breaks silently on a timing change.

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

// ── Cadence: the display's own frame interval, measured rather than assumed ────────────────────────
// Calibrated once on an IDLE page (no gesture in flight), then allowed to RATCHET DOWN — never up — if
// a later gesture is observed running faster than the calibration suggested.
//
// ⚠️ The ratchet is what makes a bad calibration self-healing. Calibration runs at module load, which
// on this page is also when the map is building its first tiles: if it lands in a busy stretch it
// over-reads the interval, and a too-high cadence hides real hitches behind a too-high threshold. A
// gesture that beats it is proof the machine can go faster, so the lower number is the truer one.
// ⚠️ It must only go DOWN. Ratcheting up would let one slow gesture raise the bar for every gesture
// after it, which is precisely how a probe stops reporting the problem it was built to find.
function noteCadence(observedMs) {
    if (!(observedMs > CADENCE_FLOOR_MS)) return;          // NaN-safe: excludes 0 and nonsense
    if (!cadenceMs || observedMs < cadenceMs) cadenceMs = observedMs;
}

function calTick(now) {
    // A gesture started mid-calibration: abandon this attempt rather than fold interaction frames into
    // the idle baseline. finish() restarts it, so calibration simply happens after the user stops.
    if (active > 0) { calSamples = []; calHandle = 0; calLast = 0; return; }
    if (calLast > 0) calSamples.push(now - calLast);
    calLast = now;
    if (calSamples.length >= CALIBRATE_FRAMES) {
        noteCadence(quantile(calSamples.slice().sort(function (a, b) { return a - b; }), 0.50));
        calibrated = true;
        calSamples = [];
        calHandle = 0;
        calLast = 0;
        return;
    }
    calHandle = requestAnimationFrame(calTick);
}

function startCalibration() {
    if (calibrated || calHandle || active > 0) return;
    calSamples = [];
    calLast = 0;
    calHandle = requestAnimationFrame(calTick);
}

// The interval past which a frame counts as a visible hitch, for the cadence we currently believe in.
function longThresholdMs() {
    const c = cadenceMs || FALLBACK_CADENCE_MS;
    return Math.max(c * LONG_FACTOR, c + LONG_MIN_SLACK_MS);
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
    if (n < MIN_SAMPLES) { samples = []; startCalibration(); return; }

    const sorted = samples.slice().sort(function (a, b) { return a - b; });
    const p50 = quantile(sorted, 0.50);

    // Ratchet BEFORE thresholding, so the very first gesture on a page whose calibration has not landed
    // yet is still judged against a real cadence rather than the 60 Hz fallback — which is exactly the
    // case that produced the bogus 100% readings. noteCadence only ever lowers it.
    // ⚠️ Residual, accepted: if the first gesture arrives before calibration AND is genuinely slow, it
    // sets the cadence too high and under-reports its own long count. `cal` in the payload says whether
    // calibration had landed, and p50/p95/max are always raw — so the sample can still be read honestly.
    noteCadence(p50);
    const threshold = longThresholdMs();

    let long = 0;
    for (let i = 0; i < n; i++) { if (samples[i] > threshold) long++; }

    const round = function (v) { return Math.round(v * 10) / 10; };
    const msg = {
        type: 'perfPan',
        n: n,
        durMs: Math.round(performance.now() - startedAt),
        p50: round(p50),
        p95: round(quantile(sorted, 0.95)),
        max: round(sorted[n - 1]),
        long: long,
        longPct: round((long / n) * 100),
        // The calibration this sample was judged against. ⚠️ Both fields are load-bearing for anyone
        // reading the log later: "12% long" means nothing without the interval that defined "long", and
        // a cadence of 33.3 vs 16.7 is the difference between a 30 Hz screen and a 60 Hz one.
        cadence: round(cadenceMs || FALLBACK_CADENCE_MS),
        longMs: round(threshold),
        cal: calibrated ? 1 : 0,
    };
    // Whatever the host can tell us about what was on screen — pane count, marker counts. Without this a
    // sample says "the drag was rough" but not what was drawing, which is exactly the comparison wanted.
    try { if (context) Object.assign(msg, context()); } catch (e) { /* context is best-effort */ }
    post(msg);
    samples = [];
    startCalibration();   // idle again — take the baseline if we never got one
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

// Take the idle baseline as soon as this module lands. It may well run while the map is still building
// its first tiles and over-read — that is what the ratchet in noteCadence() is for, and finish() retries
// after every gesture until one completes cleanly.
startCalibration();
