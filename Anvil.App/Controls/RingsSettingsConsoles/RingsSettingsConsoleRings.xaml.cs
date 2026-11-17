using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RingsSettingsConsoles
{
	/// <summary>Settings → Rings &amp; Ruler's Rings section (see the XAML header): which rings are drawn.</summary>
	public sealed partial class RingsSettingsConsoleRings : UserControl
	{
		public RingsSettingsConsoleRings()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RingsSettingsConsoleRings),
				new PropertyMetadata(null));

		/// <summary>Spacing's hint — what Auto does, or (greyed) what turns the row on.</summary>
		public string SpacingHint(bool distanceRingsOn) => Anvil.Services.SettingsText.HintFor("rings.spacing", distanceRingsOn);

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Rings defaults?",
				"Which rings are drawn, and the distance spacing, go back to the DEFAULT column."))
			{
				vm.Radar.RangeRings.ResetWhichRings();
			}
		}
	}
}
