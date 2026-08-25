using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Map tab (see the XAML header): basemap style + tile source, view extent, and
	/// state isolation. Bound to the coordinator <see cref="MapViewModel"/> — the basemap lives on it,
	/// isolation on <see cref="MapViewModel.StateIso"/>.
	/// </summary>
	public sealed partial class MapSettingsTab : UserControl
	{
		public MapSettingsTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapSettingsTab), new PropertyMetadata(null));

		// "Fit to view" — frame the current region (isolated state, else CONUS). Fire-and-forget: the camera
		// move runs through IMapService (map.fitBounds), same seam as every other map command.
		private void OnFitToViewClick(object sender, RoutedEventArgs e)
		{
			_ = ViewModel?.FitToViewAsync();
		}

		// "Reset north" — undo a right-click-drag rotate/tilt by animating bearing + pitch back to 0.
		// Fire-and-forget through IMapService (map.resetNorthPitch), same seam as Fit-to-view.
		private void OnResetNorthClick(object sender, RoutedEventArgs e)
		{
			_ = ViewModel?.ResetOrientationAsync();
		}
	}
}
