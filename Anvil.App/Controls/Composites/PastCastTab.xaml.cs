using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Anvil.Controls.Primitives;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The PastCast window's body (see the XAML header) — the Timeframe card + pickers, the historical SPC
	/// outlook and that day's storm reports. Bound to the coordinator <see cref="MapViewModel"/>; the
	/// timeframe drives <c>ViewModel.Radar</c> and the historical outlook drives <c>ViewModel.PastOutlook</c>.
	///
	/// ⚠️ THERE IS NO WIDGET-SYNC CODE HERE ANY MORE, and that is the point of the pickers. This class used
	/// to carry ~110 lines converting between the view model's <c>PastEventTime</c> (a TimeSpan) and an
	/// editable hour combo, an editable minute combo and an AM/PM checkbox pair — a `_syncing` re-entry
	/// guard, a push and a pull, a text parser, four handlers, and a Visibility callback that existed
	/// because an editable combo will not render a programmatically-set value until it is realized. A
	/// TimePicker binds to that TimeSpan directly, so all of it went. Everything left below is presentation
	/// for the summary card, which has no state of its own.
	/// </summary>
	public sealed partial class PastCastTab : UserControl
	{
		private bool _orderApplied;

		public PastCastTab()
		{
			InitializeComponent();
			// Once: Loaded fires again every time the window re-shows this body, and by then the sections
			// already ARE the order (a re-order is saved as it happens).
			Loaded += (_, _) =>
			{
				if (_orderApplied || ViewModel is null) { return; }
				_orderApplied = true;
				ViewModel.Discussions.SelectionRequested += OnDiscussionSelectionRequested;
				PanelSection.ApplyLayerOrder(Sections, ViewModel.LayerOrderFor(TemporalMode.Past));
				PanelSection.PersistExpansion(System.Linq.Enumerable.OfType<PanelSection>(Sections.Children), "past",
					ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			};
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(PastCastTab),
				new PropertyMetadata(null, (d, e) => ((PastCastTab)d).OnViewModelChanged(e)));

		// ===== The card's accent state =====
		// ⚠️ THIS LISTENER EXISTS ONLY BECAUSE THE CARD'S TWO ACCENT BRUSHES ARE VISUAL STATES — see the ⚠️
		// block above the state groups in the XAML for why they had to stop being x:Bind functions. Every
		// other value on this card is still a plain x:Bind; do not route more through here.
		private void OnViewModelChanged(DependencyPropertyChangedEventArgs e)
		{
			if (e.OldValue is MapViewModel old && old.Radar is RadarViewModel oldRadar)
			{
				oldRadar.PropertyChanged -= OnRadarPropertyChanged;
			}

			if (e.NewValue is MapViewModel now && now.Radar is RadarViewModel radar)
			{
				radar.PropertyChanged += OnRadarPropertyChanged;
			}

			ApplySelectionState();
		}

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			// The card's edge and footer both light on the DIRTY flag alone; the other card readouts are
			// x:Bind and need nothing from here.
			if (e.PropertyName == nameof(RadarViewModel.IsReplaySelectionDirty))
			{
				ApplySelectionState();
			}
		}

		private void ApplySelectionState() =>
			VisualStateManager.GoToState(this,
				ViewModel?.Radar?.IsReplaySelectionDirty == true ? "SelectionDirty" : "SelectionClean", false);

		// ===== The Timeframe block =====
		// The status line under Set Timeframe | Clear is the engine's own status (loading / loaded N frames /
		// errors / "pick a site…"), except while DIRTY — a played event re-filled the pickers over a loaded
		// window — when it says so. (It was the card's footer; the card is only the "or Atlas" row now.)

		/// <summary>The status line under the buttons.</summary>
		public string CardFooter(bool dirty, string status) =>
			dirty ? "Selection changed — Set Timeframe to load it" : status;

		// The header's placeholders ("No replay date" / "No Timeframe Loaded") dim until a window is loaded.
		public double PlaceholderOpacity(bool loaded) => loaded ? 1.0 : 0.4;

		// The clock's spot: the loaded window at 30 (console-clock face), the placeholder at 16.
		public double RangeFontSize(bool loaded) => loaded ? 30 : 16;

		// The pickers LOCK while a window is loaded (Clear unlocks them): disabled, and each label + picker dimmed
		// (not their grid — Set Timeframe and Clear share it and must stay full strength).
		public bool PickersEnabled(bool loaded) => !loaded;
		public double PickerOpacity(bool loaded) => loaded ? 0.4 : 1.0;

		private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel?.Radar.ClearReplay();

		// NOTE: the footer colour and the card's edge USED to be x:Bind functions here (FooterBrush /
		// CardStroke), resolving brushes from Application.Current.Resources. Both are visual states now — the
		// SelectionClean / SelectionDirty pair in the XAML — because that lookup resolves against the
		// application's theme rather than this element's. Do not bring them back.

		/// <summary>Load is accent whenever pressing it would DO something — that is, always except when a
		/// window is loaded and the pickers still agree with it.</summary>
		public Style? LoadStyle(bool loaded, bool dirty) =>
			Lookup(loaded && !dirty ? "DefaultButtonStyle" : "AccentButtonStyle") as Style;

		// ⚠️ TryGetValue, never the indexer: a ResourceDictionary's indexer THROWS on a missing key, and
		// this runs the moment the panel opens — a renamed style would take the window down rather than draw
		// the wrong one. Null is a survivable answer for a Style.
		// ⚠️ A STYLE IS SAFE TO LOOK UP THIS WAY, a Brush is not. DefaultButtonStyle / AccentButtonStyle are
		// keyed once, not per theme, and the brushes inside them are their own ThemeResources resolved
		// per-element; a colour key genuinely has one value per theme and this lookup picks the wrong one.
		/// <summary>Whether pressing Load would DO anything: nothing loaded yet, or the pickers have moved
		/// since. ⚠️ A loaded-and-clean window has nothing to re-fetch — the archive day is immutable — so the
		/// button goes dead rather than offering a no-op.</summary>
		public bool LoadEnabled(bool loaded, bool dirty) => !loaded || dirty;

		/// <summary>The outlook's Cycle and Opacity need BOTH a loaded window (there is no day to fetch for
		/// otherwise) and a product that is not None (nothing to tune).</summary>
		public bool OutlookDetailEnabled(bool loaded, bool hasOutlook) => loaded && hasOutlook;

		/// <summary>The outlook's header opacity additionally greys while the box hides it.</summary>
		public bool OutlookOpacityEnabled(bool loaded, bool shownOnMap) => loaded && shownOnMap;

		private static object? Lookup(string key) =>
			Application.Current.Resources.TryGetValue(key, out var value) ? value : null;

		// Header select-all boxes, same contract as NowCastTab's: IsChecked is bound ONE-WAY, so the box has
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

		// A discussion clicked ON THE MAP opens its reader here — the section may be collapsed.
		private void OnDiscussionSelectionRequested(object? sender, EventArgs e) => DiscussionsSection.IsExpanded = true;

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

		// The Timeframe card's Atlas button: the Anvil Atlas, on its Past events tab.
		private void OnOpenAtlasClick(object sender, RoutedEventArgs e) => ViewModel?.OpenAtlasOnEvents();

		// The layer ORDER: a drag (or Alt+Arrow) in the layer run is handed to the VM, which saves it for
		// THIS window and restacks the map. The saved order is applied once, on first load (constructor).

		private void OnSectionsReordered(object? sender, EventArgs e) =>
			ViewModel?.SetLayerOrder(TemporalMode.Past, PanelSection.LayerOrderOf(Sections));

		private async void OnLoadClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel?.Radar is not RadarViewModel radar)
			{
				return;
			}
			// On a successful load, hand focus back to the map so the user can click radar sites.
			if (await radar.LoadSelectedPastEventAsync())
			{
				FocusMap();
			}
		}

		// Return focus to the map WebView so the user can immediately interact with it. Only finds it when
		// this body shares the main window's XamlRoot; hosted in its own OS window (its own XamlRoot) the
		// lookup misses and this is a no-op, same as it was before the split.
		private void FocusMap()
		{
			if (XamlRoot?.Content is FrameworkElement root &&
				root.FindName("MainMapWebView") is Control map)
			{
				map.Focus(FocusState.Programmatic);
			}
		}

		// x:Bind helper: collapse a card line that has nothing to say, so the card closes up rather than
		// leaving a gap where the context or footer would be.
		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		// (The DOW event section moved to the Atlas's DOW events tab — Composites/DowEventsAtlasTab.)
	}
}
