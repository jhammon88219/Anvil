using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageManagers
{
	/// <summary>Settings → Storage's one surface (see the XAML header): the usage bar over the data table.</summary>
	public sealed partial class StorageManagerSettingsConsole : UserControl
	{
		public StorageManagerSettingsConsole()
		{
			InitializeComponent();
		}

		public StorageSettingsViewModel ViewModel
		{
			get => (StorageSettingsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StorageSettingsViewModel), typeof(StorageManagerSettingsConsole),
				new PropertyMetadata(null));

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore On disk defaults?",
				"Every Max size and Keep for goes back to the DEFAULT column. Anything over a restored limit is removed, as with any limit change."))
			{
				vm.RestoreDefaults();
			}
		}
	}
}
