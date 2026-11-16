using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Basemaps;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageSettingsConsoles
{
	/// <summary>Settings → Storage's Offline map section (see the XAML header): pick and download a map for this PC.</summary>
	public sealed partial class StorageSettingsConsoleOfflineMap : UserControl
	{
		public StorageSettingsConsoleOfflineMap()
		{
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(StorageSettingsConsoleOfflineMap),
				new PropertyMetadata(null));

		/// <summary>The folder, and Restore defaults, can't change while a download is writing with them.</summary>
		public bool Not(bool value) => !value;

		private async void OnBrowseSaveFolderClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickFolderAsync(this) is { } folder && ViewModel is { } vm) vm.SaveFolder = folder;
		}

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Offline map defaults?",
				"The area, detail and save-to folder go back to the DEFAULT column. Your map files are kept."))
			{
				vm.RestoreOfflineDefaults();
			}
		}
	}
}
