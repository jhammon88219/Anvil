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
	/// the toggle for <see cref="MapViewModel.ToggleTemporalMode"/>, and doubles as the tab ordinal in the
	/// Timeframe window (<see cref="MapViewModel.TemporalTabIndex"/>) — the three modes and its three tabs
	/// are the same three things. It is NOT an open-state; the panel has its own
	/// <see cref="MapViewModel.IsTemporalWindowOpen"/> flag, owned by its own bar key.</summary>
	public enum TemporalMode { Past, Now, Fore }

	/// <summary>Which tab the Settings window shows. Identifies a tab for <see cref="MapViewModel.OpenSettings"/>
	/// and nothing else — the window's own strip holds the display order and the labels. ⚠️ The ordinal IS the
	/// persisted index, so REORDERING these silently moves a saved tab choice; only ever append.
	/// <c>Dev</c> exists in every build but is only reachable in Debug (the strip omits its entry, and the
	/// window never loads its body, in Release).</summary>
	public enum SettingsTab { Map, Radar, Storage, Dev }

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
		private readonly ISettingsService _settingsService;

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
			_settingsService = settingsService;

			// Built before IsSettingsWindowOpen can fire (its setter refreshes the cache readout on open).
			Storage = new StorageSettingsViewModel(radarService, settingsService);

			// Restore the last Settings tab. Assigned through the PROPERTY so the clamp runs — a persisted
			// index can name a tab this build does not have (a Debug session quitting on the dev tab).
			SettingsTabIndex = settingsService.Settings.SettingsTabIndex;

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
			// them honest: re-raise them whenever the radar mode/loop or the outlook overlay changes,
			// including changes NOT driven by the toggles (e.g. clicking an on-map radar site marker starts a
			// live loop, which should light NowCast).
			// ⚠️ Nothing here touches the Timeframe panel's OPEN state. A mode turning off no longer closes a
			// window — the panel is owned by its own bar key (IsTemporalWindowOpen), which is the whole point
			// of the split. Aiming its TAB is fair game: a mode that switches on elsewhere should be the tab
			// you land on next time the panel opens.
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
					OnTemporalModesChanged();
				}
				else if (e.PropertyName == nameof(RadarViewModel.HasRadarLoop))
				{
					// A live loop starting (e.g. an on-map site-marker click) arms NowCast so it reflects reality.
					// ⚠️ This path writes the FIELD, so it does not open the panel — clicking a site marker
					// should not throw a window over the map. It aims the tab instead, so the Timeframe key
					// (which is sitting right there, unlit) opens on NowCast when the user does want it.
					if (Radar.HasRadarLoop && !Radar.IsPastEventMode && !_isNowCast)
					{
						_isNowCast = true;
						OnPropertyChanged(nameof(IsNowCast));
						TemporalTabIndex = (int)TemporalMode.Now;
						OnTemporalModesChanged();
					}
				}
			};
			Outlook.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(OutlookViewModel.IsOutlookVisible))
				{
					OnPropertyChanged(nameof(IsForeCast));
					OnTemporalModesChanged();
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
		/// mini-scrubber per product + VWP/storm-motion state). Opened by a switch in the DEV TOOLS window
		/// (it was App Settings → Advanced until that window was pared back to app behavior only); polls the
		/// WebView only while open. ⚠️ Dev Tools is Debug-only, so nothing opens this in a Release build even
		/// though the window registration itself is not gated.</summary>
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
		private bool _isSiteExplorerOnTop = true;

		/// <summary>Whether the Settings window stays on top (title-bar pin).</summary>
		public bool IsSettingsWindowOnTop
		{
			get => _isSettingsWindowOnTop;
			set => SetProperty(ref _isSettingsWindowOnTop, value);
		}

		/// <summary>Whether the Radar Sites window stays on top (title-bar pin).</summary>
		public bool IsSiteExplorerOnTop
		{
			get => _isSiteExplorerOnTop;
			set => SetProperty(ref _isSiteExplorerOnTop, value);
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
		// toggle also OPENS the Timeframe panel on that mode's tab, so the common path is still one click.
		//
		// ⚠️ A TOGGLE ONLY TOGGLES ITS MODE. Clicking a LIT key turns that mode OFF — the one behaviour every
		// user already expects from a lit toggle. It does NOT govern a window.
		// HISTORY, do not undo: these keys used to mean two things at once — "turn the mode on" when unlit,
		// "flip my settings window" when lit. The window's open state was invisible on the key, so the same
		// click did two different things depending on state the user could not see: arm NowCast by clicking a
		// site marker, or close the ForeCast window, and the only way back to the panel was to click a key
		// that looked like it would turn the feature off. Painting the second state onto the key was
		// considered and rejected as still needing to be LEARNED. The fix was to stop overloading: the panel
		// moved to its OWN key on the bar's right edge (IsTemporalWindowOpen), where the keys are already
		// honest window latches, and these three went back to being plain mode switches. Two families of key,
		// each internally consistent: CENTRE = what is on the map, RIGHT = what is on the screen.
		// The three panels became TABS of that one window (see TemporalTabIndex) — the same move the Settings
		// window made when it absorbed App Settings / Map Controls / Dev Tools. The cost, accepted: Now and
		// Fore can no longer be parked on two monitors at once. Past excludes the other two anyway, so at
		// most two tabs are ever live, and the strip doubles as a readout of which modes are on.

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
				if (value) { OpenTemporal(TemporalMode.Past); } // on → show its controls; off leaves the panel alone
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
					OpenTemporal(TemporalMode.Now); // on → show its controls (marker-click arming bypasses this setter)
				}
				else if (!Radar.IsPastEventMode)
				{
					Radar.SelectedRadarOption = Radar.RadarOptions[0]; // "None" → clear the live loop to a blank basemap
				}
				// NowCast is the one mode with no subsystem flag behind it (see the field note above), so this
				// setter is the only place its change surfaces — the other two are re-raised from their
				// subsystem subscriptions, which call this for us.
				OnTemporalModesChanged();
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
				if (value) { OpenTemporal(TemporalMode.Fore); } // on → show its controls; off leaves the panel alone
			}
		}

		/// <summary>Handles a click on a temporal mode toggle: it TOGGLES THAT MODE, and nothing else. Unlit →
		/// on (which also opens the Timeframe panel on that mode's tab); lit → off. The panel's own key is
		/// what shows and hides the panel — see the ⚠️ history note at the top of this region before making
		/// this key mean two things again.</summary>
		/// <remarks>
		/// The lit state is bound OneWay to the mode PROJECTION, and a ToggleButton has already flipped its own
		/// IsChecked by the time this runs — so if a subsystem declines the change (nothing to project onto),
		/// the binding would not push back and the key would be left lying. The re-raise at the end re-asserts
		/// it either way; it is cheap, and it is the same trap TabStrip and the pane-layout key documented.
		/// </remarks>
		public void ToggleTemporalMode(TemporalMode which)
		{
			switch (which)
			{
				case TemporalMode.Past: IsPastCast = !IsPastCast; break;
				case TemporalMode.Now: IsNowCast = !IsNowCast; break;
				case TemporalMode.Fore: IsForeCast = !IsForeCast; break;
			}

			OnPropertyChanged(which switch
			{
				TemporalMode.Past => nameof(IsPastCast),
				TemporalMode.Now => nameof(IsNowCast),
				TemporalMode.Fore => nameof(IsForeCast),
				_ => string.Empty,
			});
		}

		// ===== The Timeframe panel (ONE window, three tabs) ===============================================
		// The three temporal features' settings used to be three windows opened by their three mode keys.
		// They are now TABS of one window with its own key on the bar's right edge, so that a mode key can go
		// back to being a plain toggle (see the region header above for why).
		//
		// ⚠️ The panel's lifetime belongs to ITS key alone. Nothing in the mode setters closes it: turn every
		// mode off and the panel stays up, showing the controls of a mode that is not running — which is
		// exactly what you want when setting up a past event before loading it. Only the key (and the
		// window's caption Close, which flips the same flag) puts it away.

		private bool _isTemporalWindowOpen;
		private bool _isTemporalWindowOnTop = true;
		// Defaults to NowCast: nothing is armed at launch, so EVERY tab is greyed and the panel shows its
		// empty state — the index only decides which body appears the moment a mode starts, and NowCast is
		// the one the app is usually reaching for (a site-marker click arms it).
		private int _temporalTabIndex = (int)TemporalMode.Now;

		/// <summary>Whether the Timeframe panel is open. Two-ways with its bar key, like every other window
		/// key on the bar's right edge.</summary>
		public bool IsTemporalWindowOpen
		{
			get => _isTemporalWindowOpen;
			set => SetProperty(ref _isTemporalWindowOpen, value);
		}

		/// <summary>Whether the Timeframe panel stays on top (title-bar pin).</summary>
		public bool IsTemporalWindowOnTop
		{
			get => _isTemporalWindowOnTop;
			set => SetProperty(ref _isTemporalWindowOnTop, value);
		}

		/// <summary>How many tabs the Timeframe panel has — one per <see cref="TemporalMode"/>.</summary>
		public const int TemporalTabCount = 3;

		/// <summary>
		/// Which tab the Timeframe panel shows, as an index into <see cref="TemporalMode"/>. Deliberately NOT
		/// persisted (unlike the Settings window's tab): it tracks whichever mode was most recently turned on,
		/// and a fresh launch has no mode on at all. Clamped, so no caller can select a tab that isn't there.
		/// </summary>
		public int TemporalTabIndex
		{
			get => _temporalTabIndex;
			set => SetProperty(ref _temporalTabIndex, Math.Clamp(value, 0, TemporalTabCount - 1));
		}

		// ===== Tab availability — A TAB IS LIVE ONLY WHILE ITS MODE IS ======================================
		// One rule, applied uniformly: a tab configures a RUNNING mode, so it is greyed whenever that mode is
		// off. Nothing in these bodies starts anything — the date/start/window fields drive a replay that is
		// already armed (Load only fills it with frames), the watch/warning toggles overlay a live loop, the
		// day/product selectors pick which outlook is drawn. With the mode off there is nothing to act on.
		// ⚠️ You start a mode from its KEY, never from its tab. That is the whole two-families split: the
		// centre keys run what is on the MAP, this panel configures whatever is already running.
		// The exclusions then fall out for free — Past excludes Now + Fore, so a replay greys BOTH of their
		// tabs without a rule of its own, and the strip becomes an honest picture of what is running.
		// ⚠️ ALL THREE CAN BE OFF AT ONCE (the launch state), and then every tab is greyed — which is why the
		// window has an EMPTY STATE keyed off HasActiveTemporalMode. Don't "fix" that by force-enabling the
		// selected tab; nothing is running, and the panel should say so rather than offer dead controls.

		/// <summary>Whether the PastCast tab can be picked — only while a replay is up.</summary>
		public bool IsPastTabEnabled => IsPastCast;

		/// <summary>Whether the NowCast tab can be picked — only while live mode is armed (so never during a
		/// PastCast replay, which excludes it).</summary>
		public bool IsNowTabEnabled => IsNowCast;

		/// <summary>Whether the ForeCast tab can be picked — only while the outlook overlay is on (so never
		/// during a PastCast replay, which excludes it).</summary>
		public bool IsForeTabEnabled => IsForeCast;

		/// <summary>Whether ANY temporal mode is running. False = every tab is greyed and the Timeframe panel
		/// shows its empty state instead of a body.</summary>
		public bool HasActiveTemporalMode => IsPastCast || IsNowCast || IsForeCast;

		/// <summary>
		/// Re-raise everything that depends on which modes are running, and keep the panel off a tab that has
		/// just gone dead. Called from every place a mode can change: the Radar subscriptions (Past + the
		/// marker-click arming of Now), the Outlook subscription (Fore) and the <see cref="IsNowCast"/> setter.
		/// </summary>
		private void OnTemporalModesChanged()
		{
			OnPropertyChanged(nameof(IsPastTabEnabled));
			OnPropertyChanged(nameof(IsNowTabEnabled));
			OnPropertyChanged(nameof(IsForeTabEnabled));
			OnPropertyChanged(nameof(HasActiveTemporalMode));

			// If the selected tab's mode just stopped, fall to one that is still running. With NOTHING running
			// the index is left where it is — the empty state covers the body, and whichever mode starts next
			// selects its own tab anyway.
			if (IsTemporalTabEnabled(_temporalTabIndex)) { return; }
			if (IsPastCast) { TemporalTabIndex = (int)TemporalMode.Past; }
			else if (IsNowCast) { TemporalTabIndex = (int)TemporalMode.Now; }
			else if (IsForeCast) { TemporalTabIndex = (int)TemporalMode.Fore; }
		}

		private bool IsTemporalTabEnabled(int index) => index switch
		{
			(int)TemporalMode.Past => IsPastTabEnabled,
			(int)TemporalMode.Now => IsNowTabEnabled,
			(int)TemporalMode.Fore => IsForeTabEnabled,
			_ => false,
		};

		/// <summary>Open the Timeframe panel on a specific mode's tab (opening it if it is closed). The one
		/// entry point for "show me the controls for that timeframe" — used by the mode setters, and the
		/// mirror of <see cref="OpenSettings"/>.</summary>
		public void OpenTemporal(TemporalMode which)
		{
			TemporalTabIndex = (int)which;
			IsTemporalWindowOpen = true;
		}

		// ===== App-wide windows (Settings / Site Explorer) ================================================
		// Same model as the temporal windows above: one independent bool per window, opened by its key on the
		// bar's right edge and closed by the window's caption Close. No one-at-a-time grouping — these are real
		// OS windows, so any combination may be open at once.
		// ⚠️ There used to be FOUR keys here. Map Controls and the dev tools are no longer windows of their own
		// — they are TABS of the Settings window (see SettingsTabIndex below), which is why the bar's right
		// cluster is down to Panes / Sites / Settings. A new group of settings is a tab, not a window.
		private bool _isSettingsWindowOpen;
		private bool _isSiteExplorerOpen;

		/// <summary>Whether the Settings window is open. Opening it freshens whatever the landing tab shows
		/// live (today: the Storage tab's radar-cache size).</summary>
		public bool IsSettingsWindowOpen
		{
			get => _isSettingsWindowOpen;
			set
			{
				if (SetProperty(ref _isSettingsWindowOpen, value) && value)
				{
					RefreshForSettingsTab();
				}
			}
		}

		/// <summary>Whether the Radar Site Explorer window is open (toggled by the "Sites" button).</summary>
		public bool IsSiteExplorerOpen
		{
			get => _isSiteExplorerOpen;
			set => SetProperty(ref _isSiteExplorerOpen, value);
		}

		// ===== Settings window tabs =======================================================================
		// The Settings window is one window with a tab strip, not the three windows (App Settings / Map
		// Controls / Dev Tools) it replaced. The SELECTED TAB lives here rather than as view state on the
		// window so it survives the window being closed and reopened, persists across launches, and can be
		// targeted from anywhere via OpenSettings().

		/// <summary>How many tabs the strip actually offers. Debug builds add the dev tab; Release stops at
		/// Storage. The clamp in <see cref="SettingsTabIndex"/> is what keeps a persisted Debug index from
		/// selecting a tab that does not exist in a shipped build.</summary>
