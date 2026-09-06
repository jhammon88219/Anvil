using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Anvil.Controls.Primitives;
using Anvil.ViewModels;

namespace Anvil.Controls.Windows
{
	/// <summary>
	/// The app's ONE settings panel (see the XAML header), hosted in its own OS window by
	/// <see cref="WindowManager"/> and driven by <see cref="MapViewModel.IsSettingsWindowOpen"/>. Its tabs
	/// replaced three separate windows — App Settings, Map Controls and Dev Tools.
	/// </summary>
	public sealed partial class SettingsWindow : UserControl
	{
		public SettingsWindow()
		{
			Tabs = BuildTabs();
			InitializeComponent();
			// The Map tab is a plain child (not x:Load'd like the dev tab), so it exists now.
			MapTab.BrowseMapDataFolderRequested += (_, _) => BrowseMapDataFolderRequested?.Invoke(this, EventArgs.Empty);
			ApplyPlacement(); // establish the default arrangement even if a host never sets ViewModel
		}

		/// <summary>
		/// Raised when the Map tab's Browse… is clicked, relayed straight up. ⚠️ Neither this control nor the
		/// tab can show the picker: a WinRT <c>FolderPicker</c> needs a window HWND and BOTH are UserControls
		/// (this one is hosted in a window by <c>WindowManager</c>). MainWindow — a real Window — shows it,
		/// exactly as it does for the dev tab's report dialogs.
		/// </summary>
		public event EventHandler? BrowseMapDataFolderRequested;

		/// <summary>The strip's tabs, in <see cref="SettingsTab"/> order. Built once per window instance.</summary>
		public ObservableCollection<TabEntry> Tabs { get; }

		/// <summary>The coordinator view model; bound from the host.</summary>
		public MapViewModel ViewModel
		{
			get => (MapViewModel)GetValue(ViewModelProperty);
			set => SetValue(ViewModelProperty, value);
		}

		public static readonly DependencyProperty ViewModelProperty =
			DependencyProperty.Register(nameof(ViewModel), typeof(MapViewModel), typeof(SettingsWindow),
				new PropertyMetadata(null, OnViewModelChanged));

		// Placement is not an x:Bind: it drives THREE elements' Grid attachments as well as the strip's own
		// DP, so it goes through one code path (ApplyPlacement) instead of a binding plus a parallel handler.
		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var window = (SettingsWindow)d;

			if (e.OldValue is MapViewModel previous) previous.PropertyChanged -= window.OnViewModelPropertyChanged;
			if (e.NewValue is MapViewModel current) current.PropertyChanged += window.OnViewModelPropertyChanged;

			window.ApplyPlacement();
		}

