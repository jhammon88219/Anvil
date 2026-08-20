using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	// RadarViewModel (partial): the color-scale legend + the inspector (read value under cursor).
	public sealed partial class RadarViewModel
	{
		// ── Color-scale legend. Fed by the WebView pushing the active product's ramp (from
		//    radar-ramps.js, the single source of truth) — never hard-coded here. Updates whenever the
		//    product changes; the Color Scale tool window renders the bar from these exact stops. ──
		private RadarRampInfo? _currentRamp;

		/// <summary>The active product's color ramp (or null until the first push). Drives the legend.</summary>
		public RadarRampInfo? CurrentRamp => _currentRamp;

		/// <summary>Whether to show the color-scale legend (a product ramp is known + a loop is active).</summary>
		public bool HasColorScale => _currentRamp is not null && HasRadarDisplay;

		/// <summary>Legend heading, e.g. "Reflectivity (dBZ)".</summary>
		public string RampTitle => _currentRamp is { } r ? $"{r.Label} ({r.Unit})" : string.Empty;

		/// <summary>Legend tick labels at the low / mid / high ends of the scale.</summary>
		public string RampMinText => _currentRamp is { } r ? FormatRampValue(r.Min) : string.Empty;
		public string RampMidText => _currentRamp is { } r ? FormatRampValue((r.Min + r.Max) / 2) : string.Empty;
		public string RampMaxText => _currentRamp is { } r ? FormatRampValue(r.Max) : string.Empty;

		private static string FormatRampValue(double v) =>
			v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>Called from the view when the WebView pushes the active product's ramp.</summary>
		public void SetColorScale(RadarRampInfo? ramp)
		{
			_currentRamp = ramp;
			OnPropertyChanged(nameof(CurrentRamp));
			OnPropertyChanged(nameof(HasColorScale));
			OnPropertyChanged(nameof(RampTitle));
			OnPropertyChanged(nameof(RampMinText));
			OnPropertyChanged(nameof(RampMidText));
			OnPropertyChanged(nameof(RampMaxText));
		}

		// ── Inspector ("read the value under the cursor", RadarScope-style) ────────────────────────────
		// Inspect is a GLOBAL instrument: one armed cursor mode over the whole map, not a per-pane toggle.
		// The VALUE, though, is per pane — at one lat/lon each pane reads its OWN product's grid, so four
		// panes give four readings of the same point, each ticking on its own chip ramp. That is the whole
		// point of it in multi-pane: four numbers for one gate, read in a glance at the chip cluster.
		// The value tooltip itself is drawn in the WebView next to the cursor (instant, no host round-trip
		// per mouse move); the host only receives the numbers that drive the chip ticks.
		private bool _isInspecting;

		/// <summary>Whether inspect mode is engaged (the Map Controls window's toggle). One mode for every
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

		/// <summary>Called from the view when the WebView pushes the value under the cursor for ONE pane
		/// (null = no data there). Each pane owns its own reading and its own chip tick.</summary>
		public void SetInspectValue(int paneIndex, double? value)
		{
			if (paneIndex >= 0 && paneIndex < Panes.Count)
			{
				Panes[paneIndex].SetInspectValue(value);
			}
		}
	}
}
