using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Anvil.Dialogs;
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

		/// <summary>DEV-ONLY site-sweep engine. Non-null only in Debug builds (see the ctor). The dev
		/// button + window bind to this and are collapsed in Release via <see cref="DevVisibility"/>.</summary>
		public SiteSweepViewModel? SweepVm { get; }

		/// <summary>DEV-ONLY velocity-dealias validation engine (fixed-corpus regression scorer). Non-null
		/// only in Debug builds. Its button + window bind to this and are collapsed in Release via
		/// <see cref="DevVisibility"/>.</summary>
		public RadarValidationViewModel? ValidationVm { get; }

		/// <summary>Generic bool → Visibility for x:Bind. A Window has no Window.Resources, and x:Bind on a
		/// Window can't use {StaticResource converter}, so visibility conversions are functions here.</summary>
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>Visibility of the dev-only tools (the dev button + window). Visible in Debug,
		/// Collapsed in Release, so the sweep is never reachable in a shipped build.</summary>
#if DEBUG
		public Visibility DevVisibility => Visibility.Visible;
#else
		public Visibility DevVisibility => Visibility.Collapsed;
#endif

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

		// ===== Pane layout picker =====
		// The flyout is a radio group, so each item needs its checked state derived from the one enum on
		// the view model. x:Bind can't compare against an enum literal, and a Window can't use a
		// {StaticResource converter}, so these are x:Bind functions — typed, not stringly-typed, and each
		// re-evaluates because it takes PaneLayout as its argument.
		public bool IsSinglePane(PaneLayout layout) => layout == PaneLayout.Single;
		public bool IsTwoAcross(PaneLayout layout) => layout == PaneLayout.TwoAcross;
		public bool IsQuad(PaneLayout layout) => layout == PaneLayout.Quad;

		private void OnPaneLayoutSingle(object sender, RoutedEventArgs e) => ViewModel.Radar.PaneLayout = PaneLayout.Single;
		private void OnPaneLayoutTwoAcross(object sender, RoutedEventArgs e) => ViewModel.Radar.PaneLayout = PaneLayout.TwoAcross;
		private void OnPaneLayoutQuad(object sender, RoutedEventArgs e) => ViewModel.Radar.PaneLayout = PaneLayout.Quad;

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
			// DEV-ONLY automated site sweep. Constructed only in Debug; the button + window that reach it are
			// hidden in Release via DevVisibility, so the tool is unreachable in a shipped build. (The engine
			// TYPE lives in Anvil.Core and ships with it, but is never constructed here in Release.)
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
			_windows.Register(
				id: "settings",
				isOpen: () => ViewModel.IsSettingsWindowOpen,
				close: () => ViewModel.IsSettingsWindowOpen = false,
				buildContent: () => new Controls.Windows.AppSettingsWindow { ViewModel = ViewModel },
				title: "App Settings", width: 460, height: 420,
				alwaysOnTop: () => ViewModel.IsSettingsWindowOnTop,
				customChrome: true);
			_windows.Register(
				id: "map",
				isOpen: () => ViewModel.IsMapControlsWindowOpen,
				close: () => ViewModel.IsMapControlsWindowOpen = false,
				buildContent: () => new Controls.Windows.MapControlsWindow { ViewModel = ViewModel },
				title: "Map Controls", width: 460, height: 760,
				alwaysOnTop: () => ViewModel.IsMapControlsWindowOnTop,
				customChrome: true);
			_windows.Register(
				id: "sites",
				isOpen: () => ViewModel.IsSiteExplorerOpen,
				close: () => ViewModel.IsSiteExplorerOpen = false,
				buildContent: () => new Controls.Windows.RadarSiteExplorerWindow { ViewModel = ViewModel },
				title: "Radar Sites", width: 660, height: 470,
				alwaysOnTop: () => ViewModel.IsSiteExplorerOnTop,
				customChrome: true);
			// The three temporal features each get their OWN window, so Now + Fore (which coexist as modes)
			// can be parked side by side. Past excludes the other two by mode, so it never shares the screen.
			_windows.Register(
				id: "past",
				isOpen: () => ViewModel.IsPastWindowOpen,
				close: () => ViewModel.IsPastWindowOpen = false,
				buildContent: () => new Controls.Windows.PastCastWindow { ViewModel = ViewModel },
				title: "Past Event", width: 460, height: 610,
				alwaysOnTop: () => ViewModel.IsPastWindowOnTop,
				customChrome: true);
			_windows.Register(
				id: "now",
				isOpen: () => ViewModel.IsNowWindowOpen,
				close: () => ViewModel.IsNowWindowOpen = false,
				buildContent: () => new Controls.Windows.NowCastWindow { ViewModel = ViewModel },
				title: "Live Radar", width: 460, height: 440,
				alwaysOnTop: () => ViewModel.IsNowWindowOnTop,
				customChrome: true);
			_windows.Register(
				id: "fore",
				isOpen: () => ViewModel.IsForeWindowOpen,
				close: () => ViewModel.IsForeWindowOpen = false,
				buildContent: () => new Controls.Windows.ForeCastWindow { ViewModel = ViewModel },
				title: "SPC Outlooks", width: 460, height: 340,
				alwaysOnTop: () => ViewModel.IsForeWindowOnTop,
				customChrome: true);
			_windows.Register(
				id: "pipeline",
				isOpen: () => ViewModel.IsPipelineConsoleOpen,
				close: () => ViewModel.IsPipelineConsoleOpen = false,
				buildContent: () => new Controls.Windows.PipelineConsoleWindow { ViewModel = ViewModel },
				title: "Pipeline Console", width: 720, height: 470,
				alwaysOnTop: () => ViewModel.IsPipelineConsoleOnTop, // user-toggled via the pin in the console
				customChrome: true); // extend content into the title bar so the dark surface replaces the caption
