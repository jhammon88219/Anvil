using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.RingsSettingsConsoles
{
	/// <summary>Settings → Rings &amp; Ruler's Ruler section (see the XAML header): the ruler's anchor and the knob size.</summary>
	public sealed partial class RingsSettingsConsoleRuler : UserControl
	{
		public RingsSettingsConsoleRuler()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RingsSettingsConsoleRuler),
				new PropertyMetadata(null));

		public string Px0(double value) => $"{value:0} px";

		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Ruler defaults?",
				"Where the ruler starts, and the knob size, go back to the DEFAULT column."))
			{
				vm.ResetRuler();
			}
		}
	}
}
