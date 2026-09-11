using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.Primitives
{
	/// <summary>Which edge the bar attaches to (drives the tab/border geometry).</summary>
	public enum BarEdge
	{
		/// <summary>Bar pinned to the bottom; pull-tab sits ABOVE it (a tier of the bottom chrome).</summary>
		Bottom,
		/// <summary>Bar pinned to the top; pull-tab HANGS BELOW it (the per-pane notch).</summary>
		Top,
	}

	/// <summary>
	/// Chrome-only shell for an overlay bar: a theme-aware surface, a hairline card border
	/// (<c>CardStrokeColorDefaultSolidBrush</c>, same as the settings cards) that runs along the bar's edge
	/// and wraps around the tab, the optional <see cref="Primitives.OverlayBarTab"/> that collapses it, and
	/// the collapse behavior itself. The host fills <see cref="BarContent"/> with the actual controls, so
	/// section content is composed by the host.
	/// <para>
	/// FIVE knobs shape it. Four move only geometry - the surface, the hairline, the lapped tab and the
	/// collapse behave identically in every combination, which is the point of having one control rather
	/// than a second copy for the notch - and the fifth picks which tier's surface it wears:
	/// </para>
	/// <list type="bullet">
	/// <item><see cref="Edge"/> - BOTTOM (tab above; the bottom chrome) or TOP (tab hanging below; the
	/// per-pane notch). The chevron inverts with it.</item>
	/// <item><see cref="IsIsland"/> - whether the bar spans its host edge to edge (false: a hairline on the
	/// facing edge only) or is a centred island that has to draw its own left/right edges and round its two
	/// inner corners (true: the pane notch).</item>
	/// <item><see cref="ShowTabLabel"/> - whether the tab carries a word beside its chevron, which also
	/// decides whether the tab is the fixed shared size or content-sized. ⚠️ ON everywhere today, so every
	/// tab in the app is the same tab. It was off for the notches (four panes means four tabs, and four
	/// copies of "Hide" across the top of the map is noise where four chevrons are not) - but a wordless tab
	/// also came out a third the width and shorter, wearing the same corner radius at a smaller scale, so it
	/// read as a different tab rather than a quieter one.</item>
	/// <item><see cref="ShowTab"/> - whether this bar draws a tab at all. FALSE for both tiers of the bottom
	/// chrome, where MainWindow's rail owns the tabs; see the ⚠️ in the XAML header for why the tab cannot
	/// stay here once two of these are stacked.</item>
	/// <item><see cref="IsRaised"/> - whether this bar is a tier RESTING ON another bar: it wears the raised
	/// surface and a tighter padding, both saying the same thing about its rank.</item>
	/// </list>
	/// </summary>
	public sealed partial class OverlayBar : UserControl
	{
		public OverlayBar()
		{
			InitializeComponent();
			ApplyChrome(); // idempotent; the XAML defaults already match Bottom + full-width + labelled.
			ApplySurface();
		}

		/// <summary>The content shown inside the bar (filled by the host - e.g. the section controls).</summary>
		public object? BarContent
		{
			get => GetValue(BarContentProperty);
			set => SetValue(BarContentProperty, value);
		}

		public static readonly DependencyProperty BarContentProperty =
			DependencyProperty.Register(nameof(BarContent), typeof(object), typeof(OverlayBar), new PropertyMetadata(null));

		/// <summary>
		/// Whether the bar is shown. Pure view state when this bar draws its own tab (the four pane notches,
		/// which is what gives them four INDEPENDENT toggles for free); driven from outside when it does not
		/// (<see cref="ShowTab"/> false - the bottom chrome's two tiers, whose tabs live on MainWindow's rail).
		/// </summary>
		public bool IsOverlayBarVisible
		{
			get => (bool)GetValue(IsOverlayBarVisibleProperty);
			set => SetValue(IsOverlayBarVisibleProperty, value);
		}

		public static readonly DependencyProperty IsOverlayBarVisibleProperty =
			DependencyProperty.Register(nameof(IsOverlayBarVisible), typeof(bool), typeof(OverlayBar), new PropertyMetadata(true));

		/// <summary>Top or bottom edge (default <see cref="BarEdge.Bottom"/>).</summary>
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

		/// <summary>
		/// Whether this bar draws its own pull-tab. FALSE when the host owns the tab instead - which is what
		/// lets two of these stack into one piece of bottom chrome, with both tabs on a rail above the stack
		/// (a tab drawn here would land in the seam between the two tiers).
		/// </summary>
		public bool ShowTab
		{
			get => (bool)GetValue(ShowTabProperty);
			set => SetValue(ShowTabProperty, value);
		}

		public static readonly DependencyProperty ShowTabProperty =
			DependencyProperty.Register(nameof(ShowTab), typeof(bool), typeof(OverlayBar),
				new PropertyMetadata(true, OnChromeChanged));

		/// <summary>
		/// Whether this bar is a tier RESTING ON another bar rather than sitting on the map. It carries the
		/// raised surface and a tighter padding - both saying the same thing, that this tier is subordinate
		/// to the one below it.
		/// </summary>
		public bool IsRaised
		{
			get => (bool)GetValue(IsRaisedProperty);
			set => SetValue(IsRaisedProperty, value);
		}

		public static readonly DependencyProperty IsRaisedProperty =
			DependencyProperty.Register(nameof(IsRaised), typeof(bool), typeof(OverlayBar),
				new PropertyMetadata(false, OnSurfaceChanged));

		private static void OnChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((OverlayBar)d).ApplyChrome();

		// ⚠️ IsRaised drives BOTH halves - the face in ApplySurface, the tighter padding in ApplyChrome.
		private static void OnSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var bar = (OverlayBar)d;
			bar.ApplySurface();
			bar.ApplyChrome();
		}

		// Point the surface at the right tier. A visual state rather than an assignment because the value is
		// a theme brush and a C# lookup returns the OS theme's, not the app's (CLAUDE.md).
		private void ApplySurface() =>
			VisualStateManager.GoToState(this, IsRaised ? "RaisedSurface" : "GroundSurface", false);

		// Reposition the bar + tab for the current edge and shape. For a BOTTOM full-width labelled bar with
		// a tab (the defaults) every value here equals the XAML default, so this is a no-op; the other
		// combinations flip the geometry vertically and/or close the island's sides.
		//
		// WARNING: it is all ONE method on purpose. The knobs are not independent - the corner radius
		// depends on edge AND island - so splitting them into separate handlers is how the states start
		// disagreeing. Every branch sets every value.
		private void ApplyChrome()
		{
			bool top = Edge == BarEdge.Top;
			bool island = IsIsland;

			// Row order: the Grid has row 0 above row 1. Bottom = tab(0) over bar(1); Top = bar(0) over tab(1).
			Grid.SetRow(BarBorder, top ? 0 : 1);
			Grid.SetRow(Tab, top ? 1 : 0);

			// The bar's hairline always runs along the edge FACING THE TAB - which is still the right edge
			// when there is no tab, because that is the edge the next tier (or the rail) sits against.
			BarBorder.BorderThickness = top
				? new Thickness(island ? 1 : 0, 0, island ? 1 : 0, 1)
				: new Thickness(island ? 1 : 0, 1, island ? 1 : 0, 0);

			// Only an island rounds anything, and only its two INNER corners - the ones away from the edge it
			// is attached to. A full-width bar's corners are off-screen.
			BarBorder.CornerRadius = island
				? (top ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(8, 8, 0, 0))
				: new CornerRadius(0);

			// Three paddings, and each answers a different question. An island sits over the map with a
			// pane's worth of room, not a window's (the bar's 16,10 would make a notch noticeably taller
			// over a quad pane). A RAISED tier is tighter than the bar it rests on because it is
			// SUBORDINATE to it - at equal weight the two stop reading as a bar with a shelf on it and
			// start reading as a bar that doubled in height.
			BarBorder.Padding = island
				? new Thickness(12, 6, 12, 6)
				: IsRaised ? new Thickness(16, 7, 16, 7) : new Thickness(16, 10, 16, 10);

			// The tab: this bar's own, or none at all when the host draws it (see ShowTab).
			Tab.Edge = Edge;
			Tab.ShowLabel = ShowTabLabel;
			Tab.Visibility = ShowTab ? Visibility.Visible : Visibility.Collapsed;
		}

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;
	}
}
