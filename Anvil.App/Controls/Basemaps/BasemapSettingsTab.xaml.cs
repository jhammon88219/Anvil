using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Basemaps
{
	/// <summary>
	/// The Settings window's Basemap tab (see the XAML header). State lives on <see cref="BasemapSetupViewModel"/>; the
	/// file rows' buttons hand their row to it.
	/// </summary>
	public sealed partial class BasemapSettingsTab : UserControl
	{
		public BasemapSettingsTab()
		{
			InitializeComponent();
			// The folder can change outside this tab (the Map tab's Browse…, a file added in Explorer): re-read on show.
			Loaded += (_, _) => ViewModel?.Refresh();
			RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility == Visibility.Visible) ViewModel?.Refresh(); });
		}

		/// <summary>The map setup view model; bound from the host.</summary>
		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(BasemapSettingsTab), new PropertyMetadata(null));

		// A row button's DataContext is its BasemapFileRow (the ItemsControl's item).
		private static BasemapFileRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as BasemapFileRow;

		private void OnUseFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.UseFile(r); }
		private void OnShowFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) BasemapPickers.ShowInFolder(r.Path); }
		private void OnAskDeleteClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.AskDelete(r); }
		private void OnKeepFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.KeepFile(r); }
		private void OnDeleteFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel?.DeleteFile(r); }

		private async void OnAddFileClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickMapFileAsync(this) is { } path && ViewModel is { } vm) await vm.AddFileAsync(path);
		}

		private async void OnBrowseSaveFolderClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickFolderAsync(this) is { } folder && ViewModel is { } vm) vm.SaveFolder = folder;
		}

		/// <summary>The save folder can't move while a download is writing into it.</summary>
		public bool CanBrowse(bool isDownloading) => !isDownloading;
	}
}
