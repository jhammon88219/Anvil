using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Storage tab (see the XAML header): the on-disk radar cache — its live size, a
	/// clear action, and the persisted size cap the startup sweep enforces. Bound to the coordinator
	/// <see cref="MapViewModel"/>; the state lives on <see cref="MapViewModel.Storage"/>.
	/// </summary>
	public sealed partial class StorageSettingsTab : UserControl
	{
		public StorageSettingsTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(StorageSettingsTab), new PropertyMetadata(null));

		// "Clear now": delete the whole radar volume cache, then refresh the readout. The VM guards against
		// overlap + drives the button's enabled state.
		private async void OnClearCacheClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null)
			{
				await ViewModel.Storage.ClearCacheAsync();
			}
		}
	}
}
