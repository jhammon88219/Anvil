using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Primitives;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalWindows.TemporalWindowContents
{
	/// <summary>
	/// The PastCast window's Content (see the XAML header) — the header, the decision card and the timeframe picker
	/// (each its own control: TemporalWindowHeader, TemporalWindowPastCastUserDecisionCard,
	/// TemporalWindowPastCastTimeframePicker), then the layer sections. Bound to the coordinator
	/// <see cref="MapViewModel"/>; the historical outlook drives <c>ViewModel.PastOutlook</c>.
	/// </summary>
	public sealed partial class TemporalWindowPastCastContent : UserControl
	{
		private bool _orderApplied;

		public TemporalWindowPastCastContent()
		{
			InitializeComponent();
			// Once: Loaded fires again every time the window re-shows this Content, and by then the sections
			// already ARE the order (a re-order is saved as it happens).
			Loaded += (_, _) =>
			{
				if (_orderApplied || ViewModel is null) { return; }
				_orderApplied = true;
				DragMoveLayerExpanderHost.ApplyLayerOrder(ViewModel.LayerOrderFor(TemporalMode.Past));
				DragMoveLayerExpanderHost.PersistExpansion("past", ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalWindowPastCastContent), new PropertyMetadata(null));

		// The outlook legend's fold, persisted like a section ("past/outlook-legend"); ForeCast's twin. Set on the
		// LEGEND's Loaded — it sits in a LAZY section body.
		private const string LegendFoldKey = "past/outlook-legend";

		private void OnOutlookLegendLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is OutlookLegend legend && ViewModel is not null)
			{
				legend.IsExpanded = ViewModel.IsSectionExpanded(LegendFoldKey) ?? false;
			}
		}

		private void OnOutlookLegendExpandedChanged(object? sender, bool open) => ViewModel?.SetSectionExpanded(LegendFoldKey, open);

		// The header's date placeholder ("No replay date") dims until a window is loaded (the Readout dims its own).
		public double PlaceholderOpacity(bool loaded) => loaded ? 1.0 : 0.4;

		/// <summary>The outlook's Cycle and Opacity need BOTH a loaded window (there is no day to fetch for
		/// otherwise) and a product that is not None (nothing to tune).</summary>
		public bool OutlookDetailEnabled(bool loaded, bool hasOutlook) => loaded && hasOutlook;

		/// <summary>The outlook's header opacity additionally greys while the box hides it.</summary>
		public bool OutlookOpacityEnabled(bool loaded, bool shownOnMap) => loaded && shownOnMap;

		// Header select-all boxes, same contract as TemporalWindowNowCastContent's: IsChecked is bound ONE-WAY, so the box has
		// already flipped itself by the time Click fires and the VM's answer overwrites it. Every layer
		// section has one (the outlook's is show/hide, disabled while Product is None).
		private void OnRadarHeaderClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.Radar is RadarViewModel radar)
			{
				radar.ShowRadarLayer = !radar.ShowRadarLayer;
			}
		}

		private void OnStormReportsHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.StormReports.ToggleAll();

		private void OnStormCellsHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.StormCells.ToggleAll();

		private void OnDiscussionsHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.Discussions.ToggleAll();

		private void OnTropicalHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.Tropical.ToggleAll();

		private void OnPastWarningsHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.PastAlerts.Warnings.ToggleAll();

		private void OnPastWatchesHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.PastAlerts.Watches.ToggleAll();

		private void OnDamageSurveysHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.DamageSurveys.ToggleAll();

		private void OnOutlookHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel?.PastOutlook.ToggleShown();

		// "Discussion." on the outlook's Product row — the Outlook Discussion window (shared with ForeCast's).
		private void OnDiscussionClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) { ViewModel.IsOutlookDiscussionOpen = true; }
		}

		// x:Bind helper for the damage-survey rows' counts.
		public string Count(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

		// The layer ORDER: a drag (or Alt+Arrow) in the layer run is handed to the VM, which saves it for
		// THIS window and restacks the map. The saved order is applied once, on first load (constructor).
		private void OnLayersReordered(object? sender, EventArgs e) =>
			ViewModel?.SetLayerOrder(TemporalMode.Past, DragMoveLayerExpanderHost.LayerOrder);

		// (The DOW event section moved to the Atlas's DOW events tab — Composites/DowEventsAtlasTab.)
	}
}
