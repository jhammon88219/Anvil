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
		public ConfirmResetDialog(string title, string body, string action = "Reset")
		{
			InitializeComponent();
			Title = title;
			BodyText.Text = body;
			PrimaryButtonText = action;
		}

		/// <summary>Show it over <paramref name="anchor"/>'s window; true = the caution key (<paramref name="action"/>,
		/// "Reset" unless a caller names its own — Weather Wire's "Forget").</summary>
		public static async Task<bool> ConfirmAsync(UIElement anchor, string title, string body, string action = "Reset")
		{
			var dialog = new ConfirmResetDialog(title, body, action) { XamlRoot = anchor.XamlRoot };
			return await dialog.ShowAsync() == ContentDialogResult.Primary;
		}
	}
}
