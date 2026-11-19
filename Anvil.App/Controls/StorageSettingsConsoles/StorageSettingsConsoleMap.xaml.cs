using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageSettingsConsoles
{
	/// <summary>
	/// Settings → Storage's Which map to use section (see the XAML header): Online map | Offline map. Set up… opens the key
	/// dialog, Map files… the files dialog (which also downloads).
	/// </summary>
	public sealed partial class StorageSettingsConsoleMap : UserControl
	{
		public StorageSettingsConsoleMap()
		{
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(StorageSettingsConsoleMap),
				new PropertyMetadata(null));

		private void OnUseOfflineClick(object sender, RoutedEventArgs e) { if (ViewModel is { IsOfflineInUse: false } vm) vm.UseOffline(); }

		// No key saved yet: picking Online is the same as Set up… (the radio snaps back unless the dialog switches).
		private async void OnUseOnlineClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not { } vm || vm.IsOnlineInUse) return;
			if (vm.CanUseSavedOnline) { vm.UseSavedOnline(); return; }
			await Anvil.Dialogs.OnlineMapDialog.ShowAsync(this, vm);
			MapChoiceOnlineRadio.IsChecked = vm.IsOnlineInUse;
		}

		private async void OnSetUpOnlineClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await Anvil.Dialogs.OnlineMapDialog.ShowAsync(this, vm);
		}

		private async void OnMapFilesClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await Anvil.Dialogs.MapFilesDialog.ShowAsync(this, vm);
		}
	}
}
