using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Anvil.Dialogs;
using Anvil.Layout;
using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Windows.UI.ViewManagement;

namespace Anvil
{
	public sealed partial class MainWindow : Window, IMapView
	{
		public MapViewModel ViewModel { get; }

		/// <summary>DEV-ONLY site-sweep engine. Non-null only in Debug builds (see the ctor). Handed to the
		/// Settings window, whose Dev tab exists only in Debug.</summary>
		public SiteSweepViewModel? SweepVm { get; }

		/// <summary>DEV-ONLY velocity-dealias validation engine (fixed-corpus regression scorer). Non-null
		/// only in Debug builds. Handed to the Settings window's Debug-only Dev tab.</summary>
		public RadarValidationViewModel? ValidationVm { get; }

		/// <summary>Generic bool → Visibility for x:Bind. A Window has no Window.Resources, and x:Bind on a
		/// Window can't use {StaticResource converter}, so visibility conversions are functions here.</summary>
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// NOTE: DevVisibility is gone. It existed to collapse the dev bar key in Release; there is no dev key
		// any more — the dev tools are a tab of the Settings window, and SettingsWindow omits that tab from
		// its strip (and never constructs its body) in Release.

		// ===== Pane watermarks =====
		// The overlay grid in MainWindow.xaml has to line its cells up with the page's pane rects, so both
		// sides take the groove width from the same constant and the placement from PaneLayoutInfo.CellOf.
		// x:Bind functions rather than VM properties: this is view geometry, so it stays in the view and
		// Anvil.Core keeps no notion of grid cells.
		public GridLength PaneGutter => new(PaneLayoutInfo.GutterPx);

		public int PaneRow(PaneLayout layout, int index) => layout.CellOf(index).Row;
		public int PaneColumn(PaneLayout layout, int index) => layout.CellOf(index).Column;
		public int PaneRowSpan(PaneLayout layout, int index) => layout.CellOf(index).RowSpan;
		public int PaneColumnSpan(PaneLayout layout, int index) => layout.CellOf(index).ColumnSpan;

		/// <summary>A pane's watermark shows only while that pane is up AND a loop is actually displayed —
		/// the gate the single watermark used before multi-pane.</summary>
		public Visibility WatermarkVisibility(bool hasLoop, bool paneVisible) =>
			hasLoop && paneVisible ? Visibility.Visible : Visibility.Collapsed;

		// ===== Pane layout key =====
		// ONE key that cycles. Three drawn icons are stacked in the same cell and exactly one shows —
		// whichever layout the NEXT click lands on, so the icon advertises the action rather than the
		// state. x:Bind can't compare against an enum literal, and a Window can't use a
		// {StaticResource converter}, so the comparisons are x:Bind functions — typed, not stringly-typed,
		// and each re-evaluates because it takes the layout as its argument.
		public bool IsSinglePane(PaneLayout layout) => layout == PaneLayout.Single;
		public bool IsTwoAcross(PaneLayout layout) => layout == PaneLayout.TwoAcross;
		public bool IsQuad(PaneLayout layout) => layout == PaneLayout.Quad;

		// Which of the three stacked icons shows. Built on the comparisons above so the enum is tested in
		// exactly one place per layout.
		// ⚠️ The XAML passes Radar.NextPaneLayout here, NOT Radar.PaneLayout — these say "draw the icon for
		// this layout", and the choice of WHICH layout is the binding's. Read them with that in mind.
		public Visibility SinglePaneVisibility(PaneLayout layout) => VisibleWhen(IsSinglePane(layout));
		public Visibility DualPaneVisibility(PaneLayout layout) => VisibleWhen(IsTwoAcross(layout));
		public Visibility QuadPaneVisibility(PaneLayout layout) => VisibleWhen(IsQuad(layout));

		// The tooltip is where the CURRENT layout is named, since the icon now shows the next one. Takes
		// the current layout (not the next), and states both halves so the key is never ambiguous.
		// ⚠️ It also carries what the MARK cannot: the key is nameless now, and while the numeral says how
		// many panes the next click gives you, nothing on it explains the accent cell. One sentence here is
		// cheaper than a second visual tier on a 24px mark.
		public string PaneLayoutTooltip(PaneLayout layout) => layout switch
		{
			PaneLayout.Single => "One pane — click for two across. The accent cell is the main pane.",
			PaneLayout.TwoAcross => "Two panes — click for four. The accent cell is the main pane.",
			_ => "Four panes — click for one. The accent cell is the main pane.",
		};

