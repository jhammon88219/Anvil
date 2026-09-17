using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// How the radar LAYER is drawn (see the XAML header) — today its opacity, and the home for whatever
	/// else ends up belonging to the layer rather than to the overlays above it. Shared by both temporal
	/// windows; bound to <see cref="RadarViewModel"/>.
	/// </summary>
	public sealed partial class RadarOverlayInput : UserControl
	{
		public RadarOverlayInput()
		{
			InitializeComponent();
		}

		// x:Bind helpers for the opacity READOUT. ⚠️ It formats the VIEW MODEL's value — the number that is
		// pushed to the map — never Slider.Value, so it cannot describe a value the map is not using.
		public string Percent(double opacity) =>
			System.Math.Round(opacity * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

		// A TextBlock has no disabled state, so the readout dims with its slider through Opacity instead
		// (a theme brush can't be resolved here — see CLAUDE.md).
		public double DimUnless(bool enabled) => enabled ? 1.0 : 0.4;

		/// <summary>The radar view model; bound from the host.</summary>
		public RadarViewModel ViewModel
		{
			get => (RadarViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(RadarViewModel), typeof(RadarOverlayInput),
				new PropertyMetadata(null));
	}
}
