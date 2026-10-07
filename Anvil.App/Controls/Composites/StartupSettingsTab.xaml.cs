using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Startup tab (see the XAML header): resume the last session or one fixed start, and the
	/// home-site launch. The state lives on <see cref="MapViewModel"/> (StartupResume / StartupModeIndex /
	/// StartupOpenWindow) and <see cref="MapViewModel.SiteFavorites"/>.
	/// </summary>
	public sealed partial class StartupSettingsTab : UserControl
	{
		public StartupSettingsTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(StartupSettingsTab), new PropertyMetadata(null));
	}
}