		// The view knows only "advance"; the ORDER is the view model's (Radar.NextPaneLayout).
		private void OnCyclePaneLayout(object sender, RoutedEventArgs e) => ViewModel.Radar.CyclePaneLayout();

		// ===== Right-cluster key sizing =====
		// The three keys on the right (the cycling pane key + the Sites and Settings window buttons) are the
		// SAME SQUARE as the temporal keys at the bar's centre — they sit in one bar and have to read as one
		// set. Neither
		// cluster hardcodes that size: each measures the height its row was given and mirrors it onto Width,
		// through the shared Layout/BarKeyMetrics so the two halves can't drift apart.
		//
		// Code-behind rather than a binding for the same reason TemporalToggles is: ActualHeight raises no
		// property-changed notification in WinUI, so an x:Bind would size the keys once and then silently
		// stop tracking.
		//
		// ⚠️ WIDTH ONLY — never Height. The keys take their height from the vertical stretch (set in
		// BarChromeButtonStyle). A key that demanded the height it was last handed would hold the bar open
		// at that height and the bar could never shrink again: the row feeds the key, and the key would feed
		// the row right back.
		//
		// ⚠️ It cannot loop: setting Width re-fires SizeChanged (the cluster got wider), but HEIGHT is
		// unchanged on that pass, so every value is already correct and the guards make it a no-op.
		private void OnRightClusterSizeChanged(object sender, SizeChangedEventArgs e)
		{
			var side = BarKeyMetrics.SideFor(e.NewSize.Height);
			var glyph = BarKeyMetrics.LabelledGlyphFor(side);

			// The two names share ONE size — the one that fits the wider of them ("Settings") — so the
			// cluster reads as a set rather than as keys with their own type sizes. Probe measured once; the
			// labels are fixed strings. ⚠️ "Settings" has been the widest through every change to this
			// cluster (five keys, then three, then losing "Panes"), so the fitted size has never moved and
			// the centred temporal keys have never had to.
			if (_rightNameProbe <= 0)
			{
				_rightNameProbe = BarKeyMetrics.ProbeWidthOf(SitesName, SettingsName);
			}

			var nameFont = BarKeyMetrics.NameFontFor(side, _rightNameProbe);

			// ⚠️ The pane key is NOT in this loop — it is the one key wider than the square (below).
			foreach (var key in new Control[] { SitesKey, SettingsKey })
			{
				if (key.Width != side)
				{
					key.Width = side;
				}
			}

			// Glyph + name sizes are set on the elements themselves, NOT via the key's FontSize: the name
			// and the glyph want different sizes out of one key, so a single inherited FontSize can't serve
			// both. (It could when these keys were glyph-only — that is why this used to be one line.)
			foreach (var (glyphIcon, name) in new[]
			{
				(SitesGlyph, SitesName), (SettingsGlyph, SettingsName),
			})
			{
				glyphIcon.FontSize = glyph;
				name.FontSize = nameFont;
			}

			// ===== The pane key's mark =====
			// It is the one mark in the bar that is WIDE rather than square (it is a picture of the map
			// band), and the one that is NAMELESS and textless — so nothing above applies to it and every
			// number comes from the wide-mark helpers instead. All three layouts are sized even though only
			// one is visible; visibility flips as the layout cycles, and a hidden one must already be right.
			// ⚠️ This key is WIDER THAN THE SQUARE. The mark has to be large for the quad "4" to read at
			// all, and at that size a square key would crop it to its own border — so the key widens to hold
			// the mark rather than the mark shrinking to fit the key. Height still comes from the stretch,
			// like every other key; only the width is ours to set.
			var keyWidth = BarKeyMetrics.WideKeyWidthFor(side);
			if (PaneLayoutKey.Width != keyWidth)
			{
				PaneLayoutKey.Width = keyWidth;
			}

			var markWidth = BarKeyMetrics.WideIconWidthFor(side);
			var markHeight = BarKeyMetrics.WideIconHeightFor(side);
			var markGap = BarKeyMetrics.WideIconGapFor(side);

			foreach (var paneIcon in new[] { SinglePaneIcon, DualPaneIcon, QuadPaneIcon })
			{
				if (paneIcon.Width != markWidth)
				{
					paneIcon.Width = markWidth;
					paneIcon.Height = markHeight;
				}
			}

			// The grooves between cells, scaled with the mark rather than left at a fixed 2px — on a tall
			// bar a fixed gap closes up and the four quad cells read as one block.
			DualPaneIcon.ColumnSpacing = markGap;
			QuadPaneIcon.ColumnSpacing = markGap;
			QuadPaneIcon.RowSpacing = markGap;
		}

