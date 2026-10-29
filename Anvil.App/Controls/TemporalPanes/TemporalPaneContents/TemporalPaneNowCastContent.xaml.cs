using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Controls.Primitives;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Controls.TemporalPanes.TemporalPaneContents
{
	/// <summary>
	/// The NowCast window body (see the XAML header) — watch boxes, storm-based warnings and today's storm
	/// reports, one section each. Bound to the coordinator <see cref="MapViewModel"/>.
	/// <para>No formatting helpers here: every section is a shared control that owns its own. The code is
	/// the four header select-all clicks, which hand the decision to the VM, and the layer ORDER: the saved
	/// one applied on first load, and a re-order handed back to the VM.</para>
	/// </summary>
	public sealed partial class TemporalPaneNowCastContent : UserControl
	{
		private bool _orderApplied;

		// The header clock. Ticks 4×/s but only WRITES when the shown second changes, so a timer that drifts
		// against the wall clock never skips or doubles a second. Runs only while this body is loaded.
		private readonly DispatcherTimer _clock = new() { Interval = System.TimeSpan.FromMilliseconds(250) };
		private long _shownSecond = -1;

		public TemporalPaneNowCastContent()
		{
			InitializeComponent();
			_clock.Tick += (_, _) => UpdateClock();
			Loaded += (_, _) => { _shownSecond = -1; UpdateClock(); _clock.Start(); };
			Unloaded += (_, _) => _clock.Stop();
			// The Now + Fore order is SHARED: a drag in the ForeCast window (its ghost rows) moves these too.
			Loaded += (_, _) => { if (ViewModel is not null) { ViewModel.LayerOrderChanged += OnLayerOrderChanged; } };
			Unloaded += (_, _) => { if (ViewModel is not null) { ViewModel.LayerOrderChanged -= OnLayerOrderChanged; } };
			// Once: Loaded fires again every time the window re-shows this body, and by then the sections
			// already ARE the order (a re-order is saved as it happens).
			Loaded += (_, _) =>
			{
				if (_orderApplied || ViewModel is null) { return; }
				_orderApplied = true;
				DragMoveLayerExpanderHost.ApplyLayerOrder(ViewModel.LayerOrderFor(TemporalMode.Now));
				DragMoveLayerExpanderHost.PersistExpansion("now", ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
		}

		private void OnLayersReordered(object? sender, System.EventArgs e) =>
			ViewModel.SetLayerOrder(TemporalMode.Now, DragMoveLayerExpanderHost.LayerOrder);

		// Re-apply the shared order (a no-op when this window made the change — it is already in that order).
		private void OnLayerOrderChanged(object? sender, TemporalMode mode)
		{
			if (mode == TemporalMode.Now) { DragMoveLayerExpanderHost.ApplyLayerOrder(ViewModel.LayerOrderFor(TemporalMode.Now)); }
		}

		// (A ghost row's visibility is tp:TemporalPane.GhostVisibility — its box on, its mode running, and the FULL pane only.)

		// Header select-all boxes. IsChecked is bound ONE-WAY: the CheckBox has already flipped itself by the
		// time Click fires, and the VM's answer (raised through AllShown / ShowRadarLayer) overwrites it.
		// ⚠️ Every box goes through the PANE's section toggle — the same one its rail button uses (TemporalPaneViewModel).
		private void OnRadarHeaderClick(object sender, RoutedEventArgs e) => ViewModel.NowPane.Toggle(LayerOrder.Radar);

		private void OnWarningsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.NowPane.Toggle(LayerOrder.Warnings);

		private void OnWatchesHeaderClick(object sender, RoutedEventArgs e) => ViewModel.NowPane.Toggle(LayerOrder.Watches);

		private void OnStormReportsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.NowPane.Toggle(LayerOrder.Reports);

		private void OnStormCellsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.NowPane.Toggle(LayerOrder.Cells);

		private void OnDiscussionsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.NowPane.Toggle(LayerOrder.Discussions);

		private void OnTropicalHeaderClick(object sender, RoutedEventArgs e) => ViewModel.NowPane.Toggle(LayerOrder.Tropical);

		// ── Header (the tiles are TemporalPaneNowCastWarningTile; their helpers live there) ──

		private void UpdateClock()
		{
			var now = System.DateTimeOffset.Now;
			var second = now.ToUnixTimeSeconds();
			if (second == _shownSecond) { return; }
			_shownSecond = second;

			// The clock is the header's Readout control; the date is the header's own. (The status square is bound in
			// XAML — WarningsViewModel.HeaderSquareFill.)
			TemporalPaneHeaderNowCastReadout.Time = now;
			TemporalPaneHeader.Subtitle = now.ToString("dddd MMM d", CultureInfo.CurrentCulture);
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalPaneNowCastContent), new PropertyMetadata(null));
	}
}
