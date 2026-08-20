using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls
{
	/// <summary>Which window edge the bar attaches to (drives the tab/border geometry).</summary>
	public enum BarEdge
	{
		/// <summary>Bar pinned to the window bottom; pull-tab sits ABOVE it (the original behavior).</summary>
		Bottom,
		/// <summary>Bar pinned to the window top; pull-tab HANGS BELOW it.</summary>
		Top,
	}

	/// <summary>
	/// Chrome-only shell for an overlay bar: the centered show/hide pull-tab, the theme-aware surface + a
	/// hairline card border (<c>CardStrokeColorDefaultSolidBrush</c>, same as the settings cards) that runs
	/// along the bar's edge and wraps around the tab, and the collapse behavior. The host fills
	/// <see cref="BarContent"/> with the actual controls, so section content is composed in MainWindow. The
	/// show/hide state is pure view state and lives here, not on a view model.
	/// <para>
	/// <see cref="Edge"/> selects a BOTTOM bar (tab above, the default/original look) or a TOP bar (tab
	/// hanging below). The same chrome serves both the per-pane radar console (bottom) and the global
	/// controls bar (top). Only the tab/border geometry and the chevron flip; everything else is shared.
	/// </para>
	/// </summary>
	public sealed partial class OverlayBar : UserControl
	{
		public OverlayBar()
		{
			InitializeComponent();
			ApplyEdge(); // idempotent; XAML defaults already match Bottom.
		}

		/// <summary>The content shown inside the bar (filled by the host — e.g. the section controls).</summary>
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

		/// <summary>Top or bottom edge (default <see cref="BarEdge.Bottom"/> — the original behavior).</summary>
		public BarEdge Edge
		{
			get => (BarEdge)GetValue(EdgeProperty);
			set => SetValue(EdgeProperty, value);
		}

		public static readonly DependencyProperty EdgeProperty =
			DependencyProperty.Register(nameof(Edge), typeof(BarEdge), typeof(OverlayBar),
				new PropertyMetadata(BarEdge.Bottom, OnEdgeChanged));

		private static void OnEdgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
			((OverlayBar)d).ApplyEdge();

		// Reposition the bar + tab for the current edge. For BOTTOM (default) the values equal the XAML/style
		// defaults, so this is a no-op; for TOP the bar moves to the first row, the tab hangs below it, and
		// every border/margin/corner flips vertically so the hairline still wraps around the tab.
		private void ApplyEdge()
		{
			bool top = Edge == BarEdge.Top;

			// Row order: the Grid has row 0 above row 1. Bottom = tab(0) over bar(1); Top = bar(0) over tab(1).
			Grid.SetRow(BarBorder, top ? 0 : 1);
			Grid.SetRow(TabButton, top ? 1 : 0);

			// Bar hairline on the edge facing the tab; tab border on its three outer sides, open toward the bar.
			BarBorder.BorderThickness = top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
			TabButton.BorderThickness = top ? new Thickness(1, 0, 1, 1) : new Thickness(1, 1, 1, 0);
			TabButton.CornerRadius = top ? new CornerRadius(0, 0, 7, 7) : new CornerRadius(7, 7, 0, 0);
			// Lap the tab's (borderless) inner edge over the bar's hairline so the two merge (see the style comment).
			TabButton.Margin = top ? new Thickness(0, -2, 0, 0) : new Thickness(0, 0, 0, -2);

			// The chevron points TOWARD the bar for "hide". Bottom hides downward, top hides upward, so the glyph
			// inverts with the edge; force the x:Bind glyph to re-evaluate.
			Bindings?.Update();
		}

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed).
		public Visibility VisibleWhen(bool value) =>
			value ? Visibility.Visible : Visibility.Collapsed;

		// Pull-tab glyph: points toward the bar's edge for "hide", away for "show" — so it inverts per Edge.
		//  = ChevronUp,  = ChevronDown (Segoe Fluent Icons).
		public string ToggleGlyph(bool visible)
		{
			bool top = Edge == BarEdge.Top;
			// Bottom: hide=down, show=up.  Top: hide=up, show=down.
			bool pointUp = top ? visible : !visible;
			return pointUp ? "" : "";
		}

		public string ToggleLabel(bool visible) => visible ? "Hide" : "Show";

		private void OnToggleOverlayBarClick(object sender, RoutedEventArgs e) =>
			IsOverlayBarVisible = !IsOverlayBarVisible;
	}
}
