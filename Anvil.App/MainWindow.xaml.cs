using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Anvil.Models;
using Anvil.Services;
using Anvil.Controls.Composites;
using Anvil.ViewModels;
using Anvil.Dialogs;
using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Pickers;   // the basemap folder picker (Map settings tab)
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

		/// <summary>DEV-ONLY live basemap style tuner (Debug builds only, like the two engines above).
		/// ⚠️ Its absence in Release is why a persisted tuning is NOT applied there: a working draft is not
		/// a shipped look — the style file it exports is.</summary>
		public MapStyleTuningViewModel? TuneVm { get; }

		/// <summary>DEV-ONLY velocity-dealias validation engine (fixed-corpus regression scorer). Non-null
		/// only in Debug builds. Handed to the Settings window's Debug-only Dev tab.</summary>
		public RadarValidationViewModel? ValidationVm { get; }

		/// <summary>Generic bool → Visibility for x:Bind. A Window has no Window.Resources, and x:Bind on a
		/// Window can't use {StaticResource converter}, so visibility conversions are functions here.</summary>
		public Visibility VisibleWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

		// Pull the map-controls strip DOWN over the bar's pull-tab so the tab rises through the strip's
		// notch. Set here rather than in XAML because the amount is MapControlsStrip.BarOverlap — the same
		// arithmetic that cuts the notch, so the two can never drift — and XAML cannot bind a Thickness to
		// a static. ⚠️ Called once from the ctor; the value is fixed (the tab is a fixed size, see
		// Controls/Styles.xaml), so there is nothing to re-apply on resize.
		private void ApplyStripOverlap() =>
			MapControls.Margin = new Thickness(0, 0, 0, -MapControlsStrip.BarOverlap);


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
				buildContent: () =>
				{
					var w = new Controls.Windows.TemporalWindow { Mode = mode, ViewModel = ViewModel };
					// Only PastCast's body carries the DOW section; the other two never raise this.
					w.ImportDowEventRequested += OnImportDowEventRequested;
					return w;
				},
				title: title, width: width, height: height,
				alwaysOnTop: () => ViewModel.IsTemporalWindowOnTop(mode),
				customChrome: true,
				// ⚠️ The HEIGHT argument above is only a fallback now — these three windows measure their own
				// content and size to it, because their bodies differ a lot and none of them scrolls. Width
				// is still real: it is the width the content is measured AT.
				sizeToContent: true);
		}

		// Imports a .dow.json mobile-radar frame into the DOW library, for the PastCast window's DOW section.
		// Here for the same reason as the folder picker below: the picker needs a window HWND, and the body
		// raising the request is a UserControl inside a UserControl.
		// ⚠️ The frame is COPIED into %LocalAppData%\Anvil\DowEvents rather than referenced where it sits —
		// the WebView fetches it, so it has to be same-origin under the mapped dowevents host.
		private async void OnImportDowEventRequested(object? sender, EventArgs e)
		{
			var picker = new FileOpenPicker
			{
				SuggestedStartLocation = PickerLocationId.ComputerFolder,
			};
			// .dow.json is a DOUBLE extension and the picker only matches the last one, so this filters to
			// .json and the import validates from there.
			picker.FileTypeFilter.Add(".json");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

			try
			{
				var file = await picker.PickSingleFileAsync();
				await ViewModel.Radar.Dow.ImportAsync(file?.Path); // null (cancelled) is ignored
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "DOW frame import failed");
			}
		}

		// Picks the folder holding the offline basemap archive, for the Settings window's Map tab. Lives
		// here because a WinRT FolderPicker must be initialized with a window HWND, and the settings panel is
		// a UserControl hosted in a window rather than a window itself — the same reason the report dialogs
		// below are raised up to here.
		// ⚠️ The chosen folder takes effect on the NEXT LAUNCH: the mapdata virtual host is mapped once, in
		// this class's WebView bootstrap, before any page loads. The tab's status line says so; nothing here
		// tries to re-map a live WebView.
		private async void OnBrowseMapDataFolderRequested(object? sender, EventArgs e)
		{
			var picker = new FolderPicker
			{
				SuggestedStartLocation = PickerLocationId.ComputerFolder,
			};
			picker.FileTypeFilter.Add("*"); // required: the picker throws on show with an empty filter list
			WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

			try
			{
				var folder = await picker.PickSingleFolderAsync();
				ViewModel.SetMapDataFolder(folder?.Path); // null (cancelled) is ignored by the setter
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Basemap folder picker failed");
			}
		}

		// DEV TOOL. Writes the edited basemap out as a real style file: the PRISTINE file with every colour
		// literal replaced by that slot's resolved colour.
		//
		// ⚠️ POSITIONAL TEXT SUBSTITUTION, NOT A RE-SERIALIZED STYLE. Round-tripping 414 KB of JSON through a
		// parser reformats all ~10,000 lines and buries the real changes; replacing the Nth #rrggbb literal
		// in the original leaves formatting untouched and gives a reviewable diff. (Learned the hard way
		// doing this by hand — see docs/theming.md.)
		// ⚠️ POSITIONAL rather than a find/replace by colour, because two slots can start the same colour and
		// end different — which is the whole point of per-slot overrides.
		// ⚠️ THE ORDER IS AN ASSUMPTION: that the page enumerates slots in the same order the literals appear
		// in the file. It holds (layers in order, paint keys in insertion order, colours within a value in
		// order), but it is checked rather than trusted — a count mismatch aborts the write instead of
		// producing a scrambled style.
		// ⚠️ The COLOURS come from the page, which owns the maths (style-tune.js). Nothing here computes one.
		private async void OnExportTunedStyleRequested(object? sender, EventArgs e)
		{
#if DEBUG
			if (TuneVm is null) return;

			try
			{
				var json = await TuneVm.GetSlotColorsJsonAsync();
				if (string.IsNullOrWhiteSpace(json)) return;

				var colors = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
				if (colors is null || colors.Count == 0) return;

				// The style the theme is currently on, read from the app's own Assets (package content).
				var fileName = ViewModel.SelectedStyle?.FileName;
				if (string.IsNullOrEmpty(fileName)) return;
				var source = Path.Combine(AppContext.BaseDirectory, "Assets", "Map", fileName);
				if (!File.Exists(source)) return;

				var text = File.ReadAllText(source);
				var literal = new System.Text.RegularExpressions.Regex("#[0-9a-fA-F]{6}");

				var found = literal.Matches(text).Count;
				if (found != colors.Count)
				{
					_logger.LogError(
						"Tuned style export ABORTED: the style file has {Found} colour literals but the page " +
						"reported {Reported} slots. The two enumerations have diverged; writing would scramble " +
						"the style.", found, colors.Count);
					return;
				}

				var n = 0;
				text = literal.Replace(text, _ => colors[n++]);

				var picker = new FileSavePicker
				{
					SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
					SuggestedFileName = Path.GetFileNameWithoutExtension(fileName) + "-tuned",
				};
				picker.FileTypeChoices.Add("Map style", new List<string> { ".json" });
				WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

				var target = await picker.PickSaveFileAsync();
				if (target is null) return;   // cancelled

				await Windows.Storage.FileIO.WriteTextAsync(target, text);
				_logger.LogInformation("Exported edited basemap style to {Path} ({Count} colours placed)",
					target.Path, colors.Count);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Tuned style export failed");
			}
#else
			await Task.CompletedTask;
#endif
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

		/// <summary>Serilog-backed log for this window. Its one job today is the WebView2 death report —
		/// see <see cref="OnWebViewProcessFailed"/>.</summary>
		private readonly ILogger<MainWindow> _logger;

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
			WindowManager windows,
			ILogger<MainWindow> logger)
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
			_logger = logger;

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

			// DEV-ONLY live basemap style tuner. Constructed here so the persisted draft is restored and
			// pushed at map-ready; the Dev tab only binds it.
			TuneVm = new MapStyleTuningViewModel(_mapService);

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

			// The theme's BASE palette, applied to the whole XAML tree. This one line is what makes WinUI's
			// several hundred system brushes (text ranks, card strokes, focus rects, every stock control)
			// resolve to the same light/dark ground the theme's own surfaces in Controls/Styles.xaml are
			// authored against. Set straight after InitializeComponent, before the first render, so nothing
			// paints on the other palette first. A panel window that is ALREADY open needs the change pushed
			// to it (ApplyAppTheme); one opened later copies this window's ActualTheme itself.
			// ⚠️ This PINS the app to the chosen identity instead of following the OS. Following the OS is a
			// different feature — a "System" theme that resolves to the light or dark identity — and it is
			// not built; the two shipped themes are picked explicitly.
			ApplyAppTheme();

			// The identity can change at runtime, and the WinUI half of it lives out here: Core raises the
			// property, the view turns it into a palette. (The map half is pushed by the view model itself,
			// as one ApplyThemeAsync command.)
			ViewModel.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(MapViewModel.SelectedTheme)) ApplyAppTheme();
			};

			ApplyStripOverlap();

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
						TuneVm = TuneVm,
					};
					// Wired per-instance (the window's content is rebuilt each time it opens) so a finished
					// dev run still pops its results dialog.
					settings.SweepReportRequested += OnSweepReportRequested;
					settings.ValidationReportRequested += OnValidationReportRequested;
					settings.BrowseMapDataFolderRequested += OnBrowseMapDataFolderRequested;
					settings.ExportTunedStyleRequested += OnExportTunedStyleRequested;
					return settings;
				},
				title: "Settings", width: 520, height: 640,
				alwaysOnTop: () => ViewModel.IsSettingsWindowOnTop,
				customChrome: true);
			// DEV style editor. ⚠️ The registration is NOT #if DEBUG'd — same as the Pipeline Console's —
			// because WindowManager reconciles off a VM flag and knows nothing about build configuration.
			// Nothing opens it in Release: its switch lives on the Dev tab, which is not built there.
			_windows.Register(
				id: "styleEditor",
				isOpen: () => ViewModel.IsStyleEditorOpen,
				close: () => ViewModel.IsStyleEditorOpen = false,
				buildContent: () =>
				{
					var editor = new Controls.Windows.StyleEditorWindow
					{
						ViewModel = ViewModel,
						TuneVm = TuneVm,
					};
					editor.ExportRequested += OnExportTunedStyleRequested;
					return editor;
				},
				title: "Map style editor", width: 520, height: 720,
				alwaysOnTop: () => ViewModel.IsStyleEditorOnTop,
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

				// Stop every app-lifetime background loop BEFORE the flush. They resume on this window's
				// DispatcherQueue and end in property notifications / map pushes, so one still ticking after
				// this point is notifying a XAML tree that is being destroyed. See MapViewModel.Shutdown.
				ViewModel.Shutdown();

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
				ViewModel.IsOnlineTilesActive, ViewModel.OnlineTilesUrl, ViewModel.SelectedTheme.Id));
		}

		// Builds the page URL for the map: framed at the given center/zoom, on the given
		// basemap. The page posts 'mapReady' once its MapLibre map's 'load' fires.
		// `conus` = the launch CONUS-mask default, so the page can apply the mask on first load (before it
		// reveals) instead of waiting for the host round-trip — otherwise the world basemap flashes first.
		// `onlineTiles`/`tilesUrl` = the basemap tile source, passed the same way and for the same reason as
		// `conus`: the page builds its FIRST map on the right source instead of loading the offline basemap
		// and then re-styling onto the online one (a visible reload of every tile at launch).
		// The WinUI half of a theme change: the palette every system brush resolves against, pushed to this
		// window's tree, to any panel window already open, and to the WebView's pre-paint ground.
		// ⚠️ THE PAGE'S OWN CHROME IS NOT HERE. That travels as one IMapService.ApplyThemeAsync command from
		// the view model, because the page needs the palette set before it re-adds its layers. Splitting the
		// push across two owners is deliberate — each writes only what it can address.
		private void ApplyAppTheme()
		{
			var theme = ViewModel.SelectedTheme;

			if (Content is FrameworkElement themeRoot)
			{
				themeRoot.RequestedTheme = theme.Base == ThemeBase.Dark ? ElementTheme.Dark : ElementTheme.Light;
			}

			// ⚠️ A panel window is its own top-level XAML tree and takes its palette once, when it opens —
			// nothing propagates a later change, so open ones have to be told.
			_windows.ApplyOwnerTheme();

			// Only ever seen in the gap before the page paints, and on a dead renderer — but on a dead
			// renderer it is the whole window, so it should at least be the right color.
			if (MainMapWebView?.CoreWebView2 is not null)
			{
				MainMapWebView.DefaultBackgroundColor = ParseGroundColor(theme.GroundColor);
			}
		}

		// "#RRGGBB" (the form theme.css uses, so the two halves of the ground color read alike) → a Color.
		// ⚠️ Falls back to BLACK rather than throwing: this runs on the WebView bootstrap, and a malformed
		// value should cost a slightly-wrong flash frame, never the map.
		private static Windows.UI.Color ParseGroundColor(string hex)
		{
			if (!string.IsNullOrWhiteSpace(hex) && hex.TrimStart('#').Length == 6
				&& int.TryParse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
			{
				return Microsoft.UI.ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
			}

			return Microsoft.UI.Colors.Black;
		}

		// `themeId` = the chrome palette, passed for exactly the same reason as `conus` and the tile source:
		// --anvil-ground IS the page's background, so a post-ready push would flash the other theme's ground
		// before the map paints.
		private static string BuildMapUrl(MapRegion? region, double zoom, string styleFile, bool conus,
			bool onlineTiles, string tilesUrl, string themeId)
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
				$"&tilesUrl={Uri.EscapeDataString(tilesUrl ?? "")}" +
				$"&theme={Uri.EscapeDataString(themeId ?? "")}";
		}

		private async Task InitializeWebViewAsync(WebView2 webView, string url)
		{
			// The theme's ground, so there's no flash before the page (and MapLibre) paint.
			// ⚠️ This is ALSO what a DEAD renderer paints — see OnWebViewProcessFailed.
			// ⚠️ The page paints the same color from --anvil-ground in theme.css. C# can't read the page's
			// CSS, so the value is written in both places and AppTheme.GroundColor is the C# half.
			webView.DefaultBackgroundColor = ParseGroundColor(ViewModel.SelectedTheme.GroundColor);
			await webView.EnsureCoreWebView2Async();

			// The WebView2 death report. Subscribed FIRST, before any host mapping or navigation, so a
			// failure during startup is caught too.
			webView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;

			// The DOW library is a per-user folder under %LocalAppData% that the app IMPORTS into, so it does
			// not exist on a fresh install - create it before the host mapping below points at it.
			// (It used to live inside the read-only package, which is exactly why importing was impossible.)
			try { Directory.CreateDirectory(DowEventProvider.EventsDirectory); } catch { /* mapping just resolves to nothing */ }

			// Map each virtual host → local folder so the page can fetch everything offline, same-origin:
			//   mapassets  → bundled MapLibre style/glyphs/sprites/libraries
			//   mapdata    → the user-configured (external, ~29 GB) basemap PMTiles folder
			//   spcoutlooks/spcwatches/warnings/stormreports/radarlevel2 → the services' on-disk caches
			//   dowevents  → the user's imported DOW (mobile-radar) frame library (%LocalAppData%)
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
		/// The WebView2 process died. PURE INSTRUMENTATION — it reports and does not recover.
		/// </summary>
		/// <remarks>
		/// ⚠️ THIS IS THE "ENTIRE MAP WENT BLACK" EVENT, and until it existed the failure was invisible.
		/// The renderer dying leaves the app itself perfectly healthy — C# keeps running, the bar and the pane
		/// notches keep drawing, the refresh loops keep logging — while the WebView paints its
		/// <c>DefaultBackgroundColor</c>, which is deliberately BLACK (the no-white-flash choice above). So the
		/// symptom is a black rectangle where the map was, with working WinUI chrome around it, and nothing
		/// anywhere said why.
		/// <para>Observed 2026-09-03 on a 26-frame PastCast replay: the radar JSONL's JS event stream stopped
		/// dead mid-<c>fullPrefetch</c> while Serilog kept writing for another ~10 s. Diagnosing that took
		/// cross-referencing two logs by timestamp; this handler makes it one line. Suspected cause is renderer
		/// memory (a fully-built 26-frame legacy-volume loop retains ~2.1 GB of gate geometry — see
		/// <c>docs/app-notes.md</c>), which <see cref="CoreWebView2ProcessFailedKind"/> +
		/// <c>ExitCode</c> will confirm or refute the next time it happens.</para>
		/// <para>⚠️ Logged at CRITICAL: the app is alive but its entire map surface is gone, which is as bad as
		/// a crash from where the user sits. It ALSO writes into the radar diagnostics stream, because that is
		/// the timeline the JS events stop in — the death marker belongs in the same file as the last frame
		/// event, not only in the Serilog file.</para>
		/// <para>⚠️ It does NOT reload the WebView. <c>ProcessFailed</c> IS the hook a recovery would hang off,
		/// but recovering means re-running the whole bootstrap (host mappings, navigation, then every overlay
		/// re-added through <c>reAddAll</c>, panes and radar loop included) and deciding what happens to the
		/// loop that was on screen. That is a feature, not instrumentation; keep them separate so this handler
		/// can never be the thing that breaks.</para>
		/// </remarks>
		private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
		{
			// Everything here is best-effort and defensive: we are already in a failure path, and a throw from
			// a diagnostic handler would replace a legible report with an unhandled exception.
			var kind = "unknown";
			var reason = "unknown";
			var exitCode = -1;
			var process = string.Empty;
			var frames = 0;
			try { kind = e.ProcessFailedKind.ToString(); } catch { /* best effort */ }
			try { reason = e.Reason.ToString(); } catch { /* not populated for every kind */ }
			try { exitCode = e.ExitCode; } catch { /* not populated for every kind */ }
			try { process = e.ProcessDescription ?? string.Empty; } catch { /* not populated for every kind */ }
			try { frames = e.FrameInfosForFailedProcess?.Count ?? 0; } catch { /* frame-only kinds */ }

			_logger.LogCritical(
				"WebView2 process failed: kind={Kind} reason={Reason} exitCode={ExitCode} process={Process} frames={Frames}. " +
				"The map surface is now blank (DefaultBackgroundColor) while the app keeps running; it will NOT self-recover.",
				kind, reason, exitCode, process, frames);

			// Same event, into the radar stream — the JS events simply STOP at the moment of death, so this is
			// the line that explains the end of that timeline.
			Services.RadarDiagnostics.Log("app", "webview.processfailed",
				("kind", kind), ("reason", reason), ("exitCode", exitCode),
				("process", process), ("frames", frames));
			Services.RadarDiagnostics.FlushAll(); // the renderer is gone; don't wait on the ~2 s flush timer
		}

		/// <summary>
		/// IMapView seam: the ONLY place that touches WebView2 / ExecuteScriptAsync.
		/// Valid only once the map's CoreWebView2 has initialized, and only until the window closes.
		/// </summary>
		/// <remarks>
		/// ⚠️ The _isClosed check is the SECOND half of the shutdown story (MapViewModel.Shutdown is the
		/// first): cancelling a loop stops the NEXT cycle, but a cycle already in flight still runs to its
		/// end, and its end is usually a map push landing here. CoreWebView2 is NOT null at that point — it
		/// is non-null and dead — so the guard above cannot catch it. Same reasoning as the notch-region
		/// latch: after Closed, every WebView2 member is a projection over a torn-down object.
		/// </remarks>
		public async Task<string> RunScriptAsync(string javaScript)
		{
			if (_isClosed || MainMapWebView.CoreWebView2 is null)
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
#if DEBUG
			// Push the restored style-tuning draft. Debug only — in Release TuneVm does not exist, which is
			// deliberate: a working draft is not a shipped look (see TuneVm).
			if (TuneVm is not null)
			{
				await TuneVm.OnMapsReadyAsync();
			}
#endif
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
