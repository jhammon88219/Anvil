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
	public sealed partial class SettingsWindow : UserControl, ISettingsRowHost
	{
		public SettingsWindow()
		{
			Tabs = BuildTabs();
			InitializeComponent();
			// Every page lives in the ONE Body scroller (Visibility switches them), so a new page would open at the
			// last page's offset — scrolled to the bottom if you'd scrolled there. A page always opens at its top.
			// The rail's SelectedIndex is the one seam: a rail click AND MapViewModel.OpenSettings(tab) both move it.
			Strip.RegisterPropertyChangedCallback(TabStrip.SelectedIndexProperty, (_, _) =>
			{
				Body.ScrollToTop();
				RefreshPageHeader();
			});
		}

		// ── THE PAGE HEADER: title, description, and the About box for the page's SELECTED row ─────────────────────────
		// The selection is kept PER PAGE (a page you come back to still shows the row you picked there). Rows report a pick
		// through ISettingsRowHost (a click anywhere on the row, or keyboard focus moving into it).
		private readonly System.Collections.Generic.Dictionary<int, SettingsConsoleRow> _selectedRowByPage = new();

		public void OnRowPicked(SettingsConsoleRow row)
		{
			if (ViewModel is null) { return; }
			var page = ViewModel.SettingsTabIndex;
			if (_selectedRowByPage.TryGetValue(page, out var previous))
			{
				if (ReferenceEquals(previous, row)) { return; }
				previous.IsSelected = false;
			}
			_selectedRowByPage[page] = row;
			row.IsSelected = true;
			RefreshPageHeader();
		}

		private void RefreshPageHeader()
		{
			if (ViewModel is null) { return; }
			var page = ViewModel.SettingsTabIndex;
			var entry = System.Linq.Enumerable.FirstOrDefault(Tabs, t => t.PageIndex == page);
			PageTitle = entry?.Label ?? string.Empty;
			PageDescription = entry?.Tooltip ?? string.Empty;

			if (_selectedRowByPage.TryGetValue(page, out var row))
			{
				AboutCaption = ("About · " + row.AboutTitle).ToUpperInvariant();
				AboutText = row.AboutText;
				AboutDefault = row.Default;
			}
			else
			{
				AboutCaption = "ABOUT";
				AboutText = Anvil.Services.SettingsText.NothingSelected;
				AboutDefault = string.Empty;
			}
			HasAboutDefault = string.IsNullOrEmpty(AboutDefault) ? Visibility.Collapsed : Visibility.Visible;
		}

		public string PageTitle { get => (string)GetValue(PageTitleProperty); private set => SetValue(PageTitleProperty, value); }
		public static readonly DependencyProperty PageTitleProperty =
			DependencyProperty.Register(nameof(PageTitle), typeof(string), typeof(SettingsWindow), new PropertyMetadata(string.Empty));

		public string PageDescription { get => (string)GetValue(PageDescriptionProperty); private set => SetValue(PageDescriptionProperty, value); }
		public static readonly DependencyProperty PageDescriptionProperty =
			DependencyProperty.Register(nameof(PageDescription), typeof(string), typeof(SettingsWindow), new PropertyMetadata(string.Empty));

		public string AboutCaption { get => (string)GetValue(AboutCaptionProperty); private set => SetValue(AboutCaptionProperty, value); }
		public static readonly DependencyProperty AboutCaptionProperty =
			DependencyProperty.Register(nameof(AboutCaption), typeof(string), typeof(SettingsWindow), new PropertyMetadata("ABOUT"));

		public string AboutText { get => (string)GetValue(AboutTextProperty); private set => SetValue(AboutTextProperty, value); }
		public static readonly DependencyProperty AboutTextProperty =
			DependencyProperty.Register(nameof(AboutText), typeof(string), typeof(SettingsWindow),
				new PropertyMetadata(Anvil.Services.SettingsText.NothingSelected));

		public string AboutDefault { get => (string)GetValue(AboutDefaultProperty); private set => SetValue(AboutDefaultProperty, value); }
		public static readonly DependencyProperty AboutDefaultProperty =
			DependencyProperty.Register(nameof(AboutDefault), typeof(string), typeof(SettingsWindow), new PropertyMetadata(string.Empty));

		public Visibility HasAboutDefault { get => (Visibility)GetValue(HasAboutDefaultProperty); private set => SetValue(HasAboutDefaultProperty, value); }
		public static readonly DependencyProperty HasAboutDefaultProperty =
			DependencyProperty.Register(nameof(HasAboutDefault), typeof(Visibility), typeof(SettingsWindow), new PropertyMetadata(Visibility.Collapsed));

		// ── THE BAND's about-Anvil facts ──────────────────────────────────────────────────────────────
		/// <summary>The installed package's version ("1.0.0.0"), or "—" when there's no package identity.</summary>
		public string AppVersion { get; } = ReadVersion();

		/// <summary>"Debug" or "Release" — which build is running.</summary>
#if DEBUG
		public string BuildKind => "Debug";
#else
		public string BuildKind => "Release";
#endif

		private static string ReadVersion()
		{
			try
			{
				// ⚠️ global:: — inside Anvil.Controls.Windows, a bare Windows.* resolves to this namespace.
				var v = global::Windows.ApplicationModel.Package.Current.Id.Version;
				return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
			}
			catch (InvalidOperationException)
			{
				return "—"; // run without package identity
			}
		}

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

		private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			var window = (SettingsWindow)d;
			window.PersistSectionExpansion();
			window.RefreshPageHeader();
		}

		// ── Section open/closed, PERSISTED (AppSettings.SectionExpanded, keys "settings/<tab>/<header>") ──
		// The temporal panes' seam (DragMoveLayerExpander.PersistExpansion, here SettingsExpander's twin), one window prefix PER TAB: "Home site" is a
		// section in both Radar and Startup. ⚠️ The tab names below are part of the saved keys — don't rename them.
		private bool _sectionsPersisted;
		private Composites.DevSettingsTab? _persistedDevTab;

		private void PersistSectionExpansion()
		{
			if (_sectionsPersisted || ViewModel is null) return;
			_sectionsPersisted = true;
			// (map + edges: now Appearance — console sections that never collapse, so nothing to persist.)
			// (radar + console: now Radar — console sections, nothing to persist.)
			// (rangering: now Rings & Ruler — console sections, nothing to persist.)
			// (storage + basemap: now Storage; livedata; windowmode + startup + units: General — every page is console
			// sections now, so only Dev's expanders persist — DevSettingsTab does its own.)
		}

		// A tab body is a UserControl whose root panel holds its sections.
		private void Persist(UserControl tab, string name)
		{
			if (tab.Content is Panel root)
			{
				SettingsExpander.PersistExpansion(System.Linq.Enumerable.OfType<SettingsExpander>(root.Children), "settings/" + name,
					ViewModel.IsSectionExpanded, ViewModel.SetSectionExpanded);
			}
		}

		/// <summary>The site-sweep engine for the dev tab. NULL in Release, where that tab does not exist.</summary>
		public SiteSweepViewModel? SweepVm
		{
			get => (SiteSweepViewModel?)GetValue(SweepVmProperty);
			set => SetValue(SweepVmProperty, value);
		}

		public static readonly DependencyProperty SweepVmProperty =
			DependencyProperty.Register(nameof(SweepVm), typeof(SiteSweepViewModel), typeof(SettingsWindow), new PropertyMetadata(null));

		/// <summary>The dealias-validation engine for the dev tab. NULL in Release.</summary>
		public RadarValidationViewModel? ValidationVm
		{
			get => (RadarValidationViewModel?)GetValue(ValidationVmProperty);
			set => SetValue(ValidationVmProperty, value);
		}

		public static readonly DependencyProperty ValidationVmProperty =
			DependencyProperty.Register(nameof(ValidationVm), typeof(RadarValidationViewModel), typeof(SettingsWindow), new PropertyMetadata(null));

		/// <summary>DEV-ONLY load-time seeding run; null in Release (the Dev tab is never loaded there).</summary>
		public LoadTimeSeedingViewModel? SeedVm
		{
			get => (LoadTimeSeedingViewModel?)GetValue(SeedVmProperty);
			set => SetValue(SeedVmProperty, value);
		}

		public static readonly DependencyProperty SeedVmProperty =
			DependencyProperty.Register(nameof(SeedVm), typeof(LoadTimeSeedingViewModel), typeof(SettingsWindow), new PropertyMetadata(null));

		/// <summary>Raised when the dev tab asks to show a finished site-sweep report.</summary>
		public event EventHandler<SweepReport>? SweepReportRequested;

		/// <summary>Raised when the dev tab asks to show a finished dealias-validation report.</summary>
		public event EventHandler<RadarValidationReport>? ValidationReportRequested;

		// The strip's content. Glyphs are Segoe Fluent codepoints — ⚠️ verify one by RENDERING it in the real
		// font before swapping; a wrong codepoint ships as an empty box.
		//   E774 globe   — was the MAP bar key's glyph; that window became this tab, so its mark came along.
		//   EC05 tower   — the Atlas bar key's glyph, reused deliberately: this tab IS radar site markers.
		//   E81D ring    — Radar Range Ring: a dot inside a ring, a site and its range ring. VERIFIED by
		//                  rendering it in Segoe Fluent Icons (2026-09-24).
		//   EDA2 drive   — the only one NOT inherited from a shipped key. Eyeball it on first run.
		//   E7F4 monitor — Window Mode. NOT inherited from a shipped key and NOT verified. Eyeball it on first run.
		//   E768 play    — Radar Console: the console's play button. NOT verified. Eyeball it on first run.
		//   E7E8 power   — Startup: what happens when Anvil powers up. NOT verified. Eyeball it on first run.
		//   E701 wifi    — Live Data: live updates arriving over the air. NOT verified. Eyeball it on first run.
		//   E826 map     — Basemap: the map under the weather. NOT verified. Eyeball it on first run.
		//   E8EF calculator — Units: how numbers are spoken. NOT verified. Eyeball it on first run.
		//   E912 toolbox — was the Dev bar key's glyph (a second GEAR until that collided with Settings).
		// ⚠️ The LIST order is the rail's display order; PageIndex is the page (the SettingsTab ordinal = the saved index).
		// New pages go BEFORE the #if DEBUG block — Dev stays last in the rail and last in the enum.
		private static ObservableCollection<TabEntry> BuildTabs()
		{
			var tabs = new ObservableCollection<TabEntry>
			{
				// DISPLAY order (the user's six-page plan, 2026-11-16); each entry names its page by PageIndex, so the order is
				// free. Retired pages (Edges, Units, WindowMode, RadarConsole, Basemap) have NO entry — SettingsTabIndex sends their saved index on.
				new() { PageIndex = (int)SettingsTab.Startup, Glyph = "\uE7E8", Label = "General", Tooltip = "How Anvil starts and your home site, the units every readout speaks in, and the window layout" },
				new() { PageIndex = (int)SettingsTab.Map, Glyph = "\uE774", Label = "Appearance", Tooltip = "The app's theme, the basemap's colors, and how the bars and panes meet the map (the edge hatch)" },
				new() { PageIndex = (int)SettingsTab.Radar, Glyph = "\uEC05", Label = "Radar", Tooltip = "Live polling, radar memory, which radar-site keys show, and the radar console's scrubber" },
				new() { PageIndex = (int)SettingsTab.RangeRing, Glyph = "\uE81D", Label = "Rings & Ruler", Tooltip = "Which range rings are drawn, how each looks, the distance labels and the range ruler" },
				new() { PageIndex = (int)SettingsTab.LiveData, Glyph = "\uE701", Label = "Live Data", Tooltip = "Where live updates come from (IEM, NOAA Weather Wire or polling), the polling intervals and the connection" },
				new() { PageIndex = (int)SettingsTab.Storage, Glyph = "\uEDA2", Label = "Storage", Tooltip = "What Anvil keeps on disk and its limits, your map files, an offline map, and the online map" },
			};

#if DEBUG
			// DEV-ONLY. Appended here rather than declared in XAML because XAML has no preprocessor — this is
			// the one place the tab count differs between builds, and MapViewModel.SettingsTabCount mirrors it.
			tabs.Add(new TabEntry { Glyph = "", PageIndex = (int)SettingsTab.Dev, Label = "Dev", IsSeparated = true, Tooltip = "Site sweep, dealias validation and the pipeline console" });
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

		// The dev tab is x:Load'd, so it does not exist when this window is constructed — its report events
		// have to be wired the first time it actually appears. Loaded fires once per construction, and x:Load
		// keeps it constructed once true, so this does not re-subscribe on every tab switch.
		private void OnDevTabLoaded(object sender, RoutedEventArgs e)
		{
			if (sender is not Composites.DevSettingsTab tab) return;
			// x:Load'd, so it can arrive after the others — and a re-load is a NEW instance. Loaded can repeat per instance.
			if (!ReferenceEquals(_persistedDevTab, tab) && ViewModel is not null)
			{
				_persistedDevTab = tab;
				Persist(tab, "dev");
			}
			tab.SweepReportRequested += (_, report) => SweepReportRequested?.Invoke(this, report);
			tab.ValidationReportRequested += (_, report) => ValidationReportRequested?.Invoke(this, report);
		}
	}
}
