using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The NowCast tab body of the Timeframe window (see the XAML header) — watch boxes, storm-based warnings
	/// and today's storm reports. Bound to the coordinator <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class NowCastTab : UserControl
	{
		public NowCastTab()
		{
			InitializeComponent();
		}

		/// <summary>x:Bind formatter for the per-type active-warning counts (int → display string). Used by
		/// the "Active" readout; re-evaluates when the bound count property raises PropertyChanged.</summary>
		public string Fmt(int count) => count.ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(NowCastTab), new PropertyMetadata(null));
	}
}
