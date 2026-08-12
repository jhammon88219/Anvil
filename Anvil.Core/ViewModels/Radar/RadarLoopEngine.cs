using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	public sealed partial class RadarViewModel
	{
		// The radar loop ENGINE, extracted from the VM into its own collaborator class: site load, the
		// live-frame poll, playback, the ~5-min archive refresh, the incremental reload, and past-event
		// replay. It owns the loop lifecycle; the VM owns the bindable frame-state + presentation. Nested
		// so it can reach the VM's private frame-state fields through `_vm` without widening them — an
		// explicit seam (every VM touch is a `_vm.` call), not yet a fully decoupled service.
		private sealed class RadarLoopEngine
		{
			private readonly RadarViewModel _vm;
			internal RadarLoopEngine(RadarViewModel vm) => _vm = vm;

		// Appends one free-form radar diagnostic note. High-value events (session start, frame
		// timings, live polls, frame sources) use the typed RadarDiagnostics methods directly so
		// they also feed the rolling report; this is for the incidental lines.
		private static void Diag(string message) => Services.RadarDiagnostics.Log("vm", "note", ("msg", message));

		// Maps a loaded volume to its on-disk .V06 (the radarlevel2 host serves CacheDirectory),
		// so a suspect frame's source can be quarantined.
		private string FrameCacheFile(Models.RadarVolume v) =>
			System.IO.Path.Combine(_vm._radarService.CacheDirectory, v.LocalUrl.Substring(v.LocalUrl.LastIndexOf('/') + 1));

		// Loads a fresh loop for the site (or clears for "None"): recenters immediately, shows
		// the newest frame first, then backfills older frames; also starts the playback and
		// auto-refresh loops tied to this selection. Cancels the previous selection's work.
		internal async Task StartRadarLoopAsync(RadarSite? site)
		{
			Services.RadarDiagnostics.BeginSession(site?.Id);
			_vm._loopCts?.Cancel();
			_vm._loopCts = null;
			_vm.IsPlaying = false;
			_vm.SetLoopEngaged(false); // a freshly (re)loaded loop is stopped -> Stop disabled
			_vm.IsLoopReady = false;

			// Highlight the selected site marker (null clears it).
			if (_vm._isMapReady)
			{
				await _vm._mapService.SetSelectedRadarSiteAsync(site?.Id);
			}

			if (site is null)
			{
				ResetFrameState();
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.MaxFrameIndex));
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
				_vm.RaiseRadarReadout();
				_vm.IsInspecting = false; // no loop to inspect — drop the crosshair + marker
				if (_vm._isMapReady)
				{
					await _vm._mapService.ClearRadarAsync();
					await _vm._mapService.SetRadarSweepAsync(0); // back to the free-running sweep
				}
				return;
			}

			// Start the load timer for this click (frozen once the initial load fully renders).
			_vm._loopClickAt = DateTimeOffset.UtcNow;
			_vm._firstFrameElapsed = null;
			_vm._allFramesElapsed = null;
			_vm._initialLoadDone = false;
			_vm._loadInProgress = true;
			// Until THIS loop has actually begun (BeginRadarLoopAsync below, which bumps the JS
			// loop token), any frame-ready is a leftover from the previous selection still draining
			// through the worker — the JS token only drops stale frames AFTER the bump, so the gap
			// during the keys fetch leaks them. Ignore them so they don't pollute first-frame timing
			// or the ready count of the new session.
			_vm._loopRenderBegun = false;
			_vm._liveModeText = null; // forget the previous site's mode; the new site's poll re-sets it

			// Note: no flyTo — load the radar at the user's current view; they pan/zoom freely.
			var cts = new CancellationTokenSource();
			_vm._loopCts = cts;

			await LoadLoopAsync(site, cts.Token);

			// The frame set (incl. any live frame) is now final; record "all frames" timing once
			// every frame has reported ready (or as they finish, via OnRadarFrameReady).
			_vm._loadInProgress = false;
			MaybeRecordAllFramesLoaded();

			_ = RunPlaybackAsync(cts.Token);
			_ = RunRefreshAsync(site, cts.Token);
			_ = RunLiveFrameRefreshAsync(site, cts.Token);
			_ = RunDebugTickAsync(cts.Token);
		}

		// Zeroes all per-loop frame + live-poll state. Shared by the clear path (StartRadarLoopAsync)
		// and the Past Event Viewer's site-change clear (SelectPastSiteAsync). Callers raise the
		// relevant PropertyChanged / RaiseRadarReadout afterwards.
		private void ResetFrameState()
		{
			_vm._frameCount = 0;
			_vm._archiveCount = 0;
			_vm._hasLiveFrame = false;
			_vm._liveFrame = null;
			_vm._pendingLiveAppend = null;
			_vm._pendingLiveUpdate = null;
			_vm._liveModeText = null;
			_vm._frameTimes = Array.Empty<DateTimeOffset?>();
			_vm._frameModes = Array.Empty<string?>();
			_vm.Segments.Clear();
			_vm._loadedNewestKey = null;
			_vm._loadedKeys = Array.Empty<string>();
			_vm._lastLivePollAt = null;
			_vm._lastLivePollResult = null;
			_vm._nextLivePollAt = null;
			_vm._livePollCycleStart = null;
			_vm._lastLiveError = null;
			_vm._loopClickAt = null;
			_vm._firstFrameElapsed = null;
			_vm._allFramesElapsed = null;
		}

		/// <summary>
		/// Hard reset of the current loop: cancels the in-flight load/playback/refresh, dumps every
		/// frame, and reloads from scratch (re-list keys, re-decode, re-render — recovers a glitched
		/// loop without waiting for the ~5-min auto-refresh). No-op when no site is selected. Reuses
		/// the on-disk volume cache, so it's fast; it re-renders rather than re-downloading bytes.
		/// </summary>
		public void ResetRadarLoop()
		{
			if (_vm._selectedRadarOption?.Site is not { } site)
			{
				return;
			}

			Diag("manual loop reset");
			_ = StartRadarLoopAsync(site);
		}

		// Speculatively downloads the loop's volumes IN FULL in the background so that switching tilts
		// later costs no network — the tilt analogue of velocity prefetch, armed at the same moment (the
		// base loop has finished rendering, so nothing the user is watching competes with it).
		//
		// It prefetches whole VOLUMES rather than tilts because one download already contains every tilt;
		// fetching per tilt would re-pull the same bytes ~9 times over. The bargain is the same one
		// velocity prefetch makes — real cost paid speculatively — except the currency is bandwidth
		// (~10-30 MB per frame beyond the ~5 MB prefix the base tilt needed), not CPU. That's why it's
		// LIVE-LOOPS ONLY: a past-event replay can be far longer than the ~10-frame live loop, so the
		// same policy there could pull a gigabyte for a window the user may never re-cut. Replay still
		// supports every tilt — it just downloads on demand, and since a full download is retained as raw,
		// only the FIRST tilt switch on a replay frame pays.
		internal void StartTiltPrefetch()
		{
			if (_vm.IsPastEventMode || _vm._selectedRadarOption?.Site is not { } site || _vm._loadedKeys.Length == 0)
			{
				return;
			}

			var keys = _vm._loadedKeys;
			var ct = _vm._loopCts?.Token ?? CancellationToken.None;
			_ = Task.Run(async () =>
			{
				try
				{
					await _vm._radarService.PrefetchRawVolumesAsync(site, keys, ct);
				}
				catch (OperationCanceledException)
				{
					// Site changed / app closing.
				}
				catch (Exception ex)
				{
					// Purely speculative: a failure just means a tilt switch downloads, as it would have.
					Services.RadarDiagnostics.Log("vm", "tilt.prefetch.fail", ("error", ex.Message));
				}
			}, ct);
		}

		// Reloads the current loop at the newly-selected elevation (see the Tilt region in
		// RadarViewModel.cs). A tilt switch can't be served from the WebView's decoded frames the way a
		// PRODUCT switch is: each cached .V06 holds exactly one tilt, so the new elevation is genuinely
		// different bytes and has to come through the fetch path. That's cheap once
		// PrefetchRawVolumesAsync has the raw volumes on disk — each frame is then a local extract with
		// no download — and _selectedTiltAngle is already set, so the reload just picks it up.
		internal void ReloadForTiltChange()
		{
			if (_vm.IsPastEventMode)
			{
				if (_vm._frameCount > 0)
				{
					_ = LoadSelectedPastEventAsync(); // replay: re-fetch the same window at the new tilt
				}
				return;
			}

			if (_vm._selectedRadarOption?.Site is not { } site)
			{
				return; // no loop up: the selection is remembered and applies to the next site picked
			}

			Diag($"tilt -> {(_vm._selectedTiltAngle is { } a ? a.ToString("0.0") + "°" : "base")}");
			_ = StartRadarLoopAsync(site);
		}

		// Past mode: a site pick clears any loaded replay and highlights the new site's marker, but
		// starts NOTHING — the user sets a window and hits Load. (Mirrors StartRadarLoopAsync's clear
		// path but keeps the marker on the chosen site so it reads as "armed" and runs no live loop.)
		internal async Task SelectPastSiteAsync(RadarSite? site)
		{
			_vm._loopCts?.Cancel();
			_vm._loopCts = null;
			_vm.IsPlaying = false;
			_vm.SetLoopEngaged(false);
			_vm.IsLoopReady = false;
			_vm.IsInspecting = false;
			Services.RadarDiagnostics.BeginSession(null); // close any open session; Load opens the replay one
			ResetFrameState();
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.MaxFrameIndex));
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
			_vm.RaiseRadarReadout();
			if (_vm._isMapReady)
			{
				await _vm._mapService.SetSelectedRadarSiteAsync(site?.Id);
				await _vm._mapService.ClearRadarAsync();
				await _vm._mapService.SetRadarSweepAsync(0); // no live sweep in replay
			}
		}

		/// <summary>
		/// Loads the historical loop for the selected site over the chosen window (the Load button).
		/// Lists the archive volumes in the window, builds the loop with the same machinery as the
		/// live path, then starts playback — but with NO live poll and NO auto-refresh.
		/// </summary>
		public async Task<bool> LoadSelectedPastEventAsync()
		{
			if (!_vm._isPastEventMode)
			{
				return false;
			}

			// Build the local date from the Year/Month/Day combos; clamp the day to the month's length
			// (so e.g. day 31 in a 30-day month just uses the 30th instead of throwing). Combine with
			// the time-of-day, then convert to UTC for the bucket query (DST-correct for that date).
			var year = _vm.PastEventYearOptions[_vm._pastEventYearIndex];
			var month = _vm._pastEventMonthIndex + 1;
			var day = Math.Min(_vm._pastEventDayIndex + 1, DateTime.DaysInMonth(year, month));
			var localMidnight = new DateTimeOffset(year, month, day, 0, 0, 0,
				TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day)));
			var localStart = localMidnight + _vm._pastEventTime;
			var startUtc = localStart.ToUniversalTime();
			var endUtc = startUtc.AddMinutes(RadarViewModel.PastEventMinutesByIndex[_vm._pastEventDurationIndex]);

			// Window is now set — gray out sites with no data for this date (proactive availability).
			// Best-effort + non-blocking so it never delays the actual load.
			_ = _vm.ApplyPastAvailabilityAsync(startUtc, endUtc);

			if (_vm._selectedRadarOption?.Site is not { } site)
			{
				// No site yet: just ARM the window (date/time/range) so any site you click next loads it,
				// and report success so the flyout closes and you can go site-surfing on the map.
				_vm._pastWindowLoaded = true;
				_vm.PastEventStatus = "Window set — click a radar site on the map to load it.";
				return true;
			}

			// Highlight the loading site's on-map marker (deselecting any prior one). The not-armed path
			// does this via SelectPastSiteAsync; the armed "click another site" path comes straight here,
			// so set it here too or the previous site's marker stays lit.
			if (_vm._isMapReady)
			{
				await _vm._mapService.SetSelectedRadarSiteAsync(site.Id);
			}

			_vm.PastEventStatus = "Loading…";
			_vm._loopCts?.Cancel();
			var cts = new CancellationTokenSource();
			_vm._loopCts = cts;

			IReadOnlyList<string> keys;
			try
			{
				keys = await _vm._radarService.GetKeysForWindowAsync(site, startUtc, endUtc, cts.Token);
			}
			catch (OperationCanceledException)
			{
				return false;
			}
			catch (Exception ex)
			{
				_vm.PastEventStatus = "Couldn't list volumes: " + ex.Message;
				return false;
			}

			if (cts.Token.IsCancellationRequested || !ReferenceEquals(_vm._selectedRadarOption?.Site, site))
			{
				return false;
			}
			if (keys.Count == 0)
			{
				_vm.PastEventStatus = $"No {site.Id} data found for {localStart:MMM d, h:mm tt}.";
				return false;
			}
			// More volumes than the cap → evenly subsample across the whole window (first + last kept),
			// so a long duration becomes an overview rather than only the first chunk.
			var sampled = false;
			if (keys.Count > RadarViewModel.PastEventMaxFrames)
			{
				var pick = new List<string>(RadarViewModel.PastEventMaxFrames);
				for (var i = 0; i < RadarViewModel.PastEventMaxFrames; i++)
				{
					var idx = (int)Math.Round((double)i * (keys.Count - 1) / (RadarViewModel.PastEventMaxFrames - 1));
					pick.Add(keys[idx]);
				}
				keys = pick.Distinct().ToList();
				sampled = true;
			}

			await LoadPastLoopAsync(site, keys, startUtc, cts.Token);
			if (cts.Token.IsCancellationRequested)
			{
				return false;
			}

			MaybeRecordAllFramesLoaded();
			_ = RunPlaybackAsync(cts.Token);
			_ = RunDebugTickAsync(cts.Token);

			_vm.PastEventStatus = $"Loaded {keys.Count} frames{(sampled ? " (sampled)" : "")} · " +
				$"{localStart:MMM d, h:mm tt} +{_vm.PastEventDurationOptions[_vm._pastEventDurationIndex]}";
			_vm._pastWindowLoaded = true;
			return true;
		}

		// Builds the replay loop from the given keys — the live-free counterpart of LoadLoopCoreAsync.
		private async Task LoadPastLoopAsync(RadarSite site, IReadOnlyList<string> keys, DateTimeOffset startUtc, CancellationToken ct)
		{
			try
			{
				await _vm._loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				Services.RadarDiagnostics.BeginSession(site.Id);
				Services.RadarDiagnostics.Log("vm", "replay.load",
					("site", site.Id), ("startZ", startUtc.ToString("O")), ("frames", keys.Count));

				_vm._loopClickAt = DateTimeOffset.UtcNow;
				_vm._firstFrameElapsed = null;
				_vm._allFramesElapsed = null;
				_vm._initialLoadDone = false;
				_vm._loadInProgress = true;
				_vm._loopRenderBegun = false;

				_vm._archiveCount = keys.Count;
				_vm._frameCount = keys.Count;
				_vm._liveFrame = null;
				_vm._hasLiveFrame = false;
				_vm._pendingLiveAppend = null;
				_vm._pendingLiveUpdate = null;
				_vm._liveModeText = null;
				_vm._frameTimes = new DateTimeOffset?[_vm._frameCount];
				_vm._frameModes = new string?[_vm._frameCount];
				_vm.RebuildSegments(_vm._frameCount); // empty scrubber cells; they light as frames decode
				_vm._readyCount = 0;
				_vm._loadedKeys = keys.ToArray();
				_vm._loadedNewestKey = keys[^1];
				_vm.IsLoopReady = false;
				_vm._currentFrameIndex = 0; // start at the beginning of the event so play moves forward
				_vm._firstPaintIndex = 0;   // Rule 1: PastCast paints the oldest first — the fill is naturally left→right
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.MaxFrameIndex));
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameIndex));
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.RadarLoadingText));

				if (_vm._isMapReady)
				{
					await _vm._mapService.BeginRadarLoopAsync(site);
				}
				_vm._loopRenderBegun = true;

				// Oldest frame first (it's adopted + shown immediately) — prioritized so its prefix downloads
				// over parallel S3 streams (first paint), then the rest in parallel. A replay has NO live
				// frame competing for bandwidth and can be a LOT of frames (a 3 h window ≈ 36), so its backfill
				// runs at the higher MaxParallelReplayBackfill — measured ~2× the aggregate throughput of the
				// live backfill's 6 on this link (12 was the sweet spot; 18 regressed).
				await EnsureAndAddFrameAsync(site, keys, 0, ct, prioritized: true);

				// First paint is up. Same law as the live path: arm velocity+SRV building (Rule 3) and compute
				// the loop's one storm motion (Rules 4/5) from the OLDEST first-paint volume, both in parallel
				// with the backfill. No live frame here.
				_vm._motionRefKey = keys[0];
				_vm._lastVwpKey = null;
				if (_vm._isMapReady)
				{
					_ = _vm._mapService.PrefetchRadarVelocityAsync();
					_vm.RequestAutoStormMotion();
				}

				await BackfillFramesAsync(site, keys, 1, keys.Count, ct, MaxParallelReplayBackfill);
				_vm._loadInProgress = false;
			}
			finally
			{
				_vm._loopGate.Release();
			}
		}

		// Ticks the debug card once a second so its ages stay current while a loop is active.
		private async Task RunDebugTickAsync(CancellationToken ct)
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
				while (await timer.WaitForNextTickAsync(ct))
				{
					_vm.RaiseRadarReadout();
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Lists the recent archive volumes, fetches the near-real-time live frame from the
		// chunks bucket, begins a loop, shows the newest, then backfills the rest. The live
		// frame (when available) is appended as an extra newest frame at index _archiveCount.
		private async Task LoadLoopAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				await _vm._loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				await LoadLoopCoreAsync(site, ct);
			}
			finally
			{
				_vm._loopGate.Release();
			}
		}

		// The actual load, run under _loopGate (see LoadLoopAsync). Ends by fetching the live
		// frame inline so the whole sequence is atomic w.r.t. the live poll.
		private async Task LoadLoopCoreAsync(RadarSite site, CancellationToken ct)
		{
			IReadOnlyList<string> keys;
			try
			{
				keys = await _vm._radarService.GetRecentKeysAsync(site, _vm.LoopLength, ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch
			{
				return;
			}

			if (ct.IsCancellationRequested || keys.Count == 0 || !ReferenceEquals(_vm._selectedRadarOption?.Site, site))
			{
				Services.RadarDiagnostics.Log("vm", "loop.abort", ("keys", keys.Count), ("cancelled", ct.IsCancellationRequested));
				return;
			}

			Services.RadarDiagnostics.Log("vm", "loop.keys", ("count", keys.Count), ("newest", keys[^1]));

			// Size the loop for the archive frames only; the live frame (if it turns out to be
			// fresher) is appended afterwards by RefreshLiveFrameAsync. Loading archive first
			// also means the newest archive frame paints immediately, before the chunks fetch.
			_vm._archiveCount = keys.Count;
			_vm._liveFrame = null;
			_vm._hasLiveFrame = false;
			_vm._pendingLiveAppend = null;
			_vm._pendingLiveUpdate = null;
			_vm._frameCount = _vm._archiveCount;
			_vm._frameTimes = new DateTimeOffset?[_vm._frameCount];
			_vm._frameModes = new string?[_vm._frameCount];
			_vm.RebuildSegments(_vm._frameCount); // empty scrubber cells; they light as frames decode
			_vm._readyCount = 0;
			_vm._loadedNewestKey = keys[_vm._archiveCount - 1]; // archive newest drives the 5-min reload
			_vm._loadedKeys = keys.ToArray();               // baseline for the next incremental refresh
			_vm.IsLoopReady = false;
			_vm._currentFrameIndex = _vm._frameCount - 1; // newest archive frame
			_vm._firstPaintIndex = _vm._archiveCount - 1; // Rule 1: NowCast paints the newest first, then fills left→right
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.MaxFrameIndex));
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameIndex));
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.RadarLoadingText));

			if (_vm._isMapReady)
			{
				await _vm._mapService.BeginRadarLoopAsync(site);
			}

			// The loop (and its JS token) is now (re)started — frame-ready events from here on
			// belong to this selection, so first-frame timing can trust them.
			_vm._loopRenderBegun = true;

			// Newest archive frame first (immediate display) — prioritized: its prefix downloads over several
			// parallel S3 streams, since it's fetched ALONE and gates first paint (a single stream is slow +
			// variable). The backfill below stays single-stream-per-frame (it already overlaps 6 frames).
			var newestLoaded = await EnsureAndAddFrameAsync(site, keys, _vm._archiveCount - 1, ct, prioritized: true);

			// A VCP's designed elevation table can promise tilts the volumes don't actually contain, so a
			// tilt offered in the combo may have nothing behind it. Measured with tools/TiltCheck: KTLX in
			// VCP 212 designs 17 cuts up to 19.5°, but its volumes ship 12, topping out at 6.4° — every
			// tilt above that extracts to null. (KLOT in VCP 35 designs 12 and ships all 12, so this isn't
			// universal — we can't tell which case we're in without the whole volume, which the base tilt's
			// cheap prefix fetch never downloads.) Rather than leave the user staring at a blank loop,
			// fall back to the base tilt, which is always present. Recursion is one level deep: the base
			// tilt can't fail THIS way, so the retry can't re-trigger it.
			if (!newestLoaded && _vm._selectedTiltAngle is { } missing && !ct.IsCancellationRequested
				&& ReferenceEquals(_vm._selectedRadarOption?.Site, site))
			{
				Diag($"tilt {missing:0.00}° not present in this volume -> falling back to base tilt");
				Services.RadarDiagnostics.Log("vm", "tilt.absent", ("lvl", "warn"),
					("angle", missing), ("site", site.Id));
				SetTiltToBase();
				await LoadLoopCoreAsync(site, ct);
				return;
			}

			// First paint is up (reflectivity). Per docs/radar-loop-flow.md, arm the loop to build COMPLETE
			// frames and compute the motion NOW, both in PARALLEL with the backfill below:
			//   Rule 3 — velocity+SRV build on every backfill decode (one pass per frame), not a second sweep.
			//   Rules 4/5 — the loop's ONE storm motion, from this newest first-paint volume; SRV rides the
			//   same pass once it lands (velocity stand-in until then).
			_vm._motionRefKey = keys[^1];
			_vm._lastVwpKey = null;
			if (_vm._isMapReady)
			{
				_ = _vm._mapService.PrefetchRadarVelocityAsync(); // arms velocity+SRV building for the whole loop
				_vm.RequestAutoStormMotion();
			}

			// START the live (chunks) fetch NOW so its slow ~2-3 s chunk build OVERLAPS the archive backfill
			// below (both are independent network work — the backfill hits the archive bucket, the live path
			// the chunks bucket). Previously the live poll ran strictly AFTER the backfill, so the freshest
			// frame didn't even START fetching until ~6 s in and landed ~3 s late. We only start it here — the
			// state APPLY (ApplyLivePollAsync) still runs after the backfill, serially, so there's no
			// frame-state race. The selected tilt is passed through; a tilt the antenna hasn't reached yet in
			// the in-progress volume returns null and leaves the archive newest showing, same as always.
			var liveFetch = FetchLiveFrameAsync(site, ct);

			// Backfill the older archive frames IN PARALLEL (bounded) — each frame's cost is a full-volume AWS
			// download + bzip2 tilt extraction, so running them concurrently is the main lever for "load all
			// back frames faster".
			await BackfillFramesAsync(site, keys, 0, _vm._archiveCount - 1, ct);

			// Apply the (by now usually finished) live frame + scan mode; it appends at index _archiveCount
			// when fresher than the archive newest, and carries the scan-mode text for the card.
			await ApplyLivePollAsync(site, liveFetch, ct);
		}

		// Fetches the live (chunks) frame and, when it's newer than what's shown, applies it —
		// appending a new trailing frame or updating the existing live slot in place. Records the
		// outcome for the debug card. Best-effort: a null result just leaves the archive newest.
		private Task RefreshLiveFrameAsync(RadarSite site, CancellationToken ct) =>
			ApplyLivePollAsync(site, FetchLiveFrameAsync(site, ct), ct);

		// Starts the live (chunks) frame fetch — the slow part (~2-3 s of chunk list + downloads + bzip2).
		// Split out from the apply so the INITIAL load can OVERLAP it with the archive backfill (both are
		// independent network work) rather than running it strictly afterwards. Best-effort: records the
		// error and returns null so the archive newest simply stays shown.
		private async Task<Models.RadarVolume?> FetchLiveFrameAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				_vm._lastLiveError = null;
				return await _vm._radarService.GetLiveFrameAsync(site, _vm._selectedTiltAngle, ct);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_vm._lastLiveError = ex.Message;
				return null;
			}
		}

		// Awaits a started live fetch and applies it (record + append/update the trailing live slot). ⚠️ The
		// APPLY mutates frame-state (arrays, _frameCount, _currentFrameIndex, Segments), so it must run
		// SERIALLY with the archive load — never concurrently. The initial load starts the fetch early but
		// calls this only AFTER the backfill; the periodic poll runs both back-to-back.
		private async Task ApplyLivePollAsync(RadarSite site, Task<Models.RadarVolume?> fetch, CancellationToken ct)
		{
			var live = await fetch;
			RecordLivePoll(live);
			if (live is null || ct.IsCancellationRequested || !ReferenceEquals(_vm._selectedRadarOption?.Site, site))
			{
				return;
			}
			await ApplyLiveFrameAsync(live);
		}

		// Applies a live frame: updates the existing trailing live slot in place, or appends a new
		// one — but only when strictly newer than the current live / archive newest, so a stale
		// chunks volume can never override fresh data (the bug behind KVNX/KFDR old frames).
		private async Task ApplyLiveFrameAsync(Models.RadarVolume live)
		{
			if (_vm._hasLiveFrame)
			{
				if (_vm._liveFrame is not null && live.VolumeTime <= _vm._liveFrame.VolumeTime)
				{
					Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "skip"),
						("reason", $"not newer than current live ({live.VolumeTime:HH:mm:ss}Z <= {_vm._liveFrame.VolumeTime:HH:mm:ss}Z)"));
					return;
				}

				// DEFERRED update: _liveFrame is set eagerly so a poll during the decode window still skips
				// (dedup gate above), but the VISIBLE swap — frame time/mode, the readout, and the sweep
				// pulse — is held until the geometry actually lands (CompleteLiveUpdate, from OnRadarFrameReady).
				// Firing them here flipped the timestamp and animated a full sweep ~3-6 s BEFORE the new
				// returns decoded (the worker fetches the ~7 MB .V06 then decodes), so the sweep swept over the
				// OLD image and the time led the picture — the "swept but nothing changed" report. All the
				// readout fields read _frameTimes/_frameModes (not _liveFrame), so holding those writes keeps
				// the readout consistent with what's on screen until the swap. Mirrors the deferred append below.
				_vm._liveFrame = live;
				_vm._pendingLiveUpdate = live;
				Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "update"),
					("idx", _vm._archiveCount), ("volZ", live.VolumeTime.ToUniversalTime().ToString("HH:mm:ss")));
				if (_vm._isMapReady)
				{
					await _vm._mapService.AddRadarFrameAsync(live.LocalUrl, _vm._archiveCount); // JS re-decodes the live slot; CompleteLiveUpdate runs on frame-ready
				}
				else
				{
					CompleteLiveUpdate(live); // no WebView → no frame-ready will fire; swap immediately
				}
				return;
			}

			// No live slot yet: only append if the chunks volume is newer than the archive newest.
			var archiveNewest = _vm._archiveCount > 0 && _vm._archiveCount - 1 < _vm._frameTimes.Length
				? _vm._frameTimes[_vm._archiveCount - 1]
				: null;
			if (archiveNewest is { } an && live.VolumeTime <= an)
			{
				Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "skip"),
					("reason", $"not newer than archive ({live.VolumeTime:HH:mm:ss}Z <= {an:HH:mm:ss}Z)"));
				return;
			}

			// DEFERRED append: kick off the decode NOW but hold the VISIBLE grow (new scrubber cell + playhead
			// promotion + display swap) until the frame has actually decoded — CompleteLiveAppend runs from
			// OnRadarFrameReady. Doing it up front showed an EMPTY last cell (and jumped the playhead) for the
			// frame's ~0.5-0.8 s decode window, since the freshest frame appends AFTER the archive loop is
			// already full. Registering the frame source + starting the decode is all that happens now; the
			// scrubber/display change atomically the instant the geometry lands.
			_vm._pendingLiveAppend = live;
			Services.RadarDiagnostics.RegisterFrameSource(_vm._archiveCount, "live", FrameCacheFile(live), live.VolumeTime);
			if (_vm._isMapReady)
			{
				await _vm._mapService.AddRadarFrameAsync(live.LocalUrl, _vm._archiveCount); // JS decodes into the new slot; frame-ready completes it
			}
			else
			{
				CompleteLiveAppend(live); // no WebView → no frame-ready will fire; append immediately
			}
		}

		// Completes a deferred live append (see ApplyLiveFrameAsync): grows the loop by the freshest frame and
		// promotes the display to it, in one motion — called from OnRadarFrameReady once that frame's geometry
		// has decoded, so the new scrubber cell appears already-filled instead of blinking through an empty
		// state. Also used directly when there's no WebView to decode/report.
		private void CompleteLiveAppend(Models.RadarVolume live)
		{
			_vm._pendingLiveAppend = null;

			var grown = new DateTimeOffset?[_vm._archiveCount + 1];
			Array.Copy(_vm._frameTimes, grown, Math.Min(_vm._archiveCount, _vm._frameTimes.Length));
			grown[_vm._archiveCount] = live.VolumeTime;
			_vm._frameTimes = grown;
			var grownModes = new string?[_vm._archiveCount + 1];
			Array.Copy(_vm._frameModes, grownModes, Math.Min(_vm._archiveCount, _vm._frameModes.Length));
			grownModes[_vm._archiveCount] = live.ModeText;
			_vm._frameModes = grownModes;
			// Grow the scrubber to include the live frame's cell (keeps Segments.Count == _frameCount, so the
			// playhead — which divides the track by Segments.Count — stays aligned). The caller
			// (OnRadarFrameReady) marks this cell decoded right after, so it appears filled.
			if (_vm.Segments.Count == _vm._archiveCount)
			{
				_vm.Segments.Add(new RadarFrameSegment());
			}
			_vm._liveFrame = live;
			_vm._hasLiveFrame = true;
			_vm._frameCount = _vm._archiveCount + 1;
			_vm._currentFrameIndex = _vm._frameCount - 1; // show the live frame as the new newest
			Services.RadarDiagnostics.Log("vm", "live.apply", ("action", "append"),
				("idx", _vm._archiveCount), ("volZ", live.VolumeTime.ToUniversalTime().ToString("HH:mm:ss")),
				("frames", _vm._frameCount));
			if (_vm._loopClickAt is { } liveClick)
			{
				Services.RadarDiagnostics.Timing("live", (DateTimeOffset.UtcNow - liveClick).TotalSeconds);
			}
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.MaxFrameIndex));
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameIndex));
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
			_vm.RaiseRadarReadout();

			if (_vm._isMapReady)
			{
				_ = _vm._mapService.ShowRadarFrameAsync(_vm._archiveCount);   // promote display to the now-decoded live frame
				_ = _vm._mapService.PulseRadarSweepAsync();               // first live frame landed → one sweep pulse
			}
		}

		// Completes a DEFERRED in-place live UPDATE (see ApplyLiveFrameAsync): the freshest live volume we
		// re-decoded into the existing live slot has now landed, so publish the visible state — frame
		// time/mode, the readout, and the sweep pulse — atomically WITH the geometry. Deferring these off the
		// eager poll path stops the sweep animating over the old image (and the timestamp leading the picture)
		// during the ~3-6 s the worker spends fetching + decoding the ~7 MB volume. The display is already on
		// this slot (it's the newest), so unlike CompleteLiveAppend there's no grow/promote — just the
		// swap-time readout + one pulse. Also called directly when there's no WebView to decode.
		private void CompleteLiveUpdate(Models.RadarVolume live)
		{
			_vm._pendingLiveUpdate = null;
			if (_vm._archiveCount < _vm._frameTimes.Length)
			{
				_vm._frameTimes[_vm._archiveCount] = live.VolumeTime;
			}
			if (_vm._archiveCount < _vm._frameModes.Length)
			{
				_vm._frameModes[_vm._archiveCount] = live.ModeText;
			}
			Services.RadarDiagnostics.RegisterFrameSource(_vm._archiveCount, "live", FrameCacheFile(live), live.VolumeTime);
			if (_vm._isMapReady)
			{
				_ = _vm._mapService.PulseRadarSweepAsync(); // geometry landed -> one sweep pulse, in sync with the new returns
			}
			if (_vm._currentFrameIndex == _vm._archiveCount)
			{
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
			}
			_vm.RaiseRadarReadout();
		}

		// Records the latest live-frame poll outcome for the debug card.
		private void RecordLivePoll(Models.RadarVolume? live)
		{
			_vm._lastLivePollAt = DateTimeOffset.Now;
			_vm._lastLivePollResult = _vm._lastLiveError is not null
				? $"error: {_vm._lastLiveError}"
				: live is null
					? "null (no fresh tilt; using archive)"
					: $"ok · {live.VolumeTime.ToUniversalTime():HH:mm:ss}Z";
			// The decoded volume carries the scan mode regardless of whether it's fresh enough to
			// append as a new frame — capture it so the mode shows even for a stale/offline site.
			if (live?.ModeText is { } mode)
			{
				_vm._liveModeText = mode;
			}
			Services.RadarDiagnostics.LivePoll(_vm._lastLivePollResult, live?.VolumeTime, live?.ModeText);
			_vm.RaiseRadarReadout();
		}

		// Loads one archive frame at the current tilt and hands it to the map. Returns whether the frame
		// actually landed — the caller uses that to detect a tilt the volume doesn't contain (see
		// LoadLoopCoreAsync); a false is otherwise just a skipped frame, as before.
		private async Task<bool> EnsureAndAddFrameAsync(RadarSite site, IReadOnlyList<string> keys, int index, CancellationToken ct, bool prioritized = false)
		{
			try
			{
				var volume = await _vm._radarService.EnsureCachedAsync(site, keys[index], _vm._selectedTiltAngle, prioritized, ct);
				if (volume is null || ct.IsCancellationRequested || !ReferenceEquals(_vm._selectedRadarOption?.Site, site))
				{
					return false;
				}

				// Every cached tilt carries the VCP's full elevation table, so the tilt choices come free
				// with the frame we were fetching anyway — no extra request. Re-checked per frame because
				// a radar can change VCP mid-loop (precip <-> clear-air scan different tilts).
				_vm.UpdateTiltOptions(volume.Tilts);

				_vm._frameTimes[index] = volume.VolumeTime;
				if (index < _vm._frameModes.Length) _vm._frameModes[index] = volume.ModeText;
				Services.RadarDiagnostics.RegisterFrameSource(index, "archive", FrameCacheFile(volume), volume.VolumeTime);
				if (_vm._isMapReady)
				{
					await _vm._mapService.AddRadarFrameAsync(volume.LocalUrl, index);
				}
				return true;
			}
			catch (OperationCanceledException)
			{
				// Selection changed; stop.
			}
			catch (Exception ex)
			{
				// Skip a bad frame; the rest of the loop still loads.
				Services.RadarDiagnostics.Log("vm", "frame.fail", ("idx", index), ("error", ex.Message));
			}
			return false;
		}

		// Drops back to the base tilt without triggering a reload (the caller is already loading).
		private void SetTiltToBase()
		{
			_vm._selectedTiltAngle = null;
			_vm._radarTiltIndex = 0;
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.RadarTiltIndex));
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.SelectedTiltLabel));
		}

		// How many archive volumes to download + extract concurrently during backfill. The per-frame cost is
		// network + bzip2 extraction. 6 for the LIVE loop: it's ~10 frames AND its backfill now overlaps the
		// ~8-stream live-chunk fetch, so 6+8≈14 streams already fills the link (18+ measured as a regression).
		private const int MaxParallelBackfill = 6;
		// Higher cap for a REPLAY backfill: no live fetch competes, and a long window is many frames (a 3 h
		// event ≈ 36), so more parallel S3 streams cut the bulk download. 12 measured ~2× the aggregate
		// throughput of 6 on a home link; beyond ~12-14 it plateaus/regresses (single-stream S3 tail + the
		// bzip2 extract is only ~0.3 s/frame, so download — not CPU — is what parallelism helps here).
		private const int MaxParallelReplayBackfill = 12;

		// Loads a run of frames [startInclusive, endExclusive) with BOUNDED PARALLELISM. The per-frame
		// cost is a network download + bzip2 tilt extraction (both off the UI thread), so overlapping
		// them cuts a ~10-frame backfill from tens of seconds to a few. Concurrency is safe: each frame
		// writes its own index and caches to its own file; only the light AddRadarFrameAsync posts
		// resume on the UI thread (WebView2 is UI-affine), which serializes them naturally. Runs under
		// the caller's _loopGate, so no live poll can interleave.
		private async Task BackfillFramesAsync(RadarSite site, IReadOnlyList<string> keys, int startInclusive, int endExclusive, CancellationToken ct, int maxParallel = MaxParallelBackfill)
		{
			using var gate = new SemaphoreSlim(maxParallel);
			var tasks = new List<Task>();
			for (var i = startInclusive; i < endExclusive && !ct.IsCancellationRequested; i++)
			{
				try
				{
					await gate.WaitAsync(ct); // cap frames in flight
				}
				catch (OperationCanceledException)
				{
					break;
				}
				var index = i;
				tasks.Add(BackfillOneAsync(index));
			}
			await Task.WhenAll(tasks);

			async Task BackfillOneAsync(int index)
			{
				try
				{
					await EnsureAndAddFrameAsync(site, keys, index, ct);
				}
				finally
				{
					gate.Release();
				}
			}
		}

		/// <summary>Called by the view when a radar site marker is clicked (toggles selection).</summary>
		public void OnRadarSiteClicked(string? id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return;
			}

			var option = _vm.RadarOptions.FirstOrDefault(o => o.Site?.Id == id);
			if (option is null)
			{
				return;
			}

			// Clicking the already-selected site clears it; otherwise select it.
			var toggleOff = ReferenceEquals(option, _vm._selectedRadarOption);
			Diag($"siteClick id={id} toggleOff={toggleOff}"); // trace: a spurious reload should show a click here (or NOT)
			_vm.SelectedRadarOption = toggleOff ? _vm.RadarOptions[0] : option;
		}

		/// <summary>Called by the view when the WebView reports a loop frame finished decoding.</summary>
		public void OnRadarFrameReady(int index, bool hasData)
		{
			// Drop frame-ready events that arrive before this selection's loop has begun — they're
			// stale leftovers from the previous site draining through the worker (see _loopRenderBegun).
			if (!_vm._loopRenderBegun)
			{
				return;
			}

			// Complete a DEFERRED live append: the freshest frame we kicked off has now decoded, so grow the
			// loop + promote the display in ONE motion (this must run BEFORE the Segments[index] access below
			// so the new cell exists to be marked decoded → it appears filled immediately). See
			// ApplyLiveFrameAsync / CompleteLiveAppend.
			if (_vm._pendingLiveAppend is { } pendingLive && !_vm._hasLiveFrame && index == _vm._archiveCount)
			{
				CompleteLiveAppend(pendingLive);
			}

			// Complete a DEFERRED in-place live UPDATE: the freshest live volume we re-decoded into the
			// existing live slot has landed, so swap the readout + fire the sweep now (in sync with the new
			// returns) instead of ~3-6 s early on the poll path. Mutually exclusive with the append above
			// (that path runs only when there's no live frame yet; this one only when the slot exists).
			if (_vm._pendingLiveUpdate is { } pendingUpdate && index == _vm._archiveCount)
			{
				CompleteLiveUpdate(pendingUpdate);
			}

			_vm._readyCount++;
			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.IsTransportEnabled)); // PastCast enables the transport at a low refl count
			if (index >= 0 && index < _vm.Segments.Count)
			{
				// Mark decoded, then recompute displayed readiness for ALL cells: velocity still needs its
				// dealiased geometry (so a decoded cell may stay "loading" until the build reaches it), AND
				// Rule 2's left-to-right reveal gate depends on the whole run, not just this index.
				_vm.Segments[index].IsDecoded = true;
				_vm.RefreshSegmentReadiness();
			}
			Services.RadarDiagnostics.FrameReady(index, hasData, _vm._readyCount, _vm._frameCount);

			// First-frame timing: the moment the first frame of this click is decoded + shown.
			if (!_vm._initialLoadDone && _vm._firstFrameElapsed is null && _vm._loopClickAt is { } click)
			{
				_vm._firstFrameElapsed = DateTimeOffset.UtcNow - click;
				Services.RadarDiagnostics.Timing("first", _vm._firstFrameElapsed.Value.TotalSeconds);
				_vm.RaiseRadarReadout();
			}

			_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
			if (_vm._readyCount >= _vm._frameCount && _vm._frameCount > 0)
			{
				_vm.IsLoopReady = true;
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
			}

			MaybeRecordAllFramesLoaded();
		}

		// Records the "all frames loaded + rendered" timing once, after the initial load has
		// settled on its final frame count (so the live frame is included) and every frame has
		// reported ready. Frozen by _initialLoadDone so later live refreshes don't overwrite it.
		private void MaybeRecordAllFramesLoaded()
		{
			if (_vm._initialLoadDone || _vm._loadInProgress || _vm._frameCount == 0 || _vm._readyCount < _vm._frameCount)
			{
				return;
			}
			if (_vm._loopClickAt is { } click)
			{
				_vm._allFramesElapsed = DateTimeOffset.UtcNow - click;
				Services.RadarDiagnostics.Timing("all", _vm._allFramesElapsed.Value.TotalSeconds);
				_vm.RaiseRadarReadout();
			}
			_vm._initialLoadDone = true;
		}

		// Advances the loop while playing + ready (~0.5s/frame, with a brief dwell on newest).
		private async Task RunPlaybackAsync(CancellationToken ct)
		{
			try
			{
				var dwell = 0;
				while (!ct.IsCancellationRequested)
				{
					// Variable per-frame delay so the playback-speed combo applies immediately.
					await Task.Delay(_vm.PlaybackIntervalMs, ct);
					if (!_vm._isPlaying || !_vm._isLoopReady || _vm._frameCount == 0)
					{
						continue;
					}

					// Pause a couple of ticks on the newest frame before looping back.
					if (_vm._currentFrameIndex >= _vm._frameCount - 1 && dwell < 2)
					{
						dwell++;
						continue;
					}

					// Hold at the built frontier: don't advance onto a frame whose velocity is still being
					// dealiased in the background (that would flash blank / stall ~1.5 s mid-playback). The
					// upgrade queue builds forward from the playhead, so playback resumes on its own as each
					// next frame becomes ready. Reflectivity/CC are always ready, so this never holds there.
					var next = (_vm._currentFrameIndex + 1) % _vm._frameCount;
					if (!_vm.IsFrameDisplayReady(next))
					{
						continue;
					}

					dwell = 0;
					_vm.CurrentFrameIndex = next;
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Every ~5 min, if a newer volume exists, reloads the loop (keeps the hour current).
		private async Task RunRefreshAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
				while (await timer.WaitForNextTickAsync(ct))
				{
					if (!ReferenceEquals(_vm._selectedRadarOption?.Site, site))
					{
						return;
					}

					IReadOnlyList<string> keys;
					try
					{
						keys = await _vm._radarService.GetRecentKeysAsync(site, _vm.LoopLength, ct);
					}
					catch (OperationCanceledException)
					{
						return;
					}
					catch
					{
						continue;
					}

					if (keys.Count > 0 && keys[keys.Count - 1] != _vm._loadedNewestKey)
					{
						Services.RadarDiagnostics.Log("vm", "refresh.archive", ("newKey", keys[^1]), ("oldKey", _vm._loadedNewestKey));
						// Predictive prefetch: pull the new volume's .V06 to disk FIRST, OFF the loop's
						// critical path (no _loopGate, no loop-state changes) — so the download (the slow
						// part) happens while the loop stays fully live, and the incremental fold-in below
						// is decode-only (its EnsureCachedAsync becomes an instant disk hit). Without this,
						// the fold ran the download while HOLDING _loopGate, stalling the live-frame poll.
						await PrefetchArchiveFramesAsync(site, keys, ct);
						// Incrementally fold in the new volume (reuse the unchanged decoded frames, no
						// layer teardown) instead of a full rebuild — that rebuild blanked the radar for
						// ~1.5-6 s and flashed a stale archive frame every 5 min.
						await ReloadLoopIncrementalAsync(site, keys, ct);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}

		// Folds a newly-arrived archive volume into the loop WITHOUT a teardown: it diffs the new key
		// list against the loaded one, reindexes (in JS) the frames whose volumes are unchanged so
		// their decoded geometry is reused, and decodes only the genuinely-new volume(s). The live
		// frame is carried over too. Because the layer is never removed and the on-screen frame stays
		// up, the periodic reload no longer blanks the radar or flashes a stale archive frame.
		// Serialized under _loopGate against the live poll, like the full load.
		private async Task ReloadLoopIncrementalAsync(RadarSite site, IReadOnlyList<string> newKeys, CancellationToken ct)
		{
			try
			{
				await _vm._loopGate.WaitAsync(ct);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				if (newKeys.Count == 0 || !ReferenceEquals(_vm._selectedRadarOption?.Site, site))
				{
					return;
				}

				var oldFrameTimes = _vm._frameTimes;
				var oldFrameModes = _vm._frameModes;
				var oldReady = new bool[_vm.Segments.Count]; // reused frames keep their DECODE state (relit per product)
				for (var i = 0; i < oldReady.Length; i++) oldReady[i] = _vm.Segments[i].IsDecoded;
				var oldArchiveCount = _vm._archiveCount;
				var oldFrameCount = _vm._frameCount;
				var hadLive = _vm._hasLiveFrame;
				var oldCurrent = _vm._currentFrameIndex;
				var wasNewest = oldCurrent == oldFrameCount - 1; // user following the latest frame

				// First old index for each archive key (the loop has no duplicate volumes in practice).
				var oldIndexByKey = new Dictionary<string, int>(oldArchiveCount);
				for (var i = 0; i < _vm._loadedKeys.Length && i < oldArchiveCount; i++)
				{
					oldIndexByKey.TryAdd(_vm._loadedKeys[i], i);
				}

				var newArchiveCount = newKeys.Count;
				var newFrameCount = newArchiveCount + (hadLive ? 1 : 0);
				var newTimes = new DateTimeOffset?[newFrameCount];
				var newModes = new string?[newFrameCount];       // scan mode, reused in lockstep with times
				var newReady = new bool[newFrameCount];           // lit scrubber cells, reused in lockstep
				var mapping = new List<int[]>(newFrameCount);     // [fromIndex, toIndex] reuses
				var newIndices = new List<int>();                  // new archive slots needing decode

				for (var j = 0; j < newArchiveCount; j++)
				{
					if (oldIndexByKey.TryGetValue(newKeys[j], out var oi) && oi < oldFrameTimes.Length)
					{
						mapping.Add(new[] { oi, j });
						newTimes[j] = oldFrameTimes[oi];
						if (oi < oldFrameModes.Length) newModes[j] = oldFrameModes[oi];
						if (oi < oldReady.Length) newReady[j] = oldReady[oi];
					}
					else
					{
						newIndices.Add(j); // brand-new volume -> decode
					}
				}

				// The live (chunks) frame persists across an archive reload — carry it to the new top.
				if (hadLive && oldArchiveCount < oldFrameTimes.Length)
				{
					mapping.Add(new[] { oldArchiveCount, newArchiveCount });
					newTimes[newArchiveCount] = oldFrameTimes[oldArchiveCount];
					if (oldArchiveCount < oldFrameModes.Length) newModes[newArchiveCount] = oldFrameModes[oldArchiveCount];
					if (oldArchiveCount < oldReady.Length) newReady[newArchiveCount] = oldReady[oldArchiveCount];
				}

				// Where the displayed frame lands after the reindex (so we can keep it on screen).
				var newCurrent = -1;
				foreach (var m in mapping)
				{
					if (m[0] == oldCurrent) { newCurrent = m[1]; break; }
				}

				// Commit VM state. Don't touch IsLoopReady: most frames are already decoded, so the
				// loop stays "ready" (scrubber/playback uninterrupted). _readyCount = reused count;
				// the new frames bring it back up to newFrameCount as they decode.
				_vm._loadedKeys = newKeys.ToArray();
				_vm._loadedNewestKey = newKeys[^1];
				_vm._archiveCount = newArchiveCount;
				_vm._frameCount = newFrameCount;
				_vm._frameTimes = newTimes;
				_vm._frameModes = newModes;
				_vm._firstPaintIndex = newFrameCount - 1; // a refresh folds new volumes in at the newest end
				_vm.RebuildSegments(newFrameCount, newReady); // reindex scrubber cells; reused frames stay lit
				_vm._readyCount = mapping.Count;
				_vm._currentFrameIndex = wasNewest ? newFrameCount - 1
					: newCurrent >= 0 ? newCurrent
					: newFrameCount - 1;

				Services.RadarDiagnostics.Log("vm", "refresh.incremental",
					("reused", mapping.Count), ("new", newIndices.Count),
					("frames", newFrameCount), ("newest", newKeys[^1]));
				// Reused frames were reindexed in place (no re-decode), so RegisterFrameSource never fired
				// for them — re-map the diagnostics records in lockstep so the report's per-frame table and
				// whole-loop-age stat track the slid window instead of showing the load-time frames forever.
				Services.RadarDiagnostics.Reindex(mapping);

				if (_vm._isMapReady)
				{
					var mappingJson = System.Text.Json.JsonSerializer.Serialize(mapping);
					await _vm._mapService.RemapRadarFramesAsync(newFrameCount, mappingJson);
					// Target the desired frame: if undecoded (a just-arrived newest), JS records it as
					// pending and keeps the current frame on screen until it decodes (no blank).
					await _vm._mapService.ShowRadarFrameAsync(_vm._currentFrameIndex);
					// Live loop advanced → track the motion to the new newest (Rule 5: still ONE per loop,
					// just following "now"). gateToDoppler: on the periodic reload, only RECOMPUTE while SRV/
					// velocity is actually in view — browsing reflectivity keeps the pre-warmed motion and skips
					// the per-reload whole-loop SRV re-warm (the churn). First paint already computed it eagerly.
					_vm._motionRefKey = newKeys[^1];
					_vm.RequestAutoStormMotion(gateToDoppler: true);
				}

				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.MaxFrameIndex));
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameIndex));
				_vm.RaisePropertyChangedFor(nameof(RadarViewModel.CurrentFrameTimeText));
				_vm.RaiseRadarReadout();

				// Decode only the genuinely-new volumes (newest-first so the top updates first).
				for (var k = newIndices.Count - 1; k >= 0 && !ct.IsCancellationRequested; k--)
				{
					await EnsureAndAddFrameAsync(site, newKeys, newIndices[k], ct);
				}
			}
			finally
			{
				_vm._loopGate.Release();
			}
		}

		// Predictive prefetch: warm the on-disk .V06 cache for the volumes a reload is about to fold in,
		// WITHOUT _loopGate and WITHOUT touching loop state — so the download happens with the loop fully
		// live, and the subsequent ReloadLoopIncrementalAsync only decodes (EnsureCachedAsync then returns
		// the already-cached file instantly). Bounded parallelism like the backfill; already-cached keys
		// are a cheap File.Exists no-op inside EnsureCachedAsync, and every failure is per-frame + non-fatal
		// (the fold-in will just download that one itself, as before).
		private async Task PrefetchArchiveFramesAsync(RadarSite site, IReadOnlyList<string> newKeys, CancellationToken ct)
		{
			var loaded = new HashSet<string>(_vm._loadedKeys, StringComparer.Ordinal);
			var toPrefetch = newKeys.Where(k => !loaded.Contains(k)).ToList();
			if (toPrefetch.Count == 0)
			{
				return;
			}

			using var gate = new SemaphoreSlim(MaxParallelBackfill);
			var tasks = toPrefetch.Select(async key =>
			{
				try
				{
					await gate.WaitAsync(ct);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				try
				{
					await _vm._radarService.EnsureCachedAsync(site, key, _vm._selectedTiltAngle, cancellationToken: ct);
				}
				catch (OperationCanceledException)
				{
					// Selection changed; stop.
				}
				catch (Exception ex)
				{
					Services.RadarDiagnostics.Log("vm", "prefetch.fail", ("key", key), ("error", ex.Message));
				}
				finally
				{
					gate.Release();
				}
			});
			await Task.WhenAll(tasks);
			Services.RadarDiagnostics.Log("vm", "prefetch", ("count", toPrefetch.Count), ("newest", newKeys[^1]));
		}

		// Pulls the freshest chunks-bucket frame and (when newer) updates the trailing live slot
		// in place — or appends one if the loop didn't have a live frame yet — keeping the newest
		// frame ~1-2 min old between the slower (~5 min) archive reloads. Polls fast until the
		// first live frame lands (LiveFrameRetrySeconds), then settles to LiveFrameRefreshSeconds.
		private async Task RunLiveFrameRefreshAsync(RadarSite site, CancellationToken ct)
		{
			try
			{
				while (true)
				{
					var interval = _vm._hasLiveFrame ? _vm.RefreshIntervalSeconds : RadarViewModel.LiveFrameRetrySeconds;
					// Schedule relative to the LAST poll, whoever ran it — an archive reload runs its
					// own inline live poll (LoadLoopCoreAsync → RefreshLiveFrameAsync), so anchoring on
					// _lastLivePollAt pushes this timer out instead of double-fetching ~3s later.
					var sinceLast = _vm._lastLivePollAt is { } last ? (DateTimeOffset.Now - last).TotalSeconds : interval;
					var wait = Math.Max(1.0, interval - sinceLast);
					_vm._livePollCycleStart = DateTimeOffset.Now;
					_vm._nextLivePollAt = _vm._livePollCycleStart.Value.AddSeconds(wait);
					_vm.RaisePropertyChangedFor(nameof(RadarViewModel.RadarNextFrameProgress)); // reset the bar at the cycle start
					// The on-map sweep is no longer a continuous phase-locked rotation — it pulses once
					// when a genuinely-new frame actually lands (see ApplyLiveFrameAsync), so nothing to
					// start here.
					await Task.Delay(TimeSpan.FromSeconds(wait), ct);

					if (!ReferenceEquals(_vm._selectedRadarOption?.Site, site))
					{
						return;
					}

					// If a poll snuck in during our wait (e.g. a reload's inline poll), don't double
					// up — loop to recompute the next deadline from that poll instead.
					if (_vm._lastLivePollAt is { } recent && (DateTimeOffset.Now - recent).TotalSeconds < interval - 1)
					{
						continue;
					}

					// Gate against a concurrent archive (re)load mutating the same frame state.
					await _vm._loopGate.WaitAsync(ct);
					try
					{
						if (ReferenceEquals(_vm._selectedRadarOption?.Site, site))
						{
							await RefreshLiveFrameAsync(site, ct);
						}
					}
					finally
					{
						_vm._loopGate.Release();
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Selection changed or app shutting down.
			}
		}
			}
	}
}
