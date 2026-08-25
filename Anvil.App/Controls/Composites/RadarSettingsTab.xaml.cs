using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Radar tab (see the XAML header): radar-site marker visibility, the mile grid,
	/// and the Inspect cursor tool — the radar content drawn over the basemap, all of it global rather than
	/// per-pane. Bound to the coordinator <see cref="MapViewModel"/>; the state it drives lives on
	/// <see cref="MapViewModel.Radar"/> and <see cref="MapViewModel.MileGrid"/>.
	/// </summary>
	public sealed partial class RadarSettingsTab : UserControl
	{
		public RadarSettingsTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsTab), new PropertyMetadata(null));
	}
}
