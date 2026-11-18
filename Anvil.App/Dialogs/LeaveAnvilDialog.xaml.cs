using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Dialogs
{
	/// <summary>
	/// Asks before a link leaves Anvil for the browser (see the XAML header). <see cref="AskAsync"/> answers whether to open,
	/// and whether "Don't ask again" was ticked with it.
	/// </summary>
	public sealed partial class LeaveAnvilDialog : ContentDialog
	{
		public LeaveAnvilDialog(string name, string url)
		{
			InitializeComponent();
			LeaveAnvilDialogName.Text = name;
			LeaveAnvilDialogAddress.Text = url;
		}

		/// <summary>Show it over <paramref name="anchor"/>'s window. (Open, DontAskAgain) — DontAskAgain only when Open.</summary>
		public static async Task<(bool Open, bool DontAskAgain)> AskAsync(UIElement anchor, string name, string url)
		{
			var dialog = new LeaveAnvilDialog(name, url) { XamlRoot = anchor.XamlRoot };
			var open = await dialog.ShowAsync() == ContentDialogResult.Primary;
			return (open, open && dialog.LeaveAnvilDialogDontAsk.IsChecked == true);
		}
	}
}