		// Widest right-cluster name at the probe size — measured once (fixed strings).
		private double _rightNameProbe;

		// Opens the site-sweep results pop-up (Save / Close). Raised by the dev window on run completion
		// or its Report button.
		private async void OnSweepReportRequested(object? sender, SweepReport report)
		{
			var dialog = new SweepReportDialog(report, WinRT.Interop.WindowNative.GetWindowHandle(this))
			{
				XamlRoot = Content.XamlRoot,
			};
			await dialog.ShowAsync();
		}

		// Opens the dealias-validation results pop-up (Save / Close). Raised by the dev window on
		// run completion or its Report button.
		private async void OnValidationReportRequested(object? sender, RadarValidationReport report)
		{
			var dialog = new ValidationReportDialog(report, WinRT.Interop.WindowNative.GetWindowHandle(this))
			{
				XamlRoot = Content.XamlRoot,
			};
			await dialog.ShowAsync();
		}

		// Routes JS→C# WebView2 messages to the view models + diagnostics (owns the map-ready latch).
		private readonly WebMessageRouter _router;

		// Drives the map (JS command strings via IMapView). Kept so the host can push chrome/theme
		// concerns to the page (e.g. the OS accent for the radar-site halo) outside the view models.
		private readonly IMapService _mapService;

		// Watches for OS accent-color / light-dark changes so the radar-site status halo re-tints live,
		// the same way the OverlayBar's accent drop-shadow does. Field so it isn't garbage-collected.
		private readonly UISettings _uiSettings = new();

		// True once the page has posted map-ready; gates the accent push (the shim + page must exist).
		private bool _webReady;

		// SPC outlook data layer (fetch + cache of severe/fire-weather GeoJSON). Kept
		// here because MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ISpcOutlookService _spcOutlookService;

		// SPC watch-box data layer (fetch + cache of active watch GeoJSON). Same reason —
		// MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ISpcWatchService _spcWatchService;

		// Storm-based warning data layer (fetch + cache of active Tornado / Severe Thunderstorm warning
		// polygons). Same reason — MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly IWarningService _warningService;

		// SPC storm-report data layer (fetch + cache of Tornado / Wind / Hail verification points). Same
		// reason — MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly IStormReportService _stormReportService;

		// Level II radar data layer (fetch + cache of .V06 volumes). Kept here because
		// MainWindow owns the WebView2 host mapping for its cache folder.
		private readonly ILevel2RadarService _radarService;

		// App settings (offline basemap folder, …). Read when mapping the "mapdata" WebView host.
		private readonly ISettingsService _settingsService;

		// Hosts each app-wide panel in its own OS window (multi-monitor). MainWindow registers each window
		// with it after construction (below).
		private readonly WindowManager _windows;

