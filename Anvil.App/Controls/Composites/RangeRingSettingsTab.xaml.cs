using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Radar Range Ring tab (see the XAML header): which range rings are drawn, how each
	/// looks, the distance labels, and the ruler's anchor. Bound to the coordinator <see cref="MapViewModel"/>;
	/// the state lives on <see cref="RadarViewModel.RangeRings"/>.
	/// </summary>
	public sealed partial class RangeRingSettingsTab : UserControl
	{
		public RangeRingSettingsTab()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RangeRingSettingsTab), new PropertyMetadata(null));

		/// <summary>x:Bind readout: "100 %".</summary>
		public string Pct(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0} %");

		/// <summary>x:Bind readout: "1.2 px".</summary>
		public string Px(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0.0} px");

		/// <summary>x:Bind readout: "10 px".</summary>
		public string Px0(double value) => string.Create(CultureInfo.CurrentCulture, $"{value:0} px");

		/// <summary>x:Bind readout: "045°".</summary>
		public string Deg(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:000}°");
	}
}
