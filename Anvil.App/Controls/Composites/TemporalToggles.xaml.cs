using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
		// ===== Key sizing. The keys are SQUARE and sized off the BAR, not off constants here: the row
		// stretches to the bar's content height and every number below is derived from that measurement,
		// so the keys track the bar when it changes height (quad panes add a chip row) with nothing to
		// keep in sync by hand. These three are the only tuning knobs.

		/// <summary>Vertical inset of a key inside the row, per edge. 0 = the key fills the row, which is
		/// the tallest it can be without growing the bar; the bar's own 10px padding already supplies the
		/// breathing room that makes this read as "almost the height of the bar". Raise it to pull the keys
		/// in from the bar's edges.
		/// <para>⚠️ The inset is a MARGIN, not a smaller explicit Height, and that is load-bearing. A key's
		/// height must keep coming from the STRETCH so its desired height stays content-sized: if a key
		/// instead demanded the height it was last given, it would hold the bar open at that height and the
		/// bar could never shrink again — a ratchet, since the row feeds the key and the key would feed the
		/// row right back.</para></summary>
		private const double VerticalInset = 0;

		/// <summary>Glyph size as a fraction of the square's side.</summary>
		private const double GlyphRatio = 0.30;

		/// <summary>Horizontal breathing room inside the key, per edge, that the name must not run into.</summary>
		private const double NameInset = 5;

		/// <summary>Ceiling on the name's size. Without it a very tall bar would inflate the words past the
		/// point where they read as labels.</summary>
		private const double MaxNameFont = 15;

		/// <summary>Smallest square we will draw, in case the row measures degenerate (0 during the first
		/// layout pass, or a host that forgets to stretch us). Keeps the keys clickable rather than vanishing.</summary>
		private const double MinSide = 28;

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
			var side = Math.Max(MinSide, e.NewSize.Height - (2 * VerticalInset));
			var inset = new Thickness(0, VerticalInset, 0, VerticalInset);

			// The NAME is fitted to the key's WIDTH, not scaled off its height, because the names are long
			// ("ForeCast" is eight characters in a square barely wider than it is tall) and height alone
			// would happily pick a size that overflows. All three keys share the one size that fits the
			// widest of them — sizing each to its own text would render "NowCast" noticeably larger than
			// "ForeCast" and break the trio's symmetry.
			var nameFont = Math.Min(MaxNameFont, FitNameFont(side - (2 * NameInset)));
			var glyphFont = side * GlyphRatio;

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

		// Widest name measured at ProbeFont — computed once, since the three labels are fixed strings.
		private double _probeWidth;

		/// <summary>
		/// The font size at which the LONGEST of the three names just fits <paramref name="usableWidth"/>.
		/// <para>Measured rather than derived from a characters × average-width guess: the guess bakes in a
		/// constant that is wrong for a different typeface and goes stale the moment a name is renamed,
		/// whereas measuring asks the actual font. The probe is done once and scaled thereafter, so no
		/// measure work happens on later resizes.</para>
		/// </summary>
		private double FitNameFont(double usableWidth)
		{
			const double ProbeFont = 100;

			if (_probeWidth <= 0)
			{
				foreach (var label in new[] { PastName, NowName, ForeName })
				{
					var restore = label.FontSize;
					label.FontSize = ProbeFont;
					label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
					_probeWidth = Math.Max(_probeWidth, label.DesiredSize.Width);
					label.FontSize = restore;
				}
			}

			return _probeWidth > 0 ? ProbeFont * usableWidth / _probeWidth : MaxNameFont;
		}

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