		public MainWindow(
			MapViewModel viewModel,
			MapService mapService,
			WebMessageRouter router,
			ISpcOutlookService spcOutlookService,
			ISpcWatchService spcWatchService,
			IWarningService warningService,
			IStormReportService stormReportService,
			ILevel2RadarService radarService,
			ISettingsService settingsService,
			WindowManager windows)
		{
			// The DI container (App.ConfigureServices) built the whole graph and injected it here; this
			// window is just the composition ROOT that wires the WebView-coupled bits the container can't.
			// ⚠️ ViewModel must be assigned BEFORE InitializeComponent — Maximize() below can make x:Bind
			// evaluate synchronously, and a null ViewModel then silently breaks every binding.
			ViewModel = viewModel;
			_mapService = mapService;
			_router = router;
			_spcOutlookService = spcOutlookService;
			_spcWatchService = spcWatchService;
			_warningService = warningService;
			_stormReportService = stormReportService;
			_radarService = radarService;
			_settingsService = settingsService;
			_windows = windows;

			// MapService needs THIS window as its IMapView (the seam that runs JS). The container couldn't
			// pass it via ctor without a MainWindow↔MapService cycle, so attach now that both exist — well
			// before InitializeComponent / mapReady, so no map command can run against a null view.
			mapService.Attach(this);

			// Start the dedicated radar diagnostics for this run: a per-launch JSONL event stream +
			// a derived markdown report under a package-local Diagnostics/ folder (never auto-deleted;
			// see RadarDiagnostics). This is the primary tool for chasing intermittent radar issues.
			Services.RadarDiagnostics.Init(
				System.IO.Path.Combine(_radarService.CacheDirectory, "Diagnostics"));

#if DEBUG
			// DEV-ONLY automated site sweep. Constructed only in Debug; the Settings window's Dev tab, which is
			// the only thing that reaches it, is omitted from the tab strip in Release. (The engine TYPE lives
			// in Anvil.Core and ships with it, but is never constructed here in Release.)
			SweepVm = new SiteSweepViewModel(ViewModel.Radar);

			// DEV-ONLY velocity-dealias regression harness (fixed-corpus scorer). Same Debug-only lifetime
			// as the sweep: driven through the map service (window.radarValidate) against the bundled corpus.
			ValidationVm = new RadarValidationViewModel(_mapService, new RadarCorpusProvider());
#endif

			// Push the OS theme accent to the radar-site status halo once the page is ready, and re-push
			// whenever the OS accent/theme changes — mirrors the OverlayBar's live-tinted accent shadow.
			_router.MapReady += OnMapReadyAsync;
			_uiSettings.ColorValuesChanged += OnColorValuesChanged;

			ExtendsContentIntoTitleBar = true;
			InitializeComponent();

			// App-wide windows: every panel that isn't the radar console lives in its own native OS window
			// (multi-monitor). The manager watches the coordinator VM's open flags and opens/closes a window
			// to match; content is a fresh section instance bound to this same VM, rendered headerless (the
			// window caption is the chrome). Each flag is INDEPENDENT — any combination may be open at once.
			// The radar console (Row 1, the bottom bar) is deliberately NOT here.
			_windows.Initialize(this, ViewModel);
			// ONE settings window with a tab strip — it absorbed the former App Settings, Map Controls and Dev
			// Tools windows, which is why the bar's right cluster is down to Panes / Sites / Settings.
			// ⚠️ Sized for the TALLEST tab (Map), because WindowManager sizes a window once, at open — there
			// is no per-tab resize, and adding one would fight any size the user had dragged it to.
			// The dev VMs are handed over unconditionally; they are null in Release, where SettingsWindow
			// omits the dev tab from its strip and never constructs its body.
			_windows.Register(
				id: "settings",
				isOpen: () => ViewModel.IsSettingsWindowOpen,
				close: () => ViewModel.IsSettingsWindowOpen = false,
				buildContent: () =>
				{
					var settings = new Controls.Windows.SettingsWindow
					{
						ViewModel = ViewModel,
						SweepVm = SweepVm,
						ValidationVm = ValidationVm,
					};
					// Wired per-instance (the window's content is rebuilt each time it opens) so a finished
					// dev run still pops its results dialog.
					settings.SweepReportRequested += OnSweepReportRequested;
					settings.ValidationReportRequested += OnValidationReportRequested;
					return settings;
				},
				title: "Settings", width: 520, height: 640,
				alwaysOnTop: () => ViewModel.IsSettingsWindowOnTop,
				customChrome: true);
			_windows.Register(
				id: "sites",
				isOpen: () => ViewModel.IsSiteExplorerOpen,
				close: () => ViewModel.IsSiteExplorerOpen = false,
				buildContent: () => new Controls.Windows.RadarSiteExplorerWindow { ViewModel = ViewModel },
				title: "Radar Sites", width: 660, height: 470,
				alwaysOnTop: () => ViewModel.IsSiteExplorerOnTop,
				customChrome: true);
			// ONE window for all three timeframes, tabbed — it replaced the three separate Past/Now/Fore
			// windows so that their mode keys could stop doubling as window latches (the ⚠️ history note is
			// in MapViewModel's temporal region). Its key is "Timeframe", in the right cluster above.
			// ⚠️ Sized for the TALLEST tab (PastCast), for the same reason the Settings window is: a window is
			// sized once, at open, and a per-tab resize would fight whatever size the user dragged it to.
			_windows.Register(
				id: "temporal",
				isOpen: () => ViewModel.IsTemporalWindowOpen,
				close: () => ViewModel.IsTemporalWindowOpen = false,
				buildContent: () => new Controls.Windows.TemporalWindow { ViewModel = ViewModel },
				title: "Timeframe", width: 480, height: 700,
				alwaysOnTop: () => ViewModel.IsTemporalWindowOnTop,
				customChrome: true);
			_windows.Register(
				id: "pipeline",
				isOpen: () => ViewModel.IsPipelineConsoleOpen,
				close: () => ViewModel.IsPipelineConsoleOpen = false,
				buildContent: () => new Controls.Windows.PipelineConsoleWindow { ViewModel = ViewModel },
				title: "Pipeline Console", width: 720, height: 470,
				alwaysOnTop: () => ViewModel.IsPipelineConsoleOnTop, // user-toggled via the pin in the console
				customChrome: true); // extend content into the title bar so the dark surface replaces the caption
			// (The dev tools no longer register a window of their own — they are the Settings window's
			// Debug-only Dev tab, registered above with everything else.)

			// Start maximized.
			(AppWindow.Presenter as OverlappedPresenter)?.Maximize();

			_ = InitializeMapAsync();

			// Start the SPC outlook + watch background refresh loops (each owned by its own subsystem VM):
			// the app stays usable offline from the existing cache while fresh data downloads, then
			// keeps refreshing on a timer so a long-running session doesn't sit on stale data.
			ViewModel.Outlook.StartBackgroundRefresh();
			ViewModel.Watches.StartBackgroundRefresh();
			ViewModel.Warnings.StartBackgroundRefresh();
			ViewModel.StormReports.StartBackgroundRefresh();

			// Write a final flush + report on close so the run's last events aren't lost between
			// the ~2 s background flushes.
			Closed += (_, _) => Services.RadarDiagnostics.FlushAll();
		}

