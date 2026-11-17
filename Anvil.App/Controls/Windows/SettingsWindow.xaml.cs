using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.System;
using Anvil.Controls.Primitives;
using Anvil.Controls.SettingsSearch;
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
				IsSearchShown = false; // a page opened from anywhere (the rail, the top bar's menu) ends the results page
				Body.ScrollToTop();
				RefreshPageHeader();
				UpdateRailJoin();
			});
		}

		// ── FIND A SETTING (canvas artboards 14–15, 2026-11-17) ───────────────────────────────────────────────────────────
		// The rail's box searches Services/Settings/SettingsSearch. Suggestions = the best 6 + an "Enter — all N" line; a pick
		// LANDS on the row (its page, scrolled to, selected, flashed); Enter / the footer shows the RESULTS page (every page body
		// hidden, no rail entry selected). Leaving: Esc, clearing the box, any rail click, a page opened from the top bar.

		/// <summary>The results page is showing (the page bodies, About box and Reset all hide; no rail entry is selected).</summary>
		public bool IsSearchShown
		{
			get => (bool)GetValue(IsSearchShownProperty);
			private set => SetValue(IsSearchShownProperty, value);
		}

		public static readonly DependencyProperty IsSearchShownProperty =
			DependencyProperty.Register(nameof(IsSearchShown), typeof(bool), typeof(SettingsWindow),
				new PropertyMetadata(false, (d, _) => ((SettingsWindow)d).OnSearchShownChanged()));

		/// <summary>The box's width: a rail row's full width (the strip's) + <see cref="SearchBoxExtra"/> (the user's call — sized
		/// from the NAME it read narrower than the rows' own lifts, 2026-11-17).</summary>
		public double SearchBoxWidth
		{
			get => (double)GetValue(SearchBoxWidthProperty);
			private set => SetValue(SearchBoxWidthProperty, value);
		}

		public static readonly DependencyProperty SearchBoxWidthProperty =
			DependencyProperty.Register(nameof(SearchBoxWidth), typeof(double), typeof(SettingsWindow), new PropertyMetadata(160d));

		private const double SearchBoxExtra = 10;

		private string _searchQuery = string.Empty;
		private int _searchCount;
		private int _searchPages;

		public Visibility PageOnly(bool searching) => searching ? Visibility.Collapsed : Visibility.Visible;

		public Visibility SearchOnly(bool searching) => searching ? Visibility.Visible : Visibility.Collapsed;

		private void OnStripSizeChanged(object sender, SizeChangedEventArgs e)
		{
			SearchBoxWidth = e.NewSize.Width + SearchBoxExtra;
			UpdateRailJoin();
		}

		private void OnSearchShownChanged()
		{
			Body.ScrollToTop();
			RefreshPageHeader();
			// The strip re-lights its row through its own binding — measure the join after it has.
			DispatcherQueue.TryEnqueue(UpdateRailJoin);
		}

		private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
		{
			if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) { return; }
			var text = sender.Text.Trim();
			if (text.Length == 0)
			{
				sender.ItemsSource = null;
				IsSearchShown = false;
				return;
			}

			var hits = Anvil.Services.SettingsSearch.Find(text);
			var items = new List<SettingsSearchItem>();
			items.AddRange(hits.Take(Anvil.Services.SettingsSearch.SuggestionCount).Select(SettingsSearchItem.Match));
			if (hits.Count > 0) { items.Add(SettingsSearchItem.Footer(hits.Count)); }
			sender.ItemsSource = items;

			if (IsSearchShown) { ShowResults(text); } // the results page follows the typing once it's up
		}

		private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
		{
			sender.IsSuggestionListOpen = false;
			if (args.ChosenSuggestion is SettingsSearchItem { Hit: { } hit })
			{
				Land(hit.Key);
			}
			else if (sender.Text.Trim().Length > 0)
			{
				ShowResults(sender.Text.Trim()); // Enter, or the "all N results" line
			}
		}

		private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key != VirtualKey.Escape) { return; }
			e.Handled = true;
			SettingsSearchBox.Text = string.Empty;
			SettingsSearchBox.ItemsSource = null;
			SettingsSearchBox.IsSuggestionListOpen = false;
			IsSearchShown = false;
		}

		private void OnRailRowClicked(object sender, RoutedEventArgs e) => IsSearchShown = false;

		private void OnSearchHitPicked(object? sender, string key) => Land(key);

		private void ShowResults(string query)
		{
			var hits = Anvil.Services.SettingsSearch.Find(query);
			// One plate per page, in the RAIL's order.
			var groups = Tabs
				.Select(t => new SettingsSearchGroup(t.Label,
					hits.Where(h => h.Place.Page == t.Label).Select(SettingsSearchItem.Match).ToList()))
				.Where(g => g.Items.Count > 0)
				.ToList();
			_searchQuery = query;
			_searchCount = hits.Count;
			_searchPages = groups.Count;
			SettingsSearchResultsPage.Show(query, groups);
			if (IsSearchShown) { RefreshPageHeader(); } else { IsSearchShown = true; }
		}

		// Open the row's page, then find the row there by its AboutKey, select it (the About box fills), bring it into view
		// and flash it. UpdateLayout first: the page was hidden, so its rows aren't laid out until it shows.
		private void Land(string key)
		{
			if (ViewModel is null || Anvil.Services.SettingsSearch.PlaceOf(key) is not { } place) { return; }
			var entry = Tabs.FirstOrDefault(t => t.Label == place.Page);
			if (entry is null) { return; }

			IsSearchShown = false;
			SettingsSearchBox.IsSuggestionListOpen = false;
			ViewModel.SettingsTabIndex = entry.PageIndex;
			Body.UpdateLayout();

			if (PageFor(entry.PageIndex) is DependencyObject page && FindRow(page, key) is { } row)
			{
				OnRowPicked(row);
				if (row is UIElement element)
				{
					element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = true });
				}
				FlashWhenSettled(row);
			}
		}

		// THE FLASH WAITS FOR THE SCROLL (2026-11-17: started at once, much of it played while the page was still gliding to
		// the row). It starts on the first scroll-settled with the row fully in view — or after FlashFallback if no scroll comes
		// (the row was already in view, so nothing moves).
		private static readonly TimeSpan FlashFallback = TimeSpan.FromMilliseconds(500);
		private ISettingsPickableRow? _pendingFlash;
		private Microsoft.UI.Dispatching.DispatcherQueueTimer? _flashTimer;

		private void FlashWhenSettled(ISettingsPickableRow row)
		{
			_pendingFlash = row;
			if (_flashTimer is null)
			{
				_flashTimer = DispatcherQueue.CreateTimer();
				_flashTimer.IsRepeating = false;
				_flashTimer.Tick += (_, _) => FlushFlash();
			}
			_flashTimer.Interval = FlashFallback;
			_flashTimer.Start();
		}

		private void OnBodyScrollSettled(object? sender, EventArgs e)
		{
			if (_pendingFlash is FrameworkElement row && IsFullyInBody(row)) { FlushFlash(); }
		}

		private bool IsFullyInBody(FrameworkElement row)
		{
			var box = row.TransformToVisual(Body).TransformBounds(new global::Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
			return box.Top >= 0 && box.Bottom <= Body.ActualHeight;
		}

		private void FlushFlash()
		{
			_flashTimer?.Stop();
			var row = _pendingFlash;
			_pendingFlash = null;
			row?.Flash();
		}

		private static ISettingsPickableRow? FindRow(DependencyObject root, string key)
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);
				if (child is ISettingsPickableRow row && row.AboutKey == key) { return row; }
				if (FindRow(child, key) is { } found) { return found; }
			}
			return null;
		}

		// ── THE RAIL JOIN: the body outline reaches out round the selected rail entry (PlateOutline.SetJoin) ──────────────
		// WHY CODE: the join's box is the entry's MEASURED position (computed geometry); the shapes are declared in
		// PlateOutline.xaml. Re-measured when the selection, the rail's layout, its scroll or the body's size changes. An entry
		// scrolled even partly out of the rail drops the join (it would run over the band).
		private void OnRailJoinMoved(object sender, object e) => UpdateRailJoin();

		private void UpdateRailJoin()
		{
			// Visible = inside the rail's scroller (the search box sits above it); the join's box = the outline's coordinates.
			if (Strip.SelectedRowBounds(SettingsRailScroller) is global::Windows.Foundation.Rect inRail
				&& inRail.Top >= 0 && inRail.Bottom <= SettingsRailScroller.ActualHeight
				&& Strip.SelectedRowBounds(SettingsBodyOutline) is global::Windows.Foundation.Rect row)
			{
				SettingsBodyOutline.SetJoin(row.Left, row.Top, row.Bottom);
			}
			else
			{
				SettingsBodyOutline.ClearJoin();
			}
		}

		// ── THE PAGE HEADER: title, description, and the About box for the page's SELECTED row ─────────────────────────
		// The selection is kept PER PAGE (a page you come back to still shows the row you picked there). Rows report a pick
		// through ISettingsRowHost (a click anywhere on the row, or keyboard focus moving into it).
		private readonly System.Collections.Generic.Dictionary<int, ISettingsPickableRow> _selectedRowByPage = new();

		public void OnRowPicked(ISettingsPickableRow row)
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
			if (IsSearchShown)
			{
				PageTitle = $"Results for “{_searchQuery}”";
				PageDescription = _searchCount == 0 ? "No settings match"
					: $"{_searchCount} {(_searchCount == 1 ? "setting" : "settings")} on {_searchPages} {(_searchPages == 1 ? "page" : "pages")}";
				HasResetAll = Visibility.Collapsed;
				return;
			}
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

			// The title line's Reset all names the SHOWN page and calls ITS reset (which asks first). Dev has none.
			var shown = PageFor(page);
			ResetAllText = entry is null ? string.Empty : $"Reset all {entry.Label} settings";
			ResetAllToolTip = shown?.ResetAllToolTip ?? string.Empty;
			HasResetAll = shown is null ? Visibility.Collapsed : Visibility.Visible;
		}

		/// <summary>The body for a page slot (the SettingsTab ordinal), or null for one with nothing to reset (Dev).</summary>
		private ISettingsPage? PageFor(int page) => (SettingsTab)page switch
		{
			SettingsTab.Startup => GeneralTab,
			SettingsTab.Map => AppearanceTab,
			SettingsTab.Radar => RadarTab,
			SettingsTab.RangeRing => RangeRingTab,
			SettingsTab.LiveData => LiveDataTab,
			SettingsTab.Storage => StorageTab,
			_ => null,
		};

		private void OnResetAllClick(object sender, RoutedEventArgs e)
		{
			if (ViewModel is not null) { PageFor(ViewModel.SettingsTabIndex)?.ResetAll(); }
		}

		public string ResetAllText { get => (string)GetValue(ResetAllTextProperty); private set => SetValue(ResetAllTextProperty, value); }
		public static readonly DependencyProperty ResetAllTextProperty =
			DependencyProperty.Register(nameof(ResetAllText), typeof(string), typeof(SettingsWindow), new PropertyMetadata(string.Empty));

		public string ResetAllToolTip { get => (string)GetValue(ResetAllToolTipProperty); private set => SetValue(ResetAllToolTipProperty, value); }
		public static readonly DependencyProperty ResetAllToolTipProperty =
			DependencyProperty.Register(nameof(ResetAllToolTip), typeof(string), typeof(SettingsWindow), new PropertyMetadata(string.Empty));

		public Visibility HasResetAll { get => (Visibility)GetValue(HasResetAllProperty); private set => SetValue(HasResetAllProperty, value); }
		public static readonly DependencyProperty HasResetAllProperty =
			DependencyProperty.Register(nameof(HasResetAll), typeof(Visibility), typeof(SettingsWindow), new PropertyMetadata(Visibility.Collapsed));

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

		/// <summary>Which tab body shows: visible when the strip is on <paramref name="mine"/> and no search results show.</summary>
		public Visibility TabVisibility(int selected, bool searching, int mine) =>
			selected == mine && !searching ? Visibility.Visible : Visibility.Collapsed;

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
