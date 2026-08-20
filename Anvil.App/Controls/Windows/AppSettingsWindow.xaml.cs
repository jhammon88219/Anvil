using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Sections
{
	/// <summary>
	/// The app-wide settings panel, hosted in its own OS window by <see cref="WindowManager"/> and driven by
	/// <see cref="MapViewModel.IsSettingsWindowOpen"/> (toggled by the settings cog on the top bar; the
	/// window's caption Close clears it). Bound to the coordinator
	/// <see cref="MapViewModel"/>.
	/// </summary>
	public sealed partial class AppSettingsWindow : UserControl
	{
		public AppSettingsWindow()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(AppSettingsWindow), new PropertyMetadata(null));

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
