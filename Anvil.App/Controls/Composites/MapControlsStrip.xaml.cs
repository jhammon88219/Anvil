using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The map-tools tier of the bottom chrome: the radar-site picker left, place search center, map-only
	/// tools right (see the XAML header for the shape and the rules). It is pure content - the <c>OverlayBar</c> that hosts it owns
	/// the surface, the hairline and the padding, and MainWindow's rail owns the tab that hides it.
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

		// ===== Site markers (show / hide) =====
		// Both take the CURRENT visibility and answer for the CLICK (see the XAML header). "radar sites", never
		// "markers" — that word belongs to the marker viewer beside the search box.
		public string SitesVisibleTooltip(bool visible) => visible ? "Hide radar sites" : "Show radar sites";

		// E7B3 RedEye (open) / ED1A Hide (crossed-out) — ⚠️ unverified codepoints, see the XAML header.
		public string SitesVisibleGlyph(bool visible) => visible ? "\uE7B3" : "\uED1A";

		// ===== Range rings (master show/hide, beside the Ruler) =====
		// The tooltip speaks for the CLICK, like the site-markers toggle.
		public string RangeRingsTooltip(bool visible) => visible ? "Hide range rings" : "Show range rings";

		// ===== Site picker (home + favorites) =====
		// E80F Home / E735 FavoriteStarFill — ⚠️ unverified codepoints, see the XAML header. A site that is
		// neither gets no mark (only the FACE can show one — the lists hold only home and favorites).
		public static string PinGlyph(bool isHome, bool isFavorite) =>
			isHome ? "\uE80F" : isFavorite ? "\uE735" : string.Empty;

		public static Visibility PinVisibility(bool isHome, bool isFavorite) =>
			isHome || isFavorite ? Visibility.Visible : Visibility.Collapsed;

		// ⚠️ Every click lands here, the already-loaded site included (that one re-flies). Then RE-ASSERT the
		// face: the picker set SelectedItem itself, and a refused pick changes no property to put it back.
		private void OnSitePicked(object? sender, object item)
		{
			if (ViewModel is not { } vm || item is not RadarSiteRow row)
			{
				return;
			}
			vm.SiteFavorites.Pick(row);
			SitePicker.SelectedItem = vm.SiteFavorites.LoadedSite;
		}

		private void OnOpenAtlasClick(object sender, RoutedEventArgs e)
		{
			SitePicker.CloseDropDown();
			if (ViewModel is { } vm)
			{
				vm.IsRadarAtlasOpen = true;
			}
		}

		// Reset north — animate bearing + pitch back to 0. Fire-and-forget through IMapService, the same
		// seam the Settings window's Map tab uses.
		private void OnResetNorthClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.ResetOrientationAsync();

		// Fit to view — frame the effective region (isolated state, else CONUS).
		private void OnFitToViewClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.FitToViewAsync();

		// Pane layout (moved here from the bar): which of the three toggles is lit. x:Bind can't compare against
		// an enum literal, so one typed function per layout.
		public bool IsSinglePane(PaneLayout layout) => layout == PaneLayout.Single;
		public bool IsTwoPane(PaneLayout layout) => layout == PaneLayout.TwoAcross;
		public bool IsQuadPane(PaneLayout layout) => layout == PaneLayout.Quad;

		// ⚠️ Re-assert all three after setting: clicking the LIT toggle changes no property, so nothing would
		// re-light it after the ToggleButton unchecked itself.
		private void OnPaneLayoutClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not { } vm || sender is not FrameworkElement { Tag: string tag } ||
				!Enum.TryParse<PaneLayout>(tag, out var layout))
			{
				return;
			}
			vm.Radar.PaneLayout = layout;
			var current = vm.Radar.PaneLayout;
			SinglePaneToggle.IsChecked = IsSinglePane(current);
			TwoPaneToggle.IsChecked = IsTwoPane(current);
			QuadPaneToggle.IsChecked = IsQuadPane(current);
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

		// Place search. Only the USER's typing refreshes rows — arrowing through the list (SuggestionChosen) and
		// our own Text writes (ProgrammaticChange) must not, or picking a row would re-query as you move.
		private void OnPlaceSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
		{
			if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
			{
				ViewModel?.PlaceSearch.UpdateSuggestions(sender.Text);
			}
		}

		// Enter or a row click. The box closes its list on submit, so when the online fallback comes back with
		// several places to pick from, reopen it.
		private async void OnPlaceSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
		{
			if (ViewModel is not { } vm)
			{
				return;
			}
			var went = await vm.PlaceSearch.SubmitAsync(args.QueryText, args.ChosenSuggestion as PlaceResult);
			if (went is not null)
			{
				sender.Text = went.Display;
			}
			else if (vm.PlaceSearch.Suggestions.Count > 0)
			{
				sender.IsSuggestionListOpen = true;
			}
		}
	}
}
