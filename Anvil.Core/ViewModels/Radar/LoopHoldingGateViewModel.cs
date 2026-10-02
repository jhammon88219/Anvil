using System;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>The gate's screens. <see cref="Cancelling"/> covers the moment between the click and the
	/// loop being truncated, so a load that bails in that window can't hide the gate under the popup.
	/// <see cref="Ready"/> = the loop finished while the screen was SEEN; it waits for "View event".</summary>
	public enum LoopGateState { Hidden, Holding, ConfirmingEscape, Cancelling, Cancelled, Ready }

	/// <summary>
	/// The LOOP HOLDING GATE: while a PastCast loop loads, the map is dimmed behind a progress screen and takes
	/// no input. Sub-VM of <see cref="RadarViewModel"/> (<c>Radar.LoopGate</c>); drawn by
	/// <c>Controls/Composites/LoopHoldingGate</c> over the map band.
	/// </summary>
	/// <remarks>
	/// <code>
	///   Load ─▶ Begin ─▶ Holding ──(every frame settled)──▶ Ready ──"View event"──▶ Hidden
	///                     │  ▲                   (or Hidden at once if the screen never faded in)
	///                     │  │
	///        "Use the map"│  │"Keep waiting"
	///                     ▼  │
	///               ConfirmingEscape ──"Use the map"──▶ Hidden  (+ "Don't hold future loads" → setting off)
	///                                                    │ still loading: the BAR's readout shows the progress,
	///                                                    └ and its "Hold the map again" → ReturnToHold → Holding
	///   Holding ──"Cancel load"──▶ Cancelling ──(loop cut to its lit run)──▶ Cancelled ──"Got it"──▶ Hidden
	/// </code>
	/// WHY (2026-10-01): a PastCast load keeps a 4-core machine near 100% CPU, and WinUI 3 hands mouse input to the
	/// WebView2 THROUGH the UI thread, so the map lags until the loop is built — measured, chased, and parked
	/// (docs/performance-measurement.md). The gate turns that into an honest wait: real counts, an exit, and a
	/// way to accept the lag. The user's call: EVERY PastCast load is held, even light ones.
	/// ⚠️ TRUTH ONLY: the counts are the engine's (frames whose volume arrived; scrubber cells lit), never a
	/// timer or an estimate. The engine pushes them through <see cref="Report"/>.
	/// ⚠️ The escape is deliberately the quiet path: a borderless button, then a confirm that says what you
	/// get. The permanent opt-out lives INSIDE that confirm and in Settings → Radar → PastCast.
	/// ⚠️ READY WAITS FOR THE PRESS (the user's call, 2026-10-02): someone reading <see cref="WhyText"/> isn't yanked
	/// onto the map. But only a screen that was SEEN waits: a load finishing inside <see cref="FadeInDelayMs"/> (a
	/// cached replay) never showed the gate, so it just releases — and after "Use the map" there is nothing to wait on.
	/// </remarks>
	public sealed class LoopHoldingGateViewModel : ObservableObject
	{
		/// <summary>The confirm's sentence (wording lives here, not in XAML).</summary>
		public const string EscapeWarning = "The map and the app will be sluggish until the loop finishes loading.";

		/// <summary>The map's frosting while the gate is up, in CSS px (map.js <c>setMapBlur</c>). TUNE HERE — with
		/// the dim (LoopHoldingGate.xaml's Root background) it should leave colour washes and no readable shapes.</summary>
		public const double MapBlurPx = 14;

		/// <summary>The dim AND the blur start this long after the gate goes up, so a load that finishes inside it
		/// (a cached replay, well under a second) never visibly frosts. Input is gated at once regardless.</summary>
		public const int FadeInDelayMs = 250;

		/// <summary>Why a loop takes time — the line above everything (three drafts combined, the user's call 2026-10-02).</summary>
		public const string WhyText =
			"This is raw radar data, not a video. A radar sends a new scan every 4–6 minutes, so two hours is about 25 scans. "
			+ "Each one is a few megabytes, downloaded from NOAA's archive and drawn here on your computer, one frame at a time. "
			+ "Longer timeframes mean more frames.";

		/// <summary>The clock the "was the screen seen" test reads (ms). Swappable for tests.</summary>
		internal Func<long> NowMs { get; set; } = () => Environment.TickCount64;
		private long _heldSinceMs;
		private string? _eventName;

		// THE LOAD'S CLOCK (the user's call, 2026-10-02): Begin → the last frame built, on NowMs. The milestones feed the
		// load-time log (LoopLoadRecorder → LoopLoadLog) so a "ready in about X" line can be built from real loads.
		private long _beganMs;
		private long? _firstBuiltMs, _allDownloadedMs, _allBuiltMs;
		private bool _escaped;
		private string _elapsedText = string.Empty;
		// Where each landed frame's bytes came from, by loop index (a frame re-landing — a tilt re-cut — overwrites).
		private readonly System.Collections.Generic.Dictionary<int, RadarVolumeSource> _sources = new();

		private readonly ISettingsService _settings;
		private readonly Func<Task> _cancelLoad;
		private LoopGateState _state;
		private bool _armed;     // the loop has actually begun — counts before this are a previous loop's
		private bool _loading;   // a load is in flight, whether or not the gate is up
		private string _siteId = string.Empty;
		private string _eventLine = string.Empty;
		private int _total, _downloaded, _built;
		private string _keptText = string.Empty;
		private bool _neverHoldAgain;

		public LoopHoldingGateViewModel(ISettingsService settings, Func<Task> cancelLoad)
		{
			_settings = settings;
			_cancelLoad = cancelLoad;
		}

		public LoopGateState State
		{
			get => _state;
			private set
			{
				var was = _state;
				if (SetProperty(ref _state, value))
				{
					// The screen's clock starts when the gate comes UP (Begin, ReturnToHold), not on Keep waiting.
					if (was == LoopGateState.Hidden) _heldSinceMs = NowMs();
					OnPropertyChanged(nameof(IsShown));
					OnPropertyChanged(nameof(IsReadyShown));
					OnPropertyChanged(nameof(Title));
					OnPropertyChanged(nameof(DownloadedLabel));
					OnPropertyChanged(nameof(BuiltLabel));
					OnPropertyChanged(nameof(IsProgressShown));
					OnPropertyChanged(nameof(IsActionsShown));
					OnPropertyChanged(nameof(IsConfirmShown));
					OnPropertyChanged(nameof(IsCancelledShown));
					OnPropertyChanged(nameof(AreActionsEnabled));
				}
			}
		}

		/// <summary>The gate is up (dimming the map and swallowing its input).</summary>
		public bool IsShown => _state != LoopGateState.Hidden;
		/// <summary>Why line, title, event line and bars — every screen but the cancelled popup.</summary>
		public bool IsProgressShown => _state is LoopGateState.Holding or LoopGateState.ConfirmingEscape or LoopGateState.Cancelling
			or LoopGateState.Ready;
		public bool IsActionsShown => _state is LoopGateState.Holding or LoopGateState.Cancelling;
		public bool IsConfirmShown => _state == LoopGateState.ConfirmingEscape;
		public bool IsCancelledShown => _state == LoopGateState.Cancelled;
		/// <summary>The loop is built and the screen waits for "View event".</summary>
		public bool IsReadyShown => _state == LoopGateState.Ready;
		/// <summary>False while a cancel is being carried out (one click is enough).</summary>
		public bool AreActionsEnabled => _state == LoopGateState.Holding;

		/// <summary>The chosen saved event's "May 3, 1999 Bridge Creek-Moore, OK", or null for your own timeframe.
		/// Pushed by SavedEventsViewModel (its pick); the engine knows nothing of events.</summary>
		public string? EventName
		{
			get => _eventName;
			set
			{
				if (SetProperty(ref _eventName, string.IsNullOrWhiteSpace(value) ? null : value))
				{
					OnPropertyChanged(nameof(Title));
					OnPropertyChanged(nameof(ReadyButtonText));
				}
			}
		}

		/// <summary>"Loading May 3, 1999 Bridge Creek-Moore, OK" / "Loading the loop"; once built, "Ready: …" / "Loop ready".</summary>
		public string Title => (_state == LoopGateState.Ready, _eventName) switch
		{
			(true, null) => "Loop ready",
			(true, { } name) => $"Ready: {name}",
			(false, null) => "Loading the loop",
			(false, { } name) => $"Loading {name}",
		};

		/// <summary>The Ready screen's one button.</summary>
		public string ReadyButtonText => _eventName is null ? "View loop" : "View event";

		/// <summary>The why line (bound by the gate; wording lives here).</summary>
		public string Why => WhyText;

		/// <summary>The chosen saved event's id (the log's), pushed with <see cref="EventName"/>; null for your own timeframe.</summary>
		public string? EventId { get; set; }

		/// <summary>"That took 2 minutes and 14 seconds to load." — set when the load completes; shown on Ready.</summary>
		public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }

		/// <summary>A load ended — finished, cancelled or abandoned — with its timings. The load-time log listens.</summary>
		public event EventHandler<LoopLoadTiming>? LoadMeasured;

		/// <summary>"2 minutes and 14 seconds", "1 minute", "45 seconds", "0 seconds" (whole seconds, rounded).</summary>
		internal static string Duration(long ms)
		{
			var total = (long)Math.Round(Math.Max(0, ms) / 1000.0);
			long m = total / 60, s = total % 60;
			static string Unit(long n, string one) => $"{n} {one}{(n == 1 ? string.Empty : "s")}";
			return (m, s) switch
			{
				(0, _) => Unit(s, "second"),
				(_, 0) => Unit(m, "minute"),
				_ => $"{Unit(m, "minute")} and {Unit(s, "second")}",
			};
		}

		// ⚠️ ONE record per load, and _loading is what guarantees it: every path that measures ends the load (Complete
		// and the Abandoned paths only while _loading; a Cancel clears _loading before the load unwinds into Abandon).
		private void Measure(LoopLoadOutcome outcome, int kept = 0)
		{
			var now = NowMs();
			long? Since(long? t) => t is { } v ? v - _beganMs : null;
			LoadMeasured?.Invoke(this, new LoopLoadTiming(
				_siteId, EventId, outcome, _total, _downloaded, _built, kept,
				TotalMs: now - _beganMs,
				FirstFrameMs: Since(_firstBuiltMs), AllDownloadedMs: Since(_allDownloadedMs), AllBuiltMs: Since(_allBuiltMs),
				Escaped: _escaped, GateShown: HoldEnabled,
				CachedFrames: _sources.Values.Count(s => s == RadarVolumeSource.CachedTilt),
				LocalRawFrames: _sources.Values.Count(s => s == RadarVolumeSource.LocalRaw),
				NetworkFrames: _sources.Values.Count(s => s == RadarVolumeSource.Network)));
		}

		/// <summary>"KTBW · Sep 28, 2022 · 1:00 PM–4:00 PM" (local time, like the rest of PastCast).</summary>
		public string EventLine { get => _eventLine; private set => SetProperty(ref _eventLine, value); }

		public int Total => _total;
		public int Downloaded => _downloaded;
		public int Built => _built;

		/// <summary>0–1, the thin top bar.</summary>
		public double DownloadedFraction => _total > 0 ? Math.Clamp((double)_downloaded / _total, 0, 1) : 0;
		/// <summary>0–1, the thick bar — the scrubber's lit cells.</summary>
		public double BuiltFraction => _total > 0 ? Math.Clamp((double)_built / _total, 0, 1) : 0;

		// The bars' labels (layout A, the user's call 2026-10-02): a name over each bar's left end, "22 of 28" over its right.
		/// <summary>"Downloading" while it runs, "Downloaded" once every volume is in (or the loop is ready).</summary>
		public string DownloadedLabel => _state == LoopGateState.Ready || (_total > 0 && _downloaded >= _total) ? "Downloaded" : "Downloading";
		public string BuiltLabel => _state == LoopGateState.Ready ? "Frames built" : "Building frames";

		/// <summary>"22 of 28", or "Finding volumes…" before the list is in.</summary>
		public string DownloadedText => _total == 0 ? "Finding volumes…" : $"{_downloaded} of {_total}";

		public string BuiltText => _total == 0 ? string.Empty : $"{_built} of {_total}";

		/// <summary>The cancelled popup's sentence.</summary>
		public string KeptText { get => _keptText; private set => SetProperty(ref _keptText, value); }

		/// <summary>The confirm's "Don't hold future loads" box; applied only if the escape is taken.</summary>
		public bool NeverHoldAgain { get => _neverHoldAgain; set => SetProperty(ref _neverHoldAgain, value); }

		/// <summary>PERSISTED (AppSettings.HoldPastCastLoads), default on. Off = loads never raise the gate.</summary>
		public bool HoldEnabled
		{
			get => _settings.Settings.HoldPastCastLoads;
			set
			{
				if (_settings.Settings.HoldPastCastLoads == value) return;
				_settings.Settings.HoldPastCastLoads = value; // persists (auto-save)
				OnPropertyChanged();
			}
		}

		// ── Engine seams ──────────────────────────────────────────────────────────────────────────────

		// ── The LOAD (tracked whether or not the gate is up) ──────────────────────────────────────────
		// ⚠️ Tracking is the LOAD's, not the screen's: after "Use the map", or with holding turned off, the counts
		// keep coming, because the bar's activity readout (BarActivityViewModel) shows the same numbers there.

		/// <summary>A PastCast load is in flight (Begin → Complete / Abandon / Dismiss / Cancel).</summary>
		public bool IsLoading { get => _loading; private set => SetProperty(ref _loading, value); }

		/// <summary>The loading site's id ("KTLX") — the bar readout's short form of <see cref="EventLine"/>.</summary>
		public string SiteId { get => _siteId; private set => SetProperty(ref _siteId, value); }

		/// <summary>The load finished with every frame settled; the argument is the loop's frame count.</summary>
		public event EventHandler<int>? LoadFinished;

		// ── Engine seams ──────────────────────────────────────────────────────────────────────────────

		/// <summary>A PastCast load with a site is starting. The gate stays hidden when holding is turned off, but
		/// the load is tracked either way.</summary>
		internal void Begin(string siteId, string eventLine)
		{
			if (_loading) Measure(LoopLoadOutcome.Abandoned); // superseded by this load
			_armed = false;
			NeverHoldAgain = false;
			SetCounts(0, 0, 0);
			_beganMs = NowMs();
			_firstBuiltMs = _allDownloadedMs = _allBuiltMs = null;
			_escaped = false;
			_sources.Clear();
			ElapsedText = string.Empty;
			SiteId = siteId;
			EventLine = eventLine;
			IsLoading = true;
			State = HoldEnabled ? LoopGateState.Holding : LoopGateState.Hidden;
		}

		/// <summary>The new loop has begun (its frame arrays are this load's), so counts are now truthful.</summary>
		internal void Arm() => _armed = true;

		/// <summary>The held load's loop has begun (a Cancel has frames to keep).</summary>
		internal bool IsArmed => _armed;

		/// <summary>Whether the engine should report progress (a load is in flight and its loop has begun).</summary>
		internal bool IsTracking => _armed && _loading;

		/// <summary>A frame landed: where its bytes came from (cached tilt / local raw / network). Only this load's.</summary>
		internal void NoteSource(int index, RadarVolumeSource source)
		{
			if (IsTracking) _sources[index] = source;
		}

		internal void Report(int total, int downloaded, int built)
		{
			if (!IsTracking) return;
			SetCounts(total, downloaded, built);
		}

		/// <summary>Every frame of the loop has settled — release.</summary>
		internal void Complete()
		{
			if (!IsTracking) return;
			// ⚠️ Announce FIRST, while IsLoading is still true: the bar's readout decides on this event whether it
			// was the one carrying the load (and flashes "Loop ready"); the IsLoading flip after would clear it first.
			LoadFinished?.Invoke(this, _total);
			IsLoading = false;
			_allBuiltMs ??= NowMs();
			ElapsedText = $"That took {Duration(_allBuiltMs.Value - _beganMs)} to load.";
			Measure(LoopLoadOutcome.Finished);
			if (_state is LoopGateState.Holding or LoopGateState.ConfirmingEscape)
			{
				// A screen that was SEEN waits for "View event"; one that never faded in just releases.
				State = WasSeen ? LoopGateState.Ready : LoopGateState.Hidden;
			}
		}

		// Up at least as long as the fade-in delay = the dim actually showed (LoopHoldingGate fades in after it).
		private bool WasSeen => NowMs() - _heldSinceMs >= FadeInDelayMs;

		/// <summary>"View event" / "View loop": leave the Ready screen for the map.</summary>
		public void ViewReady()
		{
			if (_state == LoopGateState.Ready) State = LoopGateState.Hidden;
		}

		/// <summary>The load failed or was superseded (not via Cancel). Leaves a cancel in progress alone.</summary>
		internal void Abandon()
		{
			if (_loading) Measure(LoopLoadOutcome.Abandoned); // a cancel already cleared _loading and logs itself
			IsLoading = false;
			if (_state is LoopGateState.Holding or LoopGateState.ConfirmingEscape) State = LoopGateState.Hidden;
		}

		/// <summary>Site change, Clear, leaving PastCast, a NowCast load — whatever the gate was showing is moot.</summary>
		internal void Dismiss()
		{
			if (_loading) Measure(LoopLoadOutcome.Abandoned); // left mid-load (site change, Clear, mode off)
			_armed = false;
			IsLoading = false;
			State = LoopGateState.Hidden;
		}

		/// <summary>The cancel has been carried out: say what was kept.</summary>
		internal void ShowCancelled(int kept, int total)
		{
			Measure(LoopLoadOutcome.Cancelled, kept);
			_armed = false;
			IsLoading = false;
			KeptText = kept > 0
				? $"{kept} of {total} frames were complete and are kept. The loop plays what's built. Load again to resume."
				: "No frames were complete yet, so nothing was kept. Load again to resume.";
			State = LoopGateState.Cancelled;
		}

		// ── The screen's actions ──────────────────────────────────────────────────────────────────────

		public void RequestEscape()
		{
			if (_state == LoopGateState.Holding) State = LoopGateState.ConfirmingEscape;
		}

		public void KeepWaiting()
		{
			if (_state == LoopGateState.ConfirmingEscape) State = LoopGateState.Holding;
		}

		public void UseMap()
		{
			if (_state != LoopGateState.ConfirmingEscape) return;
			if (_neverHoldAgain) HoldEnabled = false;
			if (_loading) _escaped = true; // the log notes it: the user used the map while it loaded
			State = LoopGateState.Hidden; // the load stays tracked — the bar's readout carries on with it
		}

		/// <summary>"Hold the map again" (the bar readout's door): back to the gate while the load is still running.</summary>
		public void ReturnToHold()
		{
			if (_loading && _state == LoopGateState.Hidden) State = LoopGateState.Holding;
		}

		public async Task CancelLoadAsync()
		{
			if (_state != LoopGateState.Holding) return;
			IsLoading = false; // the cancel owns what happens next — a late Complete must not announce "ready"
			State = LoopGateState.Cancelling;
			await _cancelLoad();
		}

		public void AcknowledgeCancelled()
		{
			if (_state == LoopGateState.Cancelled) State = LoopGateState.Hidden;
		}

		private void SetCounts(int total, int downloaded, int built)
		{
			if (total == _total && downloaded == _downloaded && built == _built) return;
			_total = total; _downloaded = downloaded; _built = built;
			// The log's milestones — the first time each line is crossed (a Begin's zeroing never crosses one).
			if (total > 0)
			{
				if (built > 0) _firstBuiltMs ??= NowMs();
				if (downloaded >= total) _allDownloadedMs ??= NowMs();
				if (built >= total) _allBuiltMs ??= NowMs();
			}
			OnPropertyChanged(nameof(Total));
			OnPropertyChanged(nameof(Downloaded));
			OnPropertyChanged(nameof(Built));
			OnPropertyChanged(nameof(DownloadedFraction));
			OnPropertyChanged(nameof(BuiltFraction));
			OnPropertyChanged(nameof(DownloadedText));
			OnPropertyChanged(nameof(BuiltText));
			OnPropertyChanged(nameof(DownloadedLabel));
		}
	}
}
