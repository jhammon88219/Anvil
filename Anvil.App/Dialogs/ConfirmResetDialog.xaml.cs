using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Anvil.Dialogs
{
	/// <summary>
	/// Asks before a Settings reset (a section's Restore defaults or a page's Reset all). Shows the words it's handed;
	/// <see cref="ConfirmAsync"/> answers true only for Reset.
	/// </summary>
	public sealed partial class ConfirmResetDialog : ContentDialog
	{
		public ConfirmResetDialog(string title, string body)
		{
			InitializeComponent();
			Title = title;
			BodyText.Text = body;
		}

		/// <summary>Show it over <paramref name="anchor"/>'s window; true = Reset.</summary>
		public static async Task<bool> ConfirmAsync(UIElement anchor, string title, string body)
		{
			var dialog = new ConfirmResetDialog(title, body) { XamlRoot = anchor.XamlRoot };
			return await dialog.ShowAsync() == ContentDialogResult.Primary;
		}
	}
}
