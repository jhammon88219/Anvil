using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The map-tools tier of the bottom chrome: camera tools left, state isolation right (see the XAML
	/// header for the shape and the rules). It is pure content - the <c>OverlayBar</c> that hosts it owns
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

		// Reset north — animate bearing + pitch back to 0. Fire-and-forget through IMapService, the same
		// seam the Settings window's Map tab uses.
		private void OnResetNorthClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.ResetOrientationAsync();

		// Fit to view — frame the effective region (isolated state, else CONUS).
		private void OnFitToViewClick(object sender, RoutedEventArgs e) =>
			_ = ViewModel?.FitToViewAsync();

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
