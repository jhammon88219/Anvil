using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Models;

namespace Anvil.Controls.Primitives
{
	/// <summary>
	/// The SPC outlook legend (see the XAML header): solid levels, then the intensity-hatching block. Plain
	/// DPs, so the ForeCast and PastCast outlook sections both host it over their own view model.
	/// </summary>
	public sealed partial class OutlookLegend : UserControl
	{
		public OutlookLegend()
		{
			InitializeComponent();
		}

		/// <summary>The solid half of the scale, least-severe first.</summary>
		public IReadOnlyList<SpcRiskLevel> Entries
		{
			get => (IReadOnlyList<SpcRiskLevel>)GetValue(EntriesProperty);
			set => SetValue(EntriesProperty, value);
		}

		public static readonly DependencyProperty EntriesProperty =
			DependencyProperty.Register(nameof(Entries), typeof(IReadOnlyList<SpcRiskLevel>), typeof(OutlookLegend),
				new PropertyMetadata(null));

		/// <summary>The Conditional Intensity Groups, each marked with whether the outlook carries it.</summary>
		public IReadOnlyList<OutlookHatchLegendRow> HatchRows
		{
			get => (IReadOnlyList<OutlookHatchLegendRow>)GetValue(HatchRowsProperty);
			set => SetValue(HatchRowsProperty, value);
		}

		public static readonly DependencyProperty HatchRowsProperty =
			DependencyProperty.Register(nameof(HatchRows), typeof(IReadOnlyList<OutlookHatchLegendRow>), typeof(OutlookLegend),
				new PropertyMetadata(null));

		/// <summary>Whether the hatching is drawn on the map (two-way).</summary>
		public bool ShowHatching
		{
			get => (bool)GetValue(ShowHatchingProperty);
			set => SetValue(ShowHatchingProperty, value);
		}

		public static readonly DependencyProperty ShowHatchingProperty =
			DependencyProperty.Register(nameof(ShowHatching), typeof(bool), typeof(OutlookLegend),
				new PropertyMetadata(true));

		// x:Bind helper: the hatching block exists only on products that have intensity groups.
		public Visibility HasHatch(IReadOnlyList<OutlookHatchLegendRow>? rows) =>
			rows is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind helper: a hatch group absent from the outlook is dimmed as well as labelled. STATIC — called
		// from a DataTemplate. An Opacity, not a brush, so no theme brush is resolved in C#.
		public static double HatchRowOpacity(bool inOutlook) => inOutlook ? 1.0 : 0.45;

		// x:Bind helpers for the swatch's hatching. Deliberately split the SAME way outlook.js's
		// makeHatchImage takes its `back` / `fwd` flags: each asks "does this pattern include this
		// diagonal?", so the cross (CIG3) needs no case of its own and the swatch cannot drift out of step
		// with the map's tiles. STATIC because they are called from a DataTemplate.
		// ⚠️ The direction is the data here — every CIG level is black. See SpcHatchPattern.
		public static Visibility HatchBackward(SpcHatchPattern pattern) =>
			pattern is SpcHatchPattern.BackwardDiagonal or SpcHatchPattern.DiagonalCross
				? Visibility.Visible : Visibility.Collapsed;

		public static Visibility HatchForward(SpcHatchPattern pattern) =>
			pattern is SpcHatchPattern.ForwardDiagonal or SpcHatchPattern.DiagonalCross
				? Visibility.Visible : Visibility.Collapsed;
	}
}
