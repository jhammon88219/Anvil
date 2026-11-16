using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.GeneralSettingsConsoles
{
	/// <summary>
	/// Settings → General's Units section (see the XAML header): clock, time zone, distance, speed, height and hail
	/// pickers over <see cref="MapViewModel"/>'s unit properties — and date + coordinates — under a Unit set that locks them.
	/// </summary>
	public sealed partial class GeneralSettingsConsoleUnits : UserControl
	{
		public GeneralSettingsConsoleUnits()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(GeneralSettingsConsoleUnits),
				new PropertyMetadata(null));

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Units defaults?",
				"Every unit goes back to the DEFAULT column and the Unit set to Custom. Your saved custom picks are replaced too."))
			{
				vm.ResetUnits();
			}
		}
	}
}
