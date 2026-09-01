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
