using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageManagers
{
	/// <summary>Settings → Storage's table (see the XAML header): the header row + one declared row per kind.</summary>
	public sealed partial class StorageManagerSettingsConsoleDataTable : UserControl
	{
		public StorageManagerSettingsConsoleDataTable()
		{
			InitializeComponent();
		}

		public StorageSettingsViewModel ViewModel
		{
			get => (StorageSettingsViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(StorageSettingsViewModel), typeof(StorageManagerSettingsConsoleDataTable),
				new PropertyMetadata(null));
	}
}
