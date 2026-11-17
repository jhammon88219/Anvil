using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.AppearanceSettingsConsoles
{
	/// <summary>
	/// Settings → Appearance's Edges section (see the XAML header) — EDGE-HATCH (experimental bolt-on): the cross-hatch
	/// band where the chrome meets the map, its look, and each setting's default.
	/// </summary>
	public sealed partial class AppearanceSettingsConsoleEdges : UserControl
	{
		public AppearanceSettingsConsoleEdges()
		{
			InitializeComponent();
		}

		public EdgeHatchViewModel ViewModel
		{
			get => (EdgeHatchViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(EdgeHatchViewModel), typeof(AppearanceSettingsConsoleEdges),
				new PropertyMetadata(null));

		// Every reset asks first (docs/ui-patterns.md "Resets") — this one used to reset straight from the click.
		private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm && await Anvil.Dialogs.ConfirmResetDialog.ConfirmAsync(this, "Restore Edges defaults?",
				"Pattern, style, color, the four sliders and the panes' edges go back to the DEFAULT column."))
			{
				vm.RestoreDefaults();
			}
		}

		/// <summary>Visible when <paramref name="on"/> is false — the sliders' "Off with Black shadow" hint.</summary>
		public Visibility Hidden(bool on) => on ? Visibility.Collapsed : Visibility.Visible;
	}
}
