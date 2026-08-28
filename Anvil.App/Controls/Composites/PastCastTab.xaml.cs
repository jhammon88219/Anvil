using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.ViewModels;

namespace Anvil.Controls.Composites
{
	/// <summary>
	/// The PastCast tab body of the Timeframe window (see the XAML header) — the replay form, the historical
	/// SPC outlook and that day's storm reports. Bound to the coordinator <see cref="MapViewModel"/>; the
	/// replay form drives <c>ViewModel.Radar</c> and the historical outlook drives <c>ViewModel.PastOutlook</c>.
	///
	/// Date and window bind RadarViewModel indices straight from XAML; the editable hour/minute combos and
	/// the AM/PM pair convert to/from the VM's <c>PastEventTime</c> (a <see cref="TimeSpan"/>) here, so that
	/// view detail stays out of the view model.
	/// </summary>
	public sealed partial class PastCastTab : UserControl
	{
		// True while we're pushing VM state INTO the widgets, so their change events don't loop back.
		private bool _syncing;

		public PastCastTab()
		{
			InitializeComponent();
			HourCombo.ItemsSource = Enumerable.Range(1, 12).ToList();
			MinuteCombo.ItemsSource = Enumerable.Range(0, 12).Select(i => (i * 5).ToString("00")).ToList();
			Loaded += (_, _) => SyncFromViewModel();
			// An editable combo only renders a programmatically-set value once it's realized (visible), so
			// re-sync on any transition to visible rather than relying on Loaded alone.
			// ⚠️ Load-bearing now that this is a TAB body: the window keeps all three tabs loaded and switches
			// them on Visibility, so this body spends most of its life collapsed and comes back through here.
			RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
		}

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(PastCastTab),
				new PropertyMetadata(null, OnViewModelChanged));

		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var self = (PastCastTab)d;
			if (e.OldValue is MapViewModel oldVm) oldVm.Radar.PropertyChanged -= self.OnRadarPropertyChanged;
			if (e.NewValue is MapViewModel newVm) newVm.Radar.PropertyChanged += self.OnRadarPropertyChanged;
			self.SyncFromViewModel();
		}

		// Keep the time widgets in step if PastEventTime changes elsewhere (e.g. a reset).
		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (!_syncing && e.PropertyName == nameof(RadarViewModel.PastEventTime))
			{
				SyncFromViewModel();
			}
		}

		// Re-apply the VM time whenever the panel transitions to visible (see the ctor note). Deferred so
		// the editable combos are realized before we set their values.
		private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
		{
			if (Visibility == Visibility.Visible)
			{
				DispatcherQueue?.TryEnqueue(SyncFromViewModel);
			}
		}

		// VM TimeSpan -> hour / minute / AM-PM widgets. Use SelectedItem (reliable) for in-list values;
		// fall back to Text only for an off-list minute (e.g. a typed 23).
		private void SyncFromViewModel()
		{
			if (ViewModel?.Radar is not RadarViewModel radar)
			{
				return;
			}

			_syncing = true;
			TimeSpan t = radar.PastEventTime;
			bool pm = t.Hours >= 12;
			int hour12 = t.Hours % 12;
			if (hour12 == 0) hour12 = 12;

			HourCombo.SelectedItem = hour12;
			string mm = t.Minutes.ToString("00");
			if (MinuteCombo.Items.Contains(mm))
			{
				MinuteCombo.SelectedItem = mm;
			}
			else
			{
				MinuteCombo.SelectedItem = null;
				MinuteCombo.Text = mm;
			}
			AmCheck.IsChecked = !pm;
			PmCheck.IsChecked = pm;
			_syncing = false;
		}

		// hour / minute / AM-PM widgets -> VM TimeSpan.
		private void PushToViewModel()
		{
			if (_syncing || ViewModel?.Radar is not RadarViewModel radar)
			{
				return;
			}

			int hour12 = ParseClamped(HourCombo.Text, 1, 12, fallback: 12);
			int minute = ParseClamped(MinuteCombo.Text, 0, 59, fallback: 0);
			bool pm = PmCheck.IsChecked == true;
			int hour24 = (hour12 % 12) + (pm ? 12 : 0);
			radar.PastEventTime = new TimeSpan(hour24, minute, 0);
		}

		private static int ParseClamped(string? text, int min, int max, int fallback)
		{
			if (int.TryParse(text?.Trim(), out int v))
			{
				return Math.Clamp(v, min, max);
			}
			return fallback;
		}

		private void OnTimePartChanged(object sender, SelectionChangedEventArgs e) => PushToViewModel();

		private void OnTimeTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
		{
			// Accept arbitrary typed values (e.g. a minute not in the 5-step dropdown): mark handled so
			// the combo doesn't revert to a list item, push to the VM, then reflect the clamped result.
			args.Handled = true;
			PushToViewModel();
			SyncFromViewModel();
		}

		// AM/PM behave as a mutually-exclusive pair (radio-like): one is always selected.
		private void OnAmClick(object sender, RoutedEventArgs e)
		{
			AmCheck.IsChecked = true;
			PmCheck.IsChecked = false;
			PushToViewModel();
		}

		private void OnPmClick(object sender, RoutedEventArgs e)
		{
			PmCheck.IsChecked = true;
			AmCheck.IsChecked = false;
			PushToViewModel();
		}

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
		// this body shares the main window's XamlRoot; hosted in the Timeframe window (its own OS window,
		// its own XamlRoot) the lookup misses and this is a no-op, same as it was before the merge.
		private void FocusMap()
		{
			if (XamlRoot?.Content is FrameworkElement root &&
				root.FindName("MainMapWebView") is Control map)
			{
				map.Focus(FocusState.Programmatic);
			}
		}

		// x:Bind helper for the outlook's issued/valid readout.
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
	}
}
