using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Controls.AboutSettingsConsoles
{
	/// <summary>Settings → About → License (see the XAML header): the license, the source, the issue list.</summary>
	public sealed partial class AboutSettingsConsoleLicense : UserControl
	{
		public AboutSettingsConsoleLicense()
		{
			InitializeComponent();
		}

		private void OnLicenseClick(object sender, RoutedEventArgs e) => AboutAnvil.OpenLink(this, "Anvil's license (AGPL-3.0)", AboutAnvil.LicenseUrl);

		private void OnSourceClick(object sender, RoutedEventArgs e) => AboutAnvil.OpenLink(this, "Anvil's source code", AboutAnvil.SourceUrl);

		private void OnIssuesClick(object sender, RoutedEventArgs e) => AboutAnvil.OpenLink(this, "Anvil's issue list", AboutAnvil.IssuesUrl);
	}
}