		private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(MapViewModel.SettingsTabPlacement)) ApplyPlacement();
		}

		/// <summary>The site-sweep engine for the dev tab. NULL in Release, where that tab does not exist.</summary>
		public SiteSweepViewModel? SweepVm
		{
			get => (SiteSweepViewModel?)GetValue(SweepVmProperty);
			set => SetValue(SweepVmProperty, value);
		}

		public static readonly DependencyProperty SweepVmProperty =
			DependencyProperty.Register(nameof(SweepVm), typeof(SiteSweepViewModel), typeof(SettingsWindow), new PropertyMetadata(null));

		/// <summary>DEV-ONLY live basemap style tuner, handed straight to the Dev tab. Null in Release.</summary>
		public MapStyleTuningViewModel? TuneVm
		{
			get => (MapStyleTuningViewModel?)GetValue(TuneVmProperty);
			set => SetValue(TuneVmProperty, value);
		}

		public static readonly DependencyProperty TuneVmProperty =
			DependencyProperty.Register(nameof(TuneVm), typeof(MapStyleTuningViewModel), typeof(SettingsWindow), new PropertyMetadata(null));

		/// <summary>The dealias-validation engine for the dev tab. NULL in Release.</summary>
		public RadarValidationViewModel? ValidationVm
		{
			get => (RadarValidationViewModel?)GetValue(ValidationVmProperty);
			set => SetValue(ValidationVmProperty, value);
		}

		public static readonly DependencyProperty ValidationVmProperty =
			DependencyProperty.Register(nameof(ValidationVm), typeof(RadarValidationViewModel), typeof(SettingsWindow), new PropertyMetadata(null));

		/// <summary>Raised when the dev tab asks to show a finished site-sweep report.</summary>
		public event EventHandler<SweepReport>? SweepReportRequested;

		/// <summary>Raised when the dev tab asks to show a finished dealias-validation report.</summary>
		public event EventHandler<RadarValidationReport>? ValidationReportRequested;

		/// <summary>Relayed from the Dev tab's style tuner: MainWindow owns the save picker.</summary>
		public event EventHandler? ExportTunedStyleRequested;

		// The strip's content. Glyphs are Segoe Fluent codepoints — ⚠️ verify one by RENDERING it in the real
		// font before swapping; a wrong codepoint ships as an empty box.
		//   E774 globe   — was the MAP bar key's glyph; that window became this tab, so its mark came along.
		//   EC05 tower   — the Sites bar key's glyph, reused deliberately: this tab IS radar site markers.
		//   EDA2 drive   — the only one NOT inherited from a shipped key. Eyeball it on first run.
		//   E912 toolbox — was the Dev bar key's glyph (a second GEAR until that collided with Settings).
		private static ObservableCollection<TabEntry> BuildTabs()
		{
			var tabs = new ObservableCollection<TabEntry>
			{
				new() { Glyph = "", Label = "Map",     Tooltip = "Basemap, tile source, view extent and state isolation" },
				new() { Glyph = "", Label = "Radar",   Tooltip = "Which radar-site marker networks are shown" },
				new() { Glyph = "", Label = "Storage", Tooltip = "The on-disk radar cache: size, clearing and its limit" },
			};

#if DEBUG
			// DEV-ONLY. Appended here rather than declared in XAML because XAML has no preprocessor — this is
			// the one place the tab count differs between builds, and MapViewModel.SettingsTabCount mirrors it.
			tabs.Add(new TabEntry { Glyph = "", Label = "Dev", Tooltip = "Site sweep, dealias validation and the pipeline console" });
#endif

			return tabs;
		}

		/// <summary>Which tab body shows: visible when the strip is on <paramref name="mine"/>.</summary>
		public Visibility TabVisibility(int selected, int mine) =>
			selected == mine ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>
		/// Whether to CONSTRUCT the dev tab's body at all. Hard false in Release — the tab is not in the strip
		/// there and its two engine view models are null, so the body must never be built, not merely hidden.
		/// </summary>
		public bool LoadDevTab(int selected)
		{
#if DEBUG
			return selected == (int)SettingsTab.Dev;
#else
			_ = selected;
			return false;
#endif
		}

		/// <summary>Parse the persisted placement string into the strip's enum. Core stores the choice as a
		/// string precisely so it does not have to know about tab strips; this is the bridge.</summary>
		public TabPlacement PlacementOf(string? persisted) => TabPlacements.Parse(persisted);

		/// <summary>
		/// Arrange the strip, its hairline and the body for the current placement — [strip OVER body] for a
		/// top rail, [strip BESIDE body] for a side rail — and hand the placement to the strip itself.
		/// </summary>
		/// <remarks>
		/// ⚠️ This is the ONLY place the three elements' Grid.Row/Column/Span are set; the XAML deliberately
		/// pins none of them. Every value below is set on BOTH branches rather than only on the one that
		/// needs it — these are attached properties on long-lived elements, so anything left unset would
		/// keep whatever the other branch last wrote and the rail would come back half-rotated.
		/// </remarks>
		private void ApplyPlacement()
		{
			if (Strip is null) return; // called from the DP callback, which can precede InitializeComponent

			var placement = PlacementOf(ViewModel?.SettingsTabPlacement);
			Strip.Placement = placement;

			if (placement == TabPlacement.Left)
			{
				Grid.SetRow(Strip, 0); Grid.SetColumn(Strip, 0);
				Grid.SetRowSpan(Strip, 2); Grid.SetColumnSpan(Strip, 1);
				Strip.Margin = new Thickness(10, 10, 0, 10);

				Grid.SetRow(StripDivider, 0); Grid.SetColumn(StripDivider, 0);
				Grid.SetRowSpan(StripDivider, 2); Grid.SetColumnSpan(StripDivider, 1);
				StripDivider.Width = 1; StripDivider.Height = double.NaN;
				StripDivider.HorizontalAlignment = HorizontalAlignment.Right;
				StripDivider.VerticalAlignment = VerticalAlignment.Stretch;
				StripDivider.Margin = new Thickness(0, 10, 0, 10);

				Grid.SetRow(Body, 0); Grid.SetColumn(Body, 1);
				Grid.SetRowSpan(Body, 2); Grid.SetColumnSpan(Body, 1);
				Body.Margin = new Thickness(16, 14, 16, 14);
			}
			else
			{
				Grid.SetRow(Strip, 0); Grid.SetColumn(Strip, 0);
				Grid.SetRowSpan(Strip, 1); Grid.SetColumnSpan(Strip, 2);
				Strip.Margin = new Thickness(12, 10, 12, 0);

				Grid.SetRow(StripDivider, 0); Grid.SetColumn(StripDivider, 0);
				Grid.SetRowSpan(StripDivider, 1); Grid.SetColumnSpan(StripDivider, 2);
				StripDivider.Width = double.NaN; StripDivider.Height = 1;
				StripDivider.HorizontalAlignment = HorizontalAlignment.Stretch;
				StripDivider.VerticalAlignment = VerticalAlignment.Bottom;
				StripDivider.Margin = new Thickness(12, 0, 12, 0);

				Grid.SetRow(Body, 1); Grid.SetColumn(Body, 0);
				Grid.SetRowSpan(Body, 1); Grid.SetColumnSpan(Body, 2);
				Body.Margin = new Thickness(16, 14, 16, 14);
			}
		}

		// The dev tab is x:Load'd, so it does not exist when this window is constructed — its report events
		// have to be wired the first time it actually appears. Loaded fires once per construction, and x:Load
		// keeps it constructed once true, so this does not re-subscribe on every tab switch.
		private void OnDevTabLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is not Composites.DevSettingsTab tab) return;
			tab.SweepReportRequested += (_, report) => SweepReportRequested?.Invoke(this, report);
			tab.ValidationReportRequested += (_, report) => ValidationReportRequested?.Invoke(this, report);
			tab.ExportTunedStyleRequested += (_, _) => ExportTunedStyleRequested?.Invoke(this, EventArgs.Empty);
		}

		private void OnPlaceTabsTop(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) ViewModel.SettingsTabPlacement = nameof(TabPlacement.Top);
		}

		private void OnPlaceTabsLeft(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) ViewModel.SettingsTabPlacement = nameof(TabPlacement.Left);
		}
	}
}
