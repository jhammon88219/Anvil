using System;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
	public sealed partial class TemporalWindowNowCastContent : UserControl
	{
		private bool _orderApplied;

		// The header clock. Ticks 4×/s but only WRITES when the shown second changes, so a timer that drifts
		// against the wall clock never skips or doubles a second. Runs only while this body is loaded.
		private readonly DispatcherTimer _clock = new() { Interval = System.TimeSpan.FromMilliseconds(250) };
		private long _shownSecond = -1;

		public TemporalWindowNowCastContent()
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

		// A ghost row shows only while its box is on and its mode runs (MapViewModel.AreForeCastGhostsShown).
		public Visibility GhostVisibility(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

		// Header select-all boxes. IsChecked is bound ONE-WAY: the CheckBox has already flipped itself by the
		// time Click fires, and the VM's answer (raised through AllShown / ShowRadarLayer) overwrites it.
		private void OnRadarHeaderClick(object sender, RoutedEventArgs e) =>
			ViewModel.Radar.ShowRadarLayer = !ViewModel.Radar.ShowRadarLayer;

		private void OnWarningsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.Warnings.ToggleAll();

		private void OnWatchesHeaderClick(object sender, RoutedEventArgs e) => ViewModel.Watches.ToggleAll();

		private void OnStormReportsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.StormReports.ToggleAll();

		private void OnStormCellsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.StormCells.ToggleAll();

		private void OnDiscussionsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.Discussions.ToggleAll();

		private void OnTropicalHeaderClick(object sender, RoutedEventArgs e) => ViewModel.Tropical.ToggleAll();

		// ── Header ──

		/// <summary>A tile with nothing in effect is DIMMED, never hidden — the three keep their places.</summary>
		public double TileOpacity(int count) => count == 0 ? 0.4 : 1.0;

		/// <summary>A band arrow's glyph wears the BRIGHT data colour, which the button's disabled grey can't reach —
		/// so a disabled arrow dims itself.</summary>
		public double ArrowOpacity(bool enabled) => enabled ? 1.0 : 0.35;

		// A tile's tag line ⇄ its "2 of 3 · place" line, by whether its arrows have you on a warning.
		public Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
		public Visibility Hidden(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

		// The rule above a tile's state lines: only when it has warnings (a zero tile keeps its old shape).
		public Visibility NonZero(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

		private void UpdateClock()
		{
			var now = System.DateTimeOffset.Now;
			var second = now.ToUnixTimeSeconds();
			if (second == _shownSecond) { return; }
			_shownSecond = second;

			// The clock is the header's Readout control; the date and the feed status are the header's own.
			TemporalWindowHeaderNowCastReadout.Time = now;
			TemporalWindowHeader.Subtitle = now.ToString("dddd MMM d", CultureInfo.CurrentCulture);

			var updated = ViewModel?.Warnings.LastUpdated;
			TemporalWindowHeader.StatusFill = FeedBrush(updated is { } when ? now - when : null);
			TemporalWindowHeader.StatusToolTip = updated is { } at
				? $"Warnings updated {Age(now - at)}"
				: "Warnings not checked yet";
		}

		// ── Feed status bar ──
		// The warnings feed checks every 15 s, so AGING = about four misses in a row, STALE = failing for minutes.
		// ⚠️ LITERAL data colours — the same green / amber / red / grey as the Atlas's status bar + age tile and
		// the map key's square (never themed: the colour IS the status). Move the knees with the XAML header's words.
		private static readonly System.TimeSpan FeedAgingAfter = System.TimeSpan.FromMinutes(1);
		private static readonly System.TimeSpan FeedStaleAfter = System.TimeSpan.FromMinutes(5);

		private static readonly SolidColorBrush FeedCurrent = new(ColorHelper.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
		private static readonly SolidColorBrush FeedAging = new(ColorHelper.FromArgb(0xFF, 0xD2, 0x99, 0x22));
		private static readonly SolidColorBrush FeedStale = new(ColorHelper.FromArgb(0xFF, 0xF8, 0x51, 0x49));
		private static readonly SolidColorBrush FeedUnknown = new(ColorHelper.FromArgb(0xFF, 0x6E, 0x76, 0x81));

		private static SolidColorBrush FeedBrush(System.TimeSpan? age) =>
			age is not { } a ? FeedUnknown :
			a <= FeedAgingAfter ? FeedCurrent :
			a <= FeedStaleAfter ? FeedAging :
			FeedStale;

		// "14 s ago" / "3 min ago" / "2 h ago".
		private static string Age(System.TimeSpan span) =>
			span.TotalSeconds < 60 ? $"{System.Math.Max(0, (int)span.TotalSeconds)} s ago" :
			span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago" :
			$"{(int)span.TotalHours} h ago";

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalWindowNowCastContent), new PropertyMetadata(null));
	}
}
