using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// One map pane's header: a full-width raised bar, its product · legend · tilt (PaneNotchContent) centred on the pane's
	/// visible part, plus a which-pane mark in multi-pane layouts. See the XAML header for the shape and the rules.
	/// </summary>
	public sealed partial class PaneHeader : UserControl
	{
		public PaneHeader()
		{
			InitializeComponent();
		}

		/// <summary>The pane this header belongs to.</summary>
		public RadarPaneViewModel Pane
		{
			get => (RadarPaneViewModel)GetValue(PaneProperty);
			set => SetValue(PaneProperty, value);
		}

		public static readonly DependencyProperty PaneProperty =
			DependencyProperty.Register(nameof(Pane), typeof(RadarPaneViewModel), typeof(PaneHeader), new PropertyMetadata(null));

		/// <summary>The shared radar view model (the layout, tilt, whether a loop is loaded).</summary>
		public RadarViewModel Radar
		{
			get => (RadarViewModel)GetValue(RadarProperty);
			set => SetValue(RadarProperty, value);
		}

		public static readonly DependencyProperty RadarProperty =
			DependencyProperty.Register(nameof(Radar), typeof(RadarViewModel), typeof(PaneHeader), new PropertyMetadata(null));

		/// <summary>The ForeCast Short Range model: while it draws, the content shows its name + legend instead.</summary>
		public ShortRangeViewModel Model
		{
			get => (ShortRangeViewModel)GetValue(ModelProperty);
			set => SetValue(ModelProperty, value);
		}

		public static readonly DependencyProperty ModelProperty =
			DependencyProperty.Register(nameof(Model), typeof(ShortRangeViewModel), typeof(PaneHeader), new PropertyMetadata(null));

		/// <summary>One side's padding so the row centres on the pane cell's centre, not this bar's middle (set by the host).</summary>
		public Thickness ContentPadding
		{
			get => (Thickness)GetValue(ContentPaddingProperty);
			set => SetValue(ContentPaddingProperty, value);
		}

		public static readonly DependencyProperty ContentPaddingProperty =
			DependencyProperty.Register(nameof(ContentPadding), typeof(Thickness), typeof(PaneHeader), new PropertyMetadata(default(Thickness)));

		/// <summary>The whole centred row's width (mark + content), for the host's centring. 0 before layout.</summary>
		public double RowWidth => PaneHeaderRow.ActualWidth;

		/// <summary>The row's size changed — the host re-centres (its width) and re-docks the rail (its height).</summary>
		public event EventHandler? RowSizeChanged;

		private void OnRowSizeChanged(object sender, SizeChangedEventArgs e) => RowSizeChanged?.Invoke(this, EventArgs.Empty);

		/// <summary>The centred content's width changed — MainWindow copies pane 0's onto the place search box.</summary>
		public event EventHandler<double>? ContentWidthChanged;

		/// <summary>The centred content's current width (0 before layout).</summary>
		public double ContentWidth => PaneHeaderContent.ActualWidth;

		private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => ContentWidthChanged?.Invoke(this, e.NewSize.Width);

		// Quad takes the compact content (no scale numbers) — the floating selector's rule.
		public bool IsQuad(PaneLayout layout) => layout == PaneLayout.Quad;

		public Visibility MarkVisibility(PaneLayout layout) => layout == PaneLayout.Single ? Visibility.Collapsed : Visibility.Visible;

		public Visibility BottomRowVisibility(PaneLayout layout) => layout == PaneLayout.Quad ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>A mark cell (row/column in the 3x3 [*, gutter, *] grid, 0 or 2) at full strength when it is THIS pane's
		/// cell (PaneLayoutInfo.CellOf, the same rule the page's paneRects mirrors), else faint.</summary>
		public double CellOpacity(PaneLayout layout, int index, int row, int column)
		{
			var cell = layout.CellOf(index);
			var mine = layout == PaneLayout.TwoAcross ? cell.Column == column : cell.Row == row && cell.Column == column;
			return mine ? 1.0 : 0.25;
		}
	}
}
