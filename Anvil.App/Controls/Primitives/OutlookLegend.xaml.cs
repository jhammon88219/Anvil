using System;
using System.Collections.Generic;
using System.Globalization;
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

		/// <summary>Opacity (0-1) of the hatch lines + their outline on the map (two-way) — the slider row.</summary>
		public double HatchOpacity
		{
			get => (double)GetValue(HatchOpacityProperty);
			set => SetValue(HatchOpacityProperty, value);
		}

		public static readonly DependencyProperty HatchOpacityProperty =
			DependencyProperty.Register(nameof(HatchOpacity), typeof(double), typeof(OutlookLegend),
				new PropertyMetadata(1.0));

		// x:Bind helper: the slider's readout — the bound (view model's) number, as DragMoveLayerExpander's.
		public string Percent(double opacity) =>
			Math.Round(opacity * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

		// x:Bind helper: a TextBlock has no disabled state, so the readout dims with the greyed slider.
		public double ReadoutOpacity(bool showHatching) => showHatching ? 1.0 : 0.45;

		// x:Bind helper: the hatching block exists only on products that have intensity groups.
		public Visibility HasHatch(IReadOnlyList<OutlookHatchLegendRow>? rows) =>
			rows is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

		// x:Bind helper: a hatch group absent from the outlook is dimmed as well as labelled. STATIC — called
		// from a DataTemplate. An Opacity, not a brush, so no theme brush is resolved in C#.
		public static double HatchRowOpacity(bool inOutlook) => inOutlook ? 1.0 : 0.45;

		// x:Bind helpers for the swatch's hatching, one per drawn LINE and named by how it LOOKS — the same
		// split as outlook.js makeHatchImage's `slashDashed` / `slash` / `backslash`, so the swatch cannot
		// drift out of step with the map's tiles: CIG1 = dashed '/', CIG2 = solid '\', CIG3 = solid both.
		// STATIC because they are called from a DataTemplate.
		// ⚠️ The direction is the data here — every CIG level is black — and ESRI's pattern names read
		// backwards (BackwardDiagonal is '/'). See SpcHatchPattern.
		public static Visibility HatchSlashDashed(SpcHatchPattern pattern) =>
			pattern is SpcHatchPattern.BackwardDiagonal ? Visibility.Visible : Visibility.Collapsed;

		public static Visibility HatchSlash(SpcHatchPattern pattern) =>
			pattern is SpcHatchPattern.DiagonalCross ? Visibility.Visible : Visibility.Collapsed;

		public static Visibility HatchBackslash(SpcHatchPattern pattern) =>
			pattern is SpcHatchPattern.ForwardDiagonal or SpcHatchPattern.DiagonalCross
				? Visibility.Visible : Visibility.Collapsed;
	}
}
