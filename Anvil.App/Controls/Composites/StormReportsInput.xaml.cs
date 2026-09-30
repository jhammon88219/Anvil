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

		public Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		// The list's type dot. ⚠️ DATA colours — the same literals as the rows above and storm-reports.js
		// KIND_LAYERS; change all three. A NEW brush per row, not a shared static: this control lives in two OS
		// windows (Now + Past), and a brush is a UI object.
		public static Microsoft.UI.Xaml.Media.Brush KindBrush(string kind) =>
			new Microsoft.UI.Xaml.Media.SolidColorBrush(Anvil.Converters.ColorUtil.FromHex(kind switch
			{
				"torn" => "#E51919",
				"wind" => "#1663D8",
				_ => "#18A020",
			}));

		// A row click ACTS — fly there (the list keeps no selection).
		private void OnReportClick(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem is Anvil.Models.StormReportItem report)
			{
				ViewModel?.FlyTo(report);
			}
		}

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
