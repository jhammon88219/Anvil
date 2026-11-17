using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.AppearanceSettingsConsoles
{
	/// <summary>Settings → Appearance's Theme section (see the XAML header): the theme and the basemap under it.</summary>
	public sealed partial class AppearanceSettingsConsoleTheme : UserControl
	{
		public AppearanceSettingsConsoleTheme()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(AppearanceSettingsConsoleTheme),
				new PropertyMetadata(null));

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Theme defaults?",
				"The theme and basemap go back to the DEFAULT column — the basemap follows the theme again."))
			{
				vm.ResetTheme();
			}
		}
	}
}
