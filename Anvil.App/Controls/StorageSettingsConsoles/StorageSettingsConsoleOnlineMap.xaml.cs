using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageSettingsConsoles
{
	/// <summary>Settings → Storage's Online map section (see the XAML header): the Protomaps map key.</summary>
	public sealed partial class StorageSettingsConsoleOnlineMap : UserControl
	{
		public StorageSettingsConsoleOnlineMap()
		{
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(StorageSettingsConsoleOnlineMap),
				new PropertyMetadata(null));
	}
}
