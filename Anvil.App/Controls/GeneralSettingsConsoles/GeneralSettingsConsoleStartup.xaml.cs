using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.GeneralSettingsConsoles
{
	/// <summary>Settings → General's Startup section (see the XAML header): what Anvil opens on, and the home site.</summary>
	public sealed partial class GeneralSettingsConsoleStartup : UserControl
	{
		public GeneralSettingsConsoleStartup()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(GeneralSettingsConsoleStartup),
				new PropertyMetadata(null));
	}
}
