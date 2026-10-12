# Assets/Map — the web layer (the only non-C# code in this app)

Everything in this folder is served to the app's single **WebView2** under the virtual host
`https://mapassets/` (mapped from this directory by `MainWindow`). It exists because the map
is **[MapLibre GL JS](https://maplibre.org/)** — a JavaScript library that runs in the
browser engine and renders via **WebGL**. That work *cannot* be done in C#: it has to run
inside the WebView against the browser's JS/WebGL/Web Worker APIs. So this folder is, by
design, the project's irreducible non-C# surface. Everything that *can* be C# is C#
(see `../../Services`, `../../ViewModels`, `../../Models`).

## Layout — what's ours vs. borrowed vs. data

| Path | What | Language-stats |
|------|------|----------------|
| `map.html` | Page shell loaded by the WebView (`mapassets/map.html`); stays at the root. Holds every UI component's `<template>`. | counted |
| `styles/` | **Every stylesheet**: `themes/` (palettes, variables only), `pages/` (each page's shell), `components/` (one per on-map component). | counted |
| `js/` | **Our authored JS** — the orchestrator + 4 feature modules + the radar pipeline (see below). | counted |
| `vendor/` | **Third-party libraries**, vendored verbatim (not our code). ~1.2 MB. | `linguist-vendored` (excluded) |
| `style*.json` (basemaps, at root), `fonts/`, `sprites/` | **Data/config**, not code. | n/a |

`vendor/` is marked `linguist-vendored` in the repo-root `.gitattributes`, so the GitHub
language bar reflects the JS we actually wrote (in `js/`), not the ~1.2 MB of libraries.

## Where the UI is defined — before runtime, never during

Every UI component the page draws is **defined before runtime**: its markup is a `<template>` in
`map.html` (or `preview.html`), and its look is a sheet in `styles/`. A module only **instantiates**
it — `Templates.clone('tpl-…')` from `js/templates.js` — then fills in text, data attributes and
per-instance geometry, and toggles classes. The app's XAML/C# side follows the same rule: a
control's elements and looks are declared in XAML, not built in code-behind.

| May happen at runtime (JS) | Must be defined before runtime |
|---|---|
| clone a template; set `textContent` (feed text is never HTML) | the component's markup → a `<template>` |
| per-instance geometry: `left/top/width/height/transform`, an SVG's coordinates, a knob's size | every static look → a sheet in `styles/` |
| show/hide (`display`, `hidden`), toggle a class | colours → theme variables (chrome) or the component sheet (data) |
| a DATA colour that comes from the feed/product (a report's dot colour, a tropical band) | — |

**Never:** inject a `<style>`, build markup in a string (`innerHTML`, `setHTML`, `insertAdjacentHTML`),
or set a static look inline (`style.cssText`).

```
styles/
  themes/      anvil-dark.css (the base :root), anvil-light.css — VARIABLES ONLY, no selectors on elements
  pages/       map.css, preview.css — each page's own shell (layout; colours via var())
  components/  radar-site-keys.css, markers.css, radar-tools.css, map-popups.css, outlook.css
```

- **A new theme** is one file in `themes/` (one `:root[data-theme="<id>"]` block overriding values),
  linked from both pages after `anvil-dark.css`. No component sheet changes.
- **A new component** is a `<template>` in the page + a sheet in `components/` linked from the page.
  The csproj ships `styles/**/*.css` by glob, so a new sheet needs no project edit.
- **Chrome vs data:** chrome colours (surfaces, edges, the site key's gray face) are theme variables;
  data colours (status inks, product ramps, report types) are literal in the component sheet or the
  module, and never themed. See the header of `styles/themes/anvil-dark.css`.
- ⚠️ `preview.html` carries a copy of `tpl-ring-knob` (the only component it draws) — change both.

## Our authored JS (`js/`) — why each file must be JS

All authored modules live in **`js/`** (the page, styles, and `fonts/`/`sprites/`/`vendor/` stay at the
Map root). `map.html` loads `js/map.js` + `js/radar.js` as classic `<script>`s; those dynamically
`import()` the rest. `radar.js` resolves the worker relative to **its own** URL (not the page), and
`radar-decode.js` imports the vendored decoder via `../vendor/` — both so the split survives the move.

**Orchestrator**
- **`map.js`** — boots the single MapLibre map from URL params, registers `pmtiles://`, owns the
  `window.*` command shims + `applyStyle` (re-adds overlays after a basemap switch), and **delegates**
  each feature to a module. A thin (~150-line) orchestrator, not a god file.

**Feature modules** — each owns one overlay's state + logic; `map.js` delegates to it:
- **`outlook.js`** — SPC outlook fills + per-CIG hatching + nested-polygon clipping.
- **`watches.js`** — Tornado / Severe-T-storm watch boxes.
- **`radar-sites.js`** — the on-map site-marker "key" buttons (markup: `tpl-site-key`; look: `styles/components/radar-site-keys.css`).
- **`markers.js`** — the user-location marker.

**Radar pipeline** — file → colored gates on the GPU:
- **`radar.js`** — the NEXRAD/DOW **WebGL custom layer** + frame store + render loop + range ring +
  sweep + Inspect (`window.RadarLayer`). Custom GPU rendering ⇒ must be JS; the heaviest reason this
  folder exists.
- **`radar-decode.js`** — decodes a single-tilt `.V06` / a DOW frame, builds the gate geometry, and
  dealiases velocity. Pure (no DOM/GL); runs in the worker and as a main-thread fallback.
- **`radar-worker.js`** — a Web Worker that runs the decode off the UI thread. Worker ⇒ JS.
- **`radar-ramps.js`** — the color scales (one source of truth, shared by the decoder AND the legend).
- **`geo.js`** — the one site projection (shared by `radar-decode.js` AND `radar.js`'s ring/inspector,
  so overlays line up with the painted gates).

## The C# ↔ JS contract (one seam)

C# never reaches into the page's internals. The boundary is exactly one pair:

- **C# side:** `Services/MapService.cs` builds `window.fn(args…)` strings and dispatches them
  through `IMapView.RunScriptAsync` (implemented by `MainWindow`). Messages come back via
  `CoreWebView2.WebMessageReceived` (`mapReady`, `radarSiteClick`, `radarFrameReady`,
  `radarLog`).
- **JS side:** `map.js` / `radar.js` register the matching `window.*` handlers and `post(...)`
  those messages.

If you add a command, it's one method in `MapService` + one `window.*` handler here — that
1:1 mapping *is* the API.

## Adding new overlays — prefer C#, not new JS

The app is offline-first and we want to keep this folder small. MapLibre is data-driven, so
**most overlays should be built in C# as data** (GeoJSON + a layer/paint spec) and applied
through generic JS primitives — *not* a new bespoke JS function per feature. Only reach for
new JS when a feature needs **custom GPU rendering** (like the radar layer); standard
polygons/lines/points/rasters/heatmaps don't.

## Vendoring the decoder (offline, no build step)

`vendor/nexrad-level-2-data.esm.js` + `vendor/seek-bzip.esm.js` are jsDelivr `+esm` bundles
downloaded with `curl` (no Node/bundler). **Two hand-patches** must be re-applied if either is
re-downloaded (see the repo-root `CLAUDE.md` "Vendoring the radar decoder" for the canonical
notes):
1. the decoder's `import "/npm/seek-bzip@2.0.0/+esm"` is rewritten to `"./seek-bzip.esm.js"`
   (both files live in `vendor/`, so that relative import stays correct);
2. `,_ as Buffer` is appended to its final `export{…}` so `radar.js` can wrap input with the
   decoder's *own* `Buffer` (its constructor rejects any foreign Buffer — "Unknown data
   provided").

`maplibre-gl.js`/`.css` (v5.21.1) and `pmtiles.js` are likewise vendored verbatim.
