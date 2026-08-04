using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The Map Controls card floating above the OverlayBar (right side): basemap style + state isolation.
	/// Its visibility is driven by <see cref="MapViewModel.IsMapControlsCardOpen"/> (toggled by the "Map"
	/// button on the top bar); the card's down-triangle routes here to clear it. Bound to the coordinator
	/// <see cref="MapViewModel"/> (basemap lives on it, isolation on <see cref="MapViewModel.StateIso"/>).
	/// </summary>
	public sealed partial class MapControlsCard : UserControl
	{
		public MapControlsCard()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapControlsCard), new PropertyMetadata(null));

		// x:Bind function mapping a bool to Visibility (no value-converter lookup needed on a UserControl).
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// The card's down-triangle closes the Map Controls card (app-wide state; nothing else changes).
		private void OnCardCloseRequested(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null)
			{
				ViewModel.IsMapControlsCardOpen = false;
			}
		}

		// "Fit to view" — frame the current region (isolated state, else CONUS). Fire-and-forget: the camera
		// move runs through IMapService (map.fitBounds), same seam as every other map command.
		private void OnFitToViewClick(object sender, RoutedEventArgs e)
		{
			_ = ViewModel?.FitToViewAsync();
		}
	}
}
