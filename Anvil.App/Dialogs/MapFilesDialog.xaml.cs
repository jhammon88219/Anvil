using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Basemaps;
using Anvil.ViewModels;

namespace Anvil.Dialogs
{
	/// <summary>
	/// Map files… (see the XAML header): the offline maps on this PC — pick, delete, show, add — and, as its second page, the
	/// download form. One dialog because WinUI can't open a ContentDialog over another.
	/// </summary>
	public sealed partial class MapFilesDialog : ContentDialog
	{
		public MapFilesDialog(BasemapSetupViewModel viewModel)
		{
			ViewModel = viewModel; // before InitializeComponent, or x:Bind silently breaks
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel { get; }

		/// <summary>Show it over <paramref name="anchor"/>'s window, on the LIST (with no file yet, the list says so and offers
		/// Download / Add). The files are re-read first.</summary>
		public static async Task ShowAsync(UIElement anchor, BasemapSetupViewModel viewModel)
		{
			viewModel.Refresh();
			var dialog = new MapFilesDialog(viewModel) { XamlRoot = anchor.XamlRoot };
			await dialog.ShowAsync();
		}

		/// <summary>Which page the body shows: the file list (false) or the download form (true).</summary>
		public bool IsDownloadPage
		{
			get => (bool)GetValue(IsDownloadPageProperty);
			set => SetValue(IsDownloadPageProperty, value);
		}

		public static readonly DependencyProperty IsDownloadPageProperty =
			DependencyProperty.Register(nameof(IsDownloadPage), typeof(bool), typeof(MapFilesDialog), new PropertyMetadata(false));

		public Visibility Shown(bool value, bool when) => value == when ? Visibility.Visible : Visibility.Collapsed;

		public bool Not(bool value) => !value;

		// The page swap: title + the primary button (it exists only on the download page — "" hides it).
		private void ShowPage(bool download)
		{
			IsDownloadPage = download;
			Title = download ? "Download an offline map" : "Map files";
			PrimaryButtonText = download ? "Download" : "";
			DefaultButton = download ? ContentDialogButton.Primary : ContentDialogButton.Close;
		}

		private void OnShowDownloadPageClick(object sender, RoutedEventArgs e) => ShowPage(true);

		private void OnBackClick(object sender, RoutedEventArgs e) => ShowPage(false);

		// Download: start the job, stay OPEN and go back to the list, where its row shows the progress.
		private void OnDownloadClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			args.Cancel = true;
			ViewModel.StartDownload();
			ShowPage(false);
		}

		// A row button's DataContext is its BasemapFileRow (the ItemsControl's item).
		private static BasemapFileRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as BasemapFileRow;

		private void OnUseFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r && !r.IsInUse) ViewModel.UseFile(r); }
		private void OnShowFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) BasemapPickers.ShowInFolder(r.Path); }
		private void OnAskDeleteClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel.AskDelete(r); }
		private void OnKeepFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel.KeepFile(r); }
		private void OnDeleteFileClick(object sender, RoutedEventArgs e) { if (RowOf(sender) is { } r) ViewModel.DeleteFile(r); }

		private async void OnAddFileClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickMapFileAsync(this) is { } path) await ViewModel.AddFileAsync(path);
		}
	}
}
