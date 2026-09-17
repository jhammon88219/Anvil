using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The SPC storm-reports verification controls (per-type Tornado / Wind / Hail toggles, a count readout,
	/// and fill opacity), shown in both the Past and Now windows. Bound to the <see cref="StormReportsViewModel"/>,
	/// which keys the overlay to the active convective day (replay day in PastCast, today in NowCast).
	/// </summary>
	public sealed partial class StormReportsInput : UserControl
	{
		public StormReportsInput()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the per-type report counts (int → display string). Re-evaluates when
		/// the bound count property raises PropertyChanged.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		// x:Bind helper: collapse a card line that has nothing to say, so the card closes up rather than
		// leaving a gap where the context or footer would be. Same helper as PastCastTab's.
		public Microsoft.UI.Xaml.Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value)
				? Microsoft.UI.Xaml.Visibility.Collapsed
				: Microsoft.UI.Xaml.Visibility.Visible;

		// x:Bind helpers for the opacity READOUT. ⚠️ It formats the VIEW MODEL's value — the number that is
		// pushed to the map — never Slider.Value, so it cannot describe a value the map is not using.
		public string Percent(double opacity) =>
			System.Math.Round(opacity * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

		// A TextBlock has no disabled state, so the readout dims with its slider through Opacity instead
		// (a theme brush can't be resolved here — see CLAUDE.md).
		public double DimUnless(bool enabled) => enabled ? 1.0 : 0.4;

		/// <summary>The storm-reports view model; bound from the host (MainWindow → ViewModel.StormReports).</summary>
		public StormReportsViewModel ViewModel
		{
			get => (StormReportsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StormReportsViewModel), typeof(StormReportsInput), new PropertyMetadata(null));
	}
}
