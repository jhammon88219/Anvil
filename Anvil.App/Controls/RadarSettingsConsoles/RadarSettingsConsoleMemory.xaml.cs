using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RadarSettingsConsoles
{
	/// <summary>Settings → Radar's Memory section (see the XAML header): how much RAM a PastCast loop may hold.</summary>
	public sealed partial class RadarSettingsConsoleMemory : UserControl
	{
		public RadarSettingsConsoleMemory()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsConsoleMemory),
				new PropertyMetadata(null));

		public bool Not(bool value) => !value;
		public Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Memory default?",
				"Radar goes back to the recommended quarter of your RAM."))
			{
				vm.ResetRadarMemory();
			}
		}
	}
}
