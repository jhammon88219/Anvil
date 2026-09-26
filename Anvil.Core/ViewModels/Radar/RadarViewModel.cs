using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Freshness of a radar site's newest frame, driving the card's status dot.</summary>
	public enum RadarFreshness
	{
		/// <summary>No loop / not yet ready.</summary>
		None,
		/// <summary>Newest frame within ~12 min — current.</summary>
		Live,
		/// <summary>~12–30 min old.</summary>
		Recent,
		/// <summary>Over ~30 min old — the site looks stale/offline.</summary>
		Stale
	}

	/// <summary>
	/// View model for the radar subsystem: site selection + on-map markers, the animation loop
	/// (live + past-event replay), the near-real-time live frame, DOW frames, the radar card,
	/// color-scale legend, and the inspector. Extracted from MapViewModel; the transport-bar
	/// section controls bind slices of this. Drives the map through <see cref="IMapService"/>.
	/// </summary>
	public sealed partial class RadarViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IRadarSiteProvider _radarSiteProvider;
		private readonly ILevel2RadarService _radarService;
		private readonly ISettingsService _settings;   // persisted app settings (the three site-marker toggles, …)

		// The loop engine (site load, live poll, playback, refresh, incremental reload, past-event replay).
		// Extracted into its own collaborator class (RadarLoopEngine.cs); this VM owns the bindable
		// frame-state + presentation and delegates the loop lifecycle to it. Built in the ctor.
		private readonly RadarLoopEngine _engine;

		// Bridges the nested engine to the base ObservableObject's protected OnPropertyChanged.
		private void RaisePropertyChangedFor(string propertyName) => OnPropertyChanged(propertyName);

		// Appends one free-form radar diagnostic note (incidental lines; high-value events use the typed
		// RadarDiagnostics methods). Mirrors the engine's own Diag helper for the VM-side callers.
		private static void Diag(string message) => Services.RadarDiagnostics.Log("vm", "note", ("msg", message));

		// ── Public loop API — thin forwarders onto the engine (the loop logic lives in RadarLoopEngine).
		//    Kept on the VM because the view/router bind these names (WebMessageRouter, PastEventInput). ──

		/// <summary>Called by the view when a radar site marker is clicked (toggles selection).</summary>
		public void OnRadarSiteClicked(string? id) => _engine.OnRadarSiteClicked(id);

		/// <summary>Called by the view when the WebView reports a loop frame finished decoding.</summary>
		public void OnRadarFrameReady(int index, bool hasData) => _engine.OnRadarFrameReady(index, hasData);

		/// <summary>Hard reset of the current loop: cancels the in-flight load and reloads from scratch.</summary>
		public void ResetRadarLoop() => _engine.ResetRadarLoop();

		/// <summary>The bar's refresh button: poll the live (chunks) frame NOW instead of waiting for the timer.
		/// Debounced — ignored while a check is running and until <see cref="ForceLiveCheckCooldown"/> has passed
		/// since the press, so a double-click or a hammered button is one fetch. The button's IsEnabled binds
		/// <see cref="CanForceLiveCheck"/>, which is the visible cooldown.</summary>
		public async Task ForceLiveFrameCheckAsync()
		{
			if (!CanForceLiveCheck) return;
			_forceLiveBusy = true;
			OnPropertyChanged(nameof(CanForceLiveCheck));
			var pressedAt = DateTimeOffset.UtcNow;
			try
			{
				await _engine.ForceLiveFrameCheckAsync();
				var left = ForceLiveCheckCooldown - (DateTimeOffset.UtcNow - pressedAt);
				if (left > TimeSpan.Zero) await Task.Delay(left);
			}
			finally
			{
				_forceLiveBusy = false;
				OnPropertyChanged(nameof(CanForceLiveCheck));
			}
		}

		/// <summary>True when the refresh button can fire: a LIVE site is selected and no check is in its
		/// run-or-cooldown window. Re-raised by RaiseRadarReadout (selection changes + the 1 s tick).</summary>
		public bool CanForceLiveCheck => !_forceLiveBusy && !IsPastEventMode && _selectedRadarOption?.Site is not null;

		private static readonly TimeSpan ForceLiveCheckCooldown = TimeSpan.FromSeconds(2);
		private bool _forceLiveBusy;

		/// <summary>Loads the historical loop for the selected site over the chosen window (the Load button).</summary>
		public Task<bool> LoadSelectedPastEventAsync() => _engine.LoadSelectedPastEventAsync();

		// Readiness guard: radar commands only run once the map page has reported 'mapReady'
		// (set by OnMapsReadyAsync, called from MapViewModel.OnMapsReadyAsync).
		private bool _isMapReady;

		// Selected radar site option ("None" clears the layer) + radar layer opacity.
		private RadarOption? _selectedRadarOption;
		private double _radarOpacity = 0.80;
		private bool _showRadarLayer = true;

		// ShowNexradSites / ShowResearchRadars / ShowTdwrs are PERSISTED — backed by _settings.Settings, not local fields.

		// Radar loop state. The loop is a sequence of recent volumes (newest last).
		// _frameTimes[i] is set as each frame's volume caches; _readyCount tracks how many
		// have decoded in the WebView; _loadedNewestKey detects a new volume on refresh.
		// _loopCts cancels the load + auto-refresh + playback for the current selection.
		// Loop length (frame count) — user-selectable in the Radar Loop tool window via discrete
		// presets (so each change rebuilds the loop at most once). Capped at 30: memory + initial
		// decode cost grow with length, though steady-state stays cheap via the incremental reload.
		private static readonly int[] LoopLengthByIndex = { 6, 10, 15, 20, 25, 30 };
		private int _loopLengthIndex = 1; // default 10 frames
		private int LoopLength => LoopLengthByIndex[Math.Clamp(_loopLengthIndex, 0, LoopLengthByIndex.Length - 1)];
		private DateTimeOffset?[] _frameTimes = Array.Empty<DateTimeOffset?>();
		// Per-frame scan mode ("VCP 212 · precip"), parallel to _frameTimes. Populated for every
		// frame (archive via EnsureCachedAsync, live via the poll) but only SHOWN in replay, where
		// there's no live poll to drive the single _liveModeText. null = unknown for that frame.
		private string?[] _frameModes = Array.Empty<string?>();
		private int _frameCount;
		private int _readyCount;
		private int _currentFrameIndex;
		// The one frame painted first (Rule 1): the newest in NowCast, the oldest in PastCast. It's shown
		// the instant it decodes, exempt from the left-to-right reveal gate (see RefreshSegmentReadiness).
		private int _firstPaintIndex;

		/// <summary>One cell per loop frame for the segmented scrubber; each <see cref="RadarFrameSegment.IsReady"/>
		/// flips true as that frame decodes (see <see cref="OnRadarFrameReady"/>). Rebuilt on every (re)load
		/// via <see cref="RebuildSegments"/>. The scrubber cells + playhead render from this.</summary>
		public System.Collections.ObjectModel.ObservableCollection<RadarFrameSegment> Segments { get; } = new();

		// Rebuilds Segments to `count` cells (a new loop / a remap). `readyFrom` optionally carries the
		// DECODE state to seed each new index (used by the incremental remap to keep reused frames lit);
		// null seeds all not-decoded. Each cell's displayed readiness is then derived for the active
		// product. Runs on the UI thread (load/remap resume there).
		private void RebuildSegments(int count, bool[]? readyFrom = null)
		{
			Segments.Clear();
			for (var i = 0; i < count; i++)
			{
				Segments.Add(new RadarFrameSegment { IsDecoded = readyFrom is { } r && i < r.Length && r[i] });
			}
			RefreshSegmentReadiness();
		}

		// Recomputes every scrubber cell's DISPLAYED readiness (RadarFrameSegment.IsReady).
		//
		// A cell is fill-ready once its reflectivity + velocity are built (IsFrameComplete), regardless of the
		// product on screen — the two products that build PER FRAME during the backfill, so cells light one-by-
		// one as it progresses. SRV is NOT part of this: it rides the loop's ONE storm motion (a single loop-wide
		// event that lands last), so gating on it would flip the whole scrubber at once instead of incrementally;
		// SRV trails per docs/radar-loop-flow.md Rule 4 and doesn't hold the fill. (Playback uses a separate,
		// active-product gate — IsFrameDisplayReady — so browsing reflectivity never stalls on velocity.)
		//
		// docs/radar-loop-flow.md Rule 2 — the scrubber fills LEFT-TO-RIGHT toward completion. The backfill
		// decodes frames in PARALLEL (MaxParallelBackfill), so they finish out of order; lighting each cell the
		// instant it finishes makes the scrubber fill in a jumble. So we compute each cell's fill-readiness, then
		// REVEAL only a contiguous run growing from the left. The exceptions are the frames Rule 1 shows
		// immediately regardless of the backfill: the first-paint frame (newest in NowCast, oldest in PastCast)
		// and the appended live frame. Once the whole loop is built the frontier reaches the end and every cell
		// is revealed, so this only orders the transient fill — nothing stays hidden.
		private void RefreshSegmentReadiness()
		{
			var n = Segments.Count;
			// Contiguous-from-left frontier: the largest index whose cell AND every cell before it is fill-ready.
			// ⚠️ Immediate frames (first-paint, live) must NOT BLOCK the frontier. In PastCast the first-paint
			// frame is index 0 (leftmost) and decodes REFLECTIVITY-ONLY — its velocity backfills LATE (behind the
			// loop's parallel backfill in the shared decode pool). Treating that incomplete duo as a blocker
			// pinned the ENTIRE frontier until the late velocity landed, so the whole scrubber lit AT ONCE
			// instead of left-to-right (Rule 2) — making a slow load look stuck/broken. Skipping immediate frames
			// lets the frames after them reveal as they complete. (Live avoids this only because its refl-only
			// first-paint frame is the RIGHTMOST cell, so it never blocked the left-growing frontier.)
			var frontier = -1;
			for (var i = 0; i < n; i++)
			{
				if (IsFrameFillReady(i) || IsImmediateFrame(i)) frontier = i;
				else break;
			}
			for (var i = 0; i < n; i++)
			{
				// Rule 1: an immediate frame (first-paint / live) is shown the instant it's decoded, so its cell
				// lights on decode — even before its duo completes (it's the frame you're already looking at).
				// Every other cell lights on its duo (refl+velocity) once the left-to-right frontier reaches it.
				Segments[i].IsReady = IsImmediateFrame(i)
					? Segments[i].IsDecoded
					: IsFrameFillReady(i) && i <= frontier;
			}
		}

		private bool IsFrameFillReady(int i) => Segments[i].IsDecoded && IsFrameComplete(i);
		private bool IsImmediateFrame(int i) => i == _firstPaintIndex || (_hasLiveFrame && i >= _archiveCount);
		private bool _isPlaying;
		private bool _isLoopReady;
		private string? _loadedNewestKey;
		// The ordered archive keys currently loaded (parallel to archive frame indices 0.._archiveCount-1).
		// Lets a periodic refresh diff old vs new keys and reuse the unchanged decoded frames instead
		// of rebuilding the whole loop (which blanked the layer + re-decoded everything).
		private string[] _loadedKeys = Array.Empty<string>();
		private CancellationTokenSource? _loopCts;

		// ── Past Event Viewer (replay a historical window instead of the live loop) ──
		// Start of the decodable archive. 2008+ is Message-31 super-res; 1991-2007 is legacy AR2V0001
		// Message 1 (single-pol, no CC), now decoded by the vendored Message-1 path. The calendar's floor is
		// the archive's first DAY (Level2RadarService.ArchiveFirstDay, KTLX 1991-06-05); this year is only
		// the base the year INDEX counts from. Most sites start 1994-1998; empty days simply list nothing.
		private const int PastEventStartYear = 1991;
		private bool _isPastEventMode;
		// The selected window, as the indices the pickers bind to: year 1991-based, month 0-based,
		// day 0-based. ⚠️ These initializers are the FALLBACK, not the default the user gets: the ctor
		// restores the last chosen timeframe from settings (RestorePastCastSelection) and every picker
		// change persists it again (PersistPastCastSelection). They still matter — a missing or
		// unparseable persisted value lands back here (2011-05-24 17:00 +2 h, a revisited event).
		private int _pastEventYearIndex = 2011 - PastEventStartYear;
		private int _pastEventMonthIndex = 5 - 1;
		private int _pastEventDayIndex = 24 - 1;
		private TimeSpan _pastEventTime = new(17, 0, 0); // 5:00 PM
		private int _pastEventDurationIndex = 2; // 2 hours (index into PastEventDurationOptions)
		private string _pastEventStatus = string.Empty;

		// Serializes loop mutation so the archive (re)load and the live-frame poll can't
		// interleave at await points and corrupt the frame arrays / VM↔JS index state.
		private readonly System.Threading.SemaphoreSlim _loopGate = new(1, 1);

		// Near-real-time "live" frame from the chunks bucket, appended as an extra newest frame
		// (index _archiveCount) on top of the archive-bucket history. _hasLiveFrame says whether
		// that slot exists; _liveFrame holds the current one (its time gates in-place updates).
		// A faster poll (RunLiveFrameRefreshAsync) keeps it fresh between archive reloads; 30s
		// catches each new SAILS 0.5° re-scan (~every 1.5-3 min) soon after it finishes without
		// much wasted traffic (clear-air VCPs only scan ~every 10 min, the real floor there).
		// Live-frame poll cadence — user-selectable in the Radar Loop tool window. RunLiveFrameRefreshAsync
		// reads it each cycle, so a change takes effect on the next poll with no reload. (The faster
		// retry-until-first-frame cadence below is unaffected.)
		private static readonly double[] RefreshSecondsByIndex = { 20, 30, 45, 60 };
		private int _refreshIntervalIndex = 1; // default 30 s
		private double RefreshIntervalSeconds => RefreshSecondsByIndex[Math.Clamp(_refreshIntervalIndex, 0, RefreshSecondsByIndex.Length - 1)];
		// Playback animation speed — user-selectable. RunPlaybackAsync reads ms-per-frame each tick,
		// so a change applies immediately (no restart).
		private static readonly int[] PlaybackMsByIndex = { 1000, 500, 333, 250 }; // 0.5x / 1x / 1.5x / 2x
		private int _playbackSpeedIndex = 1; // default 1x (500 ms)
		private int PlaybackIntervalMs => PlaybackMsByIndex[Math.Clamp(_playbackSpeedIndex, 0, PlaybackMsByIndex.Length - 1)];
		// While no live frame exists yet (the load-time poll often hits a still-scanning volume),
		// retry faster so the live data appears sooner; back to the normal cadence once we have one.
		private const double LiveFrameRetrySeconds = 20;
		private int _archiveCount;
		private bool _hasLiveFrame;
		private Models.RadarVolume? _liveFrame;
		// A live frame whose decode we've kicked off (AddRadarFrameAsync) but whose scrubber cell + display
		// promotion we DEFERRED until it actually decodes, so the new cell appears already-filled instead of
		// blinking through empty. OnRadarFrameReady completes it via CompleteLiveAppend; cleared on a new load.
		private Models.RadarVolume? _pendingLiveAppend;
		// The same deferral for an in-place live-frame UPDATE (the slot already exists): we've kicked off the
		// re-decode but hold the visible swap — frame time/mode, the readout, and the sweep pulse — until the
		// geometry lands, so the sweep animates WITH the new returns instead of ~3-6 s before them (the worker
		// fetches the ~7 MB volume then decodes). OnRadarFrameReady completes it via CompleteLiveUpdate.
		private Models.RadarVolume? _pendingLiveUpdate;
		// Mode text (VCP/precip/SAILS) from the most recent successful live poll. Tracked
		// SEPARATELY from _liveFrame because the mode is known from any decoded live volume even
		// when we don't append it as a new frame — e.g. an offline/stale site (KVNX) whose newest
		// chunks volume merely equals the archive newest, so it's correctly not appended, yet we
		// still want to show its scan mode rather than "awaiting live frame" forever.
		private string? _liveModeText;

		// Debug card state: outcome of the most recent live-frame poll (for the on-map dev card).
		private DateTimeOffset? _lastLivePollAt;
		private string? _lastLivePollResult;
		private string? _lastLiveError;
		// When the next live-frame poll is scheduled (set by RunLiveFrameRefreshAsync before each
		// wait), so the card can show a countdown / progress to it. _livePollCycleStart is the start
		// of the current wait (the progress-bar denominator).
		private DateTimeOffset? _nextLivePollAt;
		private DateTimeOffset? _livePollCycleStart;


		// Load timing for the current selection: from the site click to the first frame ready,
		// and to ALL frames (final count, incl. the live frame) ready+rendered. Captured once per
		// click (frozen after the initial load, so the ~60s live refreshes don't overwrite them).
		private DateTimeOffset? _loopClickAt;
		private TimeSpan? _firstFrameElapsed;
		private TimeSpan? _allFramesElapsed;
		private bool _initialLoadDone;
		private bool _loadInProgress;
		// Set true once BeginRadarLoopAsync has (re)started the loop for the current selection; gates
		// OnRadarFrameReady so stale frames from the previous selection (arriving before the JS loop
		// token bumps) can't pollute this session's first-frame timing / ready count.
		private bool _loopRenderBegun;

		public RadarViewModel(IMapService mapService, IRadarSiteProvider radarSiteProvider, ILevel2RadarService radarService, IDowEventProvider dowEventProvider, ISettingsService settings, StormMotionService? stormMotion)
		{
			_mapService = mapService;
			_stormMotion = stormMotion;
			_radarSiteProvider = radarSiteProvider;
			_radarService = radarService;
			_settings = settings;
			_engine = new RadarLoopEngine(this);

			// Reopen PastCast on the timeframe the user last chose, not on the built-in default. Before any
			// binding exists, so it writes the fields directly and raises nothing.
			RestorePastCastSelection();

			// Range rings: which are drawn, how they look (Settings → Radar Range Ring). Replayed at map-ready.
			RangeRings = new RangeRingsViewModel(mapService, settings);

			// The DOW Event Viewer is its own view model (a standalone mobile-radar frame through the
			// same render path); watch its IsShowing so the shared display / color-scale gate follows it.
			Dow = new DowViewModel(mapService, dowEventProvider);
			Dow.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(DowViewModel.IsShowing))
				{
					OnPropertyChanged(nameof(HasRadarDisplay));
				}
			};

			// Radar site selector: a leading "None" entry plus the curated sites.
			var radarOptions = new List<RadarOption> { new("None", null) };
			radarOptions.AddRange(_radarSiteProvider.GetSites().Select(s => new RadarOption($"{s.Id} \u2014 {s.Name}", s)));
			RadarOptions = radarOptions;
			_selectedRadarOption = RadarOptions[0];

			// FOUR panes always exist; the layout decides how many are VISIBLE. Keeping the hidden ones
			// alive means a pane remembers its product across a trip through single-pane, and it lets the
			// notches bind to fixed Pane0…Pane3 properties instead of an index into a mutating list.
			var panes = new RadarPaneViewModel[PaneLayoutInfo.MaxPanes];
			for (var i = 0; i < panes.Length; i++)
			{
				panes[i] = new RadarPaneViewModel(i, RadarProductOptions, OnPaneProductChanged)
				{
					IsVisible = i == 0, // Single is the launch layout
				};
			}

			Panes = panes;

			// Observable rows (site + availability) — the Atlas's list and the flyout's. Their availability is
			// written ONLY by the SITE AVAILABILITY block, which pushes the same state to the markers.
			RadarSiteRows = _radarSiteProvider.GetSites().Select(s => new RadarSiteRow(s)).ToList();
			RefreshSiteEra(); // retired sites start hidden (live); pushed to the markers at map-ready

			// A loaded loop's newest frame is EVIDENCE for its own site (SITE AVAILABILITY block): a site whose
			// loop just landed a fresh frame can't sit red for up to a 10-min pass. ⚠️ It may only mark a site
			// ONLINE — a loop fills in older frames as it loads, so a transiently old "newest" must never flag a
			// healthy site down. Going DOWN is left to the pass and the Atlas's authoritative scan fetch.
			PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(NewestLoadedFrameTime)
					&& _selectedRadarOption?.Site is { } site
					&& NewestLoadedFrameTime is { } newest)
				{
					ReportSiteScan(site, newest, canMarkOffline: false);
				}
			};
		}

		// ===== Panes ====================================================================================

		/// <summary>The four panes. Only the first <see cref="VisiblePaneCount"/> are shown; the rest keep
		/// their state for when a layout brings them back.</summary>
		public IReadOnlyList<RadarPaneViewModel> Panes { get; }

		/// <summary>The MAIN pane — bottom-left in a quad, and the view you were already looking at
		/// before entering a layout. Named accessors so the notches can x:Bind directly.</summary>
		public RadarPaneViewModel Pane0 => Panes[0];
		public RadarPaneViewModel Pane1 => Panes[1];
		public RadarPaneViewModel Pane2 => Panes[2];
		public RadarPaneViewModel Pane3 => Panes[3];

		/// <summary>A pane's product changed (its notch). Only that pane re-renders in the WebView; the
		/// loop, the frames and every other pane are untouched — which is what makes a product change in a
		/// quad cost nothing for the other three.</summary>
		private void OnPaneProductChanged(RadarPaneViewModel pane)
		{
			// Re-derive the scrubber cells: the visible product SET changed, so the built frontier may
			// have moved. We deliberately do NOT blank the ready set — radar.js posts fresh build progress
			// synchronously from setProduct, so SetBuildProgress corrects it a moment later. With the trio
			// prefetched (Rule 3) it is usually already built, so the scrubber stays lit.
			RefreshSegmentReadiness();

			if (!_isMapReady)
			{
				return;
			}

			// A switch just re-renders bytes already decoded (velocity is prefetched; Rule 3) — instant.
			// (The pane's notch follows on its own: it is a XAML overlay bound to the pane view model.)
			_ = _mapService.SetRadarProductAsync(pane.Index, pane.ProductId);

			// The loop's storm motion is computed ON DEMAND — gated out while nothing Doppler is in view
			// (see IsDopplerProductActive) — so a pane switching INTO velocity/SRV must kick it if it has
			// not run for this loop yet. Deduped by _motionRefKey, so it is a no-op once computed; SRV
			// shows the velocity stand-in (Rule 4) until the motion lands.
			if (pane.IsDoppler)
			{
				RequestAutoStormMotion();
			}
		}

		/// <summary>
		/// Observable rows (site + offline state). The Radar Atlas filters a view over THESE instances
		/// (<c>RadarAtlasViewModel.FilteredSites</c>), and <c>SiteSweepViewModel</c> reads their
		/// <c>IsOffline</c> to skip dead sites — so a row is shared state, not a private list model.
		/// </summary>
		/// <remarks>
		/// ⚠️ There is NO second list and no selection mirror. A <c>SelectedSiteRow</c> property, a
		/// <c>_syncingSelection</c> guard and a <c>_rowBySite</c> dictionary used to two-way bind the dock's
		/// "Radar Sites" ListView; that list is gone, the Radar Atlas owns its own <c>SelectedSite</c>,
		/// and all three were DELETED. Selection has one source of truth, <see cref="SelectedRadarOption"/>.
		/// </remarks>
		public IReadOnlyList<RadarSiteRow> RadarSiteRows { get; }

		/// <summary>Radar site options: a leading "None" entry plus the curated sites.</summary>
		public IReadOnlyList<RadarOption> RadarOptions { get; }

		/// <summary>
		/// The selected radar option. Setting a site recenters the map on it and loads a loop
		/// of recent Level II volumes (newest shown first, older backfilled); "None" clears the
		/// radar and stops the loop.
		/// </summary>
		public RadarOption? SelectedRadarOption
		{
			get => _selectedRadarOption;
			set
			{
				if (!SetProperty(ref _selectedRadarOption, value))
				{
					return;
				}

				// Trace every selection change. A spurious reload with NO preceding "siteClick" means the
				// setter was driven programmatically (mode toggle / Radar Atlas / a binding write-back).
				Services.RadarDiagnostics.Log("vm", "select",
					("to", value?.Site?.Id ?? "none"), ("mode", _isPastEventMode ? "past" : "live"));

				Dow.OnNexradTookOver(); // a NEXRAD selection takes over the radar layer from any DOW frame
				// Open every site switch in REFLECTIVITY. SRV (and velocity, until its dealias lands) can't be
				// instant on a fresh site — the storm motion isn't computed yet — so carrying SRV over from the
				// last site would show the motion-compute delay. Reflectivity paints immediately; the user can
				// switch back once the loop is complete (which by then is instant). Only on a real site pick.
				if (value?.Site is not null && Pane0.ProductIndex != 0)
				{
					// Rule 8 (docs/radar-loop-flow.md): a site switch opens in reflectivity, so a fresh
					// loop's velocity/motion latency hides behind an instant first paint. SCOPED TO
					// SINGLE PANE — in multi-pane the pane assignment is the user's explicit intent,
					// and resetting every pane to reflectivity on each site click would destroy it.
					if (IsSinglePane)
					{
						Pane0.ProductIndex = 0;
					}
				}
				OnPropertyChanged(nameof(HasRadarLoop));
				OnPropertyChanged(nameof(HasRadarDisplay));
				RaiseRadarReadout();
				// Past Event mode: before a window is armed, a site pick just targets it (Load drives the
				// first replay); once armed, a site pick auto-loads the same window for the new site (like
				// the live loop's click-to-load). Live mode always starts the live loop on a pick.
				if (_isPastEventMode)
				{
					if (_pastWindowLoaded && value?.Site is not null)
					{
						_ = _engine.LoadSelectedPastEventAsync();
					}
					else
					{
						// Kept (not discarded) so LoadReplayAtSiteAsync can wait for the clear to finish before
						// it loads — otherwise its ClearRadarAsync could land on top of the new loop.
						_pastSiteSelect = _engine.SelectPastSiteAsync(value?.Site);
					}
				}
				else
				{
					_ = _engine.StartRadarLoopAsync(value?.Site);
					if (value?.Site is { } site)
					{
						RaiseSiteLoaded(site, replay: false);
					}
				}
			}
		}

		// ── SITE USAGE seam (the Atlas's "Your use" strip; SiteUsageTracker listens) ─────────────────
		/// <summary>
		/// A loop the USER asked for: a live site pick (raised from <see cref="SelectedRadarOption"/>), or a
		/// PastCast replay window that actually loaded (raised by the engine's success path). ⚠️ A PastCast site
		/// pick that only ARMS or targets the window is not a load. Silenced while <see cref="IsSiteUsageSuppressed"/>.
		/// </summary>
		public event EventHandler<SiteLoad>? SiteLoaded;

		/// <summary>Set by the Debug site sweep for the length of a run, so a machine cycling 200 sites doesn't
		/// read as the user's history.</summary>
		internal bool IsSiteUsageSuppressed { get; set; }

		internal void RaiseSiteLoaded(RadarSite site, bool replay)
		{
			if (!IsSiteUsageSuppressed)
			{
				SiteLoaded?.Invoke(this, new SiteLoad(site, replay));
			}
		}

		/// <summary>Whether a radar site is selected (drives the loop controls' visibility).</summary>
		public bool HasRadarLoop => _selectedRadarOption?.Site is not null;

		/// <summary>Whether ANY radar frame is currently displayed — a NEXRAD loop OR a DOW frame. Gates
		/// the product Inspect toggle and the Color Scale legend, which apply to both sources.</summary>
		public bool HasRadarDisplay => HasRadarLoop || Dow.IsShowing;

		/// <summary>The DOW Event Viewer view model (a standalone curated mobile-radar frame).</summary>
		public DowViewModel Dow { get; }

		/// <summary>The range rings' preferences — which rings, their look, the label bearing.</summary>
		public RangeRingsViewModel RangeRings { get; }

		// ── Past Event Viewer ────────────────────────────────────────────────────────────────────
		// A second radar "mode": instead of the live loop (recent volumes + a near-real-time frame
		// that auto-refreshes), replay a fixed historical window the user picks. Same loop machinery
		// (decode/render/scrub/play), but no live poll and no auto-refresh. The site is picked in the
		// normal Radar Sites list; this just supplies the time window + a Load action.

		/// <summary>Durations offered for a past-event window (label + minutes).</summary>
		public IReadOnlyList<string> PastEventDurationOptions { get; } =
			new[] { "30 min", "1 hour", "2 hours", "3 hours", "6 hours", "12 hours" };
		// ⚠️ Mirrored by SavedEventLeg.AllowedDurationMinutes (internal so the test can hold the two in step).
		internal static readonly int[] PastEventMinutesByIndex = { 30, 60, 120, 180, 360, 720 };
		// Cap on frames loaded. Short windows load every volume (~5 min apart, smooth); longer windows
		// are evenly SUBSAMPLED to this many frames (so a 12 h window is an overview, ~18 min apart,
		// rather than 140+ frames melting memory).
		private const int PastEventMaxFrames = 40;

		/// <summary>Year choices for the date picker (1991 = start of the decodable WSR-88D archive).</summary>
		public IReadOnlyList<int> PastEventYearOptions { get; } =
			Enumerable.Range(PastEventStartYear, DateTime.Now.Year - PastEventStartYear + 1).ToList();

		// ⚠️ There are no Month/Day OPTION lists: the form is a DatePicker bound through the PastEventDate
		// facade, so only the three INDEX properties below survive (the engine + the two overlay VMs read
		// them by name). A month/day combo would need its list back.

		/// <summary>Selected year index (into <see cref="PastEventYearOptions"/>).</summary>
		public int PastEventYearIndex
		{
			get => _pastEventYearIndex;
			set
			{
				var c = Math.Clamp(value, 0, PastEventYearOptions.Count - 1);
				if (SetProperty(ref _pastEventYearIndex, c)) { OnReplaySelectionChanged(); }
			}
		}

		/// <summary>Selected month index (0-11).</summary>
		public int PastEventMonthIndex
		{
			get => _pastEventMonthIndex;
			set
			{
				var c = Math.Clamp(value, 0, 11);
				if (SetProperty(ref _pastEventMonthIndex, c)) { OnReplaySelectionChanged(); }
			}
		}

		/// <summary>Selected day index (0-30).</summary>
		public int PastEventDayIndex
		{
			get => _pastEventDayIndex;
			set
			{
				var c = Math.Clamp(value, 0, 30);
				if (SetProperty(ref _pastEventDayIndex, c)) { OnReplaySelectionChanged(); }
			}
		}

		// Once a replay window has been loaded (Load pressed with data found), the window is "armed":
		// after that, picking another site auto-loads the SAME window for it (like the live loop's
		// click-to-load), instead of forcing the user back into the flyout. Reset on mode change.
		private bool _pastWindowLoaded;

		/// <summary>
		/// When true, the app is in historical-replay mode: live controls gray out, a site pick targets
		/// the Load action (until a window is armed, then it auto-loads), and toggling off clears to idle.
		/// </summary>
		public bool IsPastEventMode
		{
			get => _isPastEventMode;
			set
			{
				if (!SetProperty(ref _isPastEventMode, value))
				{
					return;
				}

				ClearReplayWindowLoaded(); // re-arm from scratch each time the mode is toggled
				RefreshSiteEra();          // retired sites: hidden live, dated in replay
				OnPropertyChanged(nameof(IsTransportEnabled)); // the transport gate differs by mode (PastCast enables earlier)
				OnPropertyChanged(nameof(CanForceLiveCheck));  // replay has no live frame to check
				// The offered tilts depend on the mode, not just the radar: a live loop shows only the
				// tilts the chunks feed can serve fresh, while replay (all-historical) offers the whole
				// VCP. Rebuild from the last-known VCP now rather than waiting for a frame to land.
				UpdateTiltOptions(null);
				// Both directions clear to a clean slate (entering: drop the live loop; leaving:
				// drop the replay loop and go idle). Setting "None" routes through the mode-aware
				// SelectedRadarOption setter, which clears the loop without starting anything.
				SelectedRadarOption = RadarOptions[0];
				PastEventStatus = value ? "Pick a site, set a start time, then Load." : string.Empty;
				// Leaving replay: restore the LIVE site availability promptly (the status loop skips its
				// pushes while in past mode, so it wouldn't refresh the markers for up to ~10 min).
				// ⚠️ The rows still hold the REPLAY DAY's availability — grey them first so replay-day dots are
				// never read as live ones, even if the live pass fails.
				if (!value)
				{
					_ = ResetThenRefreshLiveSiteStatusAsync();
				}
			}
		}

		/// <summary>Start time-of-day of the replay window (bound to a TimePicker, local time).</summary>
		public TimeSpan PastEventTime
		{
			get => _pastEventTime;
			set { if (SetProperty(ref _pastEventTime, value)) { OnReplaySelectionChanged(); } }
		}

		/// <summary>The replay window's UTC start, reconstructed from the Year/Month/Day/time controls
		/// (local midnight for the selected date + the start time-of-day, then to UTC; the day is clamped to
		/// the month's length). This VM owns the replay-date state, so overlays keyed to the replay date (the
		/// historical outlook, storm reports) read the instant from here rather than each re-deriving it.
		/// <see cref="LoadSelectedPastEventAsync"/> keeps its own inline copy because it also needs the end.</summary>
		internal DateTimeOffset ReplayStartUtc() => ReplayStartLocal().ToUniversalTime();

		/// <summary>The same instant in LOCAL time — what the Timeframe card shows, and the only form the
		/// user ever sees. <see cref="ReplayStartUtc"/> is this converted; both exist so the conversion
		/// happens in exactly one place.</summary>
		private DateTimeOffset ReplayStartLocal()
		{
			var year = PastEventYearOptions[_pastEventYearIndex];
			var month = _pastEventMonthIndex + 1;
			var day = Math.Min(_pastEventDayIndex + 1, DateTime.DaysInMonth(year, month));
			var localMidnight = new DateTimeOffset(year, month, day, 0, 0, 0,
				TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));
			return localMidnight + _pastEventTime;
		}

		/// <summary>Selected window-duration index (into <see cref="PastEventDurationOptions"/>).</summary>
		public int PastEventDurationIndex
		{
			get => _pastEventDurationIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, PastEventMinutesByIndex.Length - 1);
				if (SetProperty(ref _pastEventDurationIndex, clamped)) { OnReplaySelectionChanged(); }
			}
		}

		// ── The replay window as ONE selection: the Timeframe card ───────────────────────────────
		// The PastCast window shows a SUMMARY CARD above the pickers. The card previews what Load WILL do
		// (PastEventDateText / PastEventRangeText, both derived live from the pickers), and its footer
		// reports whether that is what is actually on the map (HasLoadedReplayWindow +
		// IsReplaySelectionDirty). Everything in this block feeds that card.
		//
		// ⚠️ WHY THE CARD NEEDS A DIRTY FLAG. PastEventStatus is written once, by the load, and describes
		// the window that WAS loaded. Touch any picker afterwards and the card would be previewing one
		// window while claiming another is on screen. The dirty flag is what lets it say so instead.

		/// <summary>
		/// The replay date as a single value, for a calendar picker.
		/// </summary>
		/// <remarks>
		/// ⚠️ THIS IS A FACADE OVER THE THREE INDEX PROPERTIES, NOT A REPLACEMENT FOR THEM. The indices are
		/// read directly by <c>RadarLoopEngine</c> and subscribed to BY NAME by both
		/// <c>StormReportsViewModel</c> and <c>PastOutlookViewModel</c>; swapping them for one date would
		/// reach into the parked radar engine for what is a form-layout change. The calendar writes a date
		/// here, and everything downstream still reads the indices exactly as it did.
		/// <para>⚠️ It raises ONLY the index properties that actually changed. Both overlay view models
		/// re-fetch on any of those three names, so raising all three for a one-day move would kick off
		/// three refreshes where one is needed.</para>
		/// <para>A null (a cleared picker) is IGNORED rather than treated as a date: there is no such thing
		/// as a replay with no date, and the last good one is a better answer than an empty control.</para>
		/// <para>⚠️ IT MUST HAND BACK THE SAME FRAME IT IS GIVEN — see <see cref="LocalMidnight"/>. This
		/// property STACK-OVERFLOWED the app when the getter emitted a zero offset: CalendarDatePicker works
		/// in LOCAL time, so a UTC-midnight value came back as the previous evening, the binding wrote the
		/// earlier day in, the getter emitted THAT at zero offset, and the two walked backwards a day per
		/// round trip — synchronously, through the binding, so it blew the stack rather than merely landing
		/// on the wrong date.</para>
		/// </remarks>
		public DateTimeOffset? PastEventDate
		{
			get
			{
				var local = ReplayStartLocal();
				return LocalMidnight(local.Year, local.Month, local.Day);
			}
			set
			{
				// ⚠️ RE-ENTRANCY GUARD, and it is not belt-and-braces. Raising below makes the binding read
				// the getter and push the result straight back into this setter; if those two ever disagree
				// again the recursion is unbounded, and a StackOverflowException cannot be caught — it takes
				// the process with it. The frame fix above is the real answer; this is what keeps a future
				// mismatch to a picker that will not move.
				if (value is not { } date || _settingPastEventDate)
				{
					return;
				}

				_settingPastEventDate = true;
				try
				{
					ApplyPastEventDate(date);
				}
				finally
				{
					_settingPastEventDate = false;
				}
			}
		}

		private bool _settingPastEventDate;

		// ⚠️ The incoming value's OWN date parts are used as-is, with no time-zone conversion: it is a
		// calendar day, not an instant. Converting a zero-offset midnight to local time would move it to the
		// previous day, which is exactly the shift that caused the overflow.
		private void ApplyPastEventDate(DateTimeOffset date)
		{
			var yearIndex = Math.Clamp(date.Year - PastEventStartYear, 0, PastEventYearOptions.Count - 1);
			var changed = false;

			if (_pastEventYearIndex != yearIndex)
			{
				_pastEventYearIndex = yearIndex;
				OnPropertyChanged(nameof(PastEventYearIndex));
				changed = true;
			}

			if (_pastEventMonthIndex != date.Month - 1)
			{
				_pastEventMonthIndex = date.Month - 1;
				OnPropertyChanged(nameof(PastEventMonthIndex));
				changed = true;
			}

			if (_pastEventDayIndex != date.Day - 1)
			{
				_pastEventDayIndex = date.Day - 1;
				OnPropertyChanged(nameof(PastEventDayIndex));
				changed = true;
			}

			if (changed)
			{
				OnReplaySelectionChanged();
			}
		}

		/// <summary>
		/// A calendar day as LOCAL midnight — the one frame every date this view model hands a picker is
		/// expressed in.
		/// </summary>
		/// <remarks>
		/// ⚠️ THE OFFSET IS THE WHOLE POINT. CalendarDatePicker interprets and returns local time, so a date
		/// handed to it at any other offset comes back as a different calendar day; see the overflow note on
		/// <see cref="PastEventDate"/>. Min, Max and the selected date all go through here so they cannot
		/// disagree about which day they mean.
		/// <para>⚠️ Built from PARTS, never from a <see cref="DateTime"/>: the
		/// <c>DateTimeOffset(DateTime, TimeSpan)</c> constructor THROWS when the DateTime's Kind is Local and
		/// the offset is not the machine's own, which crashed the panel on open once already.</para>
		/// </remarks>
		private static DateTimeOffset LocalMidnight(int year, int month, int day) =>
			new(year, month, day, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));

		/// <summary>Earliest date the calendar offers — the archive's first day with a loadable volume.</summary>
		public DateTimeOffset PastEventMinDate
		{
			get
			{
				var first = Services.Level2RadarService.ArchiveFirstDay;
				return LocalMidnight(first.Year, first.Month, first.Day);
			}
		}

		/// <summary>Latest date the calendar offers: today. ⚠️ Without a bound a calendar will happily
		/// offer 2087, which the three year/month/day combos could never express — the constraint used to
		/// live in the year list, so it has to be restated here.
		/// <para>⚠️ Goes through <see cref="LocalMidnight"/> like every other date here — same frame, same
		/// parts-based construction, same two traps avoided.</para></summary>
		public DateTimeOffset PastEventMaxDate
		{
			get
			{
				var today = DateTime.Today;
				return LocalMidnight(today.Year, today.Month, today.Day);
			}
		}

		/// <summary>Short labels for the same durations, for the Timeframe card's segmented picker.
		/// ⚠️ Kept beside <see cref="PastEventDurationOptions"/> and <c>PastEventMinutesByIndex</c>: all
		/// three are indexed by <see cref="PastEventDurationIndex"/>, same order and same length, so they
		/// only stay in step if they stay together.</summary>
		public IReadOnlyList<string> PastEventDurationShortLabels { get; } =
			new[] { "30m", "1h", "2h", "3h", "6h", "12h" };

		/// <summary>The selected date, as the card's headline ("Tue May 24, 2011").</summary>
		public string PastEventDateText => ReplayStartLocal().ToString("ddd MMM d, yyyy");

		/// <summary>The selected window, as the card's second line ("5:00 PM → 7:00 PM"). Derived from the
		/// pickers, so it previews what Load will do rather than reporting what was loaded.</summary>
		public string PastEventRangeText
		{
			get
			{
				var start = ReplayStartLocal();
				var end = start.AddMinutes(PastEventMinutesByIndex[_pastEventDurationIndex]);
				return $"{start:h:mm tt} → {end:h:mm tt}";
			}
		}

		/// <summary>Whether a replay window has been loaded (or armed) this session.</summary>
		public bool HasLoadedReplayWindow => _pastWindowLoaded;

		/// <summary>
		/// Whether the pickers now describe a DIFFERENT window from the one that was loaded — so the card
		/// can offer Load again instead of reporting a frame count for a window you have since edited away.
		/// </summary>
		public bool IsReplaySelectionDirty =>
			_pastWindowLoaded
			&& (_loadedWindowDurationIndex != _pastEventDurationIndex || _loadedWindowStartUtc != ReplayStartUtc());

		/// <summary>
		/// The UTC start of the window that was actually LOADED, or null if none has been. Distinct from
		/// <see cref="ReplayStartUtc"/>, which follows the PICKERS and so describes what Load would do next.
		/// </summary>
		/// <remarks>
		/// ⚠️ Overlays keyed to the replay day (the storm reports) must read THIS, not the pickers: between
		/// editing a date and pressing Load the two disagree, and an overlay following the pickers would be
		/// describing a day that is not on the map.
		/// </remarks>
		public DateTimeOffset? LoadedReplayStartUtc => _loadedWindowStartUtc;

		/// <summary>The UTC end of the LOADED window (start + its duration), or null if none has been. Same
		/// rule as <see cref="LoadedReplayStartUtc"/> — the damage-survey overlay filters to this span.</summary>
		public DateTimeOffset? LoadedReplayEndUtc =>
			_loadedWindowStartUtc is { } start && _loadedWindowDurationIndex >= 0
				? start.AddMinutes(PastEventMinutesByIndex[_loadedWindowDurationIndex])
				: null;

		// What the last load actually loaded, for the comparison above.
		private DateTimeOffset? _loadedWindowStartUtc;
		private int _loadedWindowDurationIndex = -1;

		/// <summary>Record that the current selection is now the loaded (or armed) window. Called by the
		/// loop engine from both of its success paths — arming without a site, and a real load.</summary>
		internal void MarkReplayWindowLoaded()
		{
			_pastWindowLoaded = true;
			_loadedWindowStartUtc = ReplayStartUtc();
			_loadedWindowDurationIndex = _pastEventDurationIndex;
			OnPropertyChanged(nameof(LoadedReplayStartUtc));
			OnPropertyChanged(nameof(LoadedReplayEndUtc));
			OnPropertyChanged(nameof(HasLoadedReplayWindow));
			OnPropertyChanged(nameof(IsReplaySelectionDirty));
			RefreshSiteEra();
		}

		// Forget the loaded window (leaving or re-entering replay mode). Nothing is loaded, so nothing can
		// be dirty.
		private void ClearReplayWindowLoaded()
		{
			_pastWindowLoaded = false;
			_loadedWindowStartUtc = null;
			_loadedWindowDurationIndex = -1;
			OnPropertyChanged(nameof(LoadedReplayStartUtc));
			OnPropertyChanged(nameof(LoadedReplayEndUtc));
			OnPropertyChanged(nameof(HasLoadedReplayWindow));
			OnPropertyChanged(nameof(IsReplaySelectionDirty));
			RefreshSiteEra();
		}

		// ── Saved events: the two seams SavedEventsViewModel drives ──────────────────────────────
		// A saved event is the pickers' three values plus a site, so applying one is just writing those —
		// through the SAME setters a hand edit uses, so the card, the dirty flag and the persisted
		// selection all follow without a parallel path.

		/// <summary>
		/// Puts a saved window into the pickers. <paramref name="startUtc"/> is converted to local time HERE
		/// and nowhere else (the pickers are local; a saved event is UTC).
		/// </summary>
		/// <remarks>
		/// ⚠️ The date goes through <see cref="LocalMidnight"/> like every other date handed to the calendar —
		/// see the stack-overflow note on <see cref="PastEventDate"/>.
		/// ⚠️ A duration the picker doesn't offer leaves the window length ALONE rather than snapping to the
		/// nearest one; the library refuses such a leg, so this is only a guard.
		/// </remarks>
		internal void ApplyReplayWindow(DateTimeOffset startUtc, int durationMinutes)
		{
			var local = startUtc.ToLocalTime();
			ApplyPastEventDate(LocalMidnight(local.Year, local.Month, local.Day));
			PastEventTime = new TimeSpan(local.Hour, local.Minute, 0);
			var index = Array.IndexOf(PastEventMinutesByIndex, durationMinutes);
			if (index >= 0)
			{
				PastEventDurationIndex = index;
			}
		}

		// The not-armed site pick's clear (SelectPastSiteAsync), so a follow-up load can wait for it.
		private Task _pastSiteSelect = Task.CompletedTask;

		/// <summary>
		/// Selects a replay site (when given) and loads the pickers' window at it — EXACTLY ONE load.
		/// </summary>
		/// <remarks>
		/// ⚠️ <b>Why this isn't just <c>SelectedRadarOption = option</c>:</b> that setter's behaviour depends on
		/// hidden state — once a window is loaded it auto-loads the new site, otherwise it only clears and
		/// highlights. So a DIFFERENT site first forgets the loaded window (always the clear-only path), the
		/// clear is AWAITED, and then one explicit load runs. Without the await, the clear's
		/// <c>ClearRadarAsync</c> could land after the new loop had begun drawing.
		/// ⚠️ The SAME site skips straight to the load — which the engine treats as a fresh replay, the same as
		/// pressing Load. Loaded and already matching = nothing to do (the archive day is immutable).
		/// ⚠️ The pickers must already hold the window: callers apply it FIRST.
		/// </remarks>
		internal async Task<bool> LoadReplayAtSiteAsync(RadarOption? option)
		{
			if (!_isPastEventMode)
			{
				return false;
			}

			if (option is not null && !ReferenceEquals(_selectedRadarOption, option))
			{
				ClearReplayWindowLoaded();
				SelectedRadarOption = option;
				await _pastSiteSelect;
			}
			else if (_pastWindowLoaded && !IsReplaySelectionDirty)
			{
				return true;
			}

			return await LoadSelectedPastEventAsync();
		}

		// Any change to date, start time or duration moves the card's preview AND can make it disagree with
		// what is loaded. One hook, called from every one of those setters, so a new field can never be
		// added that updates the pickers but not the card.
		private void OnReplaySelectionChanged()
		{
			OnPropertyChanged(nameof(PastEventDate));
			OnPropertyChanged(nameof(PastEventDateText));
			OnPropertyChanged(nameof(PastEventRangeText));
			OnPropertyChanged(nameof(IsReplaySelectionDirty));
			PersistPastCastSelection();
			RefreshSiteEra(); // dialling the date can bring a retired site into (or out of) range
		}

		// ── The chosen timeframe SURVIVES A RESTART ──────────────────────────────────────────────
		// Reopening PastCast offers the event you were last watching. The pair below is the whole feature:
		// the ctor restores, and the one selection hook above persists.
		//
		// ⚠️ IT FOLLOWS THE PICKERS, NOT THE LOAD. Anything you dial in is remembered whether or not you
		// pressed Load — "chosen" is the selection, and a half-set window you were about to load is a
		// better thing to come back to than the one before it. Nothing about the loaded state is persisted:
		// a restored selection is NOT loaded, so the card correctly opens on "Not loaded yet".
		//
		// ⚠️ Settings store VALUES (a calendar day, minutes past midnight, a window length), never the
		// picker INDICES — see the AppSettings block for why. So both directions convert.

		// Pull the last chosen timeframe out of settings, into the picker fields. ⚠️ Ctor-time only: it
		// writes the FIELDS, not the properties, so nothing raises and nothing persists straight back.
		// Every value is validated independently — one unparseable entry falls back to that field's own
		// default rather than discarding the rest of the selection.
		private void RestorePastCastSelection()
		{
			var stored = _settings.Settings;

			if (DateOnly.TryParseExact(stored.PastCastDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
				DateTimeStyles.None, out var date))
			{
				// Clamp to what the calendar actually offers (PastEventMinDate…PastEventMaxDate). A persisted
				// date can only land outside it if the machine clock moved or the file was hand-edited.
				var earliest = Services.Level2RadarService.ArchiveFirstDay;
				var today = DateOnly.FromDateTime(DateTime.Today);
				if (date < earliest) { date = earliest; }
				if (date > today) { date = today; }

				_pastEventYearIndex = Math.Clamp(date.Year - PastEventStartYear, 0, PastEventYearOptions.Count - 1);
				_pastEventMonthIndex = date.Month - 1;
				_pastEventDayIndex = date.Day - 1;
			}

			if (stored.PastCastStartMinutes is >= 0 and < 24 * 60)
			{
				_pastEventTime = TimeSpan.FromMinutes(stored.PastCastStartMinutes);
			}

			// A length the picker no longer offers keeps the default duration — never the nearest match,
			// which would silently load a different window than the one that was saved.
			var durationIndex = Array.IndexOf(PastEventMinutesByIndex, stored.PastCastDurationMinutes);
			if (durationIndex >= 0)
			{
				_pastEventDurationIndex = durationIndex;
			}
		}

		// Write the current selection back. Cheap to call on every keystroke of a date scrub: the settings
		// service debounces its own save (~500 ms), so this only ever sets three properties.
		private void PersistPastCastSelection()
		{
			// ⚠️ The DATE comes from ReplayStartLocal, not from the raw indices, because that is where the
			// day is clamped to the month's length — so what lands in settings is always a real calendar day.
			var local = ReplayStartLocal();
			var stored = _settings.Settings;
			stored.PastCastDate = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			stored.PastCastStartMinutes = (int)_pastEventTime.TotalMinutes;
			stored.PastCastDurationMinutes = PastEventMinutesByIndex[_pastEventDurationIndex];
		}

		/// <summary>Status line for the Past Event Viewer (loading / loaded N frames / errors).</summary>
		public string PastEventStatus
		{
			get => _pastEventStatus;
			private set => SetProperty(ref _pastEventStatus, value);
		}

		// ── User-tunable loop settings. ⚠️ The three OPTION LISTS these indices used to point into are
		//    GONE with the Radar Loop tool window that hosted the combos — only the index properties and
		//    their *ByIndex tables survive, so each setting sits at its default. Re-surfacing any of them
		//    means a new list beside its *ByIndex table (the two are indexed alike and must move together).

		/// <summary>Selected loop-length index; changing it rebuilds the loop at the new length.</summary>
		public int LoopLengthIndex
		{
			get => _loopLengthIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, LoopLengthByIndex.Length - 1);
				if (SetProperty(ref _loopLengthIndex, clamped) && _selectedRadarOption?.Site is { } site)
				{
					_ = _engine.StartRadarLoopAsync(site);
				}
			}
		}

		/// <summary>Selected update-interval index; applied on the next live poll (no reload).</summary>
		public int RefreshIntervalIndex
		{
			get => _refreshIntervalIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, RefreshSecondsByIndex.Length - 1);
				SetProperty(ref _refreshIntervalIndex, clamped);
			}
		}

		/// <summary>Selected playback-speed index; applied on the next animation tick.</summary>
		public int PlaybackSpeedIndex
		{
			get => _playbackSpeedIndex;
			set
			{
				var clamped = Math.Clamp(value, 0, PlaybackMsByIndex.Length - 1);
				SetProperty(ref _playbackSpeedIndex, clamped);
			}
		}

		/// <summary>Whether all loop frames have finished decoding (enables play + scrubber).</summary>
		public bool IsLoopReady
		{
			get => _isLoopReady;
			private set
			{
				if (!SetProperty(ref _isLoopReady, value))
				{
					return;
				}

				OnPropertyChanged(nameof(RadarLoadingText));
				OnPropertyChanged(nameof(IsTransportEnabled)); // NowCast gates the transport on this

				// The whole loop is decoded. Velocity/SRV building and the storm-motion compute were already
				// armed right after first paint (see the load paths) — per docs/radar-loop-flow.md Rule 3
				// (a filled frame is complete) they ride the backfill's ONE decode per frame, not a second
				// pass here. All that's left is the tilt-switch raw prefetch.
				if (value && _frameCount > 0)
				{
					_engine.StartTiltPrefetch();
				}
			}
		}

		// PastCast enables the transport once this many reflectivity frames have decoded — replay loops can
		// be long (a 3 h window ≈ 36 frames), so waiting for the WHOLE loop (as NowCast does) is too slow.
		// Safe because the scrubber is clamped to MaxReachableFrame (you can only reach decoded frames).
		private const int PastCastEarlyReadyFrames = 3;

		/// <summary>Whether the transport controls (play / prev / next, scrubber, product selector, inspect)
		/// are enabled. NowCast waits for the WHOLE reflectivity loop (<see cref="IsLoopReady"/>) since it's
		/// short; PastCast enables EARLY — once the first few refl frames decode — because replay loops can be
		/// long. Both gate on REFLECTIVITY readiness only (velocity/SRV/dual-pol build later and are handled
		/// per-frame by <see cref="MaxReachableFrame"/>).</summary>
		public bool IsTransportEnabled =>
			_isPastEventMode
				? _frameCount > 0 && _readyCount >= System.Math.Min(PastCastEarlyReadyFrames, _frameCount)
				: _isLoopReady;

		/// <summary>The furthest frame the scrubber / step may reach: the contiguous-from-left frontier of
		/// frames that are decoded AND whose ACTIVE product is displayable (<see cref="IsFrameDisplayReady"/>),
		/// so you can't scrub onto a not-yet-built frame of a slower product. Reflectivity is always
		/// display-ready, so this is the decoded range; a slower product's range grows as it builds, and in
		/// PastCast the decoded range itself grows with the backfill. Never below the current frame (so the
		/// frame you're on — e.g. the first-paint frame — is never yanked back). Read on demand at seek/step.</summary>
		public int MaxReachableFrame
		{
			get
			{
				var frontier = -1;
				for (var i = 0; i < _frameCount; i++)
				{
					if (i < Segments.Count && Segments[i].IsDecoded && IsFrameDisplayReady(i)) frontier = i;
					else break;
				}
				var max = System.Math.Max(frontier, _currentFrameIndex);
				if (max < 0) max = 0;
				return System.Math.Min(max, MaxFrameIndex);
			}
		}

		/// <summary>Scrubber maximum: the last frame index (0-based).</summary>
		public int MaxFrameIndex => _frameCount > 0 ? _frameCount - 1 : 0;

		/// <summary>The loop frame currently shown (0 = oldest, MaxFrameIndex = newest).</summary>
		public double CurrentFrameIndex
		{
			get => _currentFrameIndex;
			set
			{
				var clamped = (int)Math.Round(value < 0 ? 0 : (value > MaxFrameIndex ? MaxFrameIndex : value));
				if (!SetProperty(ref _currentFrameIndex, clamped))
				{
					return;
				}

				OnPropertyChanged(nameof(CurrentFrameTimeText));
				// Refresh the card readouts (frame N/M, time) NOW rather than waiting for the 1s tick —
				// otherwise at fast playback (≤500ms/frame) the "frame N/M" line only updates every
				// other frame and visibly lags the actual loop.
				RaiseRadarReadout();

				if (_isMapReady)
				{
					_ = _mapService.ShowRadarFrameAsync(clamped);
				}
			}
		}

		/// <summary>The displayed frame's local time (shown beside the transport). Empty until that frame's
		/// time is known — load progress is conveyed by the segmented scrubber now, not text.</summary>
		public string CurrentFrameTimeText
		{
			get
			{
				var t = (_currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length)
					? _frameTimes[_currentFrameIndex]
					: null;
				return t?.ToLocalTime().ToString("h:mm tt") ?? "";
			}
		}

		/// <summary>Toggles loop playback (no-op until the loop is fully loaded).</summary>
		public void ToggleRadarPlay()
		{
			if (!IsTransportEnabled)
			{
				return;
			}

			IsPlaying = !_isPlaying;
		}

		/// <summary>Stops the loop: halts playback and returns to the newest frame. ⚠️ The transport is ONE
		/// play/stop button gated on <see cref="IsTransportEnabled"/> — there is no separate Stop button to
		/// enable, so nothing tracks "engaged" (playing OR paused) as a state of its own.</summary>
		public void StopRadarLoop()
		{
			IsPlaying = false;
			CurrentFrameIndex = MaxFrameIndex; // snap back to the latest frame
		}

		/// <summary>Steps the shown frame by <paramref name="delta"/> (−1 = previous, +1 = next). Pauses
		/// playback so the step sticks (stepping is a manual seek), and keeps the loop engaged so Stop stays
		/// available. No-op until the loop is fully loaded. The CurrentFrameIndex setter clamps at the ends.</summary>
		public void StepFrame(int delta)
		{
			if (!IsTransportEnabled)
			{
				return;
			}

			if (_isPlaying)
			{
				IsPlaying = false; // pause in place so the manual step isn't immediately overwritten
			}

			// Stepping FORWARD can't pass the built frontier (onto a blank slower-product / undecoded frame);
			// stepping back is always allowed. The setter clamps to [0, MaxFrameIndex].
			var target = _currentFrameIndex + delta;
			if (delta > 0) target = System.Math.Min(target, MaxReachableFrame);
			CurrentFrameIndex = target;
		}

		/// <summary>Opacity (0-1) of the radar layer — the temporal windows' Radar slider. Kept while the
		/// layer is hidden, so showing it again restores this value.</summary>
		public double RadarOpacity
		{
			get => _radarOpacity;
			set
			{
				if (SetProperty(ref _radarOpacity, value) && _isMapReady)
				{
					_ = PushRadarOpacityAsync();
				}
			}
		}

		/// <summary>
		/// Show/hide the radar layer — the Radar header checkbox in the NowCast and PastCast windows. On by
		/// default; persisted (AppSettings.ShowRadarLayer, via TemporalWindowPersistence).
		/// </summary>
		/// <remarks>
		/// ⚠️ HIDING IS OPACITY 0, NOT AN UNLOAD. The loop keeps fetching and decoding underneath, so showing
		/// it again is instant — and it saves no memory. The same map command as the slider, so there is one
		/// radar opacity on the page and this VM is the only thing that decides it.
		/// </remarks>
		public bool ShowRadarLayer
		{
			get => _showRadarLayer;
			set
			{
				if (SetProperty(ref _showRadarLayer, value) && _isMapReady)
				{
					_ = PushRadarOpacityAsync();
				}
			}
		}

		// ⚠️ The ONLY radar opacity push. The page keeps its own default (radar.js `opacity`), which is why
		// OnMapsReadyAsync pushes too: without it the page drew its default until the slider first moved,
		// and the window's readout would have described a value the map was not using.
		private Task PushRadarOpacityAsync() =>
			_mapService.SetRadarOpacityAsync(_showRadarLayer ? _radarOpacity : 0);

		// ===== Storm-Relative Velocity (SRV) storm motion ===============================================
		// SRV = base velocity − the storm motion's component along each beam, so a storm's own translation is
		// removed and rotation (mesocyclones) reads near zero. The motion is ALWAYS derived automatically from
		// each volume's own FULL-VOLUME VAD wind profile (Bunkers right-mover; RadarScope-style), fully offline
		// in the decoder — there is no manual override. ⚠️ A single low tilt is too shallow for a correct
		// 0–6 km profile, so the VM hands the WebView a volume's bottom velocity tilts (EnsureVwpTiltsAsync)
		// whenever a Doppler product is in view; the WebView merges their VAD profiles → Bunkers (per volume,
		// cached) and reports the result back via SetAutoStormMotion for the readout.
		private string _autoStormMotionText = "Auto — awaiting SRV";
		/// <summary>The wind-profile provider chain (doc 01 §5). Null = no chain configured, in which case the
		/// local Level II VAD is used directly, exactly as before this existed.</summary>
		private readonly StormMotionService? _stormMotion;
		private string? _motionRefKey; // the FIRST-PAINT volume the loop's one storm motion is computed from
		private string? _lastVwpKey;   // the ref key we last asked the WebView to compute an auto motion for

		/// <summary>Human-readable readout of the current AUTOMATIC storm motion (e.g. "245° at 18 kt ·
		/// Bunkers R"), updated from the decoder each time an SRV frame is built in Auto mode.</summary>
		public string AutoStormMotionText
		{
			get => _autoStormMotionText;
			private set => SetProperty(ref _autoStormMotionText, value);
		}

		/// <summary>Records the AUTOMATIC (VAD-derived) storm motion the WebView computed for a volume's
		/// full-volume wind profile — speed in m/s, direction = bearing the storm moves toward, plus the
		/// estimate's source ("Bunkers R" / "Mean wind"). When <paramref name="insufficient"/> the volume's
		/// merged profile was too shallow to trust (SRV stays at base velocity), shown as such. Refreshes the
		/// <see cref="AutoStormMotionText"/> readout.</summary>
		/// <summary>Plain-English form of the decoder's typed no-solution reason (doc 03 §9: "storm motion is
		/// blank" must always be traceable to a named cause, never to a bare null). Unknown/empty reasons fall
		/// back to the generic wording rather than showing a raw token.</summary>
		public static string DescribeStormMotionFailure(string? why) => why switch
		{
			"shallow" => "profile too shallow",
			"foldSuspect" => "profile suspect (fold)",
			"noHead" => "no 5.5–6 km wind",
			"noTail" => "no 0–0.5 km wind",
			"fewPts" => "too few levels",
			"fewCuts" => "too few usable cuts",
			_ => "insufficient profile",
		};

		public void SetAutoStormMotion(double speedMs, double directionDeg, string? source, bool insufficient = false, string? why = null, string? tier = null)
		{
			if (insufficient)
			{
				AutoStormMotionText = "Auto — " + DescribeStormMotionFailure(why);
				return;
			}
			var kt = speedMs / 0.514444;
			var mph = speedMs * 2.23694;
			// Provider and tier are different claims — see the pipeline console for the full note.
			var origin = string.IsNullOrWhiteSpace(source) ? "" : source;
			if (!string.IsNullOrWhiteSpace(tier) && !string.Equals(tier, source, StringComparison.Ordinal))
			{
				origin = string.IsNullOrEmpty(origin) ? tier! : origin + " · " + tier;
			}

			var src = string.IsNullOrWhiteSpace(origin) ? "" : " · " + origin;
			AutoStormMotionText = $"{Math.Round(directionDeg)}° at {Math.Round(kt)} kt ({Math.Round(mph)} mph){src}";
		}

		/// <summary>Asks the WebView to compute the loop's ONE auto storm motion (docs/radar-loop-flow.md
		/// Rules 4 & 5), from the FIRST-PAINT volume's bottom velocity tilts (<see cref="_motionRefKey"/> —
		/// newest in NowCast, oldest in PastCast, whichever we already fetched for first paint).
		///
		/// <para>Fired ONCE per loop, right after first paint (see the load paths), and again if the user turns
		/// Auto on. NOT gated on the viewed product — under the law the loop always builds velocity+SRV, so the
		/// motion always computes (Rule 3). NOT per-frame: storm motion barely varies over a loop, and a
		/// per-frame motion churned scrubbing. Until it lands the WebView renders base velocity as the SRV
		/// stand-in (Rule 4's asterisk). Guarded by <see cref="_motionRefKey"/> + the WebView's own cache so it
		/// computes at most once per loop.</para></summary>
		// True when the active product needs the loop's VAD storm motion (SRV subtracts it; velocity is its
		// always-built companion). Gates the periodic RELOAD recompute ONLY (see RequestAutoStormMotion):
		// first paint computes the motion regardless of product so SRV pre-warms in the background, but the
		// ~5-min reload skips the recompute while on reflectivity — otherwise it re-warmed the whole loop's
		// SRV every reload even with SRV off-screen (the churn: 200+ needless re-decodes a soak).
		// ⚠️ MULTI-PANE: ANY visible pane showing velocity/SRV needs the motion — the loop has exactly
		// one (Rule 5), so one Doppler pane is enough to want it.
		private bool IsDopplerProductActive
		{
			get
			{
				for (var i = 0; i < VisiblePaneCount && i < Panes.Count; i++)
				{
					if (Panes[i].IsDoppler) return true;
				}

				return false;
			}
		}

		// gateToDoppler is set ONLY by the ~5-min reload: while browsing reflectivity we must not recompute the
		// motion + re-warm the whole loop's SRV on every reload (the churn). First paint + a switch INTO a
		// Doppler product pass false → compute eagerly so SRV is PRE-WARMED off-screen and the switch is instant.
		// Deduped by _motionRefKey so it runs at most once per loop reference volume.
		private void RequestAutoStormMotion(bool gateToDoppler = false)
		{
			if (!_isMapReady)
			{
				return;
			}
			if (gateToDoppler && !IsDopplerProductActive)
			{
				return; // periodic reload while on reflectivity: don't recompute/churn SRV (it's not shown)
			}
			if (_selectedRadarOption?.Site is not { } site || string.IsNullOrEmpty(_motionRefKey))
			{
				return; // no reference volume yet (no loop loaded)
			}
			if (_motionRefKey == _lastVwpKey)
			{
				return; // already computed for this loop's reference volume
			}
			_lastVwpKey = _motionRefKey;
			_ = ComputeAutoStormMotionAsync(site, _motionRefKey);
		}

		/// <summary>
		/// Resolves the loop's ONE storm motion, following the provider chain of doc 01 §5.
		/// </summary>
		/// <remarks>
		/// ⚠️ ORDER IS THE SPEC'S, NOT A PREFERENCE. The NWS's own VAD (Level III NVW) is tried first: it is
		/// computed by the ORPG from dealiased velocity over the full volume, with QC we cannot match, and it
		/// costs ONE ~3 kB fetch. Our Level II VAD is the fallback.
		///
		/// <para>The fallback is not optional. The NVW bucket is REAL-TIME ONLY, so every PastCast replay —
		/// and any site or moment the product is missing — lands on our own retrieval.</para>
		///
		/// <para>⚠️ Skipping the local VAD when NVW answers is also the big perf win: it avoids
		/// EnsureVwpTiltsAsync's ~8 tilt extractions and the 8 dealiases that follow, which is the most
		/// expensive thing in the storm-motion path.</para>
		/// </remarks>
		private async Task ComputeAutoStormMotionAsync(RadarSite site, string key)
		{
			try
			{
				if (_stormMotion is not null)
				{
					// ⚠️ Ask about the REFERENCE VOLUME's time, not whatever is loaded right now. In replay this
					// fires straight after first paint, and replay paints OLDEST-first — so the only loaded
					// frame is the far end of the window from the volume we are actually computing for. Using
					// the loaded time there would query the product ~3 h away, fall outside the provider's
					// age tolerance, and silently never use NVW on a recent replay.
					var when = Services.Level2RadarService.ParseVolumeTime(key)?.UtcDateTime
						?? NewestLoadedFrameTime?.UtcDateTime
						?? DateTime.UtcNow;
					var resolved = await _stormMotion.ResolveAsync(site.Id, when);
					if (resolved.HasSolution && resolved.RightMover is { } rm)
					{
						Diag($"storm-motion {key} from {resolved.ProfileSource}: "
							+ $"{rm.HeadingDeg:F0} deg @ {rm.SpeedKt:F0} kt");
						await _mapService.SetStormMotionAsync(
							rm.SpeedMs, rm.HeadingDeg, resolved.ProfileSource ?? "NVW",
							resolved.LevelCount, resolved.ProfileTopM, resolved.Tier);
						return;
					}

					// A named failure here is informative — it says the NWS product existed and why it was
					// unusable, rather than just "we fell back".
					Diag($"storm-motion {key}: chain declined ({resolved.Failure}) — using local VAD");
				}

				var urls = await _radarService.EnsureVwpTiltsAsync(site, key);
				if (urls.Count > 0)
				{
					await _mapService.ComputeStormMotionAsync(urls);
				}
			}
			catch (System.Exception ex)
			{
				Diag($"vwp storm-motion {key} failed: {ex.Message}");
			}
		}

		// ===== Pane layout ==============================================================================
		// A pane is a PRODUCT VIEW of one site: panes share the site, the camera and the time cursor, and
		// differ only in the moment they draw. So the layout lives on the RADAR view model — it decides how
		// many product views exist, which is what the notch grid mirrors and what the page lays its
		// maps out from. Two-pane is side-by-side only (see PaneLayout).
		private PaneLayout _paneLayout = PaneLayout.Single;

		/// <summary>How the map band is divided into panes. Setting it re-lays the page's maps out; the
		/// camera, the loaded loop and every overlay survive the change.</summary>
		public PaneLayout PaneLayout
		{
			get => _paneLayout;
			set
			{
				if (!SetProperty(ref _paneLayout, value))
				{
					return;
				}

				OnPropertyChanged(nameof(IsSinglePane));
				OnPropertyChanged(nameof(VisiblePaneCount));
				ApplyPaneLayout();
			}
		}

		/// <summary>True while exactly one pane is shown. Rule 8 (a site switch resets the product to
		/// reflectivity, hiding a fresh loop's velocity/motion latency) is scoped to this: in multi-pane the
		/// pane assignment is the user's explicit intent, so a site click must not blow it away.</summary>
		public bool IsSinglePane => _paneLayout == PaneLayout.Single;

		/// <summary>How many panes the current layout shows (1, 2 or 4).</summary>
		public int VisiblePaneCount => _paneLayout.PaneCount();

		/// <summary>
		/// The product each pane opens on for a given layout: Ref/Vel for two, Ref/Vel/SRV/CC for
		/// four. That is the engine's OWN staging order — the trio (reflectivity, velocity, SRV) is
		/// built together per <c>docs/radar-loop-flow.md</c> Rule 3, and CC leads the dual-pol second
		/// wave — so the default quad asks for nothing the loop wasn't already going to build.
		/// <para>Pane 1 is deliberately left alone: entering multi-pane must not disturb the view you
		/// were already looking at.</para>
		/// </summary>
		private static readonly string[] DefaultPaneProducts = { "reflectivity", "velocity", "srv", "cc" };

		// Which panes have been revealed at least once. A pane gets its DEFAULT product the first time
		// it appears and its REMEMBERED one every time after, so a trip through single-pane doesn't
		// silently undo a product the user set.
		private readonly bool[] _paneEverShown = new bool[PaneLayoutInfo.MaxPanes];

		private int IndexOfProduct(string id)
		{
			for (var i = 0; i < RadarProductOptions.Count; i++)
			{
				if (RadarProductOptions[i].Id == id) return i;
			}

			return 0;
		}

		/// <summary>True when no visible pane shows anything but reflectivity — the one case where every
		/// frame is display-ready by construction, since reflectivity is always built.</summary>
		private bool AllVisiblePanesAreReflectivity()
		{
			for (var i = 0; i < VisiblePaneCount && i < Panes.Count; i++)
			{
				if (!Panes[i].IsReflectivity) return false;
			}

			return true;
		}

		private void ApplyPaneLayout()
		{
			if (!_isMapReady)
			{
				return; // pushed on map-ready instead
			}

			_ = ApplyPaneLayoutAsync();
		}

		private async Task ApplyPaneLayoutAsync()
		{
			await _mapService.SetPaneLayoutAsync(
				_paneLayout.Columns(), _paneLayout.Rows(), PaneLayoutInfo.GutterPx);

			var count = _paneLayout.PaneCount();
			for (var i = 0; i < Panes.Count; i++)
			{
				Panes[i].IsVisible = i < count;
			}

			// A pane REVEALED for the first time opens on its default product; one that has been shown
			// before keeps whatever the user last put in it. Pane 0 is never reassigned — entering a
			// layout must not disturb the view you were already looking at.
			for (var i = 1; i < count && i < DefaultPaneProducts.Length; i++)
			{
				if (_paneEverShown[i])
				{
					continue;
				}

				_paneEverShown[i] = true;
				Panes[i].SetProductIndexSilently(IndexOfProduct(DefaultPaneProducts[i]));
			}

			// Push every visible pane's product. Done here rather than in the loop above so a pane that kept
			// a remembered product is re-pushed too — the page defaults a new view to reflectivity, so it
			// has to be told. (Watermarks need no push; they are a XAML overlay bound to the panes.)
			for (var i = 0; i < count; i++)
			{
				await _mapService.SetRadarProductAsync(i, Panes[i].ProductId);
			}

			RefreshSegmentReadiness(); // the visible product SET changed, so the frontier may have moved
		}

		/// <summary>The radar products (moments) selectable in the Product combo — the single source the
		/// combo binds to, mirroring the JS registry in <c>radar-products.js</c>. <see cref="RadarProductOption.Id"/>
		/// must match the JS product id passed to <c>window.setRadarProduct</c>. ⚠️ There is no per-product
		/// "expensive to build" flag any more — every product builds on demand, and the display-ready gate
		/// keys off reflectivity by id (see <c>IsFrameDisplayReady</c>).
		/// Adding a product = one entry here + the JS side (a build fn + ramp + registry entry).</summary>
		public IReadOnlyList<RadarProductOption> RadarProductOptions { get; } = new[]
		{
			new RadarProductOption("reflectivity", "Reflectivity", "Ref"),
			new RadarProductOption("velocity", "Velocity", "Vel"),
			new RadarProductOption("srv", "Storm-Rel Velocity", "SRV"),
			new RadarProductOption("cc", "Correlation Coefficient", "CC"),
			new RadarProductOption("kdp", "Specific Differential Phase", "KDP"),
			new RadarProductOption("zdr", "Differential Reflectivity", "ZDR"),
			new RadarProductOption("sw", "Spectrum Width", "SW"),
		};

		/// <summary>
		/// Fans the WebView's full ramp table (radar-ramps.js, keyed by product id) onto the product
		/// options, so the Product combo can draw EVERY product's scale — not just the active one. Pushed
		/// once when the page loads; unknown ids are ignored and a product with no ramp simply draws none.
		/// </summary>
		public void SetAllRamps(IReadOnlyDictionary<string, RadarRampInfo>? ramps)
		{
			if (ramps is null) return;
			foreach (var option in RadarProductOptions)
			{
				option.Ramp = ramps.TryGetValue(option.Id, out var ramp) ? ramp : null;
			}

			// Each pane resolves its ramp from its selected option, so they all have to re-announce now
			// that the table has landed — otherwise a notch draws its product name with no scale beside it.
			foreach (var pane in Panes)
			{
				pane.RaiseProductDerived();
			}
		}

		// ===== Tilt (elevation) selection ===============================================================
		// Unlike a PRODUCT switch — which re-renders bytes already decoded in the WebView — a TILT switch
		// needs different bytes entirely: each cached .V06 holds exactly ONE tilt, which is why the JS
		// never learned about tilts (its Math.min(elevations) picks whatever tilt the file contains). So
		// changing tilt reloads the loop through the normal load path, just with a different tilt angle.
		//
		// The choices come from the VCP's designed elevation table, which rides in every cached tilt's
		// metadata — so the list populates from the newest frame with no extra fetch, and re-populates if
		// the radar changes VCP.

		// How many tilts the LIVE loop offers, counting up from the base.
		//
		// A radar scans bottom-up over a ~4.5-min volume, so a tilt's freshness floor is set by when the
		// antenna reaches it: the bottom ~4 are cut within the first ~2 min and the chunks feed can serve
		// them ~2-3 min old, but by 8°+ the tilt isn't scanned until ~4 min in and its best-case age has
		// converged on the archive's ~5-10 min — there's nothing left to win, so offering it would just
		// be shipping stale data behind a live-looking UI. 4 matches what RadarScope exposes.
		//
		// Past Event replay is NOT capped: every frame there is historical, so 19.5° from 2013 is exactly
		// as current as 0.5° from 2013 and there's no freshness to protect. See docs/radar-tilts.md.
		private const int LiveTiltCount = 4;

		// The full designed tilt list of the last-loaded volume's VCP, before the live cap. Retained so
		// the list can be rebuilt when the temporal mode flips (live <-> replay) without waiting for the
		// next frame to land.
		private IReadOnlyList<float> _vcpAngles = Array.Empty<float>();

		/// <summary>The tilts selectable for the current site + mode (base tilt first): the whole VCP in
		/// replay, the freshest <see cref="LiveTiltCount"/> in a live loop. Empty until the first frame
		/// loads, or when the VCP doesn't parse — the combo is then disabled rather than offering a
		/// guess.</summary>
		public ObservableCollection<RadarTiltOption> RadarTiltOptions { get; } = new();

		// The angle currently loaded; null = base tilt. This is the field the fetch path keys on, so it
		// must be updated BEFORE any load is started.
		private float? _selectedTiltAngle;

		private int _radarTiltIndex;

		/// <summary>Selected index into <see cref="RadarTiltOptions"/>. Bound to the Radar console's Tilt
		/// combo. Changing it reloads the loop at that elevation (see the region comment).</summary>
		public int RadarTiltIndex
		{
			get => _radarTiltIndex;
			set
			{
				if (value < 0 || value >= RadarTiltOptions.Count || !SetProperty(ref _radarTiltIndex, value))
				{
					return;
				}

				_selectedTiltAngle = RadarTiltOptions[value].Angle;
				OnPropertyChanged(nameof(SelectedTiltLabel));
				SetTiltUnavailable(null);   // a fresh pick; the engine re-arms it if this one is absent too
				_engine.ReloadForTiltChange();
			}
		}

		/// <summary>The loaded tilt, for the Selected Site readout ("0.5°"). Empty with no loop.</summary>
		public string SelectedTiltLabel =>
			_radarTiltIndex >= 0 && _radarTiltIndex < RadarTiltOptions.Count
				? RadarTiltOptions[_radarTiltIndex].Label
				: string.Empty;

		/// <summary>Whether a tilt can be picked: a loop is up and its VCP offered more than one.</summary>
		public bool CanSelectTilt => RadarTiltOptions.Count > 1;

		private float? _tiltUnavailableAngle;

		/// <summary>
		/// Why the tilt picker snapped back to the base tilt, or empty when it didn't. A volume can carry
		/// FEWER cuts than its VCP designs — AVSET terminates the scan early once there is nothing aloft
		/// worth scanning — so a tilt the picker legitimately offers can be absent from the actual data
		/// (measured: KTLX VCP 212 designs 17 cuts to 19.5° and ships 12 topping at 6.4°).
		/// <para>⚠️ This is a NORMAL, DELIBERATE radar behaviour, not an error, and saying so is the whole
		/// point: the fallback already worked, but it was SILENT — the picker jumped back to 0.5° with no
		/// reason given, which reads as the app ignoring the click. Silent wrongness is the worst outcome
		/// this codebase has, so the fallback now names its cause.</para>
		/// <para>⚠️ Surfaced as the tilt combo's TOOLTIP, not as label text: the readouts around it carry
		/// reserved fixed widths, and a variable-length suffix would resize the notch. Same reasoning as
		/// the Location key carrying <c>LocateStatusText</c> in its tooltip because the bar has no status
		/// area.</para>
		/// </summary>
		public string TiltUnavailableNotice =>
			_tiltUnavailableAngle is { } a
				? $"{a:0.0}° is not in this volume — the radar stopped scanning below it (AVSET). "
					+ "Showing the base tilt."
				: string.Empty;

		/// <summary>Records that <paramref name="angle"/> was absent so the UI can say why it fell back;
		/// null clears it. Called by the loop engine's one fallback seam, <c>SetTiltToBase</c>.</summary>
		internal void SetTiltUnavailable(float? angle)
		{
			if (Nullable.Equals(_tiltUnavailableAngle, angle))
			{
				return;
			}

			_tiltUnavailableAngle = angle;
			OnPropertyChanged(nameof(TiltUnavailableNotice));
		}

		// Rebuilds the tilt list from a freshly-loaded volume's VCP elevation table, preserving the
		// current selection BY ANGLE (a VCP change reorders/renumbers tilts, so an index would silently
		// jump to a different elevation). Falls back to the base tilt when the loaded angle is gone from
		// the new VCP — e.g. the radar dropped from precip to clear-air, which scans fewer tilts. No-op
		// when the list is unchanged, so this can be called per frame.
		//
		// Pass null to rebuild from the last-known VCP (used when the temporal mode flips, which changes
		// the cap but not the radar).
		private void UpdateTiltOptions(IReadOnlyList<float>? angles)
		{
			if (angles is { Count: > 0 })
			{
				_vcpAngles = angles;
			}
			angles = _vcpAngles;

			// Live loops only offer tilts the chunks feed can serve FRESH; replay offers the lot.
			if (!IsPastEventMode && angles.Count > LiveTiltCount)
			{
				angles = angles.Take(LiveTiltCount).ToList();
			}

			var next = new List<RadarTiltOption>();
			if (angles is { Count: > 0 })
			{
				// The lowest angle IS the base tilt, so it takes a NULL angle rather than its own value —
				// that null is what routes it to the cheap prefix fetch and the live frame. Labels are the
				// designed angles rounded for display (0.88° reads "0.9°"), but the ANGLE carried is the
				// unrounded table value, which is what the extractor matches against.
				next.Add(new RadarTiltOption($"{angles[0]:0.0}°", null));
				for (var i = 1; i < angles.Count; i++)
				{
					next.Add(new RadarTiltOption($"{angles[i]:0.0}°", angles[i]));
				}
			}
			else
			{
				// No VCP table (a legacy/raw volume): we're showing the lowest tilt but can't know what
				// else exists, so offer only that. CanSelectTilt is then false and the combo is disabled —
				// no guessing at tilts we can't fetch.
				next.Add(new RadarTiltOption("0.5°", null));
			}

			if (next.Count == RadarTiltOptions.Count
				&& next.Zip(RadarTiltOptions).All(p => p.First.Label == p.Second.Label))
			{
				return; // same VCP, same tilts
			}

			RadarTiltOptions.Clear();
			foreach (var option in next)
			{
				RadarTiltOptions.Add(option);
			}

			// Keep showing the same ELEVATION across a VCP change where possible.
			var keep = RadarTiltOptions
				.Select((o, i) => (o, i))
				.FirstOrDefault(p => Nullable.Equals(p.o.Angle, _selectedTiltAngle));
			_radarTiltIndex = keep.o is not null ? keep.i : 0;
			_selectedTiltAngle = RadarTiltOptions[_radarTiltIndex].Angle;

			OnPropertyChanged(nameof(RadarTiltIndex));
			OnPropertyChanged(nameof(SelectedTiltLabel));
			OnPropertyChanged(nameof(CanSelectTilt));
		}

		// ===== SITE MARKERS — three toggles, one per network (MapControlsStrip, left of the site picker) =====
		// ⚠️ MAP MARKERS ONLY (2026-09-26). They used to be Settings checkboxes that switched TDWR / research off
		// APP-WIDE (Atlas list + site picker too); now every list shows every network and these hide keys on the
		// map, nothing else. All three off = no markers (the old "hide all sites" eye is gone). Each is
		// independent, PERSISTED, pushed at map-ready, and never touches a loaded loop.

		/// <summary>Whether the operational NEXRAD markers are shown. PERSISTED (<see cref="AppSettings.ShowNexradSites"/>).</summary>
		public bool ShowNexradSites
		{
			get => _settings.Settings.ShowNexradSites;
			set
			{
				if (_settings.Settings.ShowNexradSites == value) { return; }
				_settings.Settings.ShowNexradSites = value; // persists (auto-save)
				OnPropertyChanged();
				if (_isMapReady) { _ = _mapService.SetNexradSitesVisibleAsync(value); }
			}
		}

		/// <summary>
		/// Whether the research/test radar markers (e.g. KCRI) are shown. Off by default (an opt-in extra
		/// layer, mirroring RadarScope). A research site loads/renders through the same pipeline as an
		/// operational one. PERSISTED via <see cref="AppSettings.ShowResearchRadars"/> (auto-saved).
		/// </summary>
		public bool ShowResearchRadars
		{
			get => _settings.Settings.ShowResearchRadars;
			set
			{
				if (_settings.Settings.ShowResearchRadars == value) { return; }
				_settings.Settings.ShowResearchRadars = value; // persists (auto-save)
				OnPropertyChanged();
				if (_isMapReady) { _ = _mapService.SetResearchRadarsVisibleAsync(value); }
			}
		}

		// ===== SITE ERA — retired radar ids (moved / renamed: KLIX → KHDC, TPBI → TDJT) =====
		// A retired id has archive data only up to RadarSite.RetiredOn. It is useless live (it would sit red
		// forever beside its replacement), but it's the only way to replay the old radar — so it's hidden
		// live and shown in PastCast while the replay window starts on or before that day. The window is
		// the LOADED one once there is one, else the pickers (so dialling 2021 reveals KLIX before Load).
		// ⚠️ Lists AND markers follow it: lists through IsInEra (+ SiteEraKey to re-filter), markers
		// through setRadarSitesOutOfEra. RefreshSiteEra runs wherever the mode or window can change.

		/// <summary>Whether <paramref name="site"/> existed in the era being viewed. Always true for a working site.
		/// ⚠️ THE rule every site LIST follows (Atlas, site picker) — networks no longer hide from lists, only
		/// from the map (the three marker toggles). Lists re-filter on <see cref="SiteEraKey"/>.</summary>
		public bool IsInEra(RadarSite site) =>
			site.RetiredOn is null // skip the window maths for the ~200 working sites
			|| RadarSiteEra.IsInEra(site, _isPastEventMode ? _loadedWindowStartUtc ?? ReplayStartUtc() : null);

		private string _siteEraKey = "\0"; // never a real key, so the ctor's first refresh always lands

		/// <summary>The out-of-era site ids, joined — changes exactly when the set does. Lists listen for it
		/// the way they listen for the network toggles.</summary>
		public string SiteEraKey
		{
			get => _siteEraKey;
			private set => SetProperty(ref _siteEraKey, value);
		}

		private List<string> OutOfEraIds() =>
			_radarSiteProvider.GetSites().Where(s => !IsInEra(s)).Select(s => s.Id).ToList();

		private void RefreshSiteEra()
		{
			var hidden = OutOfEraIds();
			var key = string.Join(",", hidden);
			if (key == _siteEraKey)
			{
				return;
			}
			SiteEraKey = key;
			if (_isMapReady)
			{
				_ = _mapService.SetRadarSitesOutOfEraAsync(System.Text.Json.JsonSerializer.Serialize(hidden));
			}
		}

		/// <summary>
		/// Whether the TDWR markers (the FAA Terminal Doppler Weather Radar `T***` network) are shown. Off by
		/// default (an opt-in extra layer, mirroring RadarScope). A TDWR loads/renders through the same pipeline
		/// as an operational site. PERSISTED via <see cref="AppSettings.ShowTdwrs"/> (auto-saved).
		/// </summary>
		public bool ShowTdwrs
		{
			get => _settings.Settings.ShowTdwrs;
			set
			{
				if (_settings.Settings.ShowTdwrs == value) { return; }
				_settings.Settings.ShowTdwrs = value; // persists (auto-save)
				OnPropertyChanged();
				if (_isMapReady) { _ = _mapService.SetTdwrsVisibleAsync(value); }
			}
		}

		// Cancels the two APP-LIFETIME loops below when the window closes. Separate from _loopCts, which is
		// per-radar-loop and is re-created on every site click; this one is created once and never reset.
		private readonly CancellationTokenSource _shutdown = new();

		/// <summary>
		/// Stops every loop this subsystem owns. Called from <see cref="MapViewModel.Shutdown"/> on the main
		/// window's Closed.
		/// </summary>
		/// <remarks>
		/// ⚠️ Cancels BOTH tokens, and they have different owners: <c>_shutdown</c> stops the two loops
		/// started by <see cref="OnMapsReadyAsync"/>, while <c>_loopCts</c> is the ENGINE's — cancelling it is
		/// exactly what a site switch already does, so the engine's playback / refresh / live-poll / debug-tick
		/// loops stop through the path they already have. Nothing in RadarLoopEngine changes for this.
		/// </remarks>
		public void Shutdown()
		{
			_shutdown.Cancel();
			_loopCts?.Cancel();
		}

		// App-lifetime 1s tick that advances the radar next-update progress bar.
		private async Task RunProgressTickAsync()
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
				while (await timer.WaitForNextTickAsync(_shutdown.Token))
				{
					OnPropertyChanged(nameof(RadarNextFrameProgress));
					OnPropertyChanged(nameof(RadarNextFrameText));
				}
			}
			catch (OperationCanceledException)
			{
				// Window closed. This tick raises PropertyChanged straight at live bindings, so it must not
				// outlive the XAML tree it is notifying.
			}
		}

		/// <summary>Called by MapViewModel once the map page is ready: shows the radar site
		/// markers and starts the offline-status + progress loops.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await PushRadarOpacityAsync();

			// Provide the radar sites as clickable on-map markers. `research`/`tdwr` flag the extra
			// networks so the page can gate them behind the "Show Research Radars" / "Show TDWRs" toggles.
			var sites = _radarSiteProvider.GetSites()
				.Select(s => new { id = s.Id, name = s.Name, lng = s.Longitude, lat = s.Latitude,
					research = s.Class == RadarSiteClass.Research, tdwr = s.Class == RadarSiteClass.Tdwr });
			await _mapService.ShowRadarSitesAsync(System.Text.Json.JsonSerializer.Serialize(sites));

			// Push the PERSISTED per-network marker visibility (the page's defaults are NEXRAD on, the two
			// opt-in networks off; apply the saved choices explicitly so the toggles and page never disagree).
			await _mapService.SetNexradSitesVisibleAsync(ShowNexradSites);
			await _mapService.SetResearchRadarsVisibleAsync(ShowResearchRadars);
			await _mapService.SetTdwrsVisibleAsync(ShowTdwrs);
			await _mapService.SetRadarSitesOutOfEraAsync(System.Text.Json.JsonSerializer.Serialize(OutOfEraIds()));

			// The PERSISTED range-ring colour and ruler anchor (every command this VM holds is replayed here).
			await PushScopePreferencesAsync();

			// Lay the panes out from the view model's layout (Single at launch). Pushed even when it IS
			// single so the page takes its groove width from PaneLayoutInfo rather than its own default,
			// and so a restored layout applies without the user touching the picker.
			await _mapService.SetPaneLayoutAsync(
				_paneLayout.Columns(), _paneLayout.Rows(), PaneLayoutInfo.GutterPx);

			// Pre-warm the decode + VWP workers now (creating them + loading the vendored decoder), so the
			// first site click doesn't pay their cold start on the first-paint critical path.
			await _mapService.PrewarmRadarAsync();

			// Flag sites with no recent data ("offline") and keep that refreshed.
			_ = RunSiteStatusLoopAsync();

			// Drive the next-update progress bar (radar live frame).
			_ = RunProgressTickAsync();
		}

		private async Task RunSiteStatusLoopAsync()
		{
			try
			{
				while (!_shutdown.IsCancellationRequested)
				{
					await RefreshLiveSiteStatusAsync();
					await Task.Delay(TimeSpan.FromMinutes(10), _shutdown.Token);
				}
			}
			catch (OperationCanceledException)
			{
				// Window closed mid-wait; a 10-minute sleep is otherwise the longest-lived thing in the app.
			}
		}

		// ===== SITE AVAILABILITY — the ONE writer of RadarSiteRow.Availability + the marker status push =====
		// Everything that shows whether a site is up reads what this block writes: the on-map key's square, the
		// Atlas's dot / status pill / "Online only" filter, the site picker's dot, the dev sweep's skip list.
		// Evidence from three places lands here and is graded by ONE rule (RadarSiteStatus.IsFresh): the 10-min
		// archive pass, a scan time the Atlas fetched, and a loaded loop's newest frame.
		// ⚠️ Don't write a row's availability anywhere else — two writers is how a list and the map disagree.

		/// <summary>Raised after availability changes (a pass, a reset, or one site's evidence), so filtered
		/// lists can re-filter.</summary>
		public event EventHandler? SiteAvailabilityChanged;

		// The running pass. ⚠️ _sitePassId drops a result from a pass that has been superseded (a PastCast exit
		// starts a new one mid-loop; a replay load's day availability replaces it) — and Progress<T> POSTS, so a
		// report can land after its pass returned.
		private int _sitePassId;
		private bool _isSiteCheckRunning;
		private int _sitesChecked;

		/// <summary>True while a live availability pass is running (the map is cascading).</summary>
		public bool IsSiteCheckRunning
		{
			get => _isSiteCheckRunning;
			private set => SetProperty(ref _isSiteCheckRunning, value);
		}

		/// <summary>Sites whose result has landed in the running pass, out of <see cref="SiteCheckTotal"/>. Sites
		/// with no archive folder at all aren't probed one by one, so this jumps to the total when the pass ends.</summary>
		public int SitesChecked
		{
			get => _sitesChecked;
			private set => SetProperty(ref _sitesChecked, value);
		}

		public int SiteCheckTotal => RadarSiteRows.Count;

		private bool _isSiteCheckAnnounced;

		/// <summary>Whether the running pass is worth ANNOUNCING (the map's site-check toast): it began with sites
		/// still unchecked — the launch pass, or the one after leaving PastCast. The 10-min refreshes run silently.</summary>
		public bool IsSiteCheckAnnounced
		{
			get => _isSiteCheckAnnounced;
			private set => SetProperty(ref _isSiteCheckAnnounced, value);
		}

		/// <summary>Sites currently known up / down (for the "site check complete" summary).</summary>
		/// <remarks>Out-of-era (retired) sites aren't counted — live, KLIX would be a permanent "offline".</remarks>
		public int SitesOnline => RadarSiteRows.Count(r => IsInEra(r.Site) && r.Availability == SiteAvailability.Online);
		public int SitesOffline => RadarSiteRows.Count(r => IsInEra(r.Site) && r.Availability == SiteAvailability.Offline);

		/// <summary>Raised when a live pass ENDS: <c>true</c> = completed (counts are final), <c>false</c> = it
		/// failed or was cut short (a PastCast replay's availability replaced it).</summary>
		public event EventHandler<bool>? SiteCheckFinished;

		// One live pass. Skipped in PastCast, where availability is the REPLAY DAY's (ApplyPastAvailabilityAsync).
		// Each site lands the moment its probe does (OnSiteChecked → one marker), then the whole set reconciles.
		// ⚠️ A failed pass keeps what we already knew — the service throws on a total listing failure rather
		// than returning "nothing is live" and turning every site red.
		private async Task RefreshLiveSiteStatusAsync()
		{
			if (_isPastEventMode)
			{
				return;
			}

			var pass = ++_sitePassId;
			SitesChecked = 0;
			// Decided BEFORE IsSiteCheckRunning flips, so the toast sees it when it reacts to that flip.
			IsSiteCheckAnnounced = RadarSiteRows.Any(r => r.Availability == SiteAvailability.Unknown);
			IsSiteCheckRunning = true;
			var completed = false;
			try
			{
				// Constructed HERE, on the UI thread, so every report is posted back to it.
				var progress = new Progress<SiteCheckResult>(result => OnSiteChecked(pass, result));
				var live = await _radarService.GetLiveSiteIdsAsync(progress);
				// ⚠️ NOT gated on _isPastEventMode: a launch that restores PastCast flips the mode mid-pass, and
				// dropping the result then left every site grey until a replay loaded. Only a replay-day
				// availability supersedes a live pass (ApplyPastAvailabilityAsync → SupersedeLivePass).
				if (pass == _sitePassId)
				{
					await ApplySiteAvailabilityAsync(live, replayDay: false);
					completed = true;
				}
			}
			catch (Exception ex)
			{
				Services.RadarDiagnostics.Log("vm", "site.status.failed", ("error", ex.GetType().Name));
			}
			finally
			{
				if (pass == _sitePassId)
				{
					if (completed)
					{
						SitesChecked = SiteCheckTotal;
						OnPropertyChanged(nameof(SitesOnline));
						OnPropertyChanged(nameof(SitesOffline));
					}
					IsSiteCheckRunning = false;
					SiteCheckFinished?.Invoke(this, completed);
				}
			}
		}

		// One site's result, mid-pass: its row and its ONE marker, so the map cascades. Rows and marker change
		// together here exactly as they do in ApplySiteAvailabilityAsync — the one-writer rule still holds.
		private void OnSiteChecked(int pass, SiteCheckResult result)
		{
			if (pass != _sitePassId || !_isSiteCheckRunning)
			{
				return;
			}
			SitesChecked++;

			var row = RadarSiteRows.FirstOrDefault(r => string.Equals(r.Id, result.SiteId, StringComparison.OrdinalIgnoreCase));
			var next = result.IsLive ? SiteAvailability.Online : SiteAvailability.Offline;
			if (row is null || (row.Availability == next && !row.IsReplayDay))
			{
				return;
			}
			row.SetAvailability(next, isReplayDay: false);
			if (_isMapReady)
			{
				_ = _mapService.SetRadarSiteStatusAsync(row.Id, result.IsLive ? "online" : "offline");
			}
		}

		// A replay's availability is about to own the rows: drop any live pass still in flight (its results and
		// its end), and end its "running" state here since its own finally no longer will.
		private void SupersedeLivePass()
		{
			_sitePassId++;
			if (IsSiteCheckRunning)
			{
				IsSiteCheckRunning = false;
				SiteCheckFinished?.Invoke(this, false);
			}
		}

		// Leaving PastCast: grey every site ("Checking…") until the live pass lands.
		private async Task ResetThenRefreshLiveSiteStatusAsync()
		{
			foreach (var row in RadarSiteRows)
			{
				row.SetAvailability(SiteAvailability.Unknown, isReplayDay: false);
			}
			await PushSiteStatusAsync();
			SiteAvailabilityChanged?.Invoke(this, EventArgs.Empty);
			await RefreshLiveSiteStatusAsync();
		}

		// A whole pass: the given AVAILABLE ids are online, every other site offline. replayDay says which
		// question was answered (live feed vs. had data on the PastCast day), and the rows label it.
		private async Task ApplySiteAvailabilityAsync(IReadOnlyCollection<string> availableIds, bool replayDay)
		{
			var available = new HashSet<string>(availableIds, StringComparer.OrdinalIgnoreCase);
			// These awaits resume on the UI thread, so updating the observable rows is safe.
			foreach (var row in RadarSiteRows)
			{
				row.SetAvailability(available.Contains(row.Id) ? SiteAvailability.Online : SiteAvailability.Offline, replayDay);
			}
			await PushSiteStatusAsync();
			SiteAvailabilityChanged?.Invoke(this, EventArgs.Empty);

			// ⚠️ Always NAME the ids (capped): the old "only when ≤ 20" gate left a 22-site outage unreadable.
			var offline = RadarSiteRows.Where(r => r.IsOffline).Select(r => r.Id).ToList();
			Services.RadarDiagnostics.Log("vm", "site.status",
				("scope", replayDay ? "replay-day" : "live"), ("offline", offline.Count),
				("ids", string.Join(",", offline.Take(80))));
		}

		/// <summary>
		/// One piece of evidence for one site: its newest scan time, graded by <see cref="RadarSiteStatus.IsFresh"/>
		/// — the same rule as the pass. Live mode only (a replay-day status isn't about the live feed).
		/// <paramref name="canMarkOffline"/> is false for evidence that can be transiently old (a loop mid-load).
		/// </summary>
		internal void ReportSiteScan(RadarSite site, DateTimeOffset newestScanUtc, bool canMarkOffline)
		{
			if (_isPastEventMode)
			{
				return;
			}
			var row = RadarSiteRows.FirstOrDefault(r => r.Site == site);
			if (row is null)
			{
				return;
			}

			var fresh = RadarSiteStatus.IsFresh(newestScanUtc, DateTimeOffset.UtcNow);
			if (!fresh && !canMarkOffline)
			{
				return;
			}
			var next = fresh ? SiteAvailability.Online : SiteAvailability.Offline;
			if (row.Availability == next && !row.IsReplayDay)
			{
				return;
			}

			Services.RadarDiagnostics.Log("vm", "site.status.evidence",
				("site", site.Id), ("to", next.ToString()), ("scanUtc", newestScanUtc.ToString("O")));
			row.SetAvailability(next, isReplayDay: false);
			_ = PushSiteStatusAsync();
			SiteAvailabilityChanged?.Invoke(this, EventArgs.Empty);
		}

		// The markers get the SAME state the rows hold — offline, not-yet-checked, and which question it answers.
		private Task PushSiteStatusAsync()
		{
			if (!_isMapReady)
			{
				return Task.CompletedTask; // the first pass runs after map-ready and pushes then
			}
			var payload = new
			{
				offline = RadarSiteRows.Where(r => r.Availability == SiteAvailability.Offline).Select(r => r.Id).ToList(),
				unknown = RadarSiteRows.Where(r => r.Availability == SiteAvailability.Unknown).Select(r => r.Id).ToList(),
				replayDay = RadarSiteRows.Any(r => r.IsReplayDay),
			};
			return _mapService.SetRadarSitesStatusAsync(System.Text.Json.JsonSerializer.Serialize(payload));
		}

		// Past Event Viewer: gray out sites that had no data on the window's UTC date(s), so you can see
		// availability before clicking. Guarded so a late response can't clobber a live view if the user
		// left past mode meanwhile. Best-effort (a failed listing just leaves sites shown as available).
		private async Task ApplyPastAvailabilityAsync(DateTimeOffset startUtc, DateTimeOffset endUtc)
		{
			SupersedeLivePass();
			try
			{
				var available = await _radarService.GetSiteIdsForDateAsync(startUtc, endUtc);
				if (_isPastEventMode)
				{
					await ApplySiteAvailabilityAsync(available, replayDay: true);
				}
			}
			catch
			{
				// ignore
			}
		}
	}
}
