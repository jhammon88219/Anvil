using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IMapService"/>. Drives the map exclusively through the
	/// <see cref="IMapView"/> seam, so it never touches WebView2 or the UI.
	/// </summary>
	public sealed class MapService : IMapService
	{
		// Set once at startup by the composition root (see Attach), not injected via the ctor: MainWindow
		// IS the IMapView and also (transitively) depends on this service, so a ctor arg would be a
		// container resolution cycle. Non-null before any map command runs (attach happens in MainWindow's
		// ctor, long before mapReady).
		private IMapView _mapView = null!;

		public MapService() { }

		/// <summary>Attaches the view that executes the JS commands. Called once at startup by the
		/// composition root (MainWindow, which implements <see cref="IMapView"/>) right after the container
		/// creates both — this breaks the MainWindow↔MapService constructor cycle.</summary>
		public void Attach(IMapView mapView) => _mapView = mapView;

		public Task ApplyStyleAsync(MapStyle style) =>
			_mapView.RunScriptAsync(Call("applyStyle", $"https://mapassets/{style.FileName}"));

		// Tile source: the style file is unchanged either way — the page patches its ONE basemap source.
		// ⚠️ The URL is the only USER-TYPED string that reaches the page, and FormatArg quotes without
		// escaping, so a stray quote/backslash would break the whole script line. Neither is legal in a URL,
		// so escaping them here (rather than widening FormatArg for every other call site) is enough.
		public Task SetTileSourceAsync(bool online, string tilesUrl) =>
			_mapView.RunScriptAsync(Call("setTileSource", online ? "online" : "offline",
				(tilesUrl ?? "").Replace("\\", "\\\\").Replace("'", "\\'")));

		public Task SetPaneLayoutAsync(int columns, int rows, int gutterPx) =>
			_mapView.RunScriptAsync(Call("setPaneLayout", columns, rows, gutterPx));

		// SPC outlooks load the GeoJSON from the product's local cache URL.
		public Task ShowOutlookAsync(SpcOutlookProduct product) =>
			_mapView.RunScriptAsync(Call("showOutlook", product.LocalUrl));

		public Task ClearOutlookAsync() =>
			_mapView.RunScriptAsync(Call("clearOutlook"));

		public Task SetOutlookOpacityAsync(double opacity) =>
			_mapView.RunScriptAsync(Call("setOutlookOpacity", opacity));

		// SPC watch boxes: point the page at the cached watch GeoJSON, and toggle the layers.
		public Task SetWatchSourceAsync(string url) =>
			_mapView.RunScriptAsync(Call("setWatchSource", url));

		public Task SetWatchesVisibleAsync(bool visible) =>
			_mapView.RunScriptAsync(Call("setWatchesVisible", visible));

		public Task SetWatchKindsAsync(bool tornado, bool severe) =>
			_mapView.RunScriptAsync(Call("setWatchKinds", tornado, severe));

		public Task SetWatchesOpacityAsync(double opacity) =>
			_mapView.RunScriptAsync(Call("setWatchesOpacity", opacity));

		// Storm-based warning polygons: point the page at the cached warning GeoJSON, and toggle the layers.
		public Task SetWarningSourceAsync(string url) =>
			_mapView.RunScriptAsync(Call("setWarningSource", url));

		public Task SetWarningsVisibleAsync(bool visible) =>
			_mapView.RunScriptAsync(Call("setWarningsVisible", visible));

		public Task SetWarningKindsAsync(bool tornado, bool severe) =>
			_mapView.RunScriptAsync(Call("setWarningKinds", tornado, severe));

		public Task SetWarningsOpacityAsync(double opacity) =>
			_mapView.RunScriptAsync(Call("setWarningsOpacity", opacity));

		// SPC storm reports (Tornado / Wind / Hail verification dots): point the page at the cached GeoJSON,
		// toggle which types show, and set the overall opacity.
		public Task SetStormReportsSourceAsync(string url) =>
			_mapView.RunScriptAsync(Call("setStormReportsSource", url));

		public Task SetStormReportKindsAsync(bool tornado, bool wind, bool hail) =>
			_mapView.RunScriptAsync(Call("setStormReportKinds", tornado, wind, hail));

		public Task SetStormReportsOpacityAsync(double opacity) =>
			_mapView.RunScriptAsync(Call("setStormReportsOpacity", opacity));

		public Task ClearStormReportsAsync() =>
			_mapView.RunScriptAsync(Call("clearStormReports"));

		public Task<string> DescribeStormReportsAsync() =>
			_mapView.RunScriptAsync(Call("describeStormReports"));

		// The loop is driven frame-by-frame: begin (with the site's antenna coords, needed to
		// project the gates), then add each cached volume URL as a frame, then show by index.
		public Task BeginRadarLoopAsync(RadarSite site) =>
			_mapView.RunScriptAsync(Call("radarBeginLoop", site.Latitude, site.Longitude));

		public Task AddRadarFrameAsync(string localUrl, int index) =>
			_mapView.RunScriptAsync(Call("radarAddFrame", localUrl, index));

		public Task ShowRadarFrameAsync(int index) =>
			_mapView.RunScriptAsync(Call("radarShowFrame", index));

		// mappingJson is a JSON array of [from,to] index pairs; Call single-quotes it and the JS
		// shim JSON.parses it (same pattern as the radar-sites payload).
		public Task RemapRadarFramesAsync(int newCount, string mappingJson) =>
			_mapView.RunScriptAsync(Call("radarRemap", newCount, mappingJson));

		public Task RetileRadarLoopAsync(int count) =>
			_mapView.RunScriptAsync(Call("radarRetile", count));

		public Task ClearRadarAsync() =>
			_mapView.RunScriptAsync(Call("clearLevel2Radar"));

		public Task SetRadarOpacityAsync(double opacity) =>
			_mapView.RunScriptAsync(Call("setRadarOpacity", opacity));

		public Task SetRadarProductAsync(int paneIndex, string product) =>
			_mapView.RunScriptAsync(Call("setRadarProduct", paneIndex, product));

		public Task PrefetchRadarVelocityAsync() =>
			_mapView.RunScriptAsync(Call("prefetchRadarVelocity"));

		public Task PrewarmRadarAsync() =>
			_mapView.RunScriptAsync(Call("prewarmRadarWorkers"));

		// tiltUrls → a JSON array the shim JSON.parses (same single-quoted-payload pattern as radarRemap /
		// radarValidate). The WebView computes the VAD → Bunkers motion off-thread and posts it back.
		public Task ComputeStormMotionAsync(IReadOnlyList<string> tiltUrls) =>
			_mapView.RunScriptAsync(Call("computeStormMotion", System.Text.Json.JsonSerializer.Serialize(tiltUrls)));

		// ⚠️ `source` is passed RAW: Call's FormatArg already wraps a string in quotes. Serializing it first
		// double-encoded it and the readout literally showed "NVW" with the quotes in it.
		public Task SetStormMotionAsync(
			double speedMs, double directionDeg, string source, int levels, double topM, string tier) =>
			_mapView.RunScriptAsync(Call("setStormMotion", speedMs, directionDeg, source, levels, topM, tier));

		public Task SetRadarInspectAsync(bool enabled) =>
			_mapView.RunScriptAsync(Call("setRadarInspect", enabled));

		public Task ShowRadarSitesAsync(string sitesJson) =>
			_mapView.RunScriptAsync(Call("showRadarSites", sitesJson));

		public Task SetSelectedRadarSiteAsync(string? siteId) =>
			_mapView.RunScriptAsync(Call("setSelectedRadarSite", siteId ?? string.Empty));

		public Task SetRadarSweepAsync(double periodSeconds) =>
			_mapView.RunScriptAsync(Call("setRadarSweep", periodSeconds));

		public Task PulseRadarSweepAsync() =>
			_mapView.RunScriptAsync(Call("pulseRadarSweep"));

		public Task SetRadarSitesVisibleAsync(bool visible) =>
			_mapView.RunScriptAsync(Call("setRadarSitesVisible", visible));

		public Task SetResearchRadarsVisibleAsync(bool visible) =>
			_mapView.RunScriptAsync(Call("setResearchRadarsVisible", visible));

		public Task SetTdwrsVisibleAsync(bool visible) =>
			_mapView.RunScriptAsync(Call("setTdwrsVisible", visible));

		public Task SetRadarSitesStatusAsync(string offlineIdsJson) =>
			_mapView.RunScriptAsync(Call("setRadarSitesStatus", offlineIdsJson));

		public Task SetRadarSiteAccentAsync(string borderColor, string glowColor) =>
			_mapView.RunScriptAsync(Call("setRadarSiteAccent", borderColor, glowColor));

		// State Isolation: arm/disarm the mode, or drive it programmatically. The hover/click + masking are
		// in states.js; these delegate to its window.stateIso* shims.
		public Task SetStateIsolationAsync(bool armed) =>
			_mapView.RunScriptAsync(Call(armed ? "stateIsoArm" : "stateIsoDisarm"));

		public Task SelectIsolatedStateAsync(string name) =>
			_mapView.RunScriptAsync(Call("stateIsoSelect", name));

		public Task ClearStateIsolationAsync() =>
			_mapView.RunScriptAsync(Call("stateIsoClear"));

		public Task SetConusIsolationAsync(bool on) =>
			_mapView.RunScriptAsync(Call("setConusIsolation", on));

		public Task FitMapToViewAsync() =>
			_mapView.RunScriptAsync(Call("fitMapToView"));

		public Task ResetMapOrientationAsync() =>
			_mapView.RunScriptAsync(Call("resetMapNorth"));

		public Task FlyToAsync(double longitude, double latitude, double zoom) =>
			_mapView.RunScriptAsync(Call("flyTo", longitude, latitude, zoom));

		public Task ShowUserLocationAsync(double longitude, double latitude, string label) =>
			_mapView.RunScriptAsync(Call("showUserLocation", longitude, latitude, label));

		public Task ClearUserLocationAsync() =>
			_mapView.RunScriptAsync(Call("clearUserLocation"));

		public Task ShowDowFrameAsync(string url) =>
			_mapView.RunScriptAsync(Call("showDowFrame", url));

		public Task ClearDowFrameAsync() =>
			_mapView.RunScriptAsync(Call("clearDowFrame"));

		// Dev-only velocity-dealias validation harness. entriesJson is single-quoted and JSON.parsed in
		// the shim (same pattern as the remap payload). The poll returns the progress global's JSON
		// directly (ExecuteScriptAsync serializes the object) so the VM parses it without double-decoding.
		public Task StartRadarValidationAsync(string entriesJson) =>
			_mapView.RunScriptAsync(Call("radarValidate", entriesJson));

		public Task<string> PollRadarValidationAsync() =>
			_mapView.RunScriptAsync("(window.__anvilValidation||null)");

		public Task CancelRadarValidationAsync() =>
			_mapView.RunScriptAsync("if(window.__anvilValidation){window.__anvilValidation.cancel=true;}");

		// PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit). Returns the snapshot object's JSON
		// directly (ExecuteScriptAsync serializes it) so the VM parses it without double-decoding, or "null".
		public Task<string> GetPipelineSnapshotAsync() =>
			_mapView.RunScriptAsync("(window.radarPipelineSnapshot?window.radarPipelineSnapshot():null)");

		// Builds a "window.fn(a,b,c);" call string, formatting each argument for JS:
		// doubles in invariant culture, bools lowercased, strings single-quoted. This
		// centralizes the JS string-building (and culture handling) for every command.
		private static string Call(string function, params object[] args)
		{
			var rendered = string.Join(",", args.Select(FormatArg));
			return $"window.{function}({rendered});";
		}

		private static string FormatArg(object arg) => arg switch
		{
			double d => d.ToString(CultureInfo.InvariantCulture),
			bool b => b ? "true" : "false",
			string s => $"'{s}'",
			_ => arg?.ToString() ?? "null"
		};
	}
}
