using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The app-wide settings card floating above the OverlayBar (right side) — the mirror of the temporal
	/// cards. Its visibility is driven by <see cref="MapViewModel.IsSettingsCardOpen"/> (toggled by the
	/// settings cog); the card's down-triangle routes here to clear it. Bound to the coordinator
	/// <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class AppSettingsCard : UserControl
	{
		public AppSettingsCard()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(AppSettingsCard), new PropertyMetadata(null));

		// "Clear now" in the Storage section: delete the whole radar volume cache, then refresh the readout.
		// The VM guards against overlap + drives the button's enabled state.
		private async void OnClearCacheClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null)
			{
				await ViewModel.Storage.ClearCacheAsync();
			}
		}
	}
}
