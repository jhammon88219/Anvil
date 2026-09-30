using System;
using System.Globalization;
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

		// The header clock. Ticks 4×/s but only WRITES when the shown second changes, so a timer that drifts
		// against the wall clock never skips or doubles a second. Runs only while this body is loaded.
		private readonly DispatcherTimer _clock = new() { Interval = System.TimeSpan.FromMilliseconds(250) };
		private long _shownSecond = -1;

		public NowCastTab()
		{
			InitializeComponent();
			_clock.Tick += (_, _) => UpdateClock();
			Loaded += (_, _) => { _shownSecond = -1; UpdateClock(); _clock.Start(); };
			Unloaded += (_, _) => _clock.Stop();
			// Once: Loaded fires again every time the window re-shows this body, and by then the sections
			// already ARE the order (a re-order is saved as it happens).
			Loaded += (_, _) =>
			{
				if (_orderApplied || ViewModel is null) { return; }
				_orderApplied = true;
				ViewModel.Discussions.SelectionRequested += OnDiscussionSelectionRequested;
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

		private void OnStormCellsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.StormCells.ToggleAll();

		private void OnDiscussionsHeaderClick(object sender, RoutedEventArgs e) => ViewModel.Discussions.ToggleAll();

		// A discussion clicked ON THE MAP opens its reader here — the section may be collapsed.
		private void OnDiscussionSelectionRequested(object? sender, System.EventArgs e) => DiscussionsSection.IsExpanded = true;

		// ── Header ──

		/// <summary>A tile with nothing in effect is DIMMED, never hidden — the three keep their places.</summary>
		public double TileOpacity(int count) => count == 0 ? 0.4 : 1.0;

		// A tile's tag line ⇄ its "2 of 3 · place" line, by whether its arrows have you on a warning.
		public Visibility Shown(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
		public Visibility Hidden(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

		private void UpdateClock()
		{
			var now = System.DateTimeOffset.Now;
			var second = now.ToUnixTimeSeconds();
			if (second == _shownSecond) { return; }
			_shownSecond = second;

			var culture = CultureInfo.CurrentCulture;
			ClockTime.Text = now.ToString("h:mm:ss tt", culture);
			ClockDate.Text = now.ToString("ddd MMM d, yyyy", culture);

			var updated = ViewModel?.Warnings.LastUpdated;
			var age = updated is { } when ? $"updated {Age(now - when)}" : "waiting for the first warnings update";
			ClockDetail.Text = $"{ZoneLabel(now.DateTime)} · {now.UtcDateTime:HH:mm} UTC · {age}";
		}

		// "14 s ago" / "3 min ago" / "2 h ago".
		private static string Age(System.TimeSpan span) =>
			span.TotalSeconds < 60 ? $"{System.Math.Max(0, (int)span.TotalSeconds)} s ago" :
			span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min ago" :
			$"{(int)span.TotalHours} h ago";

		// Windows has no zone ABBREVIATION, only names: "Central Daylight Time" → "CDT" by initials. A name that
		// doesn't abbreviate cleanly (non-US, localized) falls back to the UTC offset.
		private static string ZoneLabel(System.DateTime local)
		{
			var zone = TimeZoneInfo.Local;
			var name = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
			var words = name.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
			if (words.Length is >= 2 and <= 4 && words.All(w => char.IsUpper(w[0])))
			{
				return new string(words.Select(w => w[0]).ToArray());
			}
			var offset = zone.GetUtcOffset(local);
			return $"UTC{(offset < System.TimeSpan.Zero ? "−" : "+")}{offset:hh\\:mm}";
		}

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
