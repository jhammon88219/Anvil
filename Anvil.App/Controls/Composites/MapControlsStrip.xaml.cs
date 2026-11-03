using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The map-tools tier of the bottom chrome: the marker viewer + Location left, map-only tools right (see the XAML
	/// header for the shape and the rules; the radar tools moved into the floating console's top row). It is pure
	/// content - the <c>OverlayBar</c> that hosts it owns the surface, the edge and the padding; View → Map tools hides it.
	/// </summary>
	/// <remarks>
	/// ⚠️ This class used to be half geometry: the strip was a content-sized island floating above the bar
	/// with a notch cut in its underside that traced the pull-tab, which needed a path builder, a uniform
	/// clearance, a notch depth, a bar overlap the host applied as a negative margin, and an invariant
	/// holding the two side columns equal so that a tool could never land on the tab. All of it is gone,
	/// because a full-width tier has no tab passing through it. docs/ui-bottom-bar.md keeps the story.
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

		// (The centre section and its AlignToConsole are GONE, 2026-11-03: the network picker, Inspect and the rings/ruler
		// picker moved into the floating console's top row, Composites/RadarConsoleHeader.) A spacer never drops below a 3 × 3.
		private const int SpacerMinColumns = 3;

		// ===== The right section starts on the ForeCast pane's INNER edge (the user's call, 2026-10-22) =====
		// ▣ ⊚                                                        ┊▥ ⁙⁙ ⌘ ⛶ ─ map ─ [Isolate]
		//                                           ForeCast's inner edge ↑
		// Controls keep their size; ONE dot spacer takes the slack. Its width is computed from its own right edge, which
		// does NOT move with it (the group is right-aligned), so a repeat call in the same layout lands on the same value.
		// (The left section's SearchSpacer is gone, 2026-11-03 — ▣ ⊚ sit at the left padding for now.)

		/// <summary>Size the right spacer so the pane-layout key's left edge lands on <paramref name="innerRight"/>, in
		/// <paramref name="root"/>'s coordinates.</summary>
		public void AlignToWindowEdges(double innerRight, UIElement root)
		{
			if (MapToolsSpacer.ActualWidth > 0 && MapControlsStripPaneLayoutPicker.ActualWidth > 0)
			{
				var end = MapToolsSpacer.TransformToVisual(root).TransformPoint(default).X + MapToolsSpacer.ActualWidth;
				var before = MapToolsSpacer.Margin.Left + RightTools.Spacing + MapControlsStripPaneLayoutPicker.ActualWidth;
				MapToolsSpacer.SetFillWidth(end - before - innerRight, SpacerMinColumns);
			}
		}

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

		// Pane layout (the square key). ⚠️ Re-assert the key's Layout after setting: a same or refused pick changes no
		// property, so the one-way binding would never put it back.
		private void OnPaneLayoutPicked(object? sender, PaneLayout layout)
		{
			if (ViewModel is not { } vm)
			{
				return;
			}
			vm.Radar.PaneLayout = layout;
			MapControlsStripPaneLayoutPicker.Layout = vm.Radar.PaneLayout;
		}

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
