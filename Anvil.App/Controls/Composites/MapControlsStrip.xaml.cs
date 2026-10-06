using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The map-tools tier of the bottom chrome: place search left, the console tools (sites, Inspect, Ruler,
	/// Rings, site picker) over the centred console, map-only tools right (see the XAML header for the shape and
	/// the rules). It is pure content - the <c>OverlayBar</c> that hosts it owns
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
			// The right group re-laying out re-runs its fit. It converges: ApplyRightTools ignores sub-pixel
			// no-ops, so the resize it causes settles on the next pass.
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

		// ===== The PastCast loading screen's lock (RadarViewModel.AreControlsLocked) =====
		// EVERY tool on this tier greys while it is up (Location and the marker viewer too — the user's call 2026-10-04);
		// the site picker is already off in PastCast.
		public bool Free(bool locked) => !locked;
		public bool FreeAnd(bool enabled, bool locked) => enabled && !locked;

		// The pane-layout marks are drawn Rectangles with fixed theme brushes: the template's Disabled state only greys
		// the Foreground, which Shapes don't read, so they dim themselves (the bar's disabled-text strength, roughly).
		public double LockedOpacity(bool locked) => locked ? 0.35 : 1.0;

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

		// ===== The centre section is EDGE-ALIGNED to the console (the section rules, docs/ui-bottom-bar.md) =====
		// network toggles ⁙⁙ [site picker] ⁙⁙⁙ Inspect Ruler Rings
		// ├ console left               ▶                console right ┤
		// Three measured values: the group's left margin (toggles on the console's left edge) and the two spacers'
		// widths (picker centred on play; tools ending on the console's right edge). The spacers are unequal because
		// play sits left of the console's middle. ⚠️ No floor against the side sections: the centre never gives way
		// (small-window handling is the user's undecided call). A spacer never drops below a 3 × 3.
		private const int SpacerMinColumns = 3;

		/// <summary>Fit the centre section between <paramref name="consoleLeft"/> (the clock's left edge) and
		/// <paramref name="consoleRight"/> (the end of the longest scan line's text — it moves as that text changes) with the site picker centred on <paramref name="play"/>, all in
		/// <paramref name="root"/>'s coordinates. Each value is a no-op for a sub-pixel change, so calling it from
		/// layout settles at once.</summary>
		public void AlignToConsole(double consoleLeft, double play, double consoleRight, UIElement root)
		{
			if (double.IsNaN(consoleLeft) || double.IsNaN(play) || double.IsNaN(consoleRight) ||
				NetworkToggles.ActualWidth <= 0 || RadarTools.ActualWidth <= 0)
			{
				return;
			}
			var origin = TransformToVisual(root).TransformPoint(default).X;
			// spacer | its margins | the panel's spacing on both sides
			var spacerChrome = LeftSpacer.Margin.Left + LeftSpacer.Margin.Right + 2 * ConsoleTools.Spacing;
			var pickerHalf = SitePicker.Width / 2;
			var left = (play - pickerHalf) - (consoleLeft + NetworkToggles.ActualWidth) - spacerChrome;
			var right = (consoleRight - RadarTools.ActualWidth) - (play + pickerHalf) - spacerChrome;
			SetIfMoved(LeftSpacer, Math.Max(LeftSpacer.WidthFor(SpacerMinColumns), left));
			SetIfMoved(RightSpacer, Math.Max(RightSpacer.WidthFor(SpacerMinColumns), right));
			var margin = consoleLeft - origin;
			if (Math.Abs(margin - ConsoleTools.Margin.Left) > 0.5)
			{
				ConsoleTools.Margin = new Thickness(margin, 0, 0, 0);
			}
		}

		private static void SetIfMoved(FrameworkElement e, double width)
		{
			if (Math.Abs(width - e.Width) > 0.5)
			{
				e.Width = width;
			}
		}

		// ===== The right side's fit =====
		// One measured width: the isolation picker's left edge lands on the bar's ATLAS key's left edge (its right
		// edge is the tier's, fixed — RightTools is right-aligned). The group is otherwise its natural width, so the
		// pane-layout toggles LEAD it and the open run sits between the site picker and them (2026-10-06: next to the
		// console tools they read as part of the centre cluster). The last Atlas edge is kept so the strip's own
		// resizes can re-run it without MainWindow.

		// Floor: past this the alignment gives way rather than the control.
		private const double IsolationMinWidth = 150;

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
			if (_alignRoot is null || double.IsNaN(_atlasLeft) || IsolationPicker.ActualWidth <= 0)
			{
				return;
			}
			var isoRight = IsolationPicker.TransformToVisual(_alignRoot).TransformPoint(default).X + IsolationPicker.ActualWidth;
			var iso = Math.Max(IsolationMinWidth, isoRight - _atlasLeft);
			if (Math.Abs(iso - IsolationPicker.Width) > 0.5)
			{
				IsolationPicker.Width = iso;
			}
		}

		/// <summary>The RIGHT section's left edge on this tier — its first key's (the 1-pane toggle's) left — in
		/// <paramref name="root"/>'s coordinates, or NaN while the tier isn't laid out (hidden). MainWindow starts the
		/// bar's activity slot there (AlignActivityBay), so the right section shares one left edge on both tiers.</summary>
		public double RightSectionLeft(UIElement root) =>
			Visibility == Visibility.Visible && IsLoaded && RightTools.ActualWidth > 0
				? RightTools.TransformToVisual(root).TransformPoint(default).X
				: double.NaN;

		/// <summary>Make the search BOX exactly <paramref name="width"/> wide: MainWindow passes the width of the
		/// bar's temporal keys. Both start on the bar's left edge, so the box spans exactly the keys below it; the
		/// marker viewer and Location hang after it.</summary>
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
			ViewModel?.OpenAtlasOnSites(); // a site-picker door: lands on Radar sites
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
