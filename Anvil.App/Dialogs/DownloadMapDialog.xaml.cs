using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Basemaps;
using Anvil.ViewModels;

namespace Anvil.Dialogs
{
	/// <summary>Download a map… (see the XAML header): the offline-download options, then Download starts the job and closes.</summary>
	public sealed partial class DownloadMapDialog : ContentDialog
	{
		public DownloadMapDialog(BasemapSetupViewModel viewModel)
		{
			ViewModel = viewModel; // before InitializeComponent, or x:Bind silently breaks
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel { get; }

		/// <summary>Show it over <paramref name="anchor"/>'s window.</summary>
		public static async Task ShowAsync(UIElement anchor, BasemapSetupViewModel viewModel)
		{
			var dialog = new DownloadMapDialog(viewModel) { XamlRoot = anchor.XamlRoot };
			await dialog.ShowAsync();
		}

		/// <summary>"anvil-l48-ak.pmtiles · 212.3 GB free", then the duplicate / resume note when there is one.</summary>
		public string FileLine(string fileName, string free, string note) =>
			string.IsNullOrEmpty(note) ? $"Saves as {fileName} · {free}" : $"Saves as {fileName} · {free} · {note}";

		private void OnDownloadClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) => ViewModel.StartDownload();

		private async void OnBrowseClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickFolderAsync(this) is { } folder) ViewModel.SaveFolder = folder;
		}
	}
}
