using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Which temporal feature a toggle click refers to (PastCast / NowCast / ForeCast). Identifies
	/// the toggle for <see cref="MapViewModel.ToggleTemporalMode"/>; it is NOT an open-state — each feature's
	/// settings window has its own independent <c>IsXWindowOpen</c> flag.</summary>
	public enum TemporalMode { Past, Now, Fore }

	/// <summary>
	/// View model for the NON-radar map concerns: selectable basemap styles + current selection,
	/// the SPC outlook day/product selection + overlay opacity, SPC watches, the outlook info/times,
	/// and map markers + user location. The radar subsystem lives in <see cref="Radar"/>
	/// (<see cref="RadarViewModel"/>). Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed class MapViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IStyleProvider _styleProvider;
		private readonly IRegionProvider _regionProvider;

		// Readiness guard: the map page must have reported 'mapReady' before style /
		// outlook commands can succeed. The view calls OnMapsReadyAsync() once the map
		// loads; until then a style change is stored and the overlay is deferred.
		private bool _isMapReady;

		private MapStyle? _selectedStyle;

		// The region the main map is framed on (CONUS).
		private MapRegion? _mainRegion;


		public MapViewModel(IMapService mapService, IStyleProvider styleProvider, IRegionProvider regionProvider, ISpcOutlookService spcOutlookService, ISpcWatchService watchService, IWarningService warningService, IStormReportService stormReportService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService, ILocationService locationService, IDowEventProvider dowEventProvider, IDispatcher dispatcher, ISettingsService settingsService, ILoggerFactory loggerFactory)
		{
			_mapService = mapService;
			_styleProvider = styleProvider;
			_regionProvider = regionProvider;

			// Built before IsSettingsWindowOpen can fire (its setter refreshes the cache readout on open).
			Storage = new StorageSettingsViewModel(radarService, settingsService);

			// Each subsystem lives in its own view model (progressively split out of this class);
			// the transport-bar section controls bind slices of them.
			Radar = new RadarViewModel(mapService, radarSiteProvider, radarService, dowEventProvider, settingsService);
			Outlook = new OutlookViewModel(mapService, spcOutlookService, dispatcher, loggerFactory.CreateLogger<OutlookViewModel>());
			PastOutlook = new PastOutlookViewModel(mapService, spcOutlookService, Radar);
			Watches = new WatchesViewModel(mapService, watchService, dispatcher, loggerFactory.CreateLogger<WatchesViewModel>());
			Warnings = new WarningsViewModel(mapService, warningService, dispatcher, loggerFactory.CreateLogger<WarningsViewModel>());
			StormReports = new StormReportsViewModel(mapService, stormReportService, Radar, dispatcher, loggerFactory.CreateLogger<StormReportsViewModel>());
			Markers = new MarkersViewModel(mapService, locationService);
			SiteExplorer = new RadarSiteExplorerViewModel(Radar, Markers, radarService, mapService);
			StateIso = new StateIsolationViewModel(mapService);
			MileGrid = new MileGridViewModel(mapService, Radar);
			PipelineConsole = new PipelineConsoleViewModel(mapService, Radar); // PIPELINE CONSOLE (remove with the feature)

			// The Past/Now/Fore toggles PROJECT subsystem state (see the Temporal toggles region), so keep
			// them honest: re-raise them — and close a now-inactive feature's settings window — whenever the
			// radar mode/loop or the outlook overlay changes, including changes NOT driven by the toggles
			// (e.g. clicking an on-map radar site marker starts a live loop, which should light NowCast).
			Radar.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(RadarViewModel.IsPastEventMode))
				{
					OnPropertyChanged(nameof(IsPastCast));
					// Entering replay takes the radar layer — disarm the (mutually exclusive) live toggle.
					if (Radar.IsPastEventMode && _isNowCast) { _isNowCast = false; OnPropertyChanged(nameof(IsNowCast)); }
					// The map has ONE outlook layer: hand it to the historical (PastOutlook) overlay in past
					// mode and back to the live outlook otherwise. Entering clears the live outlook (showing
					// today's forecast over historical radar would be wrong); PastOutlook then drives it.
					if (Radar.IsPastEventMode && Outlook.IsOutlookVisible) { Outlook.IsOutlookVisible = false; }
					// The live "now" overlays (watch boxes + storm-based warnings) are CURRENT-conditions data,
					// so they must clear when we drop into a historical replay — otherwise today's watches/
					// warnings hang over past radar. (Storm reports re-key to the replay day, so they stay.)
					if (Radar.IsPastEventMode)
					{
						if (Watches.IsVisible) { Watches.IsVisible = false; }
						if (Warnings.IsVisible) { Warnings.IsVisible = false; }
					}
					PastOutlook.OnPastModeChanged(Radar.IsPastEventMode);
					CloseWindowIfInactive();
				}
				else if (e.PropertyName == nameof(RadarViewModel.HasRadarLoop))
				{
					// A live loop starting (e.g. an on-map site-marker click) arms NowCast so it reflects reality.
					if (Radar.HasRadarLoop && !Radar.IsPastEventMode && !_isNowCast)
					{
						_isNowCast = true;
						OnPropertyChanged(nameof(IsNowCast));
						CloseWindowIfInactive();
					}
				}
			};
			Outlook.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(OutlookViewModel.IsOutlookVisible))
				{
					OnPropertyChanged(nameof(IsForeCast));
					CloseWindowIfInactive();
				}
			};

			AvailableStyles = _styleProvider.GetStyles();

			// Assign the backing field directly (not the setter) so the default
			// selection does NOT trigger a map command during construction. The page
			// loads this style via its URL, so there is nothing to re-apply. Default to
			// Data Viz Black.
			_selectedStyle = AvailableStyles.FirstOrDefault(s => s.Id == "dataVizBlack")
				?? AvailableStyles.FirstOrDefault();

			// The main map is framed on CONUS.
			var regions = _regionProvider.GetRegions();
			_mainRegion = regions.FirstOrDefault(r => r.Id == "conus") ?? regions.FirstOrDefault();
		}

		/// <summary>The radar subsystem view model (sites, loop, live frame, past-event, DOW, card,
		/// color scale, inspector). The transport-bar section controls bind to this.</summary>
		public RadarViewModel Radar { get; }

		/// <summary>The SPC outlook subsystem view model (day/product selection, info card,
		/// next-update progress, background refresh).</summary>
		public OutlookViewModel Outlook { get; }

		/// <summary>The historical SPC outlook overlay for PastCast (day 1-3 product for the replay date,
		/// from the IEM archive). Shares the map's single outlook layer with <see cref="Outlook"/>; this VM
		/// owns it in past mode (see the IsPastEventMode handler above).</summary>
		public PastOutlookViewModel PastOutlook { get; }

		/// <summary>The SPC watch-box subsystem view model (Tornado / Severe Thunderstorm Watches —
		/// current-conditions alerts surfaced under NowCast, with their own toggle + refresh loop).</summary>
		public WatchesViewModel Watches { get; }

		/// <summary>The storm-based warning subsystem view model (active Tornado / Severe Thunderstorm
		/// Warnings — the modern forecaster-drawn polygons — surfaced under NowCast with their own toggle
		/// + faster refresh loop; sits above the watch boxes on the map).</summary>
		public WarningsViewModel Warnings { get; }

		/// <summary>The SPC storm-reports verification overlay VM (Tornado / Wind / Hail dots for the active
		/// convective day — the replay day in PastCast, today in NowCast — with per-type toggles). Its own
		/// map overlay (top of the stack); surfaced in both the Past and Now windows.</summary>
		public StormReportsViewModel StormReports { get; }

		/// <summary>The map markers + user-location subsystem view model (locate action + marker editor).</summary>
		public MarkersViewModel Markers { get; }

		/// <summary>The Radar Site Explorer subsystem view model (searchable/filterable browser over the
		/// whole radar network + per-site detail). Opened by the "Sites" button on the bar.</summary>
		public RadarSiteExplorerViewModel SiteExplorer { get; }

		/// <summary>State Isolation mode (hover-to-highlight + click-to-isolate a single US state, masking
		/// everything else). An app-wide mode toggled by the "Isolate" button on the top bar; a building
		/// block for the planned stream mode.</summary>
		public StateIsolationViewModel StateIso { get; }

		/// <summary>The mile distance grid (a square grid anchored to the selected radar, hide/show + opacity).
		/// Surfaced in the Map Controls window.</summary>
		public MileGridViewModel MileGrid { get; }

		/// <summary>The App Settings "Storage" section VM (radar cache size readout + Clear + the persisted
		/// size cap). The settings service's first real consumer.</summary>
		public StorageSettingsViewModel Storage { get; }

		// PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit).
		/// <summary>The Pipeline Console: a read-only glass-cockpit over the Level-2 build pipeline (a
		/// mini-scrubber per product + VWP/storm-motion state). Ships behind a non-obvious toggle in the App
		/// Settings window; polls the WebView only while open.</summary>
		public PipelineConsoleViewModel PipelineConsole { get; }

		private bool _isPipelineConsoleOpen;
		private bool _isPipelineConsoleOnTop = true;

		/// <summary>Whether the Pipeline Console window stays above the main window (topmost). User-toggled from
		/// the pin in the console's title-bar area; <c>WindowManager</c> applies it to that window's
		/// presenter. Defaults on — the house default for every panel now, not a console quirk (see the
		/// per-window flags below).</summary>
		public bool IsPipelineConsoleOnTop
		{
			get => _isPipelineConsoleOnTop;
			set => SetProperty(ref _isPipelineConsoleOnTop, value);
		}

		// Per-window always-on-top flags (topmost), each driven by that window's title-bar pin and applied to
		// its presenter by WindowManager.
		// ⚠️ ALL DEFAULT ON, deliberately — a panel is app chrome, and it is hidden from the taskbar/Alt-Tab
		// (see WindowManager's chrome policy), so an UNPINNED panel can slip behind the main window with no
		// switcher route back. Pinned-by-default means a panel you opened is a panel you can see. Unpinning is
		// the opt-out, for parking one on a second monitor.
		// ⚠️ These are the FIELD defaults, not a re-arm on open: unpin a panel and it stays unpinned when you
		// reopen it this session. Don't "fix" that by re-arming in the open path — a toggle that silently
		// resets itself is worse than one that remembers.
		private bool _isSettingsWindowOnTop = true;
		private bool _isMapControlsWindowOnTop = true;
		private bool _isSiteExplorerOnTop = true;
		private bool _isPastWindowOnTop = true;
		private bool _isNowWindowOnTop = true;
		private bool _isForeWindowOnTop = true;
		private bool _isDevToolsWindowOnTop = true;

		/// <summary>Whether the App Settings window stays on top (title-bar pin).</summary>
		public bool IsSettingsWindowOnTop
		{
			get => _isSettingsWindowOnTop;
			set => SetProperty(ref _isSettingsWindowOnTop, value);
		}

		/// <summary>Whether the Map Controls window stays on top (title-bar pin).</summary>
		public bool IsMapControlsWindowOnTop
		{
			get => _isMapControlsWindowOnTop;
			set => SetProperty(ref _isMapControlsWindowOnTop, value);
		}

		/// <summary>Whether the Radar Sites window stays on top (title-bar pin).</summary>
		public bool IsSiteExplorerOnTop
		{
			get => _isSiteExplorerOnTop;
			set => SetProperty(ref _isSiteExplorerOnTop, value);
		}

		/// <summary>Whether the Past Event window stays on top (title-bar pin).</summary>
		public bool IsPastWindowOnTop
		{
			get => _isPastWindowOnTop;
			set => SetProperty(ref _isPastWindowOnTop, value);
		}

		/// <summary>Whether the Live Radar window stays on top (title-bar pin).</summary>
		public bool IsNowWindowOnTop
		{
			get => _isNowWindowOnTop;
			set => SetProperty(ref _isNowWindowOnTop, value);
		}

		/// <summary>Whether the SPC Outlooks window stays on top (title-bar pin).</summary>
		public bool IsForeWindowOnTop
		{
			get => _isForeWindowOnTop;
			set => SetProperty(ref _isForeWindowOnTop, value);
		}

		/// <summary>Whether the dev tools window stays on top (title-bar pin). DEV-ONLY.</summary>
		public bool IsDevToolsWindowOnTop
		{
			get => _isDevToolsWindowOnTop;
			set => SetProperty(ref _isDevToolsWindowOnTop, value);
		}
		/// <summary>Whether the Pipeline Console window is open. INDEPENDENT of the other windows (it may sit
		/// alongside them). Forwards to <see cref="PipelineConsoleViewModel.IsOpen"/> so polling runs only
		/// while it's open.</summary>
		public bool IsPipelineConsoleOpen
		{
			get => _isPipelineConsoleOpen;
			set
			{
				if (SetProperty(ref _isPipelineConsoleOpen, value))
				{
					PipelineConsole.IsOpen = value;
				}
			}
		}

		// ===== Temporal toggles (Past / Now / Fore) — INDEPENDENT, deselectable ==========================
		// The three toggles are independent on/off switches, each a PROJECTION of its subsystem's real
		// state (no duplicated flag that could desync): IsPastCast ↔ Radar replay mode, IsNowCast ↔ a live
		// radar loop being shown, IsForeCast ↔ the SPC outlook overlay. Setting a toggle drives its
		// subsystem; the Radar/Outlook PropertyChanged subscriptions (in the ctor) re-raise the toggles
		// when that state changes from anywhere (e.g. clicking an on-map site marker lights NowCast).
		// PAST is exclusive with EVERYTHING (a historical replay can't share the map with live radar OR the
		// live forecast): entering Past clears Now + Fore, and turning Now or Fore on exits Past. NOW and FORE
		// may be up TOGETHER (live radar under the live outlook). So: Past alone, or Now and/or Fore. Turning
		// one on clears whatever it excludes — see the IsPastCast/IsNowCast/IsForeCast setters + the ctor
		// Radar/Outlook subscriptions. With ALL THREE OFF the map is a blank basemap (the "cleared" state — click the active
		// toggle to reach it). ALL THREE START OFF — nothing is armed at launch (a clean map). Activating a
		// toggle also OPENS its settings window (the setters set IsXWindowOpen); a feature turning off closes
		// its window (CloseWindowIfInactive). The three windows are INDEPENDENT — each is its own OS window,
		// so Now + Fore (which coexist as modes) can sit open side by side on different monitors.

		/// <summary>PastCast (historical replay). Projection of <see cref="RadarViewModel.IsPastEventMode"/>:
		/// on enters replay (clearing any live loop), off exits replay to a blank basemap. Turning it on
		/// takes the radar layer from NowCast. Re-raised via the Radar subscription.</summary>
		public bool IsPastCast
		{
			get => Radar.IsPastEventMode;
			set
			{
				if (value == Radar.IsPastEventMode) { return; }
				Radar.IsPastEventMode = value; // both directions clear the layer (enter = arm replay, leave = blank)
				if (value) { IsPastWindowOpen = true; } // user turned PastCast on → open its window (off closes via CloseWindowIfInactive)
			}
		}

		// NowCast is an ARMED toggle (a stored flag), NOT a pure projection: "live radar mode is on." Unlike
		// PastCast (Radar.IsPastEventMode) and ForeCast (Outlook.IsOutlookVisible) — both genuine persistent
		// states — "live mode" has no subsystem flag (it's just "not replay", the default), so a projection
		// off loop-existence would snap back + DISABLE the cog whenever no site is loaded. Storing it lets
		// the toggle/cog stay on with a blank-but-armed radar. Default OFF (nothing is armed at launch — a
		// clean map); clicking a site marker starts a live loop, which arms it via the Radar subscription
		// (that path writes this FIELD directly, bypassing the setter, so it does NOT auto-open the Now window).
		// Entering replay disarms it. Set as a field initializer so construction issues no map command.
		private bool _isNowCast;

		/// <summary>NowCast (live radar). On = live mode armed (leaves replay; a site is then picked by
		/// clicking its on-map marker); off = clears the live loop to a blank basemap. Mutually exclusive
		/// with PastCast (the radar layer is live OR replaying).</summary>
		public bool IsNowCast
		{
			get => _isNowCast;
			set
			{
				if (!SetProperty(ref _isNowCast, value)) { return; }
				if (value)
				{
					if (Radar.IsPastEventMode) { Radar.IsPastEventMode = false; } // leave replay for live mode
					IsNowWindowOpen = true; // user turned NowCast on → open its window (marker-click arming bypasses this setter)
				}
				else if (!Radar.IsPastEventMode)
				{
					Radar.SelectedRadarOption = Radar.RadarOptions[0]; // "None" → clear the live loop to a blank basemap
				}
				CloseWindowIfInactive();
			}
		}

		/// <summary>ForeCast (SPC outlook overlay). Independent projection of
		/// <see cref="OutlookViewModel.IsOutlookVisible"/> — stacks on live or replay radar. Re-raised via
		/// the Outlook subscription.</summary>
		public bool IsForeCast
		{
			get => Outlook.IsOutlookVisible;
			set
			{
				if (value == Outlook.IsOutlookVisible) { return; }
				// Fore excludes Past (both directions): entering Past already clears Fore (Radar subscription);
				// turning Fore on must likewise exit replay, so Past + Fore are never up together. (Now + Fore
				// DO coexist — no exclusion between them.) Mirrors how arming NowCast leaves replay.
				if (value && Radar.IsPastEventMode) { Radar.IsPastEventMode = false; }
				Outlook.IsOutlookVisible = value;
				if (value) { IsForeWindowOpen = true; } // user turned ForeCast on → open its window (off closes via CloseWindowIfInactive)
			}
		}

		// Closes any settings window whose feature just turned off. Called from the toggle setters'
		// subsystem subscriptions so a window can't linger over an inactive feature. Each is checked on its
		// own — they're independent windows, so closing one never disturbs the others.
		private void CloseWindowIfInactive()
		{
			if (!IsPastCast) { IsPastWindowOpen = false; }
			if (!IsNowCast) { IsNowWindowOpen = false; }
			if (!IsForeCast) { IsForeWindowOpen = false; }
		}

		/// <summary>Handles a click on a temporal mode toggle. The mode's single toggle both activates the
		/// feature AND governs its settings window:
		/// <list type="bullet">
		/// <item>mode OFF → turn it on (the setter opens its window);</item>
		/// <item>mode ON → flip its window open/closed. The mode STAYS active — you leave a mode by choosing
		/// another one (Past excludes Now/Fore; Now and Fore coexist), not by clicking its own toggle.</item>
		/// </list>
		/// A window-only flip doesn't change the mode projection, so we re-raise it to re-assert the toggle's
		/// lit state (bound OneWay to the projection) after the ToggleButton flipped its own IsChecked on the
		/// click.</summary>
		public void ToggleTemporalMode(TemporalMode which)
		{
			bool active = which switch
			{
				TemporalMode.Past => IsPastCast,
				TemporalMode.Now => IsNowCast,
				TemporalMode.Fore => IsForeCast,
				_ => false,
			};

			if (!active)
			{
				switch (which) // the setter turns the mode on and opens its window
				{
					case TemporalMode.Past: IsPastCast = true; break;
					case TemporalMode.Now: IsNowCast = true; break;
					case TemporalMode.Fore: IsForeCast = true; break;
				}
				return;
			}

			// Already active → flip that feature's window only; the mode stays on, and the OTHER two windows
			// are left alone (they're independent OS windows, not a one-at-a-time group).
			switch (which)
			{
				case TemporalMode.Past: IsPastWindowOpen = !IsPastWindowOpen; break;
				case TemporalMode.Now: IsNowWindowOpen = !IsNowWindowOpen; break;
				case TemporalMode.Fore: IsForeWindowOpen = !IsForeWindowOpen; break;
			}
			OnPropertyChanged(which switch // projection unchanged → re-assert the toggle's lit state
			{
				TemporalMode.Past => nameof(IsPastCast),
				TemporalMode.Now => nameof(IsNowCast),
				TemporalMode.Fore => nameof(IsForeCast),
				_ => string.Empty,
			});
		}

		// Per-feature settings-window open flags. Each temporal feature has its OWN OS window, so these are
		// INDEPENDENT bools — opening one never closes another (Now + Fore can be parked side by side). Opened
		// by the feature's toggle, closed by the window's caption Close or by the feature turning off
		// (CloseWindowIfInactive). WindowManager watches these and reconciles each to a live Window.
		private bool _isPastWindowOpen;
		private bool _isNowWindowOpen;
		private bool _isForeWindowOpen;

		/// <summary>Whether the Past Event settings window is open.</summary>
		public bool IsPastWindowOpen
		{
			get => _isPastWindowOpen;
			set => SetProperty(ref _isPastWindowOpen, value);
		}

		/// <summary>Whether the Live Radar settings window is open.</summary>
		public bool IsNowWindowOpen
		{
			get => _isNowWindowOpen;
			set => SetProperty(ref _isNowWindowOpen, value);
		}

		/// <summary>Whether the SPC Outlooks settings window is open.</summary>
		public bool IsForeWindowOpen
		{
			get => _isForeWindowOpen;
			set => SetProperty(ref _isForeWindowOpen, value);
		}

		// ===== App-wide windows (Map Controls / App Settings / Site Explorer / dev tools) =================
		// Same model as the temporal windows above: one independent bool per window, opened by its button on
		// the top bar's right edge and closed by the window's caption Close. No one-at-a-time grouping — these
		// are real OS windows, so any combination may be open at once.
		private bool _isMapControlsWindowOpen;
		private bool _isSettingsWindowOpen;
		private bool _isSiteExplorerOpen;
		private bool _isDevToolsWindowOpen;

		/// <summary>Whether the Map Controls window (basemap style + state isolation) is open.</summary>
		public bool IsMapControlsWindowOpen
		{
			get => _isMapControlsWindowOpen;
			set => SetProperty(ref _isMapControlsWindowOpen, value);
		}

		/// <summary>Whether the App Settings window is open. Opening it freshens the radar-cache size readout
		/// in its Storage section (the value is only interesting while the window is up).</summary>
		public bool IsSettingsWindowOpen
		{
			get => _isSettingsWindowOpen;
			set
			{
				if (SetProperty(ref _isSettingsWindowOpen, value) && value)
				{
					_ = Storage.RefreshCacheSizeAsync();
				}
			}
		}

		/// <summary>Whether the Radar Site Explorer window is open (toggled by the "Sites" button).</summary>
		public bool IsSiteExplorerOpen
		{
			get => _isSiteExplorerOpen;
			set => SetProperty(ref _isSiteExplorerOpen, value);
		}

		/// <summary>Whether the DEV-ONLY dev tools window (site sweep + dealias validation) is open. The bar
		/// button that drives it is collapsed in Release, and the window is only registered when the dev VMs
		/// exist, so this stays false in a shipped build.</summary>
		public bool IsDevToolsWindowOpen
		{
			get => _isDevToolsWindowOpen;
			set => SetProperty(ref _isDevToolsWindowOpen, value);
		}

		public IReadOnlyList<MapStyle> AvailableStyles { get; }

		/// <summary>The region the main, full-window map is framed on.</summary>
		public MapRegion? MainRegion => _mainRegion;

		// NOTE: the old left/right tool-window docks (and the abandoned drag-dock direction) are gone;
		// the UI is now the bottom OverlayBar (Controls/OverlayBar) + section controls. The bar's
		// show/hide state is pure view state on the control, not here.



		/// <summary>"Fit to view": frames the current region of interest (the isolated state, else CONUS)
		/// into view. Invoked by the Map Controls window's Fit-to-view button. No-op until the map is ready.</summary>
		public Task FitToViewAsync() => _isMapReady ? _mapService.FitMapToViewAsync() : Task.CompletedTask;

		/// <summary>"Reset north": animates the map's bearing and pitch back to 0 (north up, flat), undoing a
		/// right-click-drag rotate/tilt. Invoked by the Map Controls window's Reset-north button. No-op until
		/// the map is ready.</summary>
		public Task ResetOrientationAsync() => _isMapReady ? _mapService.ResetMapOrientationAsync() : Task.CompletedTask;

		public MapStyle? SelectedStyle
		{
			get => _selectedStyle;
			set
			{
				if (ReferenceEquals(_selectedStyle, value))
				{
					return;
				}

				_selectedStyle = value;
				OnPropertyChanged();

				// Only push a style change once the map can receive it. Pre-ready
				// selections are stored and applied later by OnMapsReadyAsync.
				if (_isMapReady && value is not null)
				{
					_ = _mapService.ApplyStyleAsync(value);
				}
			}
		}



		/// <summary>
		/// Called by the view once the map page has fired its 'load' event. Enables live
		/// style switching and shows the initially-selected outlook.
		/// </summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;

			// The page loaded the selected style via its URL; re-apply it to pick up any
			// change made before the map was ready (idempotent when unchanged).
			if (_selectedStyle is not null)
			{
				await _mapService.ApplyStyleAsync(_selectedStyle);
			}

			// Hand off subsystem startup: outlook (startup overlay + progress), watches (source + toggle),
			// and radar (site markers, offline-status loop, radar progress bar). Markers has no startup
			// work — just flip its readiness so user-driven locate/marker pushes are allowed.
			Markers.SetMapReady();
			await Outlook.OnMapsReadyAsync();
			await Watches.OnMapsReadyAsync();
			await Warnings.OnMapsReadyAsync();
			await Radar.OnMapsReadyAsync();
			await PastOutlook.OnMapsReadyAsync();
			await StormReports.OnMapsReadyAsync();
			await StateIso.OnMapsReadyAsync();
			await MileGrid.OnMapsReadyAsync();
		}
	}
}
