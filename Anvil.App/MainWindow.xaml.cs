using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
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
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics;
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

		// ===== Pane notches =====
		// The overlay grid in MainWindow.xaml has to line its cells up with the page's pane rects, so both
		// sides take the groove width from the same constant and the placement from PaneLayoutInfo.CellOf.
		// x:Bind functions rather than VM properties: this is view geometry, so it stays in the view and
		// Anvil.Core keeps no notion of grid cells.
		//
		// These four placement helpers are UNCHANGED from when this grid held the pane watermarks - the
		// notch sits in exactly the same cell the watermark did, just aligned to its top centre instead of
		// its top left.
		public GridLength PaneGutter => new(PaneLayoutInfo.GutterPx);

		public int PaneRow(PaneLayout layout, int index) => layout.CellOf(index).Row;
		public int PaneColumn(PaneLayout layout, int index) => layout.CellOf(index).Column;
		public int PaneRowSpan(PaneLayout layout, int index) => layout.CellOf(index).RowSpan;
		public int PaneColumnSpan(PaneLayout layout, int index) => layout.CellOf(index).ColumnSpan;

		/// <summary>
		/// A pane's notch shows whenever that pane is up — from launch, with no loop and no site.
		///
		/// <para>⚠️ It deliberately does NOT wait for a loop. The notch is the pane's identity, so a map
		/// with no notch reads as a map with no controls rather than as one waiting for a site; appearing
		/// only once a loop lands also made it pop in over the map with no warning. It sits DORMANT
		/// instead — dimmed, with its controls disabled (see PaneNotchContent.IsDormant) — which says
		/// "this is yours once you pick a site" in a way an absent control cannot.</para>
		/// </summary>
		public Visibility NotchVisibility(bool paneVisible) =>
			paneVisible ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>
		/// Whether the notches take their tighter form (no scale numbers under the ramp, shorter ramp).
		/// Quad only: a quad pane is half the window wide, and the full notch would take a real bite out of
		/// it. Two-across panes are still wide enough for the numbers.
		/// </summary>
		public bool NotchCompact(PaneLayout layout) => layout == PaneLayout.Quad;

		// ===== Pane notches vs the TITLE BAR =====
		// ⚠️ THE TOP-ROW NOTCHES SIT INSIDE THE WINDOW'S CAPTION. This window sets
		// ExtendsContentIntoTitleBar = true (so the map runs edge to edge) and never calls SetTitleBar, so
		// WinUI applies a DEFAULT drag region across the whole top of the window. That band is NON-CLIENT:
		// the system takes its input for dragging and double-click-to-maximize, and XAML controls inside it
		// are only partly reachable. It is why a notch in the top band behaved as if its dropdown were
		// dead - the flyout opened (IsOpen true) but the clicks that should have driven it were being
		// swallowed by the caption - while the BOTTOM-row notches in a quad, which sit below the band,
		// worked perfectly. That asymmetry is what identified it.
		//
		// The fix is to punch interactive holes in the caption: every visible notch is registered as a
		// PASSTHROUGH non-client region, which hands its rect back to XAML. Everything around the notches
		// stays draggable, so the window still behaves like a window.
		//
		// ⚠️ Rects are PHYSICAL pixels relative to the window, so they are scaled by RasterizationScale -
		// pass logical units and the holes land in the wrong place on any display that is not 100%.
		private RectInt32[] _notchRegions = Array.Empty<RectInt32>();

		// ⚠️ SET ON Closed, AND EVERY NOTCH-REGION PATH MUST CHECK IT. A closing window still runs one or
		// more layout passes as its content tears down, so LayoutUpdated fires AFTER the native window is
		// gone - and every member this method touches (Content, AppWindow) is a projection over that dead
		// object, so touching one throws COMException "The WinUI Desktop Window object has already been
		// closed". Unsubscribing in Closed is not enough on its own: a pass already queued still lands.
		// (It never reproduced under VS Stop, which kills the process instead of closing the window.)
		private bool _isClosed;

		// LayoutUpdated rather than SizeChanged: a notch also MOVES without resizing (the window resizes,
		// the pane layout changes, a notch is hidden). It fires often, so the work is four transforms and an
		// equality check, and SetRegionRects is only called when the rects actually change.
		private void OnPaneNotchLayerLayoutUpdated(object? sender, object e) => UpdateNotchInputRegions();

		private void UpdateNotchInputRegions()
		{
			if (_isClosed)
			{
				return;
			}

			if (Content?.XamlRoot is not { } root)
			{
				return;
			}

			var scale = root.RasterizationScale;

			// Keep clear of the caption buttons: a passthrough rect over them would break close/maximize.
			// The insets are already physical pixels.
			var captionLeft = AppWindow.TitleBar.LeftInset;
			var captionRight = AppWindow.Size.Width - AppWindow.TitleBar.RightInset;

			var rects = new List<RectInt32>(PaneLayoutInfo.MaxPanes);
			foreach (var notch in new FrameworkElement[] { Notch0, Notch1, Notch2, Notch3 })
			{
				if (notch.Visibility != Visibility.Visible || notch.ActualWidth <= 0 || notch.ActualHeight <= 0)
				{
					continue;
				}

				var origin = notch.TransformToVisual(null).TransformPoint(new Point(0, 0));
				var left = (int)Math.Floor(origin.X * scale);
				var right = (int)Math.Ceiling((origin.X + notch.ActualWidth) * scale);
				left = Math.Max(left, (int)captionLeft);
				right = Math.Min(right, (int)captionRight);
				if (right <= left)
				{
					continue;
				}

				rects.Add(new RectInt32(
					left,
					(int)Math.Floor(origin.Y * scale),
					right - left,
					(int)Math.Ceiling(notch.ActualHeight * scale)));
			}

			var next = rects.ToArray();
			if (SameRegions(_notchRegions, next))
			{
				return;
			}

			_notchRegions = next;
			var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
			if (next.Length == 0)
			{
				source.ClearRegionRects(NonClientRegionKind.Passthrough);
			}
			else
			{
				source.SetRegionRects(NonClientRegionKind.Passthrough, next);
			}
		}

		private static bool SameRegions(RectInt32[] a, RectInt32[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}

			for (var i = 0; i < a.Length; i++)
			{
				if (a[i].X != b[i].X || a[i].Y != b[i].Y || a[i].Width != b[i].Width || a[i].Height != b[i].Height)
				{
					return false;
				}
			}

			return true;
		}

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

		// The tooltip says what the CLICK DOES, which is also what the mark draws — the key advertises its
		// action, not its state, and the tooltip agrees with it rather than describing the key twice.
		// ⚠️ Takes the CURRENT layout (not the next), because "switch to X" is phrased from where you are.
		// ⚠️ HISTORY: it used to name the current layout AND the next one ("Two panes — click for four"),
		// plus a sentence explaining the accent cell, on the reasoning that a nameless key needs the words.
		// That made the one-line tooltip carry three facts. It carries one now; the accent cell is left to
		// be inferred from the map, where the anchor pane is plainly the same corner.
		public string PaneLayoutTooltip(PaneLayout layout) => layout switch
		{
			PaneLayout.Single => "Switch to Dual Pane Mode",
			PaneLayout.TwoAcross => "Switch to Quad Pane Mode",
			_ => "Switch to Single Pane Mode",
		};

		// ===== The marker key (show / hide radar site markers) =====
		// Both of these take the CURRENT visibility and answer in terms of the CLICK, which is why the key
		// reads "Hide…" exactly when the markers are showing. The border already carries the state (lit =
		// showing, the bar's rule everywhere); repeating it in the words would leave nothing saying what the
		// button actually does.
		public string SitesVisibleTooltip(bool visible) => visible ? "Hide Radar Sites" : "Show Radar Sites";

		// E7B3 RedEye (open) / ED1A Hide (crossed-out). ⚠️ Unlike every other glyph in this bar these two
		// have NOT been checked by rendering them in the real font — a wrong codepoint ships as an empty box,
		// so verify on first run.
		public string SitesVisibleGlyph(bool visible) => visible ? "" : "";

		// The view knows only "advance"; the ORDER is the view model's (Radar.NextPaneLayout).
		private void OnCyclePaneLayout(object sender, RoutedEventArgs e) => ViewModel.Radar.CyclePaneLayout();

		// ===== The Location key (drop / remove the user-location marker) =====
		// Phrased from where you ARE, like every other action tooltip in the bar. The transient locate
		// status WINS when there is one: the bar has no status area, so a failed fix ("Location
		// unavailable") would otherwise be completely silent — the key would simply spring back up.
		public string LocationTooltip(bool hasMarker, string status) =>
			status.Length > 0 ? status
			: hasMarker ? "Remove your location marker"
			: "Drop a marker at your location";

		// ⚠️ Re-assert IsChecked AFTER the await, not before: the click has already flipped the toggle
		// optimistically, and the resolve can fail (no OS consent, no fix, offline) without changing any
		// view-model property — so nothing would raise to pull the key back down. This is the same shape
		// SplitTemporalToggle uses for a mode a subsystem may decline. The view model owns the decision of
		// what a click MEANS (drop when absent, remove when present); this only reports the outcome.
		private async void OnToggleUserLocation(object sender, RoutedEventArgs e)
		{
			await ViewModel.Markers.ToggleUserLocationAsync();
			LocationKey.IsChecked = ViewModel.Markers.HasUserLocationMarker;
		}

		// Register one temporal mode's settings window. All three are the SAME TemporalWindow class; only the
		// mode, the caption and the size differ, so the wiring lives here once rather than three times.
		//
		// ⚠️ The window is opened and closed by the SIDE CAR on that mode's bar key, and by the caption
		// Close (which routes back through this close action). There is no bar key of its own — that is the
		// whole point of the split key. And the view model closes a window whose mode has stopped
		// (MapViewModel.OnTemporalModesChanged), so a panel can never outlive the thing it configures.
		private void RegisterTemporalWindow(TemporalMode mode, string title, double width, double height)
		{
			_windows.Register(
				id: "temporal." + mode,
				isOpen: () => ViewModel.IsTemporalWindowOpen(mode),
				close: () => ViewModel.SetTemporalWindowOpen(mode, false),
				// ⚠️ Mode FIRST: the window builds its body when both properties have landed, and setting Mode
				// last would have it build the default (Past) body and then throw it away.
				buildContent: () => new Controls.Windows.TemporalWindow { Mode = mode, ViewModel = ViewModel },
				title: title, width: width, height: height,
				alwaysOnTop: () => ViewModel.IsTemporalWindowOnTop(mode),
				customChrome: true,
				// ⚠️ The HEIGHT argument above is only a fallback now — these three windows measure their own
				// content and size to it, because their bodies differ a lot and none of them scrolls. Width
				// is still real: it is the width the content is measured AT.
				sizeToContent: true);
		}

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

			// Hand the caption band back to XAML wherever a pane notch sits in it (see the PANE NOTCHES vs
			// THE TITLE BAR block above). Hooked straight after InitializeComponent so the very first layout
			// pass registers the holes - otherwise the notch is dead until something else forces a relayout.
			PaneNotchLayer.LayoutUpdated += OnPaneNotchLayerLayoutUpdated;

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
			// THREE windows, one per timeframe — one TemporalWindow class registered three times, differing
			// only in Mode, title, size and which flags it reads. They are opened by the SETTINGS RAIL at the
			// foot of each mode's key in the bar's centre cluster, never by a key of their own.
			// ⚠️ These replaced ONE tabbed "Timeframe" window (and its shared three-dot key), which had itself
			// replaced three windows. The round trip is deliberate and the reasoning is in MapViewModel's
			// temporal region: the tabbed panel existed to stop a mode key meaning two things at once, and
			// splitting the key fixes that without making the two coexisting modes share one panel.
			// ⚠️ Each SIZES ITSELF TO ITS OWN BODY now (sizeToContent), which the tabbed window could never
			// do — it had to be sized for the tallest tab, because a window is sized once, at open. The
			// heights below are only the fallback for a measure that comes back degenerate.
			RegisterTemporalWindow(TemporalMode.Past, "PastCast", 480, 700);
			// ⚠️ NowCast's fallback grew with its body: watches and warnings are a card over type rows each
			// now, not a checkbox and a slider each, which puts it within a card's height of PastCast.
			RegisterTemporalWindow(TemporalMode.Now, "NowCast", 480, 700);
			RegisterTemporalWindow(TemporalMode.Fore, "ForeCast", 480, 640);
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
			// the ~2 s background flushes. Also latch _isClosed and drop the layout hook FIRST - the
			// teardown still runs layout passes, and the notch-region work touches the native window
			// (see _isClosed).
			Closed += (_, _) =>
			{
				_isClosed = true;
				PaneNotchLayer.LayoutUpdated -= OnPaneNotchLayerLayoutUpdated;
				Services.RadarDiagnostics.FlushAll();
			};
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
