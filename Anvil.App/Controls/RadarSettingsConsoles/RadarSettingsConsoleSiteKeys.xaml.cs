using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RadarSettingsConsoles
{
	/// <summary>Settings → Radar's Site keys section (see the XAML header).</summary>
	public sealed partial class RadarSettingsConsoleSiteKeys : UserControl
	{
		public RadarSettingsConsoleSiteKeys()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsConsoleSiteKeys),
				new PropertyMetadata(null));

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Site keys defaults?",
				"The three site-key switches go back to the DEFAULT column."))
			{
				vm.ResetSiteKeys();
			}
		}
	}
}
