using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
			// Either side re-laying out (the left widens as PickerSeparator slides the site picker; the right
			// after its own resize) re-runs the right side's fit. It converges: ApplyRightTools ignores sub-pixel
			// no-ops, so the resize it causes settles on the next pass.
			LeftTools.SizeChanged += (_, _) => ApplyRightTools();
			RightTools.SizeChanged += (_, _) => ApplyRightTools();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapControlsStrip), new PropertyMetadata(null));

		// ===== Site markers (one toggle per network) =====
		// Takes the CURRENT visibility and answers for the CLICK (see the XAML header). "radar sites", never
		// "markers" — that word belongs to the marker viewer beside the search box.
		public string SitesTooltip(bool visible, string network) =>
			visible ? $"Hide {network} on the map" : $"Show {network} on the map";

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

		// The picker separator never drops below a 3 × 3's width — past that, the alignment gives way.
		private const int PickerSeparatorMinColumns = 3;

		/// <summary>Slide the (fixed-width) site picker so its RIGHT edge lands on <paramref name="rightEdge"/> (in
		/// <paramref name="root"/>'s coordinates) — MainWindow passes the end of the bar's longest scan line. It does
		/// so by setting PickerSeparator's Width: that separator's LEFT edge is fixed by the tools before it, and the
		/// picker follows its right edge after the separator's margin and the panel spacing. A no-op for a sub-pixel
		/// change, which is what makes calling it from SizeChanged safe (the resize re-fires it with the same answer).</summary>
		public void AlignSitePickerRightEdge(double rightEdge, UIElement root)
		{
			if (double.IsNaN(rightEdge) || PickerSeparator.ActualHeight <= 0)
			{
				return;
			}
			var separatorLeft = PickerSeparator.TransformToVisual(root).TransformPoint(default).X;
			// separator | its right margin | the panel's spacing | picker
			var afterSeparator = PickerSeparator.Margin.Right + LeftTools.Spacing;
			var width = rightEdge - SitePicker.Width - afterSeparator - separatorLeft;
			width = Math.Max(PickerSeparator.WidthFor(PickerSeparatorMinColumns), width);
			if (Math.Abs(width - PickerSeparator.Width) > 0.5)
			{
				PickerSeparator.Width = width;
			}
		}

		// ===== The right side's fit (the mirror of the site picker's slide) =====
		// Two measured widths, both set here: the isolation picker's left edge lands on the bar's ATLAS key's left
		// edge (its right edge is the tier's, fixed — RightTools is right-aligned), and PaneSeparator takes up
		// whatever makes RightTools exactly as wide as LeftTools. The last Atlas edge is kept so the strip's own
		// resizes can re-run it without MainWindow.

		// Floors: past these the alignment gives way rather than the controls.
		private const double IsolationMinWidth = 150;
		private const int PaneSeparatorMinColumns = 3;

		private double _atlasLeft = double.NaN;
		private UIElement? _alignRoot;

		/// <summary>Size the right side against the bar: <paramref name="atlasLeft"/> is the Atlas key's left edge in
		/// <paramref name="root"/>'s coordinates. See <see cref="ApplyRightTools"/>.</summary>
		public void AlignRightTools(double atlasLeft, UIElement root)
		{
			_atlasLeft = atlasLeft;
			_alignRoot = root;
			ApplyRightTools();
		}

		private void ApplyRightTools()
		{
			if (_alignRoot is null || double.IsNaN(_atlasLeft) ||
				IsolationPicker.ActualWidth <= 0 || LeftTools.ActualWidth <= 0 || RightTools.ActualWidth <= 0)
			{
				return;
			}
			var isoRight = IsolationPicker.TransformToVisual(_alignRoot).TransformPoint(default).X + IsolationPicker.ActualWidth;
			var iso = Math.Max(IsolationMinWidth, isoRight - _atlasLeft);
			// Everything in the right group but the two widths being set (margins and spacing included).
			var fixedPart = RightTools.ActualWidth - PaneSeparator.ActualWidth - IsolationPicker.ActualWidth;
			var sep = Math.Max(PaneSeparator.WidthFor(PaneSeparatorMinColumns), LeftTools.ActualWidth - fixedPart - iso);
			if (Math.Abs(iso - IsolationPicker.Width) > 0.5)
			{
				IsolationPicker.Width = iso;
			}
			if (Math.Abs(sep - PaneSeparator.Width) > 0.5)
			{
				PaneSeparator.Width = sep;
			}
		}

		/// <summary>Make the search BOX exactly <paramref name="width"/> wide: MainWindow passes the width of the
		/// bar's temporal keys. Both are centred on the window's midline, so the box's edges land on the keys'
		/// outer edges; the two flanks (marker viewer, Location) hang OUTSIDE that line. (It used to be the whole
		/// group that matched the keys — the user narrowed the keys to the box instead, 2026-10-01.)</summary>
		public void MatchPlaceSearchWidth(double width)
		{
			if (width > 0 && Math.Abs(width - PlaceSearchBox.Width) > 0.5)
			{
				PlaceSearchBox.Width = width;
			}
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

		// ===== Map (the basemap key + its flyout) =====
		// The tooltip speaks for the CLICK, like the site-markers toggle.
		public string MapShownTooltip(bool shown) => shown ? "Hide the map (blank background)" : "Show the map";

		public Visibility HiddenNoteVisibility(bool shown) => shown ? Visibility.Collapsed : Visibility.Visible;

		// The layer flyout's TREE rows (static: called from the row DataTemplates). The chevron shows only on an
		// expander and turns like PanelSection's (E76C right → down); the leaves show while it is open.
		public static Visibility VisibleIf(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		public static double ChevronAngle(bool expanded) => expanded ? 90 : 0;

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
			PinClearButton();
			if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
			{
				ViewModel?.PlaceSearch.UpdateSuggestions(sender.Text);
			}
		}

		// ===== The box's X stays while there is text, focused or not =====
		// The X is the inner TextBox's own template part "DeleteButton" (declared Visibility="Collapsed"). The
		// TextBox shows it only while FOCUSED: its ButtonVisible state animates it to Visible, and ButtonCollapsed
		// has NO storyboard, so in that state the element's own value shows. Setting that value here, from the
		// text, keeps the X up after focus leaves (clicking the map) — so coming back to clear it is ONE click.
		// ⚠️ Rests on the WinUI template (AutoSuggestBoxTextBoxStyle, checked against WinAppSDK 2.1 generic.xaml):
		// if a future template gives ButtonCollapsed a storyboard, this quietly reverts to focus-only.
		private Button? _clearButton;

		private void OnPlaceSearchLoaded(object sender, RoutedEventArgs e) => PinClearButton();

		// Looks the part up lazily (Loaded, then every text change) in case the inner template lands late.
		private void PinClearButton()
		{
			if (_clearButton is null && FindNamed<Button>(PlaceSearchBox, "DeleteButton") is { } button)
			{
				_clearButton = button;
				// Belt and braces: the TextBox's own handler clears the text too, but an UNFOCUSED click must not
				// depend on it — clear here and say so to the VM (the empty box is what removes the pin).
				_clearButton.Click += OnClearSearchClick;
			}
			if (_clearButton is not null)
			{
				_clearButton.Visibility = string.IsNullOrEmpty(PlaceSearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
			}
		}

		private void OnClearSearchClick(object sender, RoutedEventArgs e)
		{
			if (PlaceSearchBox.Text.Length > 0)
			{
				PlaceSearchBox.Text = string.Empty;
			}
			ViewModel?.PlaceSearch.UpdateSuggestions(string.Empty);
		}

		private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
		{
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is T match && match.Name == name)
				{
					return match;
				}
				if (FindNamed<T>(child, name) is { } found)
				{
					return found;
				}
			}
			return null;
		}

		// Enter or a row click. The box closes its list on submit, so when the online fallback comes back with
		// several places to pick from, reopen it. A place flown to lands in the box through the one-way
		// QueryText binding (the same path a PastCast saved event's town pin takes).
		private async void OnPlaceSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
		{
			if (ViewModel is not { } vm)
			{
				return;
			}
			var went = await vm.PlaceSearch.SubmitAsync(args.QueryText, args.ChosenSuggestion as PlaceResult);
			if (went is null && vm.PlaceSearch.Suggestions.Count > 0)
			{
				sender.IsSuggestionListOpen = true;
			}
		}
	}
}
