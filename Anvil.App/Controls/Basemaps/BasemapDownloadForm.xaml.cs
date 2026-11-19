using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Basemaps
{
	/// <summary>The offline-download options (see the XAML header) — the Map files dialog's download page.</summary>
	public sealed partial class BasemapDownloadForm : UserControl
	{
		public BasemapDownloadForm()
		{
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel
		{
			get => (BasemapSetupViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(BasemapSetupViewModel), typeof(BasemapDownloadForm), new PropertyMetadata(null));

		/// <summary>"Saves as anvil-l48-ak.pmtiles · 212.3 GB free", then the duplicate / resume note when there is one.</summary>
		public string FileLine(string fileName, string free, string note) =>
			string.IsNullOrEmpty(note) ? $"Saves as {fileName} · {free}" : $"Saves as {fileName} · {free} · {note}";

		private async void OnBrowseClick(object sender, RoutedEventArgs e)
		{
			if (await BasemapPickers.PickFolderAsync(this) is { } folder && ViewModel is { } vm) vm.SaveFolder = folder;
		}
	}
}
