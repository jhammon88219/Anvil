using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Primitives;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The NowCast window body (see the XAML header) — watch boxes, storm-based warnings and today's storm
	/// reports, one section each. Bound to the coordinator <see cref="MapViewModel"/>.
	/// <para>No formatting helpers here: every section is a shared control that owns its own. The code is
	/// the four header select-all clicks, which hand the decision to the VM, and the layer ORDER: the saved
	/// one applied on first load, and a re-order handed back to the VM.</para>
	/// </summary>
	public sealed partial class NowCastTab : UserControl
	{
		private bool _orderApplied;

		public NowCastTab()
		{
			InitializeComponent();
			// Once: Loaded fires again every time the window re-shows this body, and by then the sections
			// already ARE the order (a re-order is saved as it happens).
			Loaded += (_, _) =>
			{
				if (_orderApplied || ViewModel is null) { return; }
				_orderApplied = true;
				PanelSection.ApplyLayerOrder(Sections, ViewModel.LayerOrderFor(TemporalMode.Now));
				PanelSection.PersistExpansion(Sections.Children.OfType<PanelSection>(), "now",
					ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
		}

		private void OnSectionsReordered(object? sender, System.EventArgs e) =>
			ViewModel.SetLayerOrder(TemporalMode.Now, PanelSection.LayerOrderOf(Sections));

		// Header select-all boxes. IsChecked is bound ONE-WAY: the CheckBox has already flipped itself by the
		// time Click fires, and the VM's answer (raised through AllShown / ShowRadarLayer) overwrites it.
		private void OnRadarHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel.Radar.ShowRadarLayer = !ViewModel.Radar.ShowRadarLayer;

		private void OnWarningsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.Warnings.ToggleAll();

		private void OnWatchesHeaderClick(object sender, RoutedEventArgs e) => ViewModel.Watches.ToggleAll();

		private void OnStormReportsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.StormReports.ToggleAll();

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(NowCastTab), new PropertyMetadata(null));
	}
}
