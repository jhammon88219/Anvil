using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The map tools as ONE vertical column — map layers, fit to view, reset north, markers, location — the content of
	/// MainWindow's left MAP-TOOLS RAIL (see the XAML header for the shape and the rules). It is pure content: the rail
	/// owns the surface, the map-side shade and the padding; View → Map tools hides it.
	/// </summary>
	/// <remarks>
	/// It was the bottom chrome's horizontal tier until 2026-11-04 (and before that a notched island over the bar's
	/// pull-tab); docs/ui-bottom-bar.md keeps the story. Pane layout and isolation moved to the top menu bar.
	/// </remarks>
	public sealed partial class MapControlsStrip : UserControl
	{
		public MapControlsStrip()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapControlsStrip), new PropertyMetadata(null));

		// ===== The PastCast loading screen's lock (RadarViewModel.AreControlsLocked) =====
		// EVERY tool on this tier greys while it is up (Location and the marker viewer too — the user's call 2026-10-04);
		// the site picker is already off in PastCast.
		public bool Free(bool locked) => !locked;
		public bool FreeAnd(bool enabled, bool locked) => enabled && !locked;

		// (The horizontal strip's side-section alignment — AlignToWindowEdges and its dot spacer — is GONE with the bottom
		// tier, 2026-11-04: this is a vertical column now, centred in the rail by layout alone.)

		// Reset north — animate bearing + pitch back to 0. Fire-and-forget through IMapService, the same
		// seam the Settings window's Map tab uses.
		private void OnResetNorthClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.ResetOrientationAsync();

		// Fit to view — frame the effective region (isolated state, else CONUS).
		private void OnFitToViewClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.FitToViewAsync();

		// ===== Map (the basemap key + its flyout) =====
		// The tooltip speaks for the CLICK, like the site-markers toggle.
		public string MapShownTooltip(bool shown) => shown ? "Hide the map (blank background)" : "Show the map";

		public Visibility HiddenNoteVisibility(bool shown) => shown ? Visibility.Collapsed : Visibility.Visible;

		// The layer flyout's TREE rows (static: called from the row DataTemplates). The chevron shows only on an
		// expander and turns like DragMoveLayerExpander's (E76C right → down); the leaves show while it is open.
		public static Visibility VisibleIf(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		public static double ChevronAngle(bool expanded) => expanded ? 90 : 0;

		// (Pane layout moved to the top menu bar's Pane Mode menu, 2026-11-04 — MainWindow.OnPaneModeClick.)

		// Location (moved here from the bar). The transient locate status WINS in the tooltip: there is nowhere
		// else a failed fix ("Location unavailable") would show — the toggle would simply spring back up.
		public string LocationTooltip(bool hasMarker, string status) =>
			status.Length > 0 ? status
			: hasMarker ? "Remove your location marker"
			: "Drop a marker at your location";

		// ⚠️ Re-assert IsChecked AFTER the await: the click already flipped the toggle optimistically, and the
		// resolve can fail without changing any view-model property, so nothing would pull it back down.
		private async void OnToggleUserLocation(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not { } vm)
			{
				return;
			}
			await vm.Markers.ToggleUserLocationAsync();
			LocationToggle.IsChecked = vm.Markers.HasUserLocationMarker;
		}
	}
}
