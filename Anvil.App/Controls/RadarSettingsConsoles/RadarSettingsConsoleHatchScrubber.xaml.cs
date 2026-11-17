using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RadarSettingsConsoles
{
	/// <summary>Settings → Radar's Hatch scrubber section (see the XAML header): the hatch scrubber's own look.</summary>
	public sealed partial class RadarSettingsConsoleHatchScrubber : UserControl
	{
		public RadarSettingsConsoleHatchScrubber()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsConsoleHatchScrubber),
				new PropertyMetadata(null));

		/// <summary>Shown while the value is FALSE (the "only with the Hatch style" hint).</summary>
		public Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>Line color's hint — what "A" is, or (greyed) what turns the row on.</summary>
		public string ColorHint(bool isHatch) => isHatch ? "A = your Windows accent color" : "Only with the Hatch style";

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Hatch scrubber defaults?",
				"Its line color, spacing, weight and opacity go back to the DEFAULT column."))
			{
				vm.Radar.HatchLook.RestoreDefaults();
			}
		}
	}
}
