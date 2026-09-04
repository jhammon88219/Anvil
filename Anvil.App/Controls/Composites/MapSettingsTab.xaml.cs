using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Map tab (see the XAML header): basemap style + tile source, view extent, and
	/// state isolation. Bound to the coordinator <see cref="MapViewModel"/> — the basemap lives on it,
	/// isolation on <see cref="MapViewModel.StateIso"/>.
	/// </summary>
	public sealed partial class MapSettingsTab : UserControl
	{
		public MapSettingsTab()
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
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(MapSettingsTab), new PropertyMetadata(null));

		// ⚠️ The Fit-to-view and Reset-north handlers lived here and are GONE: both moved to
		// Composites/MapControlsStrip, which calls the same MapViewModel methods. This tab is now
		// binding-only — nothing left needs code.
	}
}
