using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RingsSettingsConsoles
{
	/// <summary>Settings → Rings &amp; Ruler's Distance labels section (see the XAML header).</summary>
	public sealed partial class RingsSettingsConsoleLabels : UserControl
	{
		public RingsSettingsConsoleLabels()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RingsSettingsConsoleLabels),
				new PropertyMetadata(null));

		/// <summary>The Colour hint, or why the card is greyed.</summary>
		public string LockedOr(bool distanceRingsOn, string key) => Anvil.Services.SettingsText.HintFor(key, distanceRingsOn);

		public string Pct(double value) => $"{value:0}%";
		public string Px(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + " px";
		public string Px0(double value) => $"{value:0} px";
		public string Em(double value) => value.ToString("0.00", CultureInfo.CurrentCulture) + " em";
		public string Deg(double value) => $"{value:000}°";

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Distance labels defaults?",
				"The labels' colors, text, halo, placement, position and drag knob go back to the DEFAULT column."))
			{
				vm.Radar.RangeRings.ResetLabels();
			}
		}
	}
}
