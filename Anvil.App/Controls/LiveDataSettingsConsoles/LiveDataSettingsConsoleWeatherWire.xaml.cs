using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.AboutSettingsConsoles;
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

		public bool Both(bool a, bool b) => a && b;

		public Visibility Shown(bool value, bool when) => value == when ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>Keeps typed text out from under the Edit key (the WinUI box padding is 10,5,6,6).</summary>
		public Thickness EditPadding(bool hasSavedAccount) =>
			new(10, 5, hasSavedAccount ? (double)Resources["WireButtonWidth"] + 8 : 6, 6);

		/// <summary>The reveal eye would sit under the Edit key — off while it's there.</summary>
		public PasswordRevealMode RevealMode(bool hasSavedAccount) => hasSavedAccount ? PasswordRevealMode.Hidden : PasswordRevealMode.Peek;

		/// <summary>The account's last message / where it stands, or why the card is greyed.</summary>
		public string AccountHint(bool isNwws, string hint) => isNwws ? hint : Anvil.Services.SettingsText.HintOffOf("wire.account");

		// The password goes straight to the VM, never through a binding.
		private void OnPasswordChanged(object sender, RoutedEventArgs e) => ViewModel?.SetPendingPassword(NwwsPasswordBox.Password);

		private void OnEditUserClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null) { return; }
			ViewModel.ToggleEditUser();
			if (ViewModel.IsEditingUser) { NwwsUsernameTextBox.Focus(FocusState.Programmatic); NwwsUsernameTextBox.SelectAll(); }
		}

		private void OnEditPasswordClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null) { return; }
			ViewModel.ToggleEditPassword();
			if (ViewModel.IsEditingPassword) { NwwsPasswordBox.Focus(FocusState.Programmatic); }
			else { NwwsPasswordBox.Password = string.Empty; } // Cancel keeps the saved one
		}

		private void OnSaveAccountClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.SaveAccount() == true) { NwwsPasswordBox.Password = string.Empty; } // both boxes lock again
		}

		private void OnTestConnectionClick(object sender, RoutedEventArgs e) => _ = ViewModel?.TestConnectionAsync();

		private void OnDetailsClick(object sender, RoutedEventArgs e) => ViewModel?.ToggleDetails();

		private void OnRequestAccountClick(object sender, RoutedEventArgs e) =>
			AboutAnvil.OpenLink(this, "the NWS Weather Wire account request page", LiveDataViewModel.RequestAccountUrl);

		/// <summary>Control Panel's Credential Manager (Web Credentials holds the password; Windows asks for the sign-in to show it).</summary>
		private void OnOpenCredentialManagerClick(object sender, RoutedEventArgs e)
		{
			try { Process.Start(new ProcessStartInfo("control.exe", "/name Microsoft.CredentialManager") { UseShellExecute = true }); }
			catch (Exception ex) { Debug.WriteLine($"[wire] opening Credential Manager failed: {ex.Message}"); }
		}

		private async void OnForgetAccountClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is null) { return; }
			var name = ViewModel.SavedUsername;
			if (!await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Forget this Weather Wire account?",
				$"The password for {name} is removed from Windows Credential Manager and the user name is cleared. If Weather Wire is " +
				"the source, Anvil signs out and polls until a new account is saved. Your NWS account itself isn't touched.", "Forget"))
			{
				return;
			}
			NwwsPasswordBox.Password = string.Empty;
			ViewModel.ForgetAccount();
		}
	}
}
