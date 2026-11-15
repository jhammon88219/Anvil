using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.StorageManagers;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Storage page (see the XAML header): "Open Anvil's folder" over
	/// <see cref="StorageManagerSettingsConsole"/>. Bound to the coordinator <see cref="MapViewModel"/>; the state lives
	/// on <see cref="MapViewModel.Storage"/>.
	/// </summary>
	public sealed partial class StorageSettingsTab : UserControl
	{
		public StorageSettingsTab()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(StorageSettingsTab), new PropertyMetadata(null));

		private void OnOpenAnvilFolderClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) StorageManagerFolderLauncher.Open(ViewModel.Storage.RootFolder);
		}
	}
}