		private async Task InitializeMapAsync()
		{
			// The page loads the currently-selected basemap directly (no flash to the
			// default, then re-style). The view model's default is Data Viz Black.
			var styleFile = ViewModel.SelectedStyle?.FileName ?? "style.json";
			var main = ViewModel.MainRegion;
			await InitializeWebViewAsync(MainMapWebView, BuildMapUrl(main, main?.Zoom ?? 4, styleFile, ViewModel.StateIso.IsConusIsolated,
				ViewModel.IsOnlineTilesActive, ViewModel.OnlineTilesUrl));
		}

		// Builds the page URL for the map: framed at the given center/zoom, on the given
		// basemap. The page posts 'mapReady' once its MapLibre map's 'load' fires.
		// `conus` = the launch CONUS-mask default, so the page can apply the mask on first load (before it
		// reveals) instead of waiting for the host round-trip — otherwise the world basemap flashes first.
		// `onlineTiles`/`tilesUrl` = the basemap tile source, passed the same way and for the same reason as
		// `conus`: the page builds its FIRST map on the right source instead of loading the offline basemap
		// and then re-styling onto the online one (a visible reload of every tile at launch).
		private static string BuildMapUrl(MapRegion? region, double zoom, string styleFile, bool conus,
			bool onlineTiles, string tilesUrl)
		{
			var lng = region?.Longitude ?? -95.5;
			var lat = region?.Latitude ?? 37.0;
			return "https://mapassets/map.html" +
				"?interactive=true" +
				$"&style={styleFile}" +
				$"&lng={lng.ToString(CultureInfo.InvariantCulture)}" +
				$"&lat={lat.ToString(CultureInfo.InvariantCulture)}" +
				$"&zoom={zoom.ToString(CultureInfo.InvariantCulture)}" +
				$"&conus={(conus ? "true" : "false")}" +
				$"&tiles={(onlineTiles ? "online" : "offline")}" +
				$"&tilesUrl={Uri.EscapeDataString(tilesUrl ?? "")}";
		}

