using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.GeneralSettingsConsoles
{
	/// <summary>
	/// Settings → General's Units section (see the XAML header): clock, time zone, distance, speed, height and hail
	/// pickers over <see cref="MapViewModel"/>'s unit properties, plus three presets.
	/// </summary>
	public sealed partial class GeneralSettingsConsoleUnits : UserControl
	{
		public GeneralSettingsConsoleUnits()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(GeneralSettingsConsoleUnits),
				new PropertyMetadata(null));

		private void OnPresetUs(object sender, RoutedEventArgs e) => ViewModel?.ApplyUnitPreset("us");

		private void OnPresetMetric(object sender, RoutedEventArgs e) => ViewModel?.ApplyUnitPreset("metric");

		private void OnPresetForecaster(object sender, RoutedEventArgs e) => ViewModel?.ApplyUnitPreset("forecaster");
	}
}
