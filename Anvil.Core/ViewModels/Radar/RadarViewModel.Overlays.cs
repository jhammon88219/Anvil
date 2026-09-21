using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Anvil.ViewModels
{
	// RadarViewModel (partial): the inspector (read the value under the cursor).
	// ⚠️ The color-scale LEGEND is not here and is not on this VM at all — it is per PANE now
	// (RadarPaneViewModel.Ramp → Primitives/ProductColorRamp in the notch), fed by the ONE whole-table
	// `radarRamps` push. A global CurrentRamp/RampTitle/RampMin|Mid|MaxText set used to live here for the
	// deleted Color Scale tool window, fed by a SECOND per-product `radarRamp` push; both are gone.
	public sealed partial class RadarViewModel
	{
		// ── Inspector ("read the value under the cursor", RadarScope-style) ────────────────────────────
		// Inspect is a GLOBAL instrument: one armed cursor mode over the whole map, not a per-pane toggle.
		// The VALUE, though, is per pane — at one lat/lon each pane reads its OWN product's grid, so four
		// panes give four readings of the same point, each ticking on its own notch ramp. That is the whole
		// point of it in multi-pane: four numbers for one gate, read across the four notches.
		// The value tooltip itself is drawn in the WebView next to the cursor (instant, no host round-trip
		// per mouse move); the host only receives the numbers that drive the notch ticks.
		private bool _isInspecting;

		/// <summary>Whether inspect mode is engaged (the bottom bar's Inspect key). One mode for every
		/// pane.</summary>
		public bool IsInspecting
		{
			get => _isInspecting;
			set
			{
				if (!SetProperty(ref _isInspecting, value))
				{
					return;
				}

				foreach (var pane in Panes)
				{
					pane.SetInspecting(value); // clears each pane's tick on the way out
				}

				if (_isMapReady)
				{
					_ = _mapService.SetRadarInspectAsync(value);
				}
			}
		}

		// ── Range ruler ("how far is that, and how much coverage is left past it") ────────────────────
		// The second armed instrument over the same map, and deliberately shaped like the first: ONE global
		// mode, no per-pane copy, not persisted. What it measures is geographic, so every pane draws the
		// spoke; only the handles you grab are on the primary.
		// ⚠️ NOT PERSISTED, and not replayed in OnMapsReadyAsync — like Inspect, and unlike ShowTdwrs. It is
		// a gesture you make while reading a loop, so launching into an armed ruler with no radar up would
		// read as the app having left something switched on.
		private bool _isRangeRulerOn;

		/// <summary>Whether the range ruler is armed (the tools tier's Ruler key). One spoke for every pane.</summary>
		public bool IsRangeRulerOn
		{
			get => _isRangeRulerOn;
			set
			{
				if (!SetProperty(ref _isRangeRulerOn, value))
				{
					return;
				}

				if (_isMapReady)
				{
					_ = _mapService.SetRangeRulerAsync(value);
				}
			}
		}

		// ── Range ruler ANCHOR (site, or the user's location) ──────────────────────────────────────────
		// The anchor is a PREFERENCE (persisted, Settings → Radar), unlike the armed mode above. The location
		// itself belongs to MarkersViewModel; MapViewModel forwards every change of it to SetRulerLocation, so
		// this VM holds only a copy of the point and never reaches into Markers.

		private double? _rulerLat, _rulerLon;

		/// <summary>The anchor picker's labels, in <see cref="Models.RulerAnchors.All"/> order.</summary>
		public IReadOnlyList<string> RulerAnchorLabels { get; } = Models.RulerAnchors.Labels;

		/// <summary>Two-way for the Radar tab's anchor picker. PERSISTED as the token, never the index.</summary>
		public int RulerAnchorIndex
		{
			get => Models.RulerAnchors.IndexOf(_settings.Settings.RulerAnchor);
			set
			{
				var token = Models.RulerAnchors.FromIndex(value);
				if (token == Models.RulerAnchors.Normalize(_settings.Settings.RulerAnchor))
				{
					return;
				}
				_settings.Settings.RulerAnchor = token; // persists (auto-save)
				OnPropertyChanged();
				_ = PushRulerAnchorAsync();
			}
		}

		/// <summary>Called by the coordinator whenever the user-location marker is placed, dragged, re-located
		/// or removed (null = no marker).</summary>
		public void SetRulerLocation(double? latitude, double? longitude)
		{
			_rulerLat = latitude;
			_rulerLon = longitude;
			_ = PushRulerAnchorAsync();
		}

		// ⚠️ Pushed even when the anchor is Site: it costs one call and keeps the page's copy of the point
		// current, so flipping the picker to My location later draws from the RIGHT place at once.
		private Task PushRulerAnchorAsync()
		{
			if (!_isMapReady)
			{
				return Task.CompletedTask;
			}
			var wantLocation = Models.RulerAnchors.Normalize(_settings.Settings.RulerAnchor) == Models.RulerAnchors.Location;
			var has = _rulerLat is not null && _rulerLon is not null;
			return _mapService.SetRangeRulerAnchorAsync(wantLocation, has, _rulerLon ?? 0, _rulerLat ?? 0);
		}

		// ── Scope colour (range ring + ruler) ─────────────────────────────────────────────────────────
		/// <summary>The swatches, in picker order: each preset's hex (empty = the theme's colour).</summary>
		public IReadOnlyList<string> ScopeColorSwatches { get; } = Models.ScopeColors.Presets.Select(p => p.Hex).ToArray();

		/// <summary>The swatches' names (tooltips), parallel to <see cref="ScopeColorSwatches"/>.</summary>
		public IReadOnlyList<string> ScopeColorNames { get; } = Models.ScopeColors.Presets.Select(p => p.Name).ToArray();

		/// <summary>Two-way for the Radar tab's swatch row. PERSISTED as the hex. -1 = a hand-edited colour that
		/// is not a preset (it still applies; no swatch lights).</summary>
		public int ScopeColorIndex
		{
			get => Models.ScopeColors.IndexOf(_settings.Settings.ScopeColor);
			set
			{
				if (value < 0)
				{
					return; // the picker never writes -1; don't let a binding echo erase a custom colour
				}
				var hex = Models.ScopeColors.FromIndex(value);
				if (hex == Models.ScopeColors.Normalize(_settings.Settings.ScopeColor))
				{
					return;
				}
				_settings.Settings.ScopeColor = hex; // persists (auto-save)
				OnPropertyChanged();
				if (_isMapReady)
				{
					_ = _mapService.SetScopeColorAsync(hex);
				}
			}
		}

		/// <summary>Replays the two PERSISTED ruler/scope preferences into a freshly loaded page (the page
		/// defaults to theme colours and the site anchor). Called from <see cref="OnMapsReadyAsync"/>.</summary>
		private async Task PushScopePreferencesAsync()
		{
			await _mapService.SetScopeColorAsync(Models.ScopeColors.Normalize(_settings.Settings.ScopeColor));
			await PushRulerAnchorAsync();
		}

		/// <summary>Called from the view when the WebView pushes the value under the cursor for ONE pane
		/// (null = no data there). Each pane owns its own reading and its own notch tick.</summary>
		public void SetInspectValue(int paneIndex, double? value)
		{
			if (paneIndex >= 0 && paneIndex < Panes.Count)
			{
				Panes[paneIndex].SetInspectValue(value);
			}
		}
	}
}
