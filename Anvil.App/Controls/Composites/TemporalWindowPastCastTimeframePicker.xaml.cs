using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The PastCast pickers (Date | Start Time | Window) and their buttons (Set Timeframe, Clear) — see the XAML header.
	/// ⚠️ No widget-sync code: the pickers bind the view model's values directly (their drafts live inside them).
	/// </summary>
	public sealed partial class TemporalWindowPastCastTimeframePicker : UserControl
	{
		public TemporalWindowPastCastTimeframePicker()
		{
			InitializeComponent();
			// ⚠️ LIFETIME: Radar is an app singleton (its PropertyChanged fires on every playback tick) and the PastCast
			// window builds a new Content per open — listen only while loaded, or each closed window leaks.
			Loaded += (_, _) =>
			{
				if (ViewModel?.Radar is not RadarViewModel radar) return;
				radar.PropertyChanged -= OnRadarPropertyChanged; // never twice
				radar.PropertyChanged += OnRadarPropertyChanged;
				ApplyErrorState(); // catch a change made while unloaded
			};
			Unloaded += (_, _) => { if (ViewModel?.Radar is RadarViewModel radar) radar.PropertyChanged -= OnRadarPropertyChanged; };
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalWindowPastCastTimeframePicker),
				new PropertyMetadata(null, (d, e) => ((TemporalWindowPastCastTimeframePicker)d).OnViewModelChanged(e)));

		// ===== Set Timeframe's error state =====
		// ⚠️ THIS LISTENER EXISTS ONLY BECAUSE THE ERROR'S BRUSHES ARE VISUAL STATES (theme brushes can't be resolved in
		// C#). Every other value here is a plain x:Bind; do not route more through here.
		private void OnViewModelChanged(DependencyPropertyChangedEventArgs e)
		{
			if (e.OldValue is MapViewModel old && old.Radar is RadarViewModel oldRadar)
			{
				oldRadar.PropertyChanged -= OnRadarPropertyChanged;
			}
			if (e.NewValue is MapViewModel now && now.Radar is RadarViewModel radar && IsLoaded)
			{
				radar.PropertyChanged += OnRadarPropertyChanged;
			}
			ApplyErrorState();
		}

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(RadarViewModel.HasReplayError)) { ApplyErrorState(); }
		}

		private void ApplyErrorState() =>
			VisualStateManager.GoToState(this,
				ViewModel?.Radar?.HasReplayError == true ? "ReplayErrorShown" : "NoReplayError", false);

		// ===== x:Bind functions =====

		// Set Timeframe's error face.
		public Visibility ShownWhen(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

		// Set Timeframe's THIRD face (2026-10-02): once a window is loaded the button is dead anyway, so it carries the
		// load readout ("28 frames loaded · KTLX", "Loading…", "Select a site") instead of its label. An error wins.
		public Visibility ReadoutFace(bool loaded, bool error, string count, string caption) =>
			loaded && !error && (count.Length > 0 || caption.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
		public Visibility LabelFace(bool loaded, bool error, string count, string caption) =>
			error || ReadoutFace(loaded, error, count, caption) == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

		// The error's first line ends in the spaced dash that leads into the retry hint below it.
		public string ErrorFirstLine(string error) => error + " –";

		// The pickers LOCK while a window is loaded (Clear unlocks them): disabled, and each label + picker dimmed (not
		// their grid — Set Timeframe and Clear share it and must stay full strength). ⚠️ An ERROR unlocks them even
		// over an armed window, or the button's "…or change timeframe" would be unanswerable.
		public bool PickersEnabled(bool loaded, bool error) => !loaded || error;
		public double PickerOpacity(bool loaded, bool error) => PickersEnabled(loaded, error) ? 1.0 : 0.4;

		/// <summary>Set Timeframe is accent whenever pressing it would DO something — always except when a window is
		/// loaded and the pickers still agree with it. While it carries an ERROR it is the plain style, so the error
		/// state's critical tint (ReplayErrorStates) isn't fighting the accent fill.</summary>
		public Style? LoadStyle(bool loaded, bool dirty, bool error) =>
			Lookup(error || (loaded && !dirty) ? "DefaultButtonStyle" : "AccentButtonStyle") as Style;

		/// <summary>Whether pressing Set Timeframe would DO anything: nothing loaded yet, or the pickers have moved
		/// since. ⚠️ A loaded-and-clean window has nothing to re-fetch — the archive day is immutable.</summary>
		public bool LoadEnabled(bool loaded, bool dirty, bool error) => !loaded || dirty || error; // an error = retry

		// ⚠️ TryGetValue, never the indexer: a ResourceDictionary's indexer THROWS on a missing key, and this runs the
		// moment the panel opens. ⚠️ A STYLE IS SAFE TO LOOK UP THIS WAY, a Brush is not: these styles are keyed once,
		// not per theme, and the brushes inside them are their own ThemeResources resolved per-element.
		private static object? Lookup(string key) =>
			Application.Current.Resources.TryGetValue(key, out var value) ? value : null;

		// Collapse a readout part that has nothing to say.
		public Visibility HasText(string? value) =>
			string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

		// ===== The buttons =====

		private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel?.ClearPastCastTimeframe();

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

		// Return focus to the map WebView. Only finds it when this shares the main window's XamlRoot; hosted in its own
		// OS window (its own XamlRoot) the lookup misses and this is a no-op, same as before the split.
		private void FocusMap()
		{
			if (XamlRoot?.Content is FrameworkElement root &&
				root.FindName("MainMapWebView") is Control map)
			{
				map.Focus(FocusState.Programmatic);
			}
		}
	}
}