#if DEBUG
		public const int SettingsTabCount = 4;
#else
		public const int SettingsTabCount = 3;
#endif

		private int _settingsTabIndex;

		/// <summary>
		/// Which tab the Settings window shows, as an index into its strip (see <see cref="SettingsTab"/>).
		/// PERSISTED, and CLAMPED on the way in — the stored value can name a tab this build does not have
		/// (quit Debug on the dev tab, run Release), and an out-of-range index would select nothing at all.
		/// </summary>
		public int SettingsTabIndex
		{
			get => _settingsTabIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, SettingsTabCount - 1);
				if (SetProperty(ref _settingsTabIndex, clamped))
				{
					_settingsService.Settings.SettingsTabIndex = clamped;
					RefreshForSettingsTab();
				}
			}
		}

		/// <summary>Where the Settings window draws its tab strip: <c>"Top"</c> (default) or <c>"Left"</c>.
		/// PERSISTED. Chosen from the strip's own right-click menu, since a control that configures the
		/// settings window is odd content for any one settings tab.
		/// ⚠️ Deliberately a STRING here, not an enum: "where a control draws its strip" is a VIEW concept, and
		/// the enum it parses to (<c>Anvil.Controls.Primitives.TabPlacement</c>) lives with the control in
		/// Anvil.App. Core stores the choice; it does not model tab strips.</summary>
		public string SettingsTabPlacement
		{
			get => _settingsService.Settings.SettingsTabPlacement;
			set
			{
				if (string.Equals(value, SettingsTabPlacement, StringComparison.Ordinal)) return;
				_settingsService.Settings.SettingsTabPlacement = value;
				OnPropertyChanged();
			}
		}

		/// <summary>Open the Settings window on a specific tab (opening it if it is closed). The one entry
		/// point for "take me to that setting" from anywhere in the app.</summary>
		public void OpenSettings(SettingsTab tab)
		{
			SettingsTabIndex = (int)tab;
			IsSettingsWindowOpen = true;
		}

		/// <summary>Refresh whatever the current tab shows live. Called when the window opens AND when the tab
		/// changes, because either one can be the moment a live readout first becomes visible. ⚠️ Keep this
		/// cheap and tab-scoped — it runs on every tab click.</summary>
		private void RefreshForSettingsTab()
		{
			if (!_isSettingsWindowOpen) return;
			if (_settingsTabIndex == (int)SettingsTab.Storage)
			{
				_ = Storage.RefreshCacheSizeAsync();
			}
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

		// ── Basemap TILE SOURCE (offline PMTiles vs online tiles) ──────────────────────────────────
		// Orthogonal to SelectedStyle, and deliberately so: the bundled styles are Protomaps-SCHEMA
		// styles whose only tie to the offline file is one source url, so every style renders identically
		// against either source. That is why this is a source switch rather than a second set of styles.
		// Offline is the DEFAULT and the fallback: an online config that can't work (no API key) is
		// ignored rather than obeyed, so the map is never left blank.

		/// <summary>
		/// Stream basemap tiles from <see cref="OnlineTilesUrl"/> instead of the bundled offline PMTiles
		/// file. Persisted; default off. Has no effect while <see cref="CanUseOnlineTiles"/> is false.
		/// </summary>
		public bool UseOnlineTiles
		{
			get => _settingsService.Settings.UseOnlineTiles;
			set
			{
				if (_settingsService.Settings.UseOnlineTiles == value)
				{
					return;
				}

				_settingsService.Settings.UseOnlineTiles = value; // auto-persists
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsOnlineTilesActive));
				OnPropertyChanged(nameof(TileSourceStatus));
				PushTileSource();
			}
		}

		/// <summary>
		/// Where online tiles come from — a Protomaps-schema TileJSON endpoint, a <c>{z}/{x}/{y}</c>
		/// template, or a remote <c>pmtiles://</c> archive. Persisted; defaults to the Protomaps API URL,
		/// which needs an API key appended before it can be used.
		/// </summary>
		public string OnlineTilesUrl
		{
			get => _settingsService.Settings.OnlineTilesUrl;
			set
			{
				var url = (value ?? "").Trim();
				if (_settingsService.Settings.OnlineTilesUrl == url)
				{
					return;
				}

				_settingsService.Settings.OnlineTilesUrl = url; // auto-persists
				OnPropertyChanged();
				OnPropertyChanged(nameof(CanUseOnlineTiles));
				OnPropertyChanged(nameof(IsOnlineTilesActive));
				OnPropertyChanged(nameof(TileSourceStatus));

				// Re-point a live online map at the new URL; while offline there is nothing to push.
				if (UseOnlineTiles)
				{
					PushTileSource();
				}
			}
		}

		/// <summary>
		/// Whether <see cref="OnlineTilesUrl"/> is usable. The Protomaps default ships WITHOUT a key (we
		/// can't supply one), and a keyless URL renders nothing — so a bare <c>key=</c> reads as
		/// not-yet-configured rather than being pushed to the map and blanking it.
		/// </summary>
		public bool CanUseOnlineTiles
		{
			get
			{
				var url = OnlineTilesUrl;
				if (string.IsNullOrWhiteSpace(url))
				{
					return false;
				}

				var i = url.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
				if (i < 0)
				{
					return true; // a self-hosted source needs no key
				}

				var after = url[(i + 4)..];
				var end = after.IndexOf('&');
				return (end < 0 ? after : after[..end]).Length > 0;
			}
		}

		/// <summary>The EFFECTIVE tile source: online only when it's both selected and usable. Read by the
		/// view host to frame the launch page on the right source, and by <see cref="PushTileSource"/>.</summary>
		public bool IsOnlineTilesActive => UseOnlineTiles && CanUseOnlineTiles;

		/// <summary>One-line readout under the tile-source controls — which source is actually in use, and
		/// why, when the selection and the effective source disagree.</summary>
		public string TileSourceStatus =>
			!UseOnlineTiles ? "Offline — the bundled PMTiles basemap. No network needed." :
			!CanUseOnlineTiles ? "Add your API key to the URL above; still using the offline basemap." :
			"Online — tiles stream from the URL above. Same styles, same look.";

		// Push the effective source to the page. Pre-ready changes need no push: the page is launched with
		// the source in its URL (MainWindow.BuildMapUrl), so it builds the first map on the right one
		// instead of flipping — and re-applying here would cost a needless setStyle + overlay re-add.
		private void PushTileSource()
		{
			if (_isMapReady)
			{
				_ = _mapService.SetTileSourceAsync(IsOnlineTilesActive, OnlineTilesUrl);
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
