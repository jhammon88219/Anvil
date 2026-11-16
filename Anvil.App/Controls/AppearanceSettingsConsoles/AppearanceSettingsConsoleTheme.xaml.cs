using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.AppearanceSettingsConsoles
{
	/// <summary>Settings → Appearance's Theme section (see the XAML header): the theme and the basemap under it.</summary>
	public sealed partial class AppearanceSettingsConsoleTheme : UserControl
	{
		public AppearanceSettingsConsoleTheme()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(AppearanceSettingsConsoleTheme),
				new PropertyMetadata(null));
	}
}
