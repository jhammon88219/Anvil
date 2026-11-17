using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.LiveDataSettingsConsoles
{
	/// <summary>Settings → Live Data's Weather Wire account section (see the XAML header).</summary>
	public sealed partial class LiveDataSettingsConsoleWeatherWire : UserControl
	{
		public LiveDataSettingsConsoleWeatherWire()
		{
			InitializeComponent();
		}

		public LiveDataViewModel ViewModel
		{
			get => (LiveDataViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(LiveDataViewModel), typeof(LiveDataSettingsConsoleWeatherWire),
				new PropertyMetadata(null));

		/// <summary>The account's last message, or why the card is greyed.</summary>
		public string AccountHint(bool isNwws, string message) => isNwws ? message : "Only with NOAA Weather Wire";

		// The password goes straight to the VM, never through a binding.
		private void OnPasswordChanged(object sender, RoutedEventArgs e) => ViewModel?.SetPendingPassword(NwwsPasswordBox.Password);

		private void OnSaveAccountClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.SaveAccount() == true) { NwwsPasswordBox.Password = string.Empty; } // now "Saved — type to replace"
		}

		private void OnTestConnectionClick(object sender, RoutedEventArgs e) => _ = ViewModel?.TestConnectionAsync();
	}
}
