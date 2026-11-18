using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.LiveDataSettingsConsoles
{
	/// <summary>Settings → Live Data's Polling section (see the XAML header): one row per feed.</summary>
	public sealed partial class LiveDataSettingsConsolePolling : UserControl
	{
		public LiveDataSettingsConsolePolling()
		{
			InitializeComponent();
		}

		public LiveDataViewModel ViewModel
		{
			get => (LiveDataViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(LiveDataViewModel), typeof(LiveDataSettingsConsolePolling),
				new PropertyMetadata(null));

		/// <summary>One of the three declared tags / dots (static: a DataTemplate's x:Bind resolves against the ROW).</summary>
		public static Visibility Shown(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Polling defaults?",
				"Every feed's polling interval goes back to the DEFAULT column."))
			{
				vm.ResetPolling();
			}
		}
	}
}