#if DEBUG
			// DEV-ONLY dev tools (site sweep + dealias validation). Registered only in Debug, where the dev
			// VMs exist; the "Dev" button that drives the flag is collapsed in Release.
			_windows.Register(
				id: "devtools",
				isOpen: () => ViewModel.IsDevToolsWindowOpen,
				close: () => ViewModel.IsDevToolsWindowOpen = false,
				buildContent: () =>
				{
					var dev = new Controls.Windows.DevToolsWindow
					{
						ViewModel = ViewModel,
						SweepVm = SweepVm,
						ValidationVm = ValidationVm,
					};
					// Wired per-instance (the window's content is rebuilt each time it opens) so a finished
					// run still pops its results dialog.
					dev.SweepReportRequested += OnSweepReportRequested;
					dev.ValidationReportRequested += OnValidationReportRequested;
					return dev;
				},
				title: "Dev Tools", width: 460, height: 560,
				alwaysOnTop: () => ViewModel.IsDevToolsWindowOnTop,
				customChrome: true);
#endif

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
			await InitializeWebViewAsync(MainMapWebView, BuildMapUrl(main, main?.Zoom ?? 4, styleFile, ViewModel.StateIso.IsConusIsolated));
		}

		// Builds the page URL for the map: framed at the given center/zoom, on the given
		// basemap. The page posts 'mapReady' once its MapLibre map's 'load' fires.
		// `conus` = the launch CONUS-mask default, so the page can apply the mask on first load (before it
		// reveals) instead of waiting for the host round-trip — otherwise the world basemap flashes first.
		private static string BuildMapUrl(MapRegion? region, double zoom, string styleFile, bool conus)
		{
			var lng = region?.Longitude ?? -95.5;
			var lat = region?.Latitude ?? 37.0;
			return "https://mapassets/map.html" +
				"?interactive=true" +
				$"&style={styleFile}" +
				$"&lng={lng.ToString(CultureInfo.InvariantCulture)}" +
				$"&lat={lat.ToString(CultureInfo.InvariantCulture)}" +
				$"&zoom={zoom.ToString(CultureInfo.InvariantCulture)}" +
				$"&conus={(conus ? "true" : "false")}";
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
