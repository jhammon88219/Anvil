using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.LiveDataSettingsConsoles
{
	/// <summary>Settings → Live Data's Source section (see the XAML header).</summary>
	public sealed partial class LiveDataSettingsConsoleSource : UserControl
	{
		public LiveDataSettingsConsoleSource()
		{
			InitializeComponent();
		}

		public LiveDataViewModel ViewModel
		{
			get => (LiveDataViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(LiveDataViewModel), typeof(LiveDataSettingsConsoleSource),
				new PropertyMetadata(null));

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Source default?",
				"Live updates go back to the IEM live feed. Your NOAA Weather Wire account is kept."))
			{
				vm.ResetSource();
			}
		}
	}
}
