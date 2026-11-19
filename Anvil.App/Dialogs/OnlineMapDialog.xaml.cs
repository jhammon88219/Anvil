using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Dialogs
{
	/// <summary>Set up the online map (see the XAML header): paste + test the map key, then switch — or stay open if it fails.</summary>
	public sealed partial class OnlineMapDialog : ContentDialog
	{
		public OnlineMapDialog(BasemapSetupViewModel viewModel)
		{
			ViewModel = viewModel; // before InitializeComponent, or x:Bind silently breaks
			InitializeComponent();
		}

		public BasemapSetupViewModel ViewModel { get; }

		/// <summary>Show it over <paramref name="anchor"/>'s window.</summary>
		public static async Task ShowAsync(UIElement anchor, BasemapSetupViewModel viewModel)
		{
			var dialog = new OnlineMapDialog(viewModel) { XamlRoot = anchor.XamlRoot };
			await dialog.ShowAsync();
		}

		// Test, save and switch; a key that fails keeps the dialog open with the reason in KeyMessage.
		private async void OnUseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
		{
			var deferral = args.GetDeferral();
			try
			{
				if (!await ViewModel.UseOnlineMapAsync()) args.Cancel = true;
			}
			finally
			{
				deferral.Complete();
			}
		}
	}
}
