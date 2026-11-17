using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RadarSettingsConsoles
{
	/// <summary>Settings → Radar's Live polling section (see the XAML header).</summary>
	public sealed partial class RadarSettingsConsoleLivePolling : UserControl
	{
		public RadarSettingsConsoleLivePolling()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsConsoleLivePolling),
				new PropertyMetadata(null));

		/// <summary>The mode's hint — what it checks on, in one line.</summary>
		public string ModeHint(bool fixedTime) => fixedTime ? "On a steady timer" : "On the radar's own scan plan";

		/// <summary>The interval's hint — what it is, or (greyed) what turns it on.</summary>
		public string EveryHint(bool fixedTime) => fixedTime ? "Seconds between checks for a new scan" : "Only with Fixed time";

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Live polling defaults?",
				"How the live poll checks for a new scan goes back to the DEFAULT column."))
			{
				vm.ResetLivePolling();
			}
		}
	}
}
