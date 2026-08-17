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
	/// <summary>Which temporal feature's settings card is floating above the overlay bar (opened by a
	/// split-toggle's cog). At most one is open at a time; <see cref="None"/> = no card showing.</summary>
	public enum TemporalCard { None, Past, Now, Fore }

	/// <summary>Which app-wide, RIGHT-aligned card is floating above the bar (Map Controls, App Settings, or
	/// the Radar Site Explorer). At most one open at a time; <see cref="None"/> = none showing. The right-side
	/// mirror of <see cref="TemporalCard"/> — the two sides are INDEPENDENT, so at most one left + one right
	/// card can be open together.</summary>
	public enum RightPanel { None, MapControls, Settings, SiteExplorer }

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

			// Built before the _rightPanels callback below references it (that callback refreshes the cache
			// readout when the Settings panel opens).
			Storage = new StorageSettingsViewModel(radarService, settingsService);

			// The two one-at-a-time card groups (temporal settings cards + right-side panels). Each fires the
			// enum property + all its bool projections on a change, so the x:Bind card/toggle bindings update.
			_cards = new ExclusiveOpen<TemporalCard>(TemporalCard.None, () =>
			{
				OnPropertyChanged(nameof(OpenCard));
				OnPropertyChanged(nameof(IsPastCardOpen));
				OnPropertyChanged(nameof(IsNowCardOpen));
				OnPropertyChanged(nameof(IsForeCardOpen));
			});
			_rightPanels = new ExclusiveOpen<RightPanel>(RightPanel.None, () =>
			{
				OnPropertyChanged(nameof(OpenRightPanel));
				OnPropertyChanged(nameof(IsMapControlsCardOpen));
				OnPropertyChanged(nameof(IsSettingsCardOpen));
				OnPropertyChanged(nameof(IsSiteExplorerOpen));
				// Freshen the App Settings cache readout each time that card becomes the open right panel.
				// (_rightPanels + Storage are both set in this ctor; this callback only fires at runtime,
				// well after that — the ! silences the mid-assignment false positive on _rightPanels.)
				if (_rightPanels!.Current == RightPanel.Settings) { _ = Storage.RefreshCacheSizeAsync(); }
			});

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
			// them honest: re-raise them — and close a now-inactive feature's settings card — whenever the
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
					CloseCardIfInactive();
				}
				else if (e.PropertyName == nameof(RadarViewModel.HasRadarLoop))
				{
					// A live loop starting (e.g. an on-map site-marker click) arms NowCast so it reflects reality.
					if (Radar.HasRadarLoop && !Radar.IsPastEventMode && !_isNowCast)
					{
						_isNowCast = true;
						OnPropertyChanged(nameof(IsNowCast));
						CloseCardIfInactive();
					}
				}
			};
			Outlook.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(OutlookViewModel.IsOutlookVisible))
				{
					OnPropertyChanged(nameof(IsForeCast));
					CloseCardIfInactive();
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
		/// map overlay (top of the stack); surfaced in both the Past and Now cards.</summary>
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
		/// Surfaced in the Map Controls card.</summary>
		public MileGridViewModel MileGrid { get; }

		/// <summary>The App Settings "Storage" section VM (radar cache size readout + Clear + the persisted
		/// size cap). The settings service's first real consumer.</summary>
		public StorageSettingsViewModel Storage { get; }

		// PIPELINE CONSOLE (dev/diagnostic — safe to remove as a unit).
		/// <summary>The Pipeline Console: a read-only glass-cockpit over the Level-2 build pipeline (a
		/// mini-scrubber per product + VWP/storm-motion state). Ships behind a non-obvious toggle in the App
		/// Settings card; polls the WebView only while open.</summary>
		public PipelineConsoleViewModel PipelineConsole { get; }

		private bool _isPipelineConsoleOpen;
		private bool _isPipelineConsoleOnTop = true;

		/// <summary>Whether the Pipeline Console window stays above the main window (topmost). User-toggled from
		/// the pin in the console's title-bar area; <c>CardWindowManager</c> applies it to that window's
		/// presenter. Defaults on, so a single-monitor user can watch it while working the map.</summary>
		public bool IsPipelineConsoleOnTop
		{
			get => _isPipelineConsoleOnTop;
			set => SetProperty(ref _isPipelineConsoleOnTop, value);
		}

		// Per-window always-on-top flags (topmost), each driven by that window's title-bar pin and applied to
		// its presenter by CardWindowManager. Default off (normal window ordering) — pin to float over the map.
		private bool _isSettingsCardOnTop;
		private bool _isMapControlsCardOnTop;
		private bool _isSiteExplorerOnTop;
		private bool _isTimelineOnTop;

		/// <summary>Whether the App Settings window stays on top (title-bar pin).</summary>
		public bool IsSettingsCardOnTop
		{
			get => _isSettingsCardOnTop;
			set => SetProperty(ref _isSettingsCardOnTop, value);
		}

		/// <summary>Whether the Map Controls window stays on top (title-bar pin).</summary>
		public bool IsMapControlsCardOnTop
		{
			get => _isMapControlsCardOnTop;
			set => SetProperty(ref _isMapControlsCardOnTop, value);
		}

		/// <summary>Whether the Radar Sites window stays on top (title-bar pin).</summary>
		public bool IsSiteExplorerOnTop
		{
			get => _isSiteExplorerOnTop;
			set => SetProperty(ref _isSiteExplorerOnTop, value);
		}

		/// <summary>Whether the Timeline window stays on top (title-bar pin).</summary>
		public bool IsTimelineOnTop
		{
			get => _isTimelineOnTop;
			set => SetProperty(ref _isTimelineOnTop, value);
		}
		/// <summary>Whether the Pipeline Console card is showing. INDEPENDENT of the other cards (it may float
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
		// toggle also OPENS its settings card (the setters set OpenCard); OpenCard tracks which feature's
		// settings card floats above the bar (one at a time); a feature turning off closes its card
		// (CloseCardIfInactive). Backed by the shared single-open group; see the ctor for the wiring.
		private readonly ExclusiveOpen<TemporalCard> _cards;

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
				if (value) { OpenCard = TemporalCard.Past; } // user turned PastCast on → open its card (off closes via CloseCardIfInactive)
			}
		}

		// NowCast is an ARMED toggle (a stored flag), NOT a pure projection: "live radar mode is on." Unlike
		// PastCast (Radar.IsPastEventMode) and ForeCast (Outlook.IsOutlookVisible) — both genuine persistent
		// states — "live mode" has no subsystem flag (it's just "not replay", the default), so a projection
		// off loop-existence would snap back + DISABLE the cog whenever no site is loaded. Storing it lets
		// the toggle/cog stay on with a blank-but-armed radar. Default OFF (nothing is armed at launch — a
		// clean map); clicking a site marker starts a live loop, which arms it via the Radar subscription
		// (that path writes this FIELD directly, bypassing the setter, so it does NOT auto-open the Now card).
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
					OpenCard = TemporalCard.Now; // user turned NowCast on → open its card (marker-click arming bypasses this setter)
				}
				else if (!Radar.IsPastEventMode)
				{
					Radar.SelectedRadarOption = Radar.RadarOptions[0]; // "None" → clear the live loop to a blank basemap
				}
				CloseCardIfInactive();
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
				if (value) { OpenCard = TemporalCard.Fore; } // user turned ForeCast on → open its card (off closes via CloseCardIfInactive)
			}
		}

		// Closes an open settings card whose feature just turned off. Called from the toggle setters'
		// subsystem subscriptions so a card can't linger over an inactive feature.
		private void CloseCardIfInactive()
		{
			if ((_cards.Current == TemporalCard.Past && !IsPastCast)
				|| (_cards.Current == TemporalCard.Now && !IsNowCast)
				|| (_cards.Current == TemporalCard.Fore && !IsForeCast))
			{
				OpenCard = TemporalCard.None;
			}
		}

		/// <summary>Handles a click on a temporal mode toggle. The cog is gone — the mode's single toggle
		/// both activates the feature AND governs its settings card:
		/// <list type="bullet">
		/// <item>mode OFF → turn it on (the setter opens its card);</item>
		/// <item>mode ON → flip its card open/closed. The mode STAYS active — you leave a mode by choosing
		/// another one (Past excludes Now/Fore; Now and Fore coexist), not by clicking its own toggle.</item>
		/// </list>
		/// A card-only flip doesn't change the mode projection, so we re-raise it to re-assert the toggle's
		/// lit state (bound OneWay to the projection) after the ToggleButton flipped its own IsChecked on the
		/// click.</summary>
		public void ToggleTemporalMode(TemporalCard which)
		{
			bool active = which switch
			{
				TemporalCard.Past => IsPastCast,
				TemporalCard.Now => IsNowCast,
				TemporalCard.Fore => IsForeCast,
				_ => false,
			};

			if (!active)
			{
				switch (which) // the setter turns the mode on and opens its card
				{
					case TemporalCard.Past: IsPastCast = true; break;
					case TemporalCard.Now: IsNowCast = true; break;
					case TemporalCard.Fore: IsForeCast = true; break;
				}
				return;
			}

			// Already active → flip the card only; the mode stays on.
			OpenCard = _cards.Current == which ? TemporalCard.None : which;
			OnPropertyChanged(which switch // projection unchanged → re-assert the toggle's lit state
			{
				TemporalCard.Past => nameof(IsPastCast),
				TemporalCard.Now => nameof(IsNowCast),
				TemporalCard.Fore => nameof(IsForeCast),
				_ => string.Empty,
			});
		}

		/// <summary>Which feature's settings card is showing above the bar (at most one). Opened by a
		/// split-toggle's cog, hidden by the card's own down-triangle. Independent of whether the feature is
		/// on for reading, but a cog can only set it while its feature is active (cogs are disabled otherwise),
		/// and a mode change resets it to <see cref="TemporalCard.None"/>.</summary>
		public TemporalCard OpenCard
		{
			get => _cards.Current;
			set => _cards.Current = value;
		}

		/// <summary>PastCast settings-card visibility (two-way: the cog toggles it; the card's triangle
		/// clears it). Setting false only closes it when it was the one open.</summary>
		public bool IsPastCardOpen
		{
			get => _cards.IsOpen(TemporalCard.Past);
			set => _cards.SetOpen(TemporalCard.Past, value);
		}

		/// <summary>NowCast settings-card visibility (placeholder card for now).</summary>
		public bool IsNowCardOpen
		{
			get => _cards.IsOpen(TemporalCard.Now);
			set => _cards.SetOpen(TemporalCard.Now, value);
		}

		/// <summary>ForeCast settings-card visibility.</summary>
		public bool IsForeCardOpen
		{
			get => _cards.IsOpen(TemporalCard.Fore);
			set => _cards.SetOpen(TemporalCard.Fore, value);
		}

		// ===== App-wide RIGHT-aligned cards (Map Controls / App Settings / Site Explorer) =================
		// The right-side mirror of the temporal cards, and — like them — a one-at-a-time group, so opening
		// one closes whichever right card was open (a second button can't stack on top). Modeled exactly like
		// OpenCard: a single RightPanel source of truth + three bool PROJECTIONS, so the existing button
		// (IsChecked TwoWay) and card (Visibility) bindings keep working unchanged. Deliberately INDEPENDENT
		// of the temporal OpenCard: switching temporal modes never closes a right card and vice-versa, so at
		// most one LEFT + one RIGHT card show together. (The Debug-only dev Sweep/Validate cards are NOT part
		// of this group — their open-state is a control-level DP, see MainWindow.)
		private readonly ExclusiveOpen<RightPanel> _rightPanels;

		/// <summary>Which app-wide right-aligned card is floating above the bar (at most one). Opened by its
		/// right-edge button, hidden by the card's own down-triangle. Independent of <see cref="OpenCard"/>.</summary>
		public RightPanel OpenRightPanel
		{
			get => _rightPanels.Current;
			set => _rightPanels.Current = value;
		}

		/// <summary>Whether the Map Controls card (basemap style + state isolation) floats above the bar.
		/// Two-way: the "Map" button toggles it; the card's down-triangle clears it. Opening it closes any
		/// other right card.</summary>
		public bool IsMapControlsCardOpen
		{
			get => _rightPanels.IsOpen(RightPanel.MapControls);
			set => _rightPanels.SetOpen(RightPanel.MapControls, value);
		}

		/// <summary>Whether the app-wide settings card floats above the bar. Two-way: the settings cog toggles
		/// it; the card's down-triangle clears it. Opening it closes any other right card.</summary>
		public bool IsSettingsCardOpen
		{
			get => _rightPanels.IsOpen(RightPanel.Settings);
			set => _rightPanels.SetOpen(RightPanel.Settings, value);
		}

		/// <summary>Whether the Radar Site Explorer panel is showing (toggled by the "Sites" button). Opening
		/// it closes any other right card.</summary>
		public bool IsSiteExplorerOpen
		{
			get => _rightPanels.IsOpen(RightPanel.SiteExplorer);
			set => _rightPanels.SetOpen(RightPanel.SiteExplorer, value);
		}

		public IReadOnlyList<MapStyle> AvailableStyles { get; }

		/// <summary>The region the main, full-window map is framed on.</summary>
		public MapRegion? MainRegion => _mainRegion;

		// NOTE: the old left/right tool-window docks (and the abandoned drag-dock direction) are gone;
		// the UI is now the bottom OverlayBar (Controls/OverlayBar) + section controls. The bar's
		// show/hide state is pure view state on the control, not here.



		/// <summary>"Fit to view": frames the current region of interest (the isolated state, else CONUS)
		/// into view. Invoked by the Map Controls card's Fit-to-view button. No-op until the map is ready.</summary>
		public Task FitToViewAsync() => _isMapReady ? _mapService.FitMapToViewAsync() : Task.CompletedTask;

		/// <summary>"Reset north": animates the map's bearing and pitch back to 0 (north up, flat), undoing a
		/// right-click-drag rotate/tilt. Invoked by the Map Controls card's Reset-north button. No-op until
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
