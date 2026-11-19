using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Basemaps;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageSettingsConsoles
{
	/// <summary>
	/// Settings → Storage's Which map to use section (see the XAML header): Online map | Offline map, the files nested under
	/// Offline. The rows hand their pick to <see cref="BasemapSetupViewModel"/>; Set up… and Download a map… open the dialogs.
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

		/// <summary>Download a map… greys while one is already running.</summary>
		public bool Not(bool value) => !value;

		// A row button's DataContext is its BasemapFileRow (the ItemsControl's item).
		private static BasemapFileRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as BasemapFileRow;

		private void OnUseFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r && !r.IsInUse) ViewModel?.UseFile(r); }
		private void OnShowFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) BasemapPickers.ShowInFolder(r.Path); }
		private void OnAskDeleteClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.AskDelete(r); }
		private void OnKeepFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.KeepFile(r); }
		private void OnDeleteFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.DeleteFile(r); }

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

		private async void OnDownloadClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await Anvil.Dialogs.DownloadMapDialog.ShowAsync(this, vm);
		}

		private async void OnAddFileClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickMapFileAsync(this) is { } path && ViewModel is { } vm) await vm.AddFileAsync(path);
		}
	}
}
