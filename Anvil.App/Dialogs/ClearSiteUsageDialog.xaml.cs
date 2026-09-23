using Microsoft.UI.Xaml.Controls;

namespace Anvil.Dialogs
{
	/// <summary>
	/// Confirms clearing the Radar Atlas's "Your use" history — one site, or (checkbox) every site. Shows the
	/// words it's handed; the caller acts on <see cref="ContentDialogResult.Primary"/> + <see cref="AllSites"/>.
	/// </summary>
	public sealed partial class ClearSiteUsageDialog : ContentDialog
	{
		public ClearSiteUsageDialog(string title, string body, string allSitesLabel)
		{
			InitializeComponent();
			Title = title;
			BodyText.Text = body;
			AllSitesBox.Content = allSitesLabel;
		}

		/// <summary>Whether "clear all sites instead" was ticked.</summary>
		public bool AllSites => AllSitesBox.IsChecked == true;
	}
}
