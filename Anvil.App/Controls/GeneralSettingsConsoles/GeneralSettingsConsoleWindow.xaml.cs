using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.GeneralSettingsConsoles
{
	/// <summary>Settings → General's Window section (see the XAML header): single- or multi-monitor layout.</summary>
	public sealed partial class GeneralSettingsConsoleWindow : UserControl
	{
		public GeneralSettingsConsoleWindow()
		{
			InitializeComponent();
		}

		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(GeneralSettingsConsoleWindow),
				new PropertyMetadata(null));
	}
}
