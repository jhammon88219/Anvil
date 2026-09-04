using System.Collections.Generic;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Application-level operations against the map. Talks to it only through
	/// <see cref="IMapView"/>; has no knowledge of WebView2 or the UI.
	/// </summary>
	public interface IMapService
	{
		/// <summary>Applies the given style to the map (preserves the current camera).</summary>
		Task ApplyStyleAsync(MapStyle style);

		/// <summary>
		/// Switches where the basemap's vector tiles come from: the bundled offline PMTiles file
		/// (<paramref name="online"/> false) or <paramref name="tilesUrl"/> (a Protomaps-schema TileJSON
		/// endpoint, a <c>{z}/{x}/{y}</c> template, or a remote <c>pmtiles://</c> archive).
		/// <para>⚠️ This does NOT change the style — the bundled styles are Protomaps-SCHEMA styles whose
		/// only tie to the offline file is one source url, so both modes render identically and glyphs +
		/// sprites stay local either way. The page re-resolves the CURRENT style against the new source and
		/// re-applies it, taking the same setStyle + overlay re-add path as a basemap change.</para>
		/// </summary>
		Task SetTileSourceAsync(bool online, string tilesUrl);

		/// <summary>
		/// Sets the pane grid: <paramref name="columns"/> x <paramref name="rows"/> maps in the one
		/// WebView, separated by a <paramref name="gutterPx"/>-wide groove. The page creates/destroys
		/// panes around the change; the camera, the loaded loop and every overlay survive it.
		/// <paramref name="gutterPx"/> is passed rather than hardcoded in the page so the page and the
		/// XAML watermark grid share one constant (<see cref="PaneLayoutInfo.GutterPx"/>).
		/// </summary>
		Task SetPaneLayoutAsync(int columns, int rows, int gutterPx);

		/// <summary>
		/// Shows the given SPC outlook product on the map (adds/replaces a GeoJSON
		/// source + fill/line layers loaded from the product's local cache URL).
		/// </summary>
		Task ShowOutlookAsync(SpcOutlookProduct product);

		/// <summary>Removes the SPC outlook overlay from the map, if any.</summary>
		Task ClearOutlookAsync();

		/// <summary>
		/// Sets the fill opacity (0-1) of the outlook polygons. The outlines are
		/// unaffected, so the basemap reads through the fill.
		/// </summary>
		Task SetOutlookOpacityAsync(double opacity);

		/// <summary>Starts a new radar loop for the site (clears any existing frames).</summary>
		Task BeginRadarLoopAsync(RadarSite site);

		/// <summary>
		/// Adds a cached volume as frame <paramref name="index"/> of the current loop; the
		/// WebView fetches + decodes it off-thread and posts back when the frame is ready.
		/// </summary>
		Task AddRadarFrameAsync(string localUrl, int index);

		/// <summary>Shows the loop frame at <paramref name="index"/>.</summary>
		Task ShowRadarFrameAsync(int index);

		/// <summary>
		/// Incrementally refreshes the loop: reindexes the already-decoded frames to a new ordering
		/// (reusing their geometry) instead of rebuilding from scratch, so a periodic reload doesn't
		/// blank the layer or re-decode unchanged volumes. <paramref name="mappingJson"/> is an array
		/// of <c>[fromIndex, toIndex]</c> pairs; the host then adds only the genuinely-new frames.
		/// </summary>
		Task RemapRadarFramesAsync(int newCount, string mappingJson);

		/// <summary>
		/// Re-decodes the whole loop at a newly-selected ELEVATION without tearing it down: the frames
		/// already on screen stay up (marked stale) until the host re-adds each index at the new tilt's
		/// URL, so the map never blanks (docs/radar-loop-flow.md Rule 7). The scrubber empties and
		/// re-fills as the new cut lands. <paramref name="count"/> is the current frame count.
		/// </summary>
		Task RetileRadarLoopAsync(int count);

		/// <summary>Removes the radar layer and clears the loop.</summary>
		Task ClearRadarAsync();

		/// <summary>Sets the radar layer opacity (0-1).</summary>
		Task SetRadarOpacityAsync(double opacity);

		/// <summary>Sets the rendered radar moment by its product id (e.g. "reflectivity", "velocity",
		/// "cc") — one of the ids in the JS registry (radar-products.js) / <c>RadarProductOptions</c>.
		/// <para><paramref name="paneIndex"/> is which pane to set: a pane is a product view, so this is the
		/// only radar command that addresses one. Everything else about a loop — the frames, the time
		/// cursor, the site, the tilt — is shared by every pane.</para></summary>
		Task SetRadarProductAsync(int paneIndex, string product);

		/// <summary>Speculatively builds velocity geometry for the loaded loop in the background (before
		/// the user selects the Velocity product), so a later switch to Velocity is instant. Host calls
		/// this once the reflectivity loop has finished rendering.</summary>
		Task PrefetchRadarVelocityAsync();

		/// <summary>Pre-warms the WebView's decode + VWP workers (creating them and loading the vendored
		/// decoder) so the FIRST site click doesn't pay their cold start on the first-paint critical path.
		/// Host calls this once at map-ready, before any loop. Idempotent + best-effort.</summary>
		Task PrewarmRadarAsync();

		/// <summary>Computes the AUTOMATIC (VAD-derived) storm motion for ONE volume from its bottom velocity
		/// tilts. <paramref name="tiltUrls"/> are the local (radarlevel2-host) URLs of those tilts (base first);
		/// the WebView fetches them, builds a full-volume VAD wind profile → Bunkers right-mover off-thread,
		/// caches the result per volume, applies it to that volume's SRV, and posts it back as
		/// <c>{type:"radarStormMotion"}</c> for the readout. A single low tilt is too shallow for a correct
		/// profile, which is why the whole set is needed. No-op while manual mode is active.</summary>
		Task ComputeStormMotionAsync(IReadOnlyList<string> tiltUrls);

		/// <summary>Pushes a storm motion computed by the HOST's wind-profile provider chain (Level III NVW)
		/// straight into the renderer, skipping our own VAD entirely.</summary>
		/// <remarks>⚠️ Call this OR <see cref="ComputeStormMotionAsync"/> for a given loop, never both — the
		/// chain decides. Doc 01 §5 orders the providers; the local VAD is the fallback.</remarks>
		Task SetStormMotionAsync(double speedMs, double directionDeg, string source, int levels, double topM, string tier);

		/// <summary>
		/// Enables or disables inspect mode (read the value under the cursor). While on, the WebView
		/// shows a value tooltip at the pointer and posts the value for the color-scale marker.
		/// </summary>
		Task SetRadarInspectAsync(bool enabled);

		/// <summary>
		/// Provides the radar sites to the map as clickable on-map markers. JSON is an array
		/// of <c>{ id, name, lng, lat }</c>.
		/// </summary>
		Task ShowRadarSitesAsync(string sitesJson);

		/// <summary>
		/// Tells the page where to load the cached SPC watch-box GeoJSON from. The page fetches it
		/// lazily (only when watches are shown) and re-fetches on each refresh push.
		/// </summary>
		Task SetWatchSourceAsync(string url);

		/// <summary>Shows or hides the SPC watch boxes (Tornado / Severe Thunderstorm Watches).</summary>
		Task SetWatchesVisibleAsync(bool visible);

		/// <summary>
		/// Restricts the watch boxes to the given phenomena — each flag draws that type (TO / SV) and
		/// nothing else. Independent of <see cref="SetWatchesVisibleAsync"/>: the host drives visibility
		/// from "is either type shown", and this decides which of the two are drawn when it is.
		/// </summary>
		Task SetWatchKindsAsync(bool tornado, bool severe);

		/// <summary>
		/// Sets the overall opacity (0-1) of the watch polygons. Scales both the faint fill and the bold
		/// outline together, so the slider fades the whole overlay (1 = the default look).
		/// </summary>
		Task SetWatchesOpacityAsync(double opacity);

		/// <summary>
		/// Tells the page where to load the cached storm-based warning GeoJSON from. The page fetches it
		/// lazily (only when warnings are shown) and re-fetches on each refresh push.
		/// </summary>
		Task SetWarningSourceAsync(string url);

		/// <summary>Shows or hides the storm-based warning polygons (Tornado / Severe Thunderstorm
		/// Warnings). These sit above the watch boxes.</summary>
		Task SetWarningsVisibleAsync(bool visible);

		/// <summary>
		/// Restricts the warning polygons to the given phenomena — each flag draws that type (TO / SV) and
		/// nothing else. Same relationship to <see cref="SetWarningsVisibleAsync"/> as the watch pair.
		/// </summary>
		Task SetWarningKindsAsync(bool tornado, bool severe);

		/// <summary>
		/// Sets the overall opacity (0-1) of the warning polygons. Scales both the faint fill and the bold
		/// outline together, so the slider fades the whole overlay (1 = the default look).
		/// </summary>
		Task SetWarningsOpacityAsync(double opacity);

		/// <summary>
		/// Tells the page where to load the cached storm-report GeoJSON (Tornado / Wind / Hail points) from.
		/// The page fetches it with no-store (today's file grows through the day) and re-renders.
		/// </summary>
		Task SetStormReportsSourceAsync(string url);

		/// <summary>Shows the storm-report dots by type — each flag toggles that type's layer independently
		/// (all false hides the overlay without tearing down the source).</summary>
		Task SetStormReportKindsAsync(bool tornado, bool wind, bool hail);

		/// <summary>Sets the overall opacity (0-1) of the storm-report dots.</summary>
		Task SetStormReportsOpacityAsync(double opacity);

		/// <summary>
		/// Tears the storm-report overlay down: drops the source, the three layers and any open popup, and
		/// forgets the loaded day.
		/// </summary>
		/// <remarks>
		/// ⚠️ This exists because there was NO way to empty the overlay, only to re-point it at another day.
		/// Every path that ends with "there is nothing to show" — leaving a loaded replay, a day whose
		/// reports won't fetch — used to just return, leaving the PREVIOUS day's dots drawn over the new
		/// mode's map. It also resets the page's own kind flags, so the caller must re-push
		/// <see cref="SetStormReportKindsAsync"/> when a day comes back (the normal show path already does).
		/// </remarks>
		Task ClearStormReportsAsync();

		/// <summary>Reads the page's storm-report state back as a short JSON string (url, feature count, kind
		/// flags, whether the layers exist) — a diagnostic seam, so a "the dots are still there" report can be
		/// answered with what the PAGE thinks it is drawing rather than with what the host believes it pushed.</summary>
		/// <remarks>
		/// ⚠️ DELIBERATELY UNCALLED — break-glass, not plumbing. It is what settled the 2026-09-04 stale-dots
		/// bug after two rounds were lost to reasoning about which side was wrong, so the seam is kept wired
		/// end to end (here → MapService → window.describeStormReports → storm-reports.js describe()) and you
		/// add the ONE await where you are debugging. Don't delete it as dead code, and don't wire it into the
		/// normal show path — a readback on every push is noise. ⚠️ feats=-1 means the page's fetch had not
		/// resolved yet, not that data is missing.
		/// </remarks>
		Task<string> DescribeStormReportsAsync();

		/// <summary>Highlights the selected site marker (empty clears the highlight).</summary>
		Task SetSelectedRadarSiteAsync(string? siteId);

		/// <summary>
		/// Stops/removes the selected site's radar sweep (call with <paramref name="periodSeconds"/>
		/// &lt;= 0 on clear / entering replay). The sweep is a one-shot pulse now — see
		/// <see cref="PulseRadarSweepAsync"/> to fire one on a new frame.
		/// </summary>
		Task SetRadarSweepAsync(double periodSeconds);

		/// <summary>
		/// Fires ONE radar-sweep pulse (arm + trailing afterglow, one revolution then hides) — called
		/// when a genuinely-new frame lands, as a "fresh data arrived" cue. The range ring stays up.
		/// </summary>
		Task PulseRadarSweepAsync();

		/// <summary>
		/// Shows or hides all radar site marker buttons. Independent of the radar layer —
		/// hiding the markers never clears or hides an active radar loop.
		/// </summary>
		Task SetRadarSitesVisibleAsync(bool visible);

		/// <summary>
		/// Shows or hides just the research/test radar markers (e.g. KCRI) — the "Show Research
		/// Radars" toggle. Off by default; operational markers and any active loop are unaffected.
		/// </summary>
		Task SetResearchRadarsVisibleAsync(bool visible);

		/// <summary>
		/// Shows or hides just the Terminal Doppler Weather Radar markers (the FAA `T***` network) —
		/// the "Show TDWRs" toggle. Off by default; operational markers and any active loop are unaffected.
		/// </summary>
		Task SetTdwrsVisibleAsync(bool visible);

		/// <summary>
		/// Marks which site markers are offline (no recent data in the feed). JSON is an array
		/// of site IDs; those markers render in the muted "offline" style.
		/// </summary>
		Task SetRadarSitesStatusAsync(string offlineIdsJson);

		/// <summary>
		/// Sets the accent color driving the "available" site-marker status halo, so it matches the
		/// OS theme accent (like the OverlayBar's accent drop-shadow). <paramref name="borderColor"/>
		/// is a CSS color for the ring; <paramref name="glowColor"/> a CSS color for its soft glow.
		/// </summary>
		Task SetRadarSiteAccentAsync(string borderColor, string glowColor);

		// ── State Isolation ──
		// Armed = the WebView highlights a US state on hover and isolates it on click (covers everything
		// outside that state with the basemap's water color). The hover/click + masking live entirely in
		// states.js; these just arm/disarm the mode and, optionally, drive it programmatically.

		/// <summary>Arms or disarms State Isolation mode. Armed = hover-to-highlight + click-to-isolate a
		/// state on the map; disarmed restores the full map (dropping any active isolation).</summary>
		Task SetStateIsolationAsync(bool armed);

		/// <summary>Programmatically isolates a state by name (e.g. "Texas"); arms the mode if it wasn't.
		/// The on-map click path does this in the WebView directly — this is for a picker / stream-mode presets.</summary>
		Task SelectIsolatedStateAsync(string name);

		/// <summary>Exits isolation but stays armed (back to hover mode so another state can be picked).</summary>
		Task ClearStateIsolationAsync();

		/// <summary>Sets the base map extent: on = mask everything outside CONUS (the contiguous 48 + DC),
		/// off = the full map. Independent of single-state isolation (an isolated state still overrides it
		/// until cleared). CONUS is the launch default.</summary>
		Task SetConusIsolationAsync(bool on);

		/// <summary>Frames the current region of interest into view (center + zoom via fitBounds): the
		/// isolated state if one is isolated, else CONUS. A "fit to view" / recenter action.</summary>
		Task FitMapToViewAsync();

		/// <summary>Resets the map's orientation — animates bearing and pitch back to 0 (north up, flat) —
		/// undoing a right-click-drag rotate/tilt. A "reset north" action.</summary>
		Task ResetMapOrientationAsync();

		/// <summary>Animates the map to the given center and zoom.</summary>
		Task FlyToAsync(double longitude, double latitude, double zoom);

		/// <summary>
		/// Places (or moves) the user-location marker at the given coordinates. <paramref name="label"/>
		/// is the marker tooltip (e.g. the resolved place name or "Device location").
		/// </summary>
		Task ShowUserLocationAsync(double longitude, double latitude, string label);

		/// <summary>Removes the user-location marker, if any.</summary>
		Task ClearUserLocationAsync();

		/// <summary>
		/// Shows a single curated DOW (mobile-radar) frame from its <c>dowevents</c> host URL, reusing
		/// the radar render pipeline. The WebView fetches the <c>.dow.json</c> and decodes it on the
		/// main thread (one sweep), centred on the truck's position carried in the frame.
		/// </summary>
		Task ShowDowFrameAsync(string url);

		/// <summary>Removes the shown DOW frame (clears the radar layer).</summary>
		Task ClearDowFrameAsync();

		// ── Dev-only: velocity-dealias validation harness (see RadarValidationViewModel) ──
		// The scorer replays a fixed corpus through the real decode/dealias path and reports each
		// volume's over-unfold ratio. Because the async decode's Promise can't be awaited through
		// ExecuteScriptAsync, the VM starts the run then polls a JS progress global (like the site
		// sweep polls RadarDiagnostics), rather than routing a message back.

		/// <summary>Starts a validation run over the given corpus. <paramref name="entriesJson"/> is a JSON
		/// array of <c>{ id, url, lat, lon }</c>; the WebView decodes each volume (forcing the velocity
		/// dealias build) and accumulates results into its <c>window.__anvilValidation</c> global.</summary>
		Task StartRadarValidationAsync(string entriesJson);

		/// <summary>Reads back the current run's progress global as JSON (<c>{ total, done, finished,
		/// results:[{id, gatesOver, gatesTotal, ratio, error}] }</c>), or the literal <c>null</c> before a
		/// run starts. Polled until <c>finished</c>.</summary>
		Task<string> PollRadarValidationAsync();

		/// <summary>Signals the in-flight validation run to stop after the current volume.</summary>
		Task CancelRadarValidationAsync();

		// PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit): read-only inner-state poll.
		/// <summary>Reads a read-only snapshot of the radar loop's inner build state (per-frame
		/// per-product build codes + VWP/storm-motion state) as JSON, or the literal <c>null</c> when no
		/// loop is loaded. Polled by <c>PipelineConsoleViewModel</c> only while the console card is open.</summary>
		Task<string> GetPipelineSnapshotAsync();
	}
}
