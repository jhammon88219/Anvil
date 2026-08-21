using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Anvil.ViewModels;

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

		/// <summary>Label size as a fraction of the square's side, so the word scales with the key.</summary>
		private const double FontRatio = 0.24;

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
			var font = side * FontRatio;
			var inset = new Thickness(0, VerticalInset, 0, VerticalInset);

			Apply(PastPill);
			Apply(NowPill);
			Apply(ForePill);

			void Apply(ToggleButton pill)
			{
				if (pill.Width != side)
				{
					pill.Width = side;
				}

				if (pill.FontSize != font)
				{
					pill.FontSize = font;
				}

				if (!pill.Margin.Equals(inset))
				{
					pill.Margin = inset;
				}
			}
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
