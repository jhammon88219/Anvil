using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.StorageManagers;
using Anvil.Services;

namespace Anvil.Controls.AboutSettingsConsoles
{
	/// <summary>Settings → About → Your copy (see the XAML header): version facts, Copy details, the logs folder.</summary>
	public sealed partial class AboutSettingsConsoleYourCopy : UserControl
	{
		public AboutSettingsConsoleYourCopy()
		{
			InitializeComponent();
		}

		public string VersionText => $"{AboutAnvil.Version} · {AboutAnvil.BuildKind}";

		public string WindowsAppSdk => AboutAnvil.WindowsAppSdk;

		public string MapEngine => $"WebView2 {AboutAnvil.WebView2} · MapLibre GL JS {AboutAnvil.MapLibreVersion}";

		public string LogsPath => StorageService.LogsDirectory;

		private void OnCopyClick(object sender, RoutedEventArgs e) => AboutAnvil.CopyDetails();

		private void OnOpenLogsClick(object sender, RoutedEventArgs e) => StorageManagerFolderLauncher.Open(StorageService.LogsDirectory);
	}
}
