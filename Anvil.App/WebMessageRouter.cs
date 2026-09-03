using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Anvil.ViewModels;

namespace Anvil
{
	/// <summary>
	/// Routes JS→C# WebView2 messages (posted by map.js / radar.js) to the view models and the
	/// radar diagnostics. Extracted from MainWindow so the window isn't a giant message switch.
	/// Owns the one-shot map-ready latch.
	///
	/// Each message is a JSON object with a <c>type</c>. Synchronous messages dispatch through the
	/// <see cref="_handlers"/> table (built once in the ctor); <c>mapReady</c> is the one exception — a
	/// one-shot async latch handled inline. The per-message property reads go through the typed helpers
	/// at the bottom (<see cref="Str"/>/<see cref="Dbl"/>/…) instead of repeating the
	/// TryGetProperty/ValueKind dance at every call site.
	/// </summary>
	public sealed class WebMessageRouter
	{
		// Ramp payloads (radarRamp/radarRamps) come from JS with camelCase keys; deserialize case-insensitively.
		private static readonly JsonSerializerOptions RampJsonOptions = new() { PropertyNameCaseInsensitive = true };

		private readonly MapViewModel _viewModel;

		// type → handler for every synchronous message. mapReady is NOT here (it's an async one-shot).
		private readonly Dictionary<string, Action<JsonElement>> _handlers;

		private bool _mapReady;

		/// <summary>
		/// Raised once, right after the first map-ready is handled, so the host can run WebView setup
		/// that isn't a view-model concern (e.g. pushing the OS theme accent to the page).
		/// </summary>
		public event Func<Task>? MapReady;

		public WebMessageRouter(MapViewModel viewModel)
		{
			_viewModel = viewModel;
			_handlers = new Dictionary<string, Action<JsonElement>>
			{
				["radarLog"] = HandleRadarLog,
				["radarFrame"] = static root => Services.RadarDiagnostics.JsFrame(root),
				["radarStormMotion"] = HandleStormMotion,
				["radarBuildProgress"] = HandleBuildProgress,
				["radarRender"] = static root => Services.RadarDiagnostics.JsRender(root),
				["radarRamps"] = HandleRadarRamps,
				["radarRamp"] = HandleRadarRamp,
				["radarInspect"] = HandleRadarInspect,
				["radarSiteClick"] = HandleRadarSiteClick,
				["stateIsolated"] = HandleStateIsolated,
				["markerClick"] = HandleMarkerClick,
				["markerMoved"] = HandleMarkerMoved,
				["radarFrameReady"] = HandleRadarFrameReady,
			};
		}

		public async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
		{
			var message = args.TryGetWebMessageAsString();
			if (string.IsNullOrEmpty(message))
			{
				return;
			}

			try
			{
				using var doc = JsonDocument.Parse(message);
				var root = doc.RootElement;
				if (root.ValueKind != JsonValueKind.Object ||
					!root.TryGetProperty("type", out var typeEl))
				{
					return;
				}
				var type = typeEl.GetString();
				if (type is null)
				{
					return;
				}

				// The page posts {type:"mapReady"} once its map's 'load' fires. It's an async one-shot
				// (awaits host setup + the VM's map-ready fan-out), so it's handled here rather than in the
				// synchronous handler table. Enable style / outlook commands the first time we see it.
				if (type == "mapReady")
				{
					if (!_mapReady)
					{
						_mapReady = true;
						// Push the theme accent (MapReady) BEFORE the VM's map-ready work, which creates the
						// radar-site markers: their halo reads a CSS var, so setting it first means the markers
						// render in the accent from the start instead of flashing the green fallback for the
						// ~second OnMapsReadyAsync takes.
						if (MapReady is not null)
						{
							await MapReady.Invoke();
						}
						await _viewModel.OnMapsReadyAsync();
					}
					return;
				}

				if (_handlers.TryGetValue(type, out var handler))
				{
					handler(root);
				}
			}
			catch (JsonException)
			{
				// Ignore malformed messages from the page.
			}
		}

		// ── Handlers (one per message type) ─────────────────────────────────────────────────────────────

		// Free-form diagnostics line from radar.js (also echoed to the VS Output window).
		private static void HandleRadarLog(JsonElement root)
		{
			if (root.TryGetProperty("msg", out var msgEl))
			{
				var msg = msgEl.GetString();
				System.Diagnostics.Debug.WriteLine($"[radar-js] {msg}");
				if (msg is not null) Services.RadarDiagnostics.JsLog(msg);
			}
		}

		// The automatic (VAD-derived) storm motion the WebView computed for the displayed volume's
		// full-volume wind profile — drives the App Settings "Auto" readout. speed is m/s; the VM
		// converts to knots. `insufficient` means the profile was too shallow to trust.
		private void HandleStormMotion(JsonElement root) =>
			_viewModel.Radar.SetAutoStormMotion(
				Dbl(root, "speedMs"), Dbl(root, "dirDeg"), Str(root, "source"), Flag(root, "insufficient"),
				Str(root, "why"), Str(root, "tier"));

