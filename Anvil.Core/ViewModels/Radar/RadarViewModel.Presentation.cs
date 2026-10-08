using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	// RadarViewModel (partial): the radar card presentation + next-update progress bar.
	public sealed partial class RadarViewModel
	{
		// ── Polished radar card (the user-facing presentation). The monospace RadarDebugText
		//    above feeds the collapsible "Diagnostics" expander; these are the headline values.
		//    All recomputed together via RaiseRadarReadout (frame changes + the 1s tick). ──

		/// <summary>Card header, e.g. "KVNX · Vance AFB".</summary>
		public string RadarCardTitle
		{
			get
			{
				var site = _selectedRadarOption?.Site;
				if (site is null) return string.Empty;
				return string.IsNullOrWhiteSpace(site.Name) ? site.Id : $"{site.Id} · {site.Name}";
			}
		}

		/// <summary>The selected site's ICAO id (e.g. "KTLX"), or empty when none.</summary>
		public string RadarSiteId => _selectedRadarOption?.Site?.Id ?? string.Empty;

		/// <summary>The selected site's descriptive name (e.g. "Norman"), or empty.</summary>
		public string RadarSiteName => _selectedRadarOption?.Site?.Name ?? string.Empty;

		/// <summary>Site coordinates for the card subheader.</summary>
		public string RadarCardCoords
		{
			get
			{
				var site = _selectedRadarOption?.Site;
				return site is null ? string.Empty : $"{site.Latitude:0.000}, {site.Longitude:0.000}";
			}
		}

		/// <summary>Headline: the displayed frame's local time, or load progress.</summary>
		public string RadarCardTime
		{
			get
			{
				if (_selectedRadarOption?.Site is null) return string.Empty;
				// Show the displayed frame's time as soon as IT is available (the newest frame
				// loads first, ~sub-second) rather than waiting for the whole loop to decode.
				var t = (_currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length) ? _frameTimes[_currentFrameIndex] : null;
				// Replay frames need the date — "5:00 PM" is ambiguous on a historical event; a live
				// loop is obviously today, so it stays time-only.
				if (t is { } x)
					return IsPastEventMode
						? x.ToLocalTime().ToString("h:mm tt · MMM d, yyyy")
						: x.ToLocalTime().ToString("h:mm tt");
				return string.Empty; // no "loading" text; the segmented scrubber shows load progress
			}
		}

		/// <summary>"frame 10/10 · live" under the headline time.</summary>
		public string RadarFrameDetail
		{
			get
			{
				if (_selectedRadarOption?.Site is null) return string.Empty;
				var hasTime = _currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length && _frameTimes[_currentFrameIndex] is not null;
				if (!hasTime) return string.Empty;
				var src = (_hasLiveFrame && _currentFrameIndex == _archiveCount) ? "live" : "archive";
				return $"frame {_currentFrameIndex + 1}/{_frameCount} · {src}";
			}
		}

		/// <summary>Backfill progress while older loop frames are still decoding in the background,
		/// e.g. "4/10 frames…" — the newest frame paints almost immediately, but the rest can
		/// take a few seconds, and without this the user has no sign more frames are still coming.
		/// The "Loading" label is the row header (see RadarControls.xaml), so it's not repeated here.
		/// Empty once the whole loop has decoded (<see cref="IsLoopReady"/>).</summary>
		public string RadarLoadingText =>
			(_frameCount > 0 && !_isLoopReady) ? $"{_readyCount}/{_frameCount} frames…" : string.Empty;

		/// <summary>Scan mode (VCP/precip/SAILS) from the latest live poll.</summary>
		public string RadarModeText
		{
			get
			{
				if (_selectedRadarOption?.Site is null) return string.Empty;
				// Replay has no live poll to fill _liveModeText, so read the DISPLAYED frame's own mode
				// (VCP + regime parsed from its cached tilt). Live loops normally keep the single poll
				// value, which carries the fuller "0.5°×N · SAILS" detail across all frames — but a site
				// that's offline/stale (e.g. the intermittent test radar KCRI) or whose live poll hasn't
				// landed yet never fills _liveModeText, so fall back to the displayed archive frame's own
				// mode (the same source replay uses) before giving up to "—".
				if (IsPastEventMode)
				{
					return DisplayedFrameMode() ?? (_isLoopReady ? "—" : "loading…");
				}
				return _liveModeText ?? DisplayedFrameMode() ?? (_isLoopReady ? "—" : "loading…");
			}
		}

		/// <summary>
		/// The VOLUME's scan strategy out of a mode string: everything before the "0.5°" sweep token —
		/// "VCP 212 · precip · SAILS ×1 · 0.5°×2" → "VCP 212 · precip · SAILS ×1". The token is
		/// dropped because it restates the SAILS count (2 sweeps = the base + 1 extra) and read as a
		/// contradiction ("×1" vs "×2") beside it. ⚠️ The ONE cut rule: the bar's Scan readout and the Radar
		/// Atlas's Scan mode both call it, so they can't disagree. No token (archive "VCP 212 · precip", "—",
		/// "loading…") → the string unchanged.
		/// </summary>
		public static string ScanStrategyText(string? mode)
		{
			if (string.IsNullOrEmpty(mode)) return string.Empty;
			var idx = mode.IndexOf("0.5°", System.StringComparison.Ordinal);
			return idx > 0 ? mode[..idx].TrimEnd(' ', '·') : mode;
		}

		// The VCP/regime mode string of the currently displayed frame, parsed from its cached tilt
		// (null when that frame's mode isn't known yet). Used by both replay and the live-loop fallback.
		private string? DisplayedFrameMode() =>
			(_currentFrameIndex >= 0 && _currentFrameIndex < _frameModes.Length)
				? _frameModes[_currentFrameIndex] : null;

		// ACTIVE-ready, pushed from radar.js (radarBuildProgress activeReady[]): frame i has every visible pane's
		// product built (SRV read as velocity while its motion is pending — what's on screen). Gates PLAYBACK's
		// built-frontier hold (IsFrameDisplayReady), so playback never advances onto a frame a pane can't draw.
		private bool[] _activeReady = Array.Empty<bool>();
		// FILL-BUILT, pushed alongside (fillBuilt[]): reflectivity + velocity (+ any visible dual-pol) built — the
		// scrubber-fill gate (docs/radar-loop-flow.md Rule 2), independent of the active product, so browsing
		// reflectivity never stalls on velocity you're not watching. SRV is NOT included: it rides the loop's one
		// storm motion (loop-wide, lands last) and trails per Rule 4, so it doesn't hold the per-frame fill.
		private bool[] _fillBuilt = Array.Empty<bool>();

		/// <summary>Receives the build state from the WebView. <paramref name="activeReady"/> is the visible panes'
		/// per-frame build state (gates playback); <paramref name="fillBuilt"/> is refl+velocity built per frame
		/// (gates the scrubber fill); <paramref name="decoding"/> is what a worker is decoding now. All may be null.
		/// Refreshes the scrubber cells so frames light as they build.</summary>
		public void SetBuildProgress(int built, int total, bool[]? activeReady, bool[]? fillBuilt = null, bool[]? decoding = null)
		{
			_activeReady = activeReady ?? Array.Empty<bool>();
			_fillBuilt = fillBuilt ?? Array.Empty<bool>();
			_decoding = decoding ?? Array.Empty<bool>();
			AnnounceLiveDecoding();
			RefreshSegmentReadiness(); // → UpdateLoopGate → the loading screen's cells
		}

		// Frames a page worker is decoding right now (radar.js workerJob) — only the loading screen's build cells read it.
		private bool[] _decoding = Array.Empty<bool>();

		// Whether frame idx is fill-built (reflectivity + velocity built, per the page). A frame the array hasn't
		// reported yet reads as NOT built: radar.js posts radarBuildProgress right after every successful decode
		// (the pair at applyFrameResult), so the cell lights the moment its velocity is built — never
		// fills-then-empties by falling back to a looser signal, and never counts a live-appended frame the array
		// doesn't cover yet.
		private bool IsFrameFillBuilt(int idx)
		{
			if (idx < 0 || idx >= _fillBuilt.Length) return false;
			return _fillBuilt[idx];
		}

		// Whether frame idx is ACTIVE-ready (ready to display). Every product EXCEPT reflectivity is built on
		// demand (the decode builds only what's on screen), so any of them can be not-yet-built for a frame;
		// reflectivity is always built. Missing/out-of-range progress info returns true so playback never
		// stalls on absent data.
		// ⚠️ MULTI-PANE: radar.js computes that array against EVERY visible pane's product, so advancing
		// onto a frame that is blank in any pane is already excluded. The only shortcut left is the
		// all-reflectivity case, where nothing can be unbuilt — checked across the visible panes, not just
		// pane 1, or a quad of Ref/Vel/SRV/CC would play straight through frames three panes can't draw.
		private bool IsFrameDisplayReady(int idx)
		{
			if (AllVisiblePanesAreReflectivity()) return true; // reflectivity is always built
			if (_activeReady.Length == 0) return true;        // no progress pushed yet — don't stall
			if (idx < 0 || idx >= _activeReady.Length) return true;
			return _activeReady[idx];
		}

		/// <summary>How old the frame ON SCREEN is — its scan START to now, e.g. "45 s ago", "1 min 40 s ago", "12 min ago",
		/// "13 yr 2 mo ago" (a replay). The user's calls (2026-10-08): SECONDS while they matter (under 10 min); counted from
		/// the scan's START (the oldest part of the picture — never reads fresher than the data); and the frame being SHOWN,
		/// so it always matches the time above it (scrubbed back to 1:30, it says how old 1:30 is). Re-raised every second
		/// by the readout tick.</summary>
		public string RadarAgeText => AgeReferenceTime is { } t ? $"{AgeWords(DateTimeOffset.Now - t)} ago" : "—";

		/// <summary>The scan the age counts from: the DISPLAYED frame's (its scan start), else the newest loaded one.</summary>
		internal DateTimeOffset? AgeReferenceTime =>
			(_currentFrameIndex >= 0 && _currentFrameIndex < _frameTimes.Length ? _frameTimes[_currentFrameIndex] : null)
			?? NewestFrameTime();

		/// <summary>"45 s" under a minute, "1 min 40 s" (or "2 min") under 10 minutes, then the two largest units as
		/// before ("12 min", "1 hr 6 min", "13 yr 2 mo"). A future stamp (clock skew) reads "0 s".</summary>
		internal static string AgeWords(TimeSpan span)
		{
			if (span < TimeSpan.Zero) span = TimeSpan.Zero;
			if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds} s";
			if (span.TotalMinutes < 10)
			{
				var s = span.Seconds;
				return s == 0 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalMinutes} min {s} s";
			}
			return FormatAge(span);
		}

		// Renders a TimeSpan as its two largest non-zero units (yr/mo/day/hr/min). Month/year
		// lengths are averaged (30.44 / 365.25 days) — fine for a fuzzy "ago" readout.
		private static string FormatAge(TimeSpan span)
		{
			double totalDays = span.TotalDays;
			int years = (int)(totalDays / 365.25);
			double remDays = totalDays - years * 365.25;
			int months = (int)(remDays / 30.44);
			int days = (int)(remDays - months * 30.44);

			var parts = new (int Value, string Unit)[]
			{
				(years, "yr"), (months, "mo"), (days, "day"), (span.Hours, "hr"), (span.Minutes, "min"),
			};
			var shown = parts.SkipWhile(p => p.Value == 0).Take(2).Where(p => p.Value > 0);
			var text = string.Join(" ", shown.Select(p => $"{p.Value} {p.Unit}"));
			return text.Length == 0 ? "just now" : text;
		}

		/// <summary>
		/// Collection time of the freshest LOADED frame — the exact instant the Selected Site readout
		/// shows (null when no loop). Exposed so the Radar Atlas can report the SAME number for the
		/// site the loop is showing, instead of a second (staler) estimate fetched from the buckets: the
		/// live frame's real sweep time is only knowable by building the frame, which is far too expensive
		/// to redo per site click. Re-raised by RaiseRadarReadout, so readers track the loop live.
		/// </summary>
		public DateTimeOffset? NewestLoadedFrameTime => NewestFrameTime();

		/// <summary>Raw age, in minutes, of the SAME frame <see cref="RadarAgeText"/> describes (the one on screen; null when
		/// no frame yet). Drives the smooth fresh→stale color ramp applied to the age readout — see RadarControls.AgeBrush —
		/// so the colour always agrees with the words under it.</summary>
		public double? RadarAgeMinutes =>
			AgeReferenceTime is { } t ? (DateTimeOffset.Now - t).TotalMinutes : null;

		/// <summary>Loop coverage, e.g. "12:36 – 1:12 PM · 10 frames".</summary>
		public string RadarLoopSpanText
		{
			get
			{
				if (_selectedRadarOption?.Site is null || _archiveCount == 0) return string.Empty;
				var oldest = _frameTimes.Length > 0 ? _frameTimes[0] : null;
				var newest = (_archiveCount - 1 < _frameTimes.Length) ? _frameTimes[_archiveCount - 1] : null;
				static string L(DateTimeOffset? x) => x?.ToLocalTime().ToString("h:mm tt") ?? "—";
				// Suffix the event date in replay so the time-only span isn't ambiguous on a past event.
				var date = IsPastEventMode && newest is { } n ? $" · {n.ToLocalTime():MMM d, yyyy}" : string.Empty;
				return $"{L(oldest)} – {L(newest)} · {_frameCount} frames{date}";
			}
		}

		/// <summary>Freshness of the newest frame, driving the status dot color.</summary>
		public RadarFreshness RadarStatus
		{
			get
			{
				// Based on the newest frame's age — available once that frame loads, no need to
				// wait for the full loop. None (gray) until then.
				if (NewestFrameTime() is not { } t) return RadarFreshness.None;
				var mins = (DateTimeOffset.Now - t).TotalMinutes;
				if (mins <= 12) return RadarFreshness.Live;
				return mins <= 30 ? RadarFreshness.Recent : RadarFreshness.Stale;
			}
		}

		// Freshest frame time available: the live slot if present, else the newest archive frame.
		private DateTimeOffset? NewestFrameTime()
		{
			if (_selectedRadarOption?.Site is null) return null;
			var live = (_hasLiveFrame && _archiveCount < _frameTimes.Length) ? _frameTimes[_archiveCount] : null;
			var arch = (_archiveCount > 0 && _archiveCount - 1 < _frameTimes.Length) ? _frameTimes[_archiveCount - 1] : null;
			if (live is { } l && arch is { } a) return l > a ? l : a;
			return live ?? arch;
		}

		// ── Next-update progress indicators (radar live frame). A 1s UI tick (RunProgressTickAsync)
		//    re-raises these so the bars fill smoothly. Math is shared in NextUpdate. ──

		/// <summary>Progress (0-100) toward the next live-frame poll, for the Selected Site bar.</summary>
		public double RadarNextFrameProgress => NextUpdate.ProgressOf(_livePollCycleStart, _nextLivePollAt);

		/// <summary>Countdown label to the next live-frame poll (e.g. "next ~12s").</summary>
		public string RadarNextFrameText => _selectedRadarOption?.Site is null ? "" : NextUpdate.CountdownOf(_nextLivePollAt);

		// Raises the polished card properties + the diagnostics text together (frame changes,
		// the 1s tick, selection changes). One call so notifications can't drift out of sync.
		private void RaiseRadarReadout()
		{
			OnPropertyChanged(nameof(RadarCardTitle));
			OnPropertyChanged(nameof(RadarSiteId));
			OnPropertyChanged(nameof(RadarSiteName));
			OnPropertyChanged(nameof(RadarCardCoords));
			OnPropertyChanged(nameof(RadarCardTime));
			OnPropertyChanged(nameof(DisplayedFrameTimeUtc));
			OnPropertyChanged(nameof(RadarFrameDetail));
			OnPropertyChanged(nameof(RadarModeText));
			OnPropertyChanged(nameof(RadarAgeText));
			OnPropertyChanged(nameof(RadarAgeMinutes));
			OnPropertyChanged(nameof(CanForceLiveCheck));
			OnPropertyChanged(nameof(NewestLoadedFrameTime));
			OnPropertyChanged(nameof(RadarLoopSpanText));
			OnPropertyChanged(nameof(RadarStatus));
			OnPropertyChanged(nameof(RadarLoadingText));
		}

		/// <summary>Whether the loop is currently playing. ⚠️ The play/stop button's glyph is picked by the
		/// view (RadarControls' <c>PlayStopGlyph</c> x:Bind function) off this bool — the VM does not carry
		/// a glyph property.</summary>
		public bool IsPlaying
		{
			get => _isPlaying;
			private set => SetProperty(ref _isPlaying, value);
		}
	}
}
