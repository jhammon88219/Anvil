using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>Which edge the bar attaches to (drives the tab/border geometry).</summary>
	public enum BarEdge
	{
		/// <summary>Bar pinned to the bottom; pull-tab sits ABOVE it (the app's one bar).</summary>
		Bottom,
		/// <summary>Bar pinned to the top; pull-tab HANGS BELOW it (the per-pane notch).</summary>
		Top,
	}

	/// <summary>
	/// Chrome-only shell for an overlay bar: the centered show/hide pull-tab, the theme-aware surface + a
	/// hairline card border (<c>CardStrokeColorDefaultSolidBrush</c>, same as the settings cards) that runs
	/// along the bar's edge and wraps around the tab, and the collapse behavior. The host fills
	/// <see cref="BarContent"/> with the actual controls, so section content is composed by the host. The
	/// show/hide state is pure view state and lives here, not on a view model - which is what gives the four
	/// pane notches four INDEPENDENT hide toggles for free.
	/// <para>
	/// THREE knobs shape it, and each moves only geometry - the surface, the hairline, the lapped tab and
	/// the collapse behave identically in every combination, which is the point of having one control
	/// rather than a second copy for the notch:
	/// </para>
	/// <list type="bullet">
	/// <item><see cref="Edge"/> - BOTTOM (tab above; the app's one bar) or TOP (tab hanging below; the
	/// per-pane notch). The chevron inverts with it.</item>
	/// <item><see cref="IsIsland"/> - whether the bar spans its host edge to edge (false: a hairline on the
	/// facing edge only) or is a centred island that has to draw its own left/right edges and round its two
	/// inner corners (true: the pane notch).</item>
	/// <item><see cref="ShowTabLabel"/> - whether the tab reads "Hide"/"Show" beside its chevron, which
	/// also decides whether the tab is the fixed shared size or content-sized. ⚠️ ON in both shapes today,
	/// so the notch tab and the bar tab are the SAME tab. It was off for the notch (four panes means four
	/// tabs, and four copies of "Hide" across the top of the map is noise where four chevrons are not) -
	/// but a wordless tab also came out a third the width and shorter, wearing the same corner radius at a
	/// smaller scale, so it read as a different tab rather than a quieter one. The knob is kept, unused,
	/// because it is the only expression of "a tab may be wordless"; delete it if nothing wants it back.</item>
	/// </list>
	/// </summary>
	public sealed partial class OverlayBar : UserControl
	{
		public OverlayBar()
		{
			InitializeComponent();
			ApplyChrome(); // idempotent; the XAML defaults already match Bottom + full-width + labelled.
		}

		/// <summary>The content shown inside the bar (filled by the host - e.g. the section controls).</summary>
		public object? BarContent
		{
			get => GetValue(BarContentProperty);
			set => SetValue(BarContentProperty, value);
		}

		public static readonly DependencyProperty BarContentProperty =
			DependencyProperty.Register(nameof(BarContent), typeof(object), typeof(OverlayBar), new PropertyMetadata(null));

		/// <summary>Whether the bar is shown (the pull-tab toggles it). Pure view state.</summary>
		public bool IsOverlayBarVisible
		{
			get => (bool)GetValue(IsOverlayBarVisibleProperty);
			set => SetValue(IsOverlayBarVisibleProperty, value);
		}

		public static readonly DependencyProperty IsOverlayBarVisibleProperty =
			DependencyProperty.Register(nameof(IsOverlayBarVisible), typeof(bool), typeof(OverlayBar), new PropertyMetadata(true));

		/// <summary>Top or bottom edge (default <see cref="BarEdge.Bottom"/> - the app's one bar).</summary>
		public BarEdge Edge
		{
			get => (BarEdge)GetValue(EdgeProperty);
			set => SetValue(EdgeProperty, value);
		}

		public static readonly DependencyProperty EdgeProperty =
			DependencyProperty.Register(nameof(Edge), typeof(BarEdge), typeof(OverlayBar),
				new PropertyMetadata(BarEdge.Bottom, OnChromeChanged));

		/// <summary>
		/// A centred island rather than a bar spanning its host edge to edge. An island has two more edges
		/// to draw (left and right) and two corners to round; a full-width bar has neither, because both of
		/// those run off the side of the window.
		/// </summary>
		public bool IsIsland
		{
			get => (bool)GetValue(IsIslandProperty);
			set => SetValue(IsIslandProperty, value);
		}

		public static readonly DependencyProperty IsIslandProperty =
			DependencyProperty.Register(nameof(IsIsland), typeof(bool), typeof(OverlayBar),
				new PropertyMetadata(false, OnChromeChanged));

		/// <summary>Whether the pull-tab reads "Hide"/"Show" beside its chevron (see the class remarks).</summary>
		public bool ShowTabLabel
		{
			get => (bool)GetValue(ShowTabLabelProperty);
			set => SetValue(ShowTabLabelProperty, value);
		}

		public static readonly DependencyProperty ShowTabLabelProperty =
			DependencyProperty.Register(nameof(ShowTabLabel), typeof(bool), typeof(OverlayBar),
				new PropertyMetadata(true, OnChromeChanged));

		private static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((OverlayBar)d).ApplyChrome();

		// Reposition the bar + tab for the current edge and shape. For a BOTTOM full-width labelled bar (the
		// defaults) every value here equals the XAML/style default, so this is a no-op; the other
		// combinations flip the geometry vertically and/or close the island's sides.
		//
		// WARNING: it is all ONE method on purpose. The three knobs are not independent - the corner radius
		// depends on edge AND island, the tab padding on the label - so splitting them into three handlers
		// is how the states start disagreeing. Every branch sets every value.
		private void ApplyChrome()
		{
			bool top = Edge == BarEdge.Top;
			bool island = IsIsland;

			// Row order: the Grid has row 0 above row 1. Bottom = tab(0) over bar(1); Top = bar(0) over tab(1).
			Grid.SetRow(BarBorder, top ? 0 : 1);
			Grid.SetRow(TabButton, top ? 1 : 0);

			// The bar's hairline always runs along the edge FACING THE TAB. An island adds its left and right
			// edges; a full-width bar leaves those open, since they run off the side of the window.
			BarBorder.BorderThickness = top
				? new Thickness(island ? 1 : 0, 0, island ? 1 : 0, 1)
				: new Thickness(island ? 1 : 0, 1, island ? 1 : 0, 0);

			// Only an island rounds anything, and only its two INNER corners - the ones away from the edge it
			// is attached to. A full-width bar's corners are off-screen.
			BarBorder.CornerRadius = island
				? (top ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(8, 8, 0, 0))
				: new CornerRadius(0);

			// An island sits over the map with a pane's worth of room, not a window's, so it is tighter than
			// the bar: the bar's 16,10 would make a notch noticeably taller over a quad pane.
			BarBorder.Padding = island ? new Thickness(12, 6, 12, 6) : new Thickness(16, 10, 16, 10);

			// Tab border on its three outer sides, open toward the bar, so the two hairlines merge.
			TabButton.BorderThickness = top ? new Thickness(1, 0, 1, 1) : new Thickness(1, 1, 1, 0);
			// Lap the tab's (borderless) inner edge over the bar's hairline so the two merge (see the style comment).
			TabButton.Margin = top ? new Thickness(0, -2, 0, 0) : new Thickness(0, 0, 0, -2);
			// A tab with no word in it only has to hold a chevron, so it loses the label's side padding -
			// otherwise an unlabelled tab is a wide empty lozenge.
			TabButton.Padding = ShowTabLabel ? new Thickness(16, 3, 16, 3) : new Thickness(9, 2, 9, 2);

			// ⚠️ THE LABELLED TAB IS A FIXED SIZE, and every tab in the app is labelled now, so this is the
			// one tab shape - the notch's included. Three reasons, the first two in Controls/Styles.xaml beside
			// the numbers: the label flips "Hide"/"Show", which would resize a content-sized tab on every click;
			// Composites/MapControlsStrip cuts a notch that TRACES this tab at a uniform offset, and cannot
			// trace a width nobody knows; and a content-sized tab is sized by its glyph's font metrics, which is
			// nobody's decision - it is what made the notch tab a third the bar tab's width. Keep the resources
			// and the strip in step: these are the single source for both.
			// (The NaN branch survives only for a ShowTabLabel=False host, of which there are none - see the
			// class remarks. It is content-sizing, not a second designed shape.)
			TabButton.Width = ShowTabLabel ? SharedSize("OverlayBarTabWidth") : double.NaN;
			TabButton.Height = ShowTabLabel ? SharedSize("OverlayBarTabHeight") : double.NaN;
			TabButton.CornerRadius = top
				? new CornerRadius(0, 0, SharedSize("OverlayBarTabRadius"), SharedSize("OverlayBarTabRadius"))
				: new CornerRadius(SharedSize("OverlayBarTabRadius"), SharedSize("OverlayBarTabRadius"), 0, 0);

			// The chevron points TOWARD the bar for "hide"; force the x:Bind glyph + label to re-evaluate.
			Bindings?.Update();
		}

		/// <summary>Reads one of the app-wide tab sizes from Controls/Styles.xaml. Falls back to the value
		/// baked in here if the resource is missing, so a mis-keyed lookup degrades to a correct-looking tab
		/// rather than a zero-sized one.</summary>
		private static double SharedSize(string key) =>
			Application.Current.Resources.TryGetValue(key, out var v) && v is double d ? d
				: key == "OverlayBarTabRadius" ? 7 : key == "OverlayBarTabHeight" ? 28 : 96;

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		// Pull-tab glyph: points toward the bar's edge for "hide", away for "show" - so it inverts per Edge.
		private const string ChevronUp = "\uE70E";   // Segoe Fluent ChevronUp
		private const string ChevronDown = "\uE70D"; // Segoe Fluent ChevronDown

		public string ToggleGlyph(bool visible)
		{
			bool top = Edge == BarEdge.Top;
			// Bottom: hide=down, show=up.  Top: hide=up, show=down.
			bool pointUp = top ? visible : !visible;
			return pointUp ? ChevronUp : ChevronDown;
		}

		public string ToggleLabel(bool visible) => visible ? "Hide" : "Show";

		/// <summary>Tooltip for the pull-tab - it carries the wording an unlabelled tab drops.</summary>
		public string ToggleTooltip(bool visible) => visible ? "Hide these controls" : "Show the controls";

		private void OnToggleOverlayBarClick(object sender, RoutedEventArgs e) =>
			IsOverlayBarVisible = !IsOverlayBarVisible;
	}
}
