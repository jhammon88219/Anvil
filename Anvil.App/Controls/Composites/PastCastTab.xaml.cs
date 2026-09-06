using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
		public PastCastTab()
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

		// ===== The summary card =====
		// Three states, and the card's whole job is telling them apart:
		//   nothing loaded  → "Not loaded yet",         Load is the accent action
		//   loaded, clean   → the load's own status,    Load steps back to a plain button
		//   loaded, dirty   → "Selection changed…",     Load lights again, and the card's edge with it
		// The dirty state is the one the old trailing status line could not show: that text was written by
		// the load and never revisited, so after an edit it described a window that was no longer selected.

		/// <summary>The card's footer line.</summary>
		public string CardFooter(bool loaded, bool dirty, string status) =>
			!loaded ? "Not loaded yet" :
			dirty ? "Selection changed — press Load" :
			status;

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

		private static object? Lookup(string key) =>
			Application.Current.Resources.TryGetValue(key, out var value) ? value : null;

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

		public Visibility VisibleWhen(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		public Visibility HiddenWhen(bool on) => on ? Visibility.Collapsed : Visibility.Visible;

		// ===== DOW event section =====
		// ⚠️ Import raises an event instead of showing the picker: a WinRT FileOpenPicker must be
		// initialized with a window HWND, and this is a UserControl inside another UserControl
		// (TemporalWindow) hosted in a window by WindowManager. So the request bubbles up to MainWindow,
		// the same chain the Map settings tab uses for the basemap folder.
		public event EventHandler? ImportDowEventRequested;

		private void OnImportDowClick(object sender, RoutedEventArgs e) =>
			ImportDowEventRequested?.Invoke(this, EventArgs.Empty);

		private async void OnLoadDowClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await vm.Radar.Dow.LoadDowEventAsync();
		}

		private async void OnClearDowClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await vm.Radar.Dow.ClearDowEventAsync();
		}

		private async void OnRemoveDowClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is { } vm) await vm.Radar.Dow.RemoveSelectedAsync();
		}
	}
}
