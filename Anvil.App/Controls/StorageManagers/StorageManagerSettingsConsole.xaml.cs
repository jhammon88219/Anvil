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
	}
}
