using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Primitives;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The app's ONE timeframe panel (see the XAML header), hosted in its own OS window by
	/// <see cref="WindowManager"/> and driven by <see cref="MapViewModel.IsTemporalWindowOpen"/>. Its tabs
	/// replaced three separate windows — Past Event, Live Radar and SPC Outlooks — so that the three mode
	/// keys in the bar could go back to being plain toggles.
	/// </summary>
	public sealed partial class TemporalWindow : UserControl
	{
		public TemporalWindow()
		{
			Tabs = BuildTabs();
			InitializeComponent();
		}

		/// <summary>The strip's tabs, in <see cref="TemporalMode"/> order. Built once per window instance.</summary>
		public ObservableCollection<TabEntry> Tabs { get; }

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(TemporalWindow),
				new PropertyMetadata(null, OnViewModelChanged));

		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var window = (TemporalWindow)d;

			if (e.OldValue is MapViewModel previous) previous.PropertyChanged -= window.OnViewModelPropertyChanged;
			if (e.NewValue is MapViewModel current) current.PropertyChanged += window.OnViewModelPropertyChanged;

			window.ApplyTabAvailability();
		}

		private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(MapViewModel.IsPastTabEnabled)
				or nameof(MapViewModel.IsNowTabEnabled)
				or nameof(MapViewModel.IsForeTabEnabled))
			{
				ApplyTabAvailability();
			}
		}

		// Tab availability is not an x:Bind: a DataTemplate's x:Bind resolves against the ENTRY, so a tab
		// cannot read the view model itself (the same wall the strip's IsVertical hits). The host pushes the
		// answer onto the entry instead — which is also why TabEntry.IsEnabled is documented as the HOST's
		// property, not the strip's.
		//
		// ⚠️ Only the TAB is disabled here. Moving the SELECTION off a tab whose mode just stopped is the
		// view model's job (OnTemporalModesChanged) — doing it here too would give one rule two owners that
		// could disagree.
		private void ApplyTabAvailability()
		{
			// A tab that is off says WHY in the way that is actually useful: during a replay the reason is
			// the replay (Past excludes both of these), not "you haven't switched it on".
			bool replaying = ViewModel?.IsPastTabEnabled ?? false;
			string blockedByReplay = " is unavailable during a PastCast replay — leave PastCast to use it";

			Apply(TemporalMode.Past, ViewModel?.IsPastTabEnabled ?? false, PastTabTooltip,
				"PastCast is off — turn it on with the PastCast key in the bar to set up a replay");

			Apply(TemporalMode.Now, ViewModel?.IsNowTabEnabled ?? false, NowTabTooltip,
				replaying ? "NowCast" + blockedByReplay
					: "NowCast is off — turn it on with the NowCast key in the bar");

			Apply(TemporalMode.Fore, ViewModel?.IsForeTabEnabled ?? false, ForeTabTooltip,
				replaying ? "ForeCast" + blockedByReplay
					: "ForeCast is off — turn it on with the ForeCast key in the bar");

			void Apply(TemporalMode which, bool enabled, string onTip, string offTip)
			{
				var tab = Tabs[(int)which];
				tab.IsEnabled = enabled;
				tab.Tooltip = enabled ? onTip : offTip;
			}
		}

		private const string PastTabTooltip =
			"Historical replay: date, start, window — plus that day's outlook and storm reports";
		private const string NowTabTooltip =
			"Live conditions: watch boxes, storm-based warnings and today's storm reports";
		private const string ForeTabTooltip =
			"SPC outlooks: day, product, opacity and the legend";

		// The strip's content — deliberately the SAME glyphs and the SAME names as the three mode keys in
		// the bottom bar's centre cluster, because they are the same three things: a tab is where you
		// configure the mode that key runs. All three codepoints are already shipping on those keys, so
		// none of them needs the usual "render it before you trust it" check.
		//   E81C history — PastCast's key glyph.
		//   E93E signal  — NowCast's.
		//   E753 cloud   — ForeCast's.
		// ⚠️ ALL THREE start DISABLED here and take their real state from ApplyTabAvailability once a view
		// model lands — a tab that flashed available for one layout pass would be clickable during it. That
		// is also the honest launch state: nothing is armed, so nothing is configurable.
		private static ObservableCollection<TabEntry> BuildTabs() => new()
		{
			new() { Glyph = "", Label = "PastCast", IsEnabled = false, Tooltip = PastTabTooltip },
			new() { Glyph = "", Label = "NowCast",  IsEnabled = false, Tooltip = NowTabTooltip },
			new() { Glyph = "", Label = "ForeCast", IsEnabled = false, Tooltip = ForeTabTooltip },
		};

		/// <summary>Which tab body shows: the selected one, and only while SOMETHING is running. With every
		/// mode off there is no live tab to be on, so no body shows and the empty state takes the space.</summary>
		public Visibility TabVisibility(int selected, int mine, bool hasActiveMode) =>
			hasActiveMode && selected == mine ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>The empty state: shown only when no mode is running at all.</summary>
		public Visibility EmptyVisibility(bool hasActiveMode) =>
			hasActiveMode ? Visibility.Collapsed : Visibility.Visible;
	}
}
