using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Anvil.Layout;
using Anvil.ViewModels;
// ⚠️ Windows.Foundation.Size, imported rather than written inline: this file's namespace sits under
// Anvil.Controls, where a leading "Windows." binds to the sibling Anvil.Controls.Windows and fails to
// resolve. A using directive is outside the namespace, where Windows still means the global one.
using Windows.Foundation;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The overlay bar's CENTRE section: three temporal features (PastCast / NowCast / ForeCast), each a
	/// SINGLE toggle. A click activates the mode and opens that feature's settings window (see
	/// <see cref="PastCastWindow"/> / <see cref="NowCastWindow"/> / <see cref="ForeCastWindow"/> — three
	/// INDEPENDENT windows); clicking the active mode flips its window open/closed, and you leave a mode by
	/// choosing another (Past excludes Now/Fore; Now and Fore coexist). The lit state binds OneWay
	/// to <see cref="MapViewModel.IsPastCast"/> etc.; the click routes through
	/// <see cref="MapViewModel.ToggleTemporalMode"/>. Binds the coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class TemporalToggles : UserControl
	{
		// ===== Key sizing. The keys are SQUARE and sized off the BAR, not off constants: the row stretches
		// to the bar's content height and every number is derived from that measurement, so the keys track
		// the bar when it changes height (quad panes add a chip row) with nothing to keep in sync by hand.
		//
		// The SIZE rule itself lives in Layout/BarKeyMetrics, NOT here, because MainWindow's right cluster
		// sizes its five keys by the same rule and the two sets sit side by side in one bar — a local copy
		// would let the halves drift apart the first time either was tuned.
		//
		// ⚠️ That now includes the NAME fitting (BarKeyMetrics.NameInset / MaxNameFont / NameFontFor /
		// ProbeWidthOf). It used to live here, as knobs private to this control, back when these were the
		// only keys carrying a word. The right cluster's keys are named too now, so the rule had to move —
		// a name rendered a point larger on one half of the bar than the other is exactly the drift.

		public TemporalToggles()
		{
			InitializeComponent();
		}

		// Square the keys off the row's measured height, and scale the label with them.
		//
		// This is code-behind rather than a binding because ActualHeight does NOT raise property-changed
		// notifications in WinUI — an x:Bind to it would set the width once and then silently stop
		// tracking, which is exactly the staleness the adaptive sizing exists to avoid. SizeChanged is
		// the reliable signal.
		//
		// ⚠️ It cannot loop: setting Width re-fires SizeChanged (the row got wider), but HEIGHT is
		// unchanged on that second pass, so the values are already correct and the equality guards make
		// it a no-op. Height flows down from the bar and never from these writes.
		private void OnPillRowSizeChanged(object sender, SizeChangedEventArgs e)
		{
			// The key's height is whatever the stretch gives it: the row, less the inset margin. Width
			// mirrors that number, which is what makes it square.
			var side = BarKeyMetrics.SideFor(e.NewSize.Height);
			var inset = new Thickness(0, BarKeyMetrics.VerticalInset, 0, BarKeyMetrics.VerticalInset);

			// The NAME is fitted to the key's WIDTH, not scaled off its height, because the names are long
			// ("ForeCast" is eight characters in a square barely wider than it is tall) and height alone
			// would happily pick a size that overflows. All three keys share the one size that fits the
			// widest of them — sizing each to its own text would render "NowCast" noticeably larger than
			// "ForeCast" and break the trio's symmetry. Probe measured once (fixed strings).
			if (_probeWidth <= 0)
			{
				_probeWidth = BarKeyMetrics.ProbeWidthOf(PastName, NowName, ForeName);
			}

			var nameFont = BarKeyMetrics.NameFontFor(side, _probeWidth);
			var glyphFont = BarKeyMetrics.LabelledGlyphFor(side);

			Apply(PastPill, PastGlyph, PastName);
			Apply(NowPill, NowGlyph, NowName);
			Apply(ForePill, ForeGlyph, ForeName);

			void Apply(ToggleButton pill, FontIcon glyph, TextBlock name)
			{
				if (pill.Width != side)
				{
					pill.Width = side;
				}

				if (!pill.Margin.Equals(inset))
				{
					pill.Margin = inset;
				}

				glyph.FontSize = glyphFont;
				name.FontSize = nameFont;
			}
		}

		// Widest name measured at the probe size — computed once, since the three labels are fixed strings.
		private double _probeWidth;

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalToggles), new PropertyMetadata(null));

		// Each mode's single toggle routes here (IsChecked is OneWay to the mode projection, so the click
		// drives the VM, not the reverse). See MapViewModel.ToggleTemporalMode for the activate/flip rules.
		private void OnPastClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalMode.Past);
		private void OnNowClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalMode.Now);
		private void OnForeClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleTemporalMode(TemporalMode.Fore);
	}
}
