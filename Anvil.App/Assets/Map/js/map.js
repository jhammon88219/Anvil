// One reusable page hosting N MapLibre maps — one per PANE. The host (MainWindow) loads it once,
// passing interactivity, initial framing, and basemap style as URL parameters:
//   ?interactive=true|false & style & lng & lat & zoom & conus & tiles & tilesUrl
// The page makes no decisions of its own — it renders what the host asks for and
// exposes a few command shims the host drives over the IMapView seam.
//
// WHAT IS ON SCREEN — the pane grid (quad shown; the gutter is paneGutter px of page background):
//
//   ┌──────────────┬──────────────┐   maps[0] is the PRIMARY pane and sits BOTTOM-LEFT — the same
//   │   maps[2]    │   maps[3]    │   arrangement MainWindow's notch grid uses. paneRects()
//   ├──────────────┼──────────────┤   below is the page's half of that rule; the XAML grid is the
//   │   maps[0]    │   maps[1]    │   other half. ⚠️ CHANGE BOTH OR THEY DRIFT.
//   │   (PRIMARY)  │              │
//   └──────────────┴──────────────┘
//
// WHAT IS IN EACH PANE — the z-stack, bottom to top (who sits where is decided by layers.js +
// each module's beforeId; this is the assembled result, and the order reAddAll() restores):
//
//   ── top ──   storm-report dots      (no beforeId — always over everything)
//               place labels           ┐ basemap symbol layers
//               state / country lines  ┘ basemap line layers
//               mile grid              ← under the borders, over the radar
//               warning polygons       ┐ both target firstBoundaryLayerId; watches are re-added
//               watch boxes            ┘ FIRST, so warnings land above them
//               outlook fills + hatch  ← under the labels (firstSymbolLayerId)
//               RADAR (WebGL layer)    ← beneath the watch fill, via radar.js's own beforeId chain
//   ── base ──  basemap (bundled PMTiles, or online tiles — same styles either way, see tileSourceFor)
//
//   Riding above all of it, NOT in the stack: the state-isolation mask (added last, on top), and the
//   DOM-overlay markers — radar site keys + the user-location dot — which are HTML elements over the
//   canvas rather than GL layers, and therefore PRIMARY-PANE ONLY.
//
// ===== MULTI-PANE =====
// A pane is a PRODUCT VIEW of one site: every pane shares the site, the camera and the time cursor,
// and differs only in which radar moment it draws. That is why there are N maps rather than one:
// MapLibre renders a basemap once per camera per canvas, so panes can only be separate maps. What is
// NOT duplicated is the expensive half — radar.js keeps ONE decoded frame store, worker pool, decode
// cache and storm motion for the whole loop, so a pane costs a GL upload of geometry already decoded,
// not a decode. The overlay modules were already written as (map, …) functions over shared data, so
// fanning them across panes is a loop, not a rewrite.
//
// Layouts: 1x1, 2x1 (side by side) and 2x2. Two-pane is side-by-side only — there is deliberately no
// stacked variant. maps[0] is the PRIMARY pane: it owns the launch sequence, the mapReady post, and
// the overlays that are DOM markers rather than GL layers (radar sites, the user-location marker),
// which are per-pane-expensive and would make site-picking ambiguous if they appeared in all four.
const params = new URLSearchParams(location.search);
const interactive = params.get('interactive') === 'true';
// `let`, not const: applyStyle updates it so a pane created LATER is built on the current basemap
// rather than the launch one.
let styleUrl = 'https://mapassets/' + (params.get('style') || 'style.json');
// ---- Basemap TILE SOURCE (offline PMTiles vs online tiles) -----------------------------------------
// The five styles are Protomaps-SCHEMA styles: every layer filters on protomaps source-layers, and the
// ONLY thing tying them to the bundled ~29 GB file is one source url. So "go online" is a SOURCE SWAP,
// not a second set of styles — the look is identical either way, and glyphs/sprites stay local (no
// online typography drift, no extra CORS surface). resolveStyle() does the patch; see tileSourceFor.
// Passed as URL params so the launch map is built on the right source instead of flipping after ready.
let tileMode = params.get('tiles') === 'online' ? 'online' : 'offline';
let tileUrl = params.get('tilesUrl') || '';
// The RESOLVED style handed to new maps: the style OBJECT once patched (online), or just the style URL
// (offline, where nothing needs patching). A pane created later reads this, so it matches its siblings.
let styleSpec = null;
const lng = parseFloat(params.get('lng'));
const lat = parseFloat(params.get('lat'));
const zoom = parseFloat(params.get('zoom'));