		// Velocity build progress: how many loaded frames have their (lazily-built, dealiased) velocity
		// geometry ready (drives the "Building velocity N/M" readout + holds playback at the built frontier),
		// plus the per-frame trio (refl+vel+SRV) completeness that drives the scrubber fill.
		private void HandleBuildProgress(JsonElement root) =>
			_viewModel.Radar.SetBuildProgress(
				Int(root, "built"), Int(root, "total"), BoolArray(root, "ready"), BoolArray(root, "complete"));

		// The FULL ramp table keyed by product id (pushed once when radar-ramps.js loads) — lets the Product
		// combo draw every product's scale next to its name, not just the active one.
		private void HandleRadarRamps(JsonElement root)
		{
			if (root.TryGetProperty("ramps", out var rampsEl))
			{
				var ramps = JsonSerializer.Deserialize<Dictionary<string, Models.RadarRampInfo>>(
					rampsEl.GetRawText(), RampJsonOptions);
				_viewModel.Radar.SetAllRamps(ramps);
			}
		}

		// The active product's color ramp (pushed from radar-ramps.js) — feeds the inspect marker.
		private void HandleRadarRamp(JsonElement root)
		{
			if (root.TryGetProperty("ramp", out var rampEl))
			{
				var ramp = JsonSerializer.Deserialize<Models.RadarRampInfo>(rampEl.GetRawText(), RampJsonOptions);
				_viewModel.Radar.SetColorScale(ramp);
			}
		}

		// Inspect-mode value under the cursor (pushed from radar.js as the pointer moves) — drives the live
		// tick on that pane's chip ramp. has=false clears it. `pane` says WHICH pane reported: Inspect is
		// one cursor over the map, but each pane reads its own product's grid at that point, so the panes
		// report independently.
		private void HandleRadarInspect(JsonElement root)
		{
			var has = Flag(root, "has");
			double? val = has ? DblOrNull(root, "value") : null;
			_viewModel.Radar.SetInspectValue(Int(root, "pane"), val);
		}

		// A radar site marker was clicked.
		private void HandleRadarSiteClick(JsonElement root)
		{
			if (root.TryGetProperty("id", out var siteEl))
			{
				_viewModel.Radar.OnRadarSiteClicked(siteEl.GetString());
			}
		}

		// State Isolation reported which state is isolated (name) or that it cleared (name absent/null).
		private void HandleStateIsolated(JsonElement root) =>
			_viewModel.StateIso.OnStateIsolated(Str(root, "name"));

		// A map marker (e.g. the user-location marker) was clicked — select it for editing.
		private void HandleMarkerClick(JsonElement root)
		{
			if (root.TryGetProperty("id", out var mIdEl))
			{
				_viewModel.Markers.OnMarkerClicked(mIdEl.GetString());
			}
		}

		// A draggable marker was moved — record the refined position (needs all three fields).
		private void HandleMarkerMoved(JsonElement root)
		{
			if (root.TryGetProperty("id", out var idEl) &&
				root.TryGetProperty("lng", out var lngEl) &&
				root.TryGetProperty("lat", out var latEl))
			{
				_viewModel.Markers.OnMarkerMoved(idEl.GetString(), lngEl.GetDouble(), latEl.GetDouble());
			}
		}

		// A radar loop frame finished decoding in the WebView.
		private void HandleRadarFrameReady(JsonElement root)
		{
			if (root.TryGetProperty("index", out var idxEl))
			{
				_viewModel.Radar.OnRadarFrameReady(idxEl.GetInt32(), Flag(root, "hasData"));
			}
		}

		// ── Typed JSON readers: pull a named property with a sane default, so a missing/mistyped field
		//    degrades gracefully instead of throwing. ────────────────────────────────────────────────────

		// True only when the named property is present and JSON `true`.
		private static bool Flag(JsonElement root, string name) =>
			root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

		private static double Dbl(JsonElement root, string name, double fallback = 0) =>
			root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : fallback;

		private static double? DblOrNull(JsonElement root, string name) =>
			root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

		private static int Int(JsonElement root, string name, int fallback = 0) =>
			root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : fallback;

		private static string? Str(JsonElement root, string name) =>
			root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

		// A JSON bool array → bool[] (each element true iff JSON `true`), or null when the field is absent
		// or not an array. Used for the per-frame ready/complete flags.
		private static bool[]? BoolArray(JsonElement root, string name)
		{
			if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
			{
				return null;
			}
			var arr = new bool[el.GetArrayLength()];
			var i = 0;
			foreach (var e in el.EnumerateArray())
			{
				arr[i++] = e.ValueKind == JsonValueKind.True;
			}
			return arr;
		}
	}
}
