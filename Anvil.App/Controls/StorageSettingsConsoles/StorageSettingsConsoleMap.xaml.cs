using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Basemaps;
using Anvil.ViewModels;

namespace Anvil.Controls.StorageSettingsConsoles
{
	/// <summary>
	/// Settings → Storage's Map section (see the XAML header): the map in use and the map files. The file rows' buttons hand
	/// their row to <see cref="BasemapSetupViewModel"/>.
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

		// A row button's DataContext is its BasemapFileRow (the ItemsControl's item).
		private static BasemapFileRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as BasemapFileRow;

		private void OnUseFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r && !r.IsInUse) ViewModel?.UseFile(r); }
		private void OnUseOnlineClick(object sender, RoutedEventArgs e) { if (ViewModel is { IsOnlineInUse: false } vm) vm.UseSavedOnline(); }
		private void OnShowFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) BasemapPickers.ShowInFolder(r.Path); }
		private void OnAskDeleteClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.AskDelete(r); }
		private void OnKeepFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.KeepFile(r); }
		private void OnDeleteFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.DeleteFile(r); }

		private async void OnAddFileClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickMapFileAsync(this) is { } path && ViewModel is { } vm) await vm.AddFileAsync(path);
		}
	}
}