try {
    // Register the pmtiles:// protocol so MapLibre can read the local file. ONE protocol instance for
    // every pane — this is what lets the panes share the basemap's directory/tile fetch cache instead
    // of each paying for its own.
    const protocol = new pmtiles.Protocol();
    maplibregl.addProtocol('pmtiles', protocol.tile);

    // ---- Pane state -------------------------------------------------------------------------------
    const MAX_PANES = 4;
    // Width of the groove between panes, in CSS px. The host passes this in setPaneLayout so the XAML
    // notch grid and the page cannot drift apart — this value is only the pre-layout default.
    let paneGutter = 5;
    let maps = [];            // live maps, contiguous; index = pane index, maps[0] = PRIMARY
    let cols = 1, rows = 1;
    let syncingCamera = false; // re-entrancy guard for the camera mirror

    function primary() { return maps[0]; }
    function forEachMap(fn) { for (let i = 0; i < maps.length; i++) fn(maps[i], i); }

    // ---- Overlay module cache ---------------------------------------------------------------------
    // Each per-feature module is dynamically imported once and cached here; the window.* shims below
    // delegate to it, passing the map(s). Every module keeps its DATA at module level (shared by every
    // pane, which is what we want — one fetch, N renders) and only its LAYERS per map, so the same
    // module instance serves all the panes.
    var Outlook = null;
    var Watches = null;
    var Warnings = null;
    var StormReports = null;
    var Grid = null;
    var Markers = null;
    var RadarSites = null;
    var States = null;

    // Restore every overlay onto one map, in stack order. TWO callers: applyStyle (setStyle drops all
    // custom sources/layers) and a NEWLY CREATED pane (which starts with nothing but the basemap). One
    // list, so a pane and a re-style can never disagree about what an overlay stack looks like.
    // ⚠️ ORDER IS LOAD-BEARING: outlook first so radar's beforeId can target it and slot in beneath;
    // states LAST so the isolation mask lands on top of everything.
    function reAddAll(map) {
        if (Outlook) Outlook.reAdd(map);                 // re-add the outlook (reuse clipped data, or re-fetch)
        if (Watches) Watches.reAdd(map);                 // re-add the watch layers (data is still in memory)
        if (Warnings) Warnings.reAdd(map);               // re-add the warning polygons (above the watches)
        if (StormReports) StormReports.reAdd(map);       // re-add the storm-report dots (top of the stack)
        if (window.RadarLayer) window.RadarLayer.reAdd(map);  // this pane's own radar layer + range ring
        if (Grid) Grid.reAdd(map);                       // re-anchor the mile grid over the radar, under the borders/labels
        if (States) States.reAdd(map);                   // re-add LAST so the isolation mask lands on top of everything
    }

    // ---- Camera sync ------------------------------------------------------------------------------
    // Panes share ONE camera. Whichever map the user drives, the others are jumped (not eased) to the
    // exact same camera so they stay locked frame-for-frame. Animated moves (flyTo / fitBounds /
    // resetNorthPitch) are issued to the PRIMARY only and reach the others through here — issuing the
    // animation to every map instead would have each one's 'move' fighting the others' jumpTo.
    function onPaneMove(e) {
        if (syncingCamera) return;
        const src = e.target;
        syncingCamera = true;
        const c = src.getCenter(), z = src.getZoom(), b = src.getBearing(), p = src.getPitch();
        for (let i = 0; i < maps.length; i++) {
            const other = maps[i];
            if (other === src) continue;
            other.jumpTo({ center: c, zoom: z, bearing: b, pitch: p });
        }
        syncingCamera = false;
    }

    // ---- Pane layout ------------------------------------------------------------------------------
    // Panes are positioned with the gutter already subtracted, so a pane's canvas never touches its
    // neighbour's; the groove divs then decorate the gap. Both derive from the same rects, so the
    // hairlines always land exactly on the pane edges.
    function paneRects() {
        const host = document.getElementById('panes');
        const W = host.clientWidth, H = host.clientHeight;
        const gx = (cols > 1) ? paneGutter : 0;
        const gy = (rows > 1) ? paneGutter : 0;
        const pw = (W - gx * (cols - 1)) / cols;
        const ph = (H - gy * (rows - 1)) / rows;
        const out = [];
        // ⚠️ Pane 0 is the MAIN pane and sits BOTTOM-LEFT, so rows fill from the bottom up. It is the pane
        // you were already looking at before entering a layout, and it belongs next to the bar that drives
        // it rather than diagonally opposite. Within a row, panes still read left to right, so a quad is
        // 0=bottom-left, 1=bottom-right, 2=top-left, 3=top-right — a straight vertical mirror of reading
        // order. The bar's chip cluster mirrors this exact arrangement, so a chip sits where its pane is.
        for (let r = rows - 1; r >= 0; r--) {
            for (let c = 0; c < cols; c++) {
                out.push({ left: c * (pw + gx), top: r * (ph + gy), width: pw, height: ph, W: W, H: H });
            }
        }
        return out;
    }

    // NB: the PANE WATERMARK (the product label in each pane's corner) is NOT drawn here — it is a XAML
    // control overlaid on the WebView (Controls/Primitives/PaneWatermark, placed by MainWindow), so its
    // whole visual can be edited under XAML Hot Reload while the app runs. MainWindow's overlay grid
    // mirrors the pane arrangement paneRects() lays out above; keep the two in step.

    function applyPaneLayout() {
        const rects = paneRects();
        for (let i = 0; i < MAX_PANES; i++) {
            const el = document.getElementById('pane' + i);
            if (!el) continue;
            if (i < rects.length) {
                el.style.display = 'block';
                el.style.left = rects[i].left + 'px';
                el.style.top = rects[i].top + 'px';
                el.style.width = rects[i].width + 'px';
                el.style.height = rects[i].height + 'px';
            } else {
                el.style.display = 'none';
            }
        }
        // Grooves: one vertical (2 columns) and/or one horizontal (2 rows).
        const gv = document.getElementById('gutterV');
        const gh = document.getElementById('gutterH');
        if (gv) {
            if (cols > 1) {
                gv.style.display = 'block';
                // Both columns are equal, so the seam is one pane-width in — derived from the CELL SIZE,
                // not from a particular rect's position, which no longer implies a corner now that rows
                // fill bottom-up.
                gv.style.left = rects[0].width + 'px';
                gv.style.top = '0px';
                gv.style.width = paneGutter + 'px';
                gv.style.height = rects[0].H + 'px';
            } else {
                gv.style.display = 'none';
            }
        }
        if (gh) {
            if (rows > 1) {
                gh.style.display = 'block';
                gh.style.left = '0px';
                // ⚠️ The TOP row's bottom edge — i.e. one pane-height down. rects[0] is now the BOTTOM-left
                // pane (pane 0 is the main pane), so adding its own top would put the groove below the
                // window; both rows are equal, so the cell height alone is the answer.
                gh.style.top = rects[0].height + 'px';
                gh.style.width = rects[0].W + 'px';
                gh.style.height = paneGutter + 'px';
            } else {
                gh.style.display = 'none';
            }
        }
        forEachMap(function (m) { try { m.resize(); } catch (e) { /* mid-teardown */ } });
    }

    // Build the replacement basemap source for ONLINE mode, or null to keep the style file's own
    // (offline `pmtiles://https://mapdata/...`) source untouched. One config string covers every online
    // option because the three forms are distinguishable: a remote PMTiles archive and a TileJSON
    // endpoint both carry their own zoom range + attribution, so MapLibre reads the metadata; a raw
    // {z}/{x}/{y} template carries none, and the Protomaps basemap tileset tops out at z15 — without
    // maxzoom MapLibre would request tiles up to z22 that do not exist.
    function tileSourceFor(base) {
        if (tileMode !== 'online' || !tileUrl) return null;
        const src = { type: 'vector' };
        if (base && base.attribution) src.attribution = base.attribution;
        if (/^pmtiles:\/\//i.test(tileUrl) || /\.json($|\?)/i.test(tileUrl)) {
            src.url = tileUrl;
        } else {
            src.tiles = [tileUrl];
            src.minzoom = 0;
            src.maxzoom = 15;
        }
        return src;
    }

    // Resolve a style file into what MapLibre should be handed.
    // ⚠️ OFFLINE (the default) resolves to the URL UNCHANGED — no fetch, no parse, so the default path
    // is byte-for-byte today's behavior and pays nothing on the first-paint critical path. Only online
    // fetches the style to patch its one source. A failed fetch falls back to the plain URL, so a bad
    // online config degrades to the offline basemap instead of a blank map.
    function resolveStyle(url) {
        if (tileMode !== 'online' || !tileUrl) return Promise.resolve(url);
        return fetch(url)
            .then(function (r) { return r.json(); })
            .then(function (style) {
                const id = style.sources && Object.keys(style.sources)[0];
                const src = id ? tileSourceFor(style.sources[id]) : null;
                if (id && src) style.sources[id] = src;
                return style;
            })
            .catch(function (e) {
                console.error('online style patch failed, using the offline basemap: ' + e);
                return url;
            });
    }

    // Every map gets its OWN copy of the resolved style — MapLibre takes ownership of the object it is
    // given, so four panes sharing one would have them mutating each other's style.
    function styleForNewMap() {
        const spec = styleSpec || styleUrl;
        return typeof spec === 'string' ? spec : structuredClone(spec);
    }

    function createPaneMap(i) {
        // A pane joins the existing camera, style and orientation, so it appears already locked to its
        // siblings instead of flying in from the launch view.
        const ref = maps[0];
        const m = new maplibregl.Map({
            container: 'pane' + i,
            style: styleForNewMap(),
            center: ref ? ref.getCenter() : [lng, lat],
            zoom: ref ? ref.getZoom() : zoom,
            bearing: ref ? ref.getBearing() : 0,
            pitch: ref ? ref.getPitch() : 0,
            interactive: interactive,
            attributionControl: false,
            // One world, not an endless horizontal loop of copies. `renderWorldCopies:false` stops the
            // repeat; `minZoom` stops zooming out past a single-globe view (where the copies were the
            // whole point of the complaint). NOT `maxBounds` — the site list is the full WSR-88D network,
            // including Guam / Okinawa / Korea, so panning off CONUS has to stay possible.
            renderWorldCopies: false,
            minZoom: 2
        });
        m.on('move', onPaneMove);
        // The primary's load runs the LAUNCH sequence (mask, reveal, mapReady) — see below. A pane added
        // later just needs the overlay stack the others already have.
        if (i > 0) m.once('load', function () { reAddAll(m); });
        return m;
    }

    // Host command: choose the pane grid. cols x rows, plus the groove width so the page and the XAML
    // notch grid share one constant. Panes are created/destroyed around the layout change; the
    // camera, the loop and every overlay survive it.
    window.setPaneLayout = function (c, r, gutterPx) {
        cols = Math.max(1, c | 0);
        rows = Math.max(1, r | 0);
        if (typeof gutterPx === 'number' && gutterPx >= 0) paneGutter = gutterPx;
        const want = Math.min(MAX_PANES, cols * rows);
        // Tear surplus panes down FIRST, while their GL context is still live, so the radar layer can
        // release its buffers/program before the context goes away.
        while (maps.length > want) {
            const m = maps.pop();
            if (window.RadarLayer && window.RadarLayer.detachView) window.RadarLayer.detachView(m);
            try { m.remove(); } catch (e) { /* already gone */ }
        }
        applyPaneLayout();                                   // size the survivors before new ones attach
        for (let i = maps.length; i < want; i++) maps[i] = createPaneMap(i);
        applyPaneLayout();
        if (window.RadarLayer && window.RadarLayer.setViews) window.RadarLayer.setViews(maps);
    };

    window.addEventListener('resize', applyPaneLayout);

    // The launch pane is created by the BOOT at the very bottom of this file, not here: the style has to
    // be resolved against the tile source first (see resolveStyle). Everything between here and there is
    // a function or a window.* shim definition, none of which touch a live map, and the host cannot send
    // a command before the mapReady latch the boot arms — so deferring creation costs nothing.

    // Host commands (C# -> JS via RunScriptAsync). Style swap re-applies the style to EVERY pane; flyTo
    // animates the primary and the camera sync carries the rest; show/clearOutlook + setOutlookOpacity
    // drive the SPC overlay across all panes.
    // Guards a resolve that a LATER style/source change has already superseded — online resolution is a
    // fetch, so two quick switches can land out of order and leave the map on the older one.
    let styleGen = 0;
    window.applyStyle = function (url) {
        styleUrl = url; // a pane created later is built on the CURRENT basemap, not the launch one
        const gen = ++styleGen;
        resolveStyle(url).then(function (spec) {
            if (gen !== styleGen) return;
            styleSpec = spec;
            forEachMap(function (m) {
                m.setStyle(styleForNewMap(), { diff: true });
                // setStyle drops our custom sources/layers/images — re-add them once the new style settles.
                // Reuse the already-clipped data; only re-fetch if it isn't loaded yet.
                m.once('idle', function () { reAddAll(m); });
            });
        });
    };
    // Host command: switch where the basemap's vector tiles come from — the bundled offline PMTiles file
    // or an online source (Protomaps API TileJSON, a raw {z}/{x}/{y} template, or a remote PMTiles
    // archive; tileSourceFor tells them apart). The STYLE is untouched, so the app's look is identical
    // across the switch; this just re-resolves the current style against the new source and re-applies
    // it, taking the same setStyle + reAdd path as a basemap change.
    window.setTileSource = function (mode, url) {
        tileMode = mode === 'online' ? 'online' : 'offline';
        tileUrl = url || '';
        window.applyStyle(styleUrl);
    };
    // SPC outlook overlay (probability fills + per-CIG hatching; nested groups clipped) lives in
    // outlook.js — load once and delegate (passing each map). applyStyle calls Outlook.reAdd(map).
    import('./outlook.js').then(function (m) { Outlook = m; }).catch(function (e) { console.error('outlook.js load failed: ' + e); });
    window.showOutlook = function (url) { if (Outlook) forEachMap(function (m) { Outlook.show(m, url); }); };
    window.clearOutlook = function () { if (Outlook) forEachMap(function (m) { Outlook.clear(m); }); };
    window.setOutlookOpacity = function (opacity) { if (Outlook) forEachMap(function (m) { Outlook.setOpacity(m, opacity); }); };

    // Watches and warnings both carry a `phenom` of TO (tornado) or SV (severe thunderstorm), and both
    // hosts offer one checkbox per phenomenon. The host sends two bools; the overlay wants the list of
    // phenom values to draw. ⚠️ These are the SAME codes the two modules colour by (see their `colors`
    // tables) — the filter and the colour read one property, so they cannot disagree about what a
    // tornado is.
    function phenomKinds(torn, severe) {
        const kinds = [];
        if (torn) kinds.push('TO');
        if (severe) kinds.push('SV');
        return kinds;
    }

    // SPC watch boxes live in watches.js — load once and delegate (passing each map). applyStyle calls
    // Watches.reAdd(map) after a basemap switch (setStyle drops the layers; the data stays in memory).
    import('./watches.js').then(function (m) { Watches = m; }).catch(function (e) { console.error('watches.js load failed: ' + e); });
    window.setWatchSource = function (url) { if (Watches) forEachMap(function (m) { Watches.setSource(m, url); }); };
    window.setWatchesVisible = function (on) { if (Watches) forEachMap(function (m) { Watches.setVisible(m, on); }); };
    window.setWatchKinds = function (torn, severe) { if (Watches) forEachMap(function (m) { Watches.setKinds(m, phenomKinds(torn, severe)); }); };
    window.setWatchesOpacity = function (o) { if (Watches) forEachMap(function (m) { Watches.setOpacity(m, o); }); };

    // Storm-based warning polygons live in warnings.js — same lazy-load/delegate pattern as watches.
    // Warnings sit ABOVE the watch boxes (imminent-threat layer). applyStyle calls Warnings.reAdd(map).
    import('./warnings.js').then(function (m) { Warnings = m; }).catch(function (e) { console.error('warnings.js load failed: ' + e); });
    window.setWarningSource = function (url) { if (Warnings) forEachMap(function (m) { Warnings.setSource(m, url); }); };
    window.setWarningsVisible = function (on) { if (Warnings) forEachMap(function (m) { Warnings.setVisible(m, on); }); };
    window.setWarningKinds = function (torn, severe) { if (Warnings) forEachMap(function (m) { Warnings.setKinds(m, phenomKinds(torn, severe)); }); };
    window.setWarningsOpacity = function (o) { if (Warnings) forEachMap(function (m) { Warnings.setOpacity(m, o); }); };

    // SPC storm-report dots (Tornado / Wind / Hail verification) live in storm-reports.js — same lazy-load/
    // delegate pattern. They sit on TOP of the stack. applyStyle calls StormReports.reAdd(map).
    import('./storm-reports.js').then(function (m) { StormReports = m; }).catch(function (e) { console.error('storm-reports.js load failed: ' + e); });
    window.setStormReportsSource = function (url) { if (StormReports) forEachMap(function (m) { StormReports.setSource(m, url); }); };
    window.setStormReportKinds = function (torn, wind, hail) { if (StormReports) forEachMap(function (m) { StormReports.setKinds(m, torn, wind, hail); }); };
    window.setStormReportsOpacity = function (o) { if (StormReports) forEachMap(function (m) { StormReports.setOpacity(m, o); }); };
    // Empty the overlay outright (leaving a replay, or a day whose reports won't load). Without this the
    // host could only ever re-point the dots at ANOTHER day, so "no day" left the old day's dots drawn.
    window.clearStormReports = function () { if (StormReports) forEachMap(function (m) { StormReports.clear(m); }); };
    // Diagnostic readback — what the PAGE thinks it is drawing, per pane. Reported to the host so a
    // "the dots are still there" bug can be pinned to the side that is actually wrong.
    window.describeStormReports = function () {
        if (!StormReports) return 'module-not-loaded';
        var out = [];
        forEachMap(function (m, i) { out.push('[' + i + '] ' + StormReports.describe(m)); });
        return out.join(' ');
    };

    // Mile distance grid (square grid anchored to the selected radar) lives in grid.js — same lazy-load/
    // delegate pattern. Sits over the radar, under the borders/labels. applyStyle calls Grid.reAdd(map).
    import('./grid.js').then(function (m) { Grid = m; }).catch(function (e) { console.error('grid.js load failed: ' + e); });
    window.showMileGrid = function (lat, lon, spacingMiles) { if (Grid) forEachMap(function (m) { Grid.show(m, lat, lon, spacingMiles); }); };
    window.clearMileGrid = function () { if (Grid) forEachMap(function (m) { Grid.clear(m); }); };
    window.setMileGridOpacity = function (o) { if (Grid) forEachMap(function (m) { Grid.setOpacity(m, o); }); };

    // Animated camera moves go to the PRIMARY only; onPaneMove mirrors them to the other panes as they
    // play, so the panes stay locked without four animations fighting each other.
    window.flyTo = function (lng, lat, zoom) { primary().flyTo({ center: [lng, lat], zoom: zoom }); };

    // Level II radar shims — delegate to RadarLayer (radar.js). None of them take a map: RadarLayer
    // holds ONE loop rendered through N views, so the loop commands are pane-agnostic and the only
    // pane-addressed one is setRadarProduct.
    window.radarBeginLoop = function (lat, lon) {
        if (window.RadarLayer) window.RadarLayer.beginLoop(lat, lon);
    };
    window.radarAddFrame = function (url, index) {
        if (window.RadarLayer) window.RadarLayer.addFrame(url, index);
    };
    window.radarShowFrame = function (index) {
        if (window.RadarLayer) window.RadarLayer.showFrame(index);
    };
    window.radarRemap = function (newCount, mappingJson) {
        if (window.RadarLayer) window.RadarLayer.remap(newCount, mappingJson);
    };
    // Tilt switch: re-decode every frame at the new elevation WITHOUT tearing the loop down (the
    // frames on screen stay up, marked stale, until their replacements land).
    window.radarRetile = function (count) {
        if (window.RadarLayer) window.RadarLayer.retile(count);
    };
    window.clearLevel2Radar = function () {
        if (window.RadarLayer) window.RadarLayer.clear();
    };
    window.setRadarOpacity = function (opacity) {
        if (window.RadarLayer) window.RadarLayer.setOpacity(opacity);
    };
    // The PRIMARY pane's rendered product, tracked so the legend can be (re)pushed for it (see below).
    // Panes 2-4 carry their own products; the legend feed follows pane 1.
    var radarProduct = 'reflectivity';
    window.setRadarProduct = function (pane, product) {
        if (window.RadarLayer) window.RadarLayer.setProduct(pane, product);
        if ((pane | 0) === 0) { radarProduct = product; postRampFor(product); }
    };
    // Speculatively build velocity in the background (host calls this once reflectivity has rendered),
    // so a later switch to the Velocity product is instant.
    window.prefetchRadarVelocity = function () {
        if (window.RadarLayer) window.RadarLayer.prefetchVelocity();
    };
    // Compute the AUTO (VAD-derived) storm motion for one volume from its bottom velocity tilt URLs — the
    // host calls this for the displayed volume while SRV/auto is active (a single tilt is too shallow to be
    // correct, so the motion comes from a full-volume wind profile). urls = a JSON array string.
    // Storm motion supplied by the HOST's wind-profile provider chain (Level III NVW), rather than computed
    // from our own Level II VAD. See radar.js applyExternalMotion.
    window.setStormMotion = function (speedMs, dirDeg, source, layers, topM, tier) {
        if (window.RadarLayer) window.RadarLayer.setStormMotion(speedMs, dirDeg, source, layers, topM, tier);
    };
    window.computeStormMotion = function (urls) {
        if (window.RadarLayer) window.RadarLayer.computeStormMotion(urls);
    };
    // Inspect mode: read the value under the cursor (RadarScope-style). Delegates to RadarLayer,
    // which tracks the mouse and posts {type:"radarInspect"} for the color-scale marker.
    window.setRadarInspect = function (on) {
        if (window.RadarLayer) window.RadarLayer.setInspect(on);
    };
    // PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit): read-only inner-state snapshot,
    // polled by the host only while the Pipeline Console card is open. Returns null if no loop is loaded.
    window.radarPipelineSnapshot = function () {
        return window.RadarLayer ? window.RadarLayer.pipelineSnapshot() : null;
    };

    // DOW Event Viewer shims — show / clear a single curated mobile-radar frame (a .dow.json from
    // the dowevents host). showDow reuses the whole radar render path; clear tears it down.
    window.showDowFrame = function (url) {
        if (window.RadarLayer) window.RadarLayer.showDow(url);
    };
    window.clearDowFrame = function () {
        if (window.RadarLayer) window.RadarLayer.clear();
    };

    // Color-scale legend feed. The ramps in radar-ramps.js are the SINGLE source of truth for gate
    // colors, so the host's legend tool window is fed from them (never hard-coded): eager-load the
    // ramps and push the active product's ramp to the host on load + on every product switch. The
    // host renders the bar from these exact stops, so the legend can't drift from the pixels.
    var radarRamps = null;
    function postRampFor(product) {
        var r = radarRamps && radarRamps[product];
        if (r && window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'radarRamp', ramp: r }));
        }
    }
    import('./radar-ramps.js').then(function (m) {
        // Key each exported ramp by its own `id` (matches the product id from radar-products.js), so a
        // new product's ramp is picked up automatically — no per-product edit here.
        radarRamps = {};
        Object.keys(m).forEach(function (k) {
            var r = m[k];
            if (r && r.id && Array.isArray(r.stops)) radarRamps[r.id] = r;
        });
        // Push the WHOLE table once: the Product combo draws EVERY product's ramp next to its name, so the
        // host needs them all — not just the active one (which postRampFor sends for the inspect marker).
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'radarRamps', ramps: radarRamps }));
        }
        postRampFor(radarProduct); // whatever product is active once the ramps are loaded
    }).catch(function (e) { /* legend stays empty if ramps can't load */ });

    // User-location marker (the pulsing blue dot) lives in markers.js — load it once and delegate the
    // shims to it (passing the map). It's only invoked on a "My Location" click, long after this loads,
    // so the cached `Markers` is always ready by then; the guards are belt-and-suspenders.
    // ⚠️ PRIMARY-ONLY: a maplibregl.Marker is a DOM overlay bound to one map, so mirroring it would mean
    // N marker objects to keep in sync for no added information (every pane shows the same ground).
    import('./markers.js').then(function (m) { Markers = m; }).catch(function (e) { console.error('markers.js load failed: ' + e); });
    window.showUserLocation = function (lng, lat, label) { if (Markers) Markers.show(primary(), lng, lat, label); };
    window.clearUserLocation = function () { if (Markers) Markers.clear(); };

    // Radar site marker buttons live in radar-sites.js — load once and delegate (passing the map).
    // ⚠️ PRIMARY-ONLY, deliberately: these are ~160 DOM markers plus a collision fan-out recomputed on
    // every pan/zoom, so they are the one overlay that is genuinely expensive per pane — and site
    // picking wants ONE unambiguous surface. Clicking a marker in pane 1 re-sites every pane.
    var pendingSiteAccent = null; // accent pushed before the module loaded (map-ready can beat the import)
    import('./radar-sites.js').then(function (m) {
        RadarSites = m;
        if (pendingSiteAccent) { m.setAccent(pendingSiteAccent[0], pendingSiteAccent[1]); pendingSiteAccent = null; }
    }).catch(function (e) { console.error('radar-sites.js load failed: ' + e); });
    window.showRadarSites = function (json) { if (RadarSites) RadarSites.show(primary(), json); };
    window.setSelectedRadarSite = function (id) { if (RadarSites) RadarSites.setSelected(id); };
    window.setRadarSitesStatus = function (json) { if (RadarSites) RadarSites.setStatus(json); };
    window.setRadarSitesVisible = function (visible) { if (RadarSites) RadarSites.setVisible(visible); };
    window.setResearchRadarsVisible = function (visible) { if (RadarSites) RadarSites.setResearchVisible(visible); };
    window.setTdwrsVisible = function (visible) { if (RadarSites) RadarSites.setTdwrVisible(visible); };
    // The OS theme accent for the site-status halo. Cache if the module hasn't loaded yet so the
    // map-ready push (which can beat the dynamic import) isn't dropped.
    window.setRadarSiteAccent = function (border, glow) {
        if (RadarSites) RadarSites.setAccent(border, glow);
        else pendingSiteAccent = [border, glow];
    };

    // Radar sweep pulse. The host (C#) calls pulseRadarSweep() when a genuinely-new frame lands; we
    // delegate to the radar layer, which runs ONE sweep revolution (arm + trailing afterglow) then
    // hides the arm, leaving the range ring. setRadarSweep(period<=0) stops/removes it (clear/replay).
    window.pulseRadarSweep = function () {
        if (window.RadarLayer) window.RadarLayer.pulseSweep();
    };
    window.setRadarSweep = function (periodSeconds) {
        if (window.RadarLayer) window.RadarLayer.setSweep(periodSeconds);
    };

    // State Isolation lives in states.js — arm hover mode, then click a state to cover everything else with
    // the map's water color. Driven by StateIsolationViewModel via IMapService (the "Isolate" top-bar
    // toggle → stateIsoArm/Disarm; SelectIsolatedState → stateIsoSelect; ClearIsolation → stateIsoClear).
    // states.js posts {type:"stateIsolated", name} back so the VM tracks the isolated state.
    // __isoTest is a dev convenience for driving it straight from the WebView console.
    //
    // ⚠️ MULTI-PANE: states.js keeps its isolation state at MODULE level (which state, which rings, whether
    // CONUS is on) and renders it per map. So the real command runs on the PRIMARY — which owns the hover
    // handlers, the click-to-pick, and the one {stateIsolated} post back to the host — and every other pane
    // then MIRRORS the result through reAdd, whose whole job is "render the current isolation onto this
    // map". That keeps the mask, the border outline and the pan/zoom lock identical in every pane without
    // duplicating the interaction (shared hover state across four maps leaves stuck highlights) and without
    // posting the same isolation change to the host four times.
    // Coverage radius (km) for the state-isolation site filter: one WSR-88D reflectivity range. While a
    // state is isolated, a radar-site marker stays only if its umbrella reaches that state (radar-sites.js).
    var STATE_ISO_COVERAGE_KM = 230;
    // Kept as a promise so the launch path (map 'load' below) can apply the initial CONUS mask as soon as
    // states.js is ready, rather than waiting for the host's mapReady round-trip.
    var statesReady = import('./states.js').then(function (m) {
        States = m;
        // On every isolation change, filter the radar-site markers to those covering the isolated state
        // (rings) — or restore all when it clears (null). RadarSites is loaded by then (isolation is a
        // user action long after startup); guarded regardless.
        m.setOnIsolationChange(function (rings) { if (RadarSites) RadarSites.setIsolation(rings, STATE_ISO_COVERAGE_KM); });
        return m;
    }).catch(function (e) { console.error('states.js load failed: ' + e); return null; });
    // Run a states command on the primary, then mirror the resulting isolation onto every other pane.
    // Returns whatever the primary call returned (setConus returns a promise the launch path awaits).
    // ⚠️ Mirrors with syncTo, NOT reAdd. reAdd early-outs when there is nothing masked, which is right for
    // a style switch but wrong here: turning the mask OFF is exactly the case where the other panes still
    // have one and need it REMOVED. Using reAdd left panes 2-4 masked when CONUS was unmasked.
    function statesOnAllPanes(fn) {
        if (!States) return undefined;
        var result = fn(primary());
        for (var i = 1; i < maps.length; i++) States.syncTo(maps[i]);
        return result;
    }
    window.stateIsoArm = function () { statesOnAllPanes(function (m) { States.arm(m); }); };
    window.stateIsoDisarm = function () { statesOnAllPanes(function (m) { States.disarm(m); }); };
    window.stateIsoClear = function () { statesOnAllPanes(function (m) { States.clear(m); }); };
    window.stateIsoSelect = function (name) { statesOnAllPanes(function (m) { States.isolateState(m, name); }); };
    // Base extent: mask to CONUS (on) or show the full map (off). CONUS is the launch default.
    window.setConusIsolation = function (on) { statesOnAllPanes(function (m) { return States.setConus(m, on); }); };
    // Fit the current region of interest (isolated state, else CONUS) into view (center + zoom). Primary
    // only — it's an animated fitBounds, and the camera sync carries the other panes.
    window.fitMapToView = function () { if (States) States.fitToView(primary()); };
    // Reset the map's orientation: right-click-drag can rotate off north AND tilt into a 3D pitch
    // (MapLibre's default dragRotate/pitchWithRotate), with no on-screen control to undo it since we
    // dropped the built-in nav controls. resetNorthPitch animates bearing + pitch back to 0 (north up,
    // flat). Driven by the Map Controls card's "Reset north" button. Primary only — animated, so the
    // camera sync carries the rest.
    window.resetMapNorth = function () { primary().resetNorthPitch(); };
    window.__isoTest = function (name) { if (!States) return; States.arm(primary()); if (name) window.stateIsoSelect(name); };

    // Fade out the launch cover (map.html #mapcover) once the initial view is ready. Called after the
    // launch mask is applied (or immediately when none is requested). Idempotent; a hard fallback also
    // fires it so a failed states.js load can never leave the app stuck on a black screen.
    var revealed = false;
    function revealMap() {
        if (revealed) return;
        revealed = true;
        var cover = document.getElementById('mapcover');
        if (cover) cover.classList.add('hidden');
    }
    // Whether to mask to CONUS at launch — the host's default, passed as a URL param so the page applies
    // the mask on first paint (under the cover) instead of flashing the world basemap while it waits for
    // the mapReady round-trip. The host still owns the CONUS toggle after this via setConusIsolation.
    var launchConus = params.get('conus') === 'true';

    // Tell the host this map is ready to receive commands. LAUNCH SEQUENCE — the primary pane only; it is
    // the only pane that exists at launch (single is the launch layout), and mapReady is a one-shot latch
    // on the host side. Called by the boot below, once the primary map exists.
    function attachLaunchSequence(map) {
        map.on('load', function () {
            // Apply the launch mask BEFORE revealing so Canada/Mexico/oceans never flash. When CONUS is the
            // launch default we wait for states.js + the boundary fetch, apply the mask, then reveal only once
            // the map reports 'idle' — i.e. it has actually FINISHED rendering the mask layer (a guessed
            // frame-count reveal was too early: the cover faded while the mask was still being drawn). A hard
            // 3 s fallback guarantees the cover lifts even if states.js failed to load.
            setTimeout(revealMap, 3000);
            var maskReady = launchConus
                ? statesReady.then(function (m) { return m ? m.setConus(map, true) : null; })
                : Promise.resolve();
            maskReady.then(function () {
                // 'idle' fires when no more rendering is pending — the mask fill is on screen by then. Nudge a
                // repaint so it fires promptly even if the map had already settled.
                map.once('idle', revealMap);
                if (map.triggerRepaint) map.triggerRepaint();
            }).catch(revealMap);

            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'mapReady' }));
            }
        });
    }

    // ---- BOOT ------------------------------------------------------------------------------------
    // Create the launch pane (single; the host switches layout later via setPaneLayout) once the style is
    // resolved against the tile source. Offline resolves synchronously to the style URL, so the default
    // path reaches this in the same microtask it always did.
    resolveStyle(styleUrl).then(function (spec) {
        styleSpec = spec;
        maps[0] = createPaneMap(0);
        // radar.js is loaded before this file, so RadarLayer is already there. Hand it the view list now so
        // it has a view before any radar command arrives — setPaneLayout would otherwise be the first to do
        // it, and a radar command that beat it would have nowhere to draw.
        if (window.RadarLayer && window.RadarLayer.setViews) window.RadarLayer.setViews(maps);
        attachLaunchSequence(maps[0]);
    }).catch(function (e) {
        // The boot runs after the synchronous try/catch below has already returned, so it carries its own:
        // without this a failure here would leave the launch cover up over a black window forever.
        console.error('map boot failed: ' + e);
        revealMap();
    });
} catch (err) {
    document.body.insertAdjacentHTML('beforeend',
        '<div style="position:absolute;top:8px;left:8px;z-index:10;' +
        'font:12px Segoe UI;background:#c00;color:#fff;padding:4px 8px;border-radius:4px;">' +
        'JS error: ' + err.message + '</div>');
}
