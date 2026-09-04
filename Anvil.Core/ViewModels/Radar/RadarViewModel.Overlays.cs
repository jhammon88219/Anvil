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
