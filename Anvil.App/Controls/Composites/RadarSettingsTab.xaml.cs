using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The Settings window's Radar tab (see the XAML header): radar-site marker visibility for the two opt-in
	/// networks (TDWR + research) — the radar content drawn over the basemap, global rather than per-pane.
	/// Bound to the coordinator <see cref="MapViewModel"/>; the state it drives lives on
	/// <see cref="MapViewModel.Radar"/>.
	/// </summary>
	public sealed partial class RadarSettingsTab : UserControl
	{
		public RadarSettingsTab()
		{
			InitializeComponent();
		}

		// NowCast live polling: the explainer follows the mode; the interval row greys (never hides) in regime-aware mode.
		public string PollingExplainer(bool fixedTime) => fixedTime
			? "Checks on a steady timer, whatever the radar is doing."
			: "Checks for a new scan when the radar's own scan plan says one is due: right after each base sweep, SAILS " +
			  "rescans included. Quiet clear-air volumes are checked a few times, fast severe-weather modes within seconds. " +
			  "Never waits more than 2 minutes.";

		public double FixedRowOpacity(bool fixedTime) => fixedTime ? 1.0 : 0.4;

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(RadarSettingsTab), new PropertyMetadata(null));
	}
}