		private async Task InitializeWebViewAsync(WebView2 webView, string url)
		{
			// Dark default so there's no white flash before the page (and MapLibre) paint.
			webView.DefaultBackgroundColor = Microsoft.UI.Colors.Black;
			await webView.EnsureCoreWebView2Async();

			// The curated DOW frames folder ships with the app (its README is Content), so it normally
			// exists; guard the create for the rare read-only-package case (which would otherwise throw).
			try { Directory.CreateDirectory(DowEventProvider.EventsDirectory); } catch { /* folder ships with the app */ }

			// Map each virtual host → local folder so the page can fetch everything offline, same-origin:
			//   mapassets  → bundled MapLibre style/glyphs/sprites/libraries
			//   mapdata    → the user-configured (external, ~29 GB) basemap PMTiles folder
			//   spcoutlooks/spcwatches/warnings/stormreports/radarlevel2 → the services' on-disk caches
			//   dowevents  → the bundled curated DOW (mobile-radar) frames
			// Services own their cache folders; MainWindow owns the WebView2 mappings.
			var hostFolders = new (string Host, string Folder)[]
			{
				("mapassets", Path.Combine(AppContext.BaseDirectory, "Assets", "Map")),
				("mapdata", _settingsService.MapDataFolder),
				(SpcOutlookService.CacheHostName, _spcOutlookService.CacheDirectory),
				(SpcWatchService.CacheHostName, _spcWatchService.CacheDirectory),
				(WarningService.CacheHostName, _warningService.CacheDirectory),
				(StormReportService.CacheHostName, _stormReportService.CacheDirectory),
				(Level2RadarService.CacheHostName, _radarService.CacheDirectory),
				(DowEventProvider.HostName, DowEventProvider.EventsDirectory),
			};
			foreach (var (host, folder) in hostFolders)
			{
				webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
					host, folder, CoreWebView2HostResourceAccessKind.Allow);
			}

#if DEBUG
			// DEV-ONLY: serve the fixed velocity-dealias corpus (Assets/RadarCorpus, Debug-only Content) so
			// window.radarValidate can fetch each volume same-origin. Not mapped in Release (the folder
			// isn't bundled there and the harness is unreachable).
			webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
				Services.RadarCorpusProvider.CorpusHostName, Services.RadarCorpusProvider.CorpusDirectory,
				CoreWebView2HostResourceAccessKind.Allow);
#endif

			webView.CoreWebView2.WebMessageReceived += _router.OnWebMessageReceived;
			webView.Source = new Uri(url);
		}

		/// <summary>
		/// IMapView seam: the ONLY place that touches WebView2 / ExecuteScriptAsync.
		/// Valid only once the map's CoreWebView2 has initialized.
		/// </summary>
		public async Task<string> RunScriptAsync(string javaScript)
		{
			if (MainMapWebView.CoreWebView2 is null)
			{
				return string.Empty;
			}

			return await MainMapWebView.CoreWebView2.ExecuteScriptAsync(javaScript);
		}

		// Map-ready: the page + its window.setRadarSiteAccent shim now exist, so push the accent.
		private async Task OnMapReadyAsync()
		{
			_webReady = true;
			await PushRadarSiteAccentAsync();
		}

		// OS accent/theme changed (fires on a background thread) — re-push on the UI thread so the
		// radar-site halo tracks the system accent live, like the OverlayBar's accent drop-shadow.
		private void OnColorValuesChanged(UISettings sender, object args) =>
			DispatcherQueue?.TryEnqueue(() => { if (_webReady) _ = PushRadarSiteAccentAsync(); });

		// Pushes the current theme-aware accent to the page as the "available" site-marker halo color.
		private Task PushRadarSiteAccentAsync()
		{
			var (border, glow) = RadarSiteAccentCss();
			return _mapService.SetRadarSiteAccentAsync(border, glow);
		}

		// The theme-aware accent read LIVE from UISettings (matching OverlayBar.AccentShadowColor): the
		// lightened accent variant on dark, the darkened one on light, so the ring reads on either
		// backdrop. Returns a CSS hex for the ring border + a soft rgba for its glow.
		private (string border, string glow) RadarSiteAccentCss()
		{
			var theme = (Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Dark;
			var type = theme == ElementTheme.Light ? UIColorType.AccentDark1 : UIColorType.AccentLight2;
			var c = _uiSettings.GetColorValue(type);
			var border = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
			var glow = string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},0.55)", c.R, c.G, c.B);
			return (border, glow);
		}
	}
}
