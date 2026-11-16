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
	}
}
