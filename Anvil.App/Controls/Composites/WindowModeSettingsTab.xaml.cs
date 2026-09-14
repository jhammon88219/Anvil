using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Window Mode tab (see the XAML header): the single- vs multi-monitor choice. Only
	/// Single is built; the state lives on <see cref="MapViewModel.MonitorMode"/>.
	/// </summary>
	public sealed partial class WindowModeSettingsTab : UserControl
	{
		public WindowModeSettingsTab()
		{
			InitializeComponent();
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(WindowModeSettingsTab), new PropertyMetadata(null));
	}
}
