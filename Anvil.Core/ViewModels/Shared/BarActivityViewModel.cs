using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>What the bar's activity readout can be showing. ⚠️ DECLARATION ORDER IS PRIORITY — when several
	/// run at once the first one listed takes the slot and the rest wait behind a "+n" tag. A new activity is a
	/// member here (in its rank) plus a source that calls <see cref="BarActivityViewModel.Set"/>/<c>Clear</c>/<c>Flash</c>.
	/// Search ranks FIRST: it answers a gesture the user just made, so it must never wait behind a "+n". LiveFrame
	/// ranks LAST: it is news every few minutes, never a reason to hide anything else.</summary>
	public enum BarActivityKind { Search, Loop, SiteCheck, LiveFrame }

	/// <summary>The readout's colour: the mark and the bar. Radar = loop work (the gate's built-bar blue),
	/// Housekeeping = app chores, Done = the finish flash, Failed = a flash that says something didn't work,
	/// Idle = the dimmed resting line (nothing active).</summary>
	public enum BarActivityTone { Radar, Housekeeping, Done, Failed, Idle }

	/// <summary>
	/// The BAR'S ACTIVITY READOUT (<c>MapViewModel.Activity</c> → <c>Controls/Composites/BarActivityReadout</c>, in the
	/// bar between the temporal keys and Atlas): what Anvil is WORKING ON right now, when knowing it explains why the
	/// map looks unfinished. Empty bar otherwise.
	/// </summary>
	/// <remarks>
	/// <para>⚠️ ONE SLOT, A PRIORITY STACK: each <see cref="BarActivityKind"/> holds one entry; the highest-ranked
	/// ACTIVE entry is shown, the others count into <see cref="QueuedText"/>. Nothing rotates (rotation reads as
	/// flicker). When an activity finishes it may <see cref="Flash"/> a result for <see cref="FlashMs"/>, then yields.</para>
	/// <para>⚠️ TRUTH ONLY, like the gate: every count is a source's own (the loop's are the gate's — one feed,
	/// <c>RadarViewModel.UpdateLoopGate</c>). No bar is drawn for work that has no real fraction.</para>
	/// <para>The sources today: the PLACE SEARCH's status (moved here from beside the search box, 2026-10-06 — a
	/// variable-width line there broke the left section's edge); the PastCast LOOP while the gate is NOT up (after
	/// "Use the map", or holding turned off) — clicking it is "Hold the map again"; the announced SITE CHECK
	/// (moved here from the map's toast); and NowCast's NEW LIVE FRAME (replaced the map's sweep pulse, 2026-10-07).</para>
	/// </remarks>
	public sealed class BarActivityViewModel : ObservableObject
	{
		/// <summary>How long a finish flash ("Loop ready", "Site check complete") holds before the slot yields.</summary>
		public const int FlashMs = 3000;

		private sealed class Entry
		{
			public bool Active, Flashing, CanReopen;
			public string Title = string.Empty, Detail = string.Empty;
			public double Progress = -1, Secondary = -1; // < 0 = no such bar
			public BarActivityTone Tone;
			public int Version;
		}

		private readonly Entry[] _entries;
		private readonly Func<int, Task> _delay;
		private LoopHoldingGateViewModel? _gate;
		private RadarViewModel? _radar;
		private bool _siteChecking;

		private BarActivityKind _kind;
		private bool _isShown, _canReopen, _hasProgress, _hasSecondary;
		private string _title = string.Empty, _detail = string.Empty, _queuedText = string.Empty;
		private double _progress, _secondary;
		private BarActivityTone _tone;

		/// <param name="delay">The flash timer (tests pass their own); defaults to <see cref="Task.Delay(int)"/>.</param>
		public BarActivityViewModel(Func<int, Task>? delay = null)
		{
			_delay = delay ?? Task.Delay;
			_entries = new Entry[Enum.GetValues<BarActivityKind>().Length];
			for (var i = 0; i < _entries.Length; i++) _entries[i] = new Entry();
		}

		// ── What the readout draws (the top-ranked active entry, flattened for x:Bind) ────────────────

		public bool IsShown { get => _isShown; private set => SetProperty(ref _isShown, value); }
		public BarActivityKind Kind { get => _kind; private set => SetProperty(ref _kind, value); }
		public string Title { get => _title; private set => SetProperty(ref _title, value); }
		/// <summary>The right-hand count ("22 of 39 built", "84 / 159").</summary>
		public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
		public bool HasProgress { get => _hasProgress; private set => SetProperty(ref _hasProgress, value); }
		/// <summary>0–1, the main bar.</summary>
		public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
		public bool HasSecondary { get => _hasSecondary; private set => SetProperty(ref _hasSecondary, value); }
		/// <summary>0–1, the thin line under it (the loop's "downloaded").</summary>
		public double Secondary { get => _secondary; private set => SetProperty(ref _secondary, value); }
		public BarActivityTone Tone { get => _tone; private set => SetProperty(ref _tone, value); }
		/// <summary>Clicking the readout goes back to the activity's own screen (the loop: the gate).</summary>
		public bool CanReopen { get => _canReopen; private set => SetProperty(ref _canReopen, value); }
		/// <summary>"+1" while other activities wait behind the one shown; empty otherwise.</summary>
		public string QueuedText { get => _queuedText; private set => SetProperty(ref _queuedText, value); }

		/// <summary>The readout was clicked: the loop goes back to its gate ("Hold the map again").</summary>
		public void Reopen()
		{
			if (_isShown && _canReopen && _kind == BarActivityKind.Loop) _gate?.ReturnToHold();
		}

		// ── The stack ─────────────────────────────────────────────────────────────────────────────────

		/// <summary>Show (or update) an activity in progress. A pending flash for the same kind is dropped.</summary>
		internal void Set(BarActivityKind kind, string title, string detail, double progress = -1, double secondary = -1,
			BarActivityTone tone = BarActivityTone.Radar, bool canReopen = false)
		{
			var e = _entries[(int)kind];
			if (e.Flashing) { e.Flashing = false; e.Version++; }
			e.Active = true; e.Title = title; e.Detail = detail;
			e.Progress = progress; e.Secondary = secondary; e.Tone = tone; e.CanReopen = canReopen;
			Recompute();
		}

		/// <summary>The activity ended with nothing to say.</summary>
		internal void Clear(BarActivityKind kind)
		{
			var e = _entries[(int)kind];
			if (!e.Active) return;
			e.Active = false; e.Flashing = false; e.Version++;
			Recompute();
		}

		/// <summary>The activity ended: hold a result line for <see cref="FlashMs"/>, then yield.</summary>
		internal void Flash(BarActivityKind kind, string title, string detail, BarActivityTone tone, bool fullBar)
		{
			var e = _entries[(int)kind];
			var version = ++e.Version;
			e.Active = true; e.Flashing = true; e.Title = title; e.Detail = detail;
			e.Progress = fullBar ? 1 : -1; e.Secondary = -1; e.Tone = tone; e.CanReopen = false;
			Recompute();
			_ = ExpireAsync(e, version);
		}

		internal bool IsFlashing(BarActivityKind kind) => _entries[(int)kind].Flashing;

		private async Task ExpireAsync(Entry e, int version)
		{
			await _delay(FlashMs);
			if (e.Version != version) return; // replaced by a newer Set/Flash/Clear
			e.Active = false; e.Flashing = false;
			Recompute();
		}

		private void Recompute()
		{
			var top = -1; var active = 0;
			for (var i = 0; i < _entries.Length; i++)
			{
				if (!_entries[i].Active) continue;
				active++;
				if (top < 0) top = i;
			}
			QueuedText = active > 1 ? $"+{active - 1}" : string.Empty;
			if (top < 0)
			{
				CanReopen = false;
				IsShown = false; // the plate DIMS (it never disappears) and shows the idle line
				ApplyIdle();
				return;
			}
			var e = _entries[top];
			Kind = (BarActivityKind)top;
			Title = e.Title; Detail = e.Detail; Tone = e.Tone; CanReopen = e.CanReopen;
			HasProgress = e.Progress >= 0; Progress = Math.Clamp(e.Progress, 0, 1);
			HasSecondary = e.Secondary >= 0; Secondary = Math.Clamp(e.Secondary, 0, 1);
			IsShown = true;
		}

		// ── Source: the PastCast LOOP (the gate's load, whenever the gate itself isn't up) ─────────────

		/// <summary>Follow a loop holding gate's load. Shown only while the load runs WITHOUT the gate on screen —
		/// with the gate up the bar would just repeat it.</summary>
		internal void WatchLoop(LoopHoldingGateViewModel gate)
		{
			_gate = gate;
			gate.PropertyChanged += (_, _) => SyncLoop();
			gate.LoadFinished += (_, frames) =>
			{
				// Announce "ready" only if the bar was the one carrying this load (the gate's own release says it otherwise).
				var e = _entries[(int)BarActivityKind.Loop];
				if (e.Active && !e.Flashing)
				{
					Flash(BarActivityKind.Loop, "Loop ready", $"{frames} frames", BarActivityTone.Done, fullBar: true);
				}
			};
			SyncLoop();
		}

		private void SyncLoop()
		{
			if (_gate is not { } g) return;
			if (g.IsLoading && !g.IsShown)
			{
				var detail = g.Total == 0 ? "Finding volumes…" : $"{g.Built} of {g.Total} built";
				Set(BarActivityKind.Loop, "Loading the loop", detail,
					g.BuiltFraction, g.DownloadedFraction, BarActivityTone.Radar, canReopen: true);
			}
			else if (!IsFlashing(BarActivityKind.Loop))
			{
				Clear(BarActivityKind.Loop);
			}
		}

		// ── Source: NowCast's NEW LIVE FRAME (replaced the map's sweep pulse, 2026-10-07) ─────────────────

		/// <summary>Follow EVERY NowCast live poll (the user's call: "I like to see what's going on"), in the loop's look
		/// (variant A): the thin line is the poll's chunk download, the main bar the new frame's build in the gate's
		/// steps (⅓ a worker decoding it, ⅔ built and waiting to be drawn). A poll with nothing newer ends on a quiet
		/// "No new scan"; a new frame ends on "Complete" only once the page has DRAWN it — both hold for
		/// <see cref="FlashMs"/>, like "Loop ready".</summary>
		internal void WatchLiveFrame(RadarViewModel radar)
		{
			_liveRadar = radar;
			// The idle line follows the radar's 1 s countdown tick, and any change of mode / site / schedule.
			radar.PropertyChanged += (_, e) =>
			{
				if (_isShown) return;
				if (e.PropertyName is nameof(RadarViewModel.RadarNextFrameText) or nameof(RadarViewModel.ExpectedNextScanAt)
					or nameof(RadarViewModel.IsPastEventMode) or nameof(RadarViewModel.SelectedRadarOption)
					or nameof(RadarViewModel.LivePollingModeIndex))
				{
					ApplyIdle();
				}
			};
			ApplyIdle();
			radar.LiveFrameActivity += (_, a) =>
			{
				var at = a.VolumeTime is { } t ? t.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.CurrentCulture) : null;
				var scan = $"{at} scan";
				switch (a.Stage)
				{
					case LiveFrameStage.Checking:
						Set(BarActivityKind.LiveFrame, "Checking for a new scan",
							a.Total > 0 ? $"{a.Done} of {a.Total} chunks" : "Listing chunks",
							0, a.Total > 0 ? (double)a.Done / a.Total : 0);
						break;
					case LiveFrameStage.Unchanged:
						Flash(BarActivityKind.LiveFrame, "No new scan",
							at is null ? string.Empty : $"{scan} is the newest", BarActivityTone.Housekeeping, fullBar: false);
						break;
					case LiveFrameStage.Failed:
						Flash(BarActivityKind.LiveFrame, "Couldn't check for a new scan", "the next poll tries again",
							BarActivityTone.Failed, fullBar: false);
						break;
					case LiveFrameStage.Found:
						Set(BarActivityKind.LiveFrame, "New scan", $"{scan} · downloaded", 0, 1);
						break;
					case LiveFrameStage.Decoding:
						Set(BarActivityKind.LiveFrame, "New scan", $"{scan} · building", RadarViewModel.DecodingStep, 1);
						break;
					case LiveFrameStage.Painting:
						Set(BarActivityKind.LiveFrame, "New scan", $"{scan} · painting", RadarViewModel.LitStep, 1);
						break;
					case LiveFrameStage.Shown:
						// "Complete" only once DRAWN (the user's rule); built but not drawn (scrubbed back, a hidden window) = "Ready".
						Flash(BarActivityKind.LiveFrame, $"New frame · {at}", a.Drawn ? "Complete" : "Ready", BarActivityTone.Done, fullBar: true);
						break;
					default:
						if (!IsFlashing(BarActivityKind.LiveFrame)) Clear(BarActivityKind.LiveFrame);
						break;
				}
			};
		}

		// ── IDLE: the slot never disappears (the user's call, 2026-10-07: "so the user always knows to look there") ──
		// Nothing active → the plate DIMS (the readout draws IsShown=false at 45%) and shows, while NowCast polls, the
		// countdown to the next check + the expected scan (regime-aware) or the interval (fixed time); otherwise empty.
		// No site id (the slot is narrow, and the site picker sits beside it).

		private RadarViewModel? _liveRadar;

		private void ApplyIdle()
		{
			Kind = BarActivityKind.LiveFrame;
			Tone = BarActivityTone.Idle;
			HasProgress = false;
			if (_liveRadar is { IsLivePolling: true, NextLivePollAt: { } next } r)
			{
				var rem = (int)Math.Ceiling(Math.Max(0, (next - DateTimeOffset.Now).TotalSeconds));
				Title = $"Next check in {rem / 60}:{rem % 60:00}";
				Detail = r.IsRegimeAwarePolling ? ScanDue(r.ExpectedNextScanAt) : $"every {r.RefreshIntervalSeconds:0} s";
				HasSecondary = true;
				Secondary = Math.Clamp(r.RadarNextFrameProgress / 100, 0, 1);
			}
			else
			{
				Title = Detail = string.Empty;
				HasSecondary = false;
			}
		}

		private static string ScanDue(DateTimeOffset? at) =>
			at is not { } t ? "No scan prediction yet"
			: t <= DateTimeOffset.Now ? "Scan due now"
			: $"Scan due ~{t.ToLocalTime().ToString("h:mm tt", System.Globalization.CultureInfo.CurrentCulture)}";

		// ── Source: the PLACE SEARCH's status (was a line beside the search box) ───────────────────────

		/// <summary>Follow the place search: the online geocoder RUNS in the slot (no bar — it has no real fraction), and
		/// its results FLASH ("No places match …" as a failure, "Pick a place" as a prompt). An emptied status (typing,
		/// a pin landing) clears whatever is still up.</summary>
		internal void WatchPlaceSearch(PlaceSearchViewModel search)
		{
			search.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName != nameof(PlaceSearchViewModel.StatusText)) return;
				if (search.IsSearchingOnline)
				{
					Set(BarActivityKind.Search, "Searching places online", string.Empty, tone: BarActivityTone.Housekeeping);
				}
				else if (search.HasStatus)
				{
					Flash(BarActivityKind.Search, search.StatusText, string.Empty,
						search.FoundNothing ? BarActivityTone.Failed : BarActivityTone.Housekeeping, fullBar: false);
				}
				else
				{
					Clear(BarActivityKind.Search);
				}
			};
		}

		// ── Source: the announced SITE CHECK (was the map's SiteCheckToast) ───────────────────────────

		/// <summary>Follow the radar's site-availability passes. ANNOUNCED passes only (launch, leaving PastCast —
		/// the ones that start from grey keys); the 10-minute refreshes stay silent.</summary>
		internal void WatchSiteCheck(RadarViewModel radar)
		{
			_radar = radar;
			radar.PropertyChanged += OnRadarPropertyChanged;
			radar.SiteCheckFinished += OnSiteCheckFinished;
			if (radar.IsSiteCheckRunning && radar.IsSiteCheckAnnounced) ShowSiteCheck(); // already running at build
		}

		private void OnRadarPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (_radar is not { } r) return;
			if (e.PropertyName == nameof(RadarViewModel.IsSiteCheckRunning) && r.IsSiteCheckRunning && r.IsSiteCheckAnnounced)
			{
				ShowSiteCheck();
			}
			else if (e.PropertyName == nameof(RadarViewModel.SitesChecked) && _siteChecking)
			{
				ShowSiteCheck();
			}
		}

		private void ShowSiteCheck()
		{
			if (_radar is not { } r) return;
			_siteChecking = true;
			var total = r.SiteCheckTotal;
			Set(BarActivityKind.SiteCheck, "Checking radar sites", $"{r.SitesChecked} / {total}",
				total == 0 ? 0 : (double)r.SitesChecked / total, tone: BarActivityTone.Housekeeping);
		}

		private void OnSiteCheckFinished(object? sender, bool completed)
		{
			if (!_siteChecking || _radar is not { } r) return;
			_siteChecking = false;
			if (completed)
			{
				Flash(BarActivityKind.SiteCheck, "Site check complete", $"{r.SitesOnline} online · {r.SitesOffline} offline",
					BarActivityTone.Done, fullBar: true);
			}
			else if (r.IsPastEventMode)
			{
				Clear(BarActivityKind.SiteCheck); // entering PastCast cut the pass short on purpose (it checks no sites) — nothing failed
			}
			else
			{
				Flash(BarActivityKind.SiteCheck, "Couldn't check radar sites", "last known status stays",
					BarActivityTone.Failed, fullBar: false);
			}
		}
	}
}
