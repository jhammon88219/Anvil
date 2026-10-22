using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The bottom console's MODEL MODE (Composites/ModelConsole): one forecast run's frames on the scrubber, the playhead,
	/// play / step, and the WHOLE-RUN DOWNLOAD that fills the cells. Owned by <see cref="ShortRangeViewModel"/>, which
	/// decides WHICH run, field and range (<see cref="Load"/>); this class plays it.
	/// </summary>
	/// <remarks>
	/// ⚠️ NOT THE RADAR LOOP: radar's console drives the sealed RadarLoopEngine; this is its own small loop over model
	/// frames. It reuses only the DRAWING scrubbers, whose cells are <see cref="RadarFrameSegment"/>s (IsReady/Fill).
	/// ⚠️ THE WHOLE RUN DOWNLOADS AS SOON AS IT LOADS (the user's call, 2026-10-21): nearest-now outward, so what you look
	/// at first lands first; a "run so far" swath IN ORDER (each builds on the last — HrrrService.RunSoFarAsync). A cell
	/// lights when its frame is built on disk; the page then fetches + inflates ~70 KB per step, which is instant enough
	/// to play (the page keeps nothing but the frame on screen).
	/// ⚠️ RANGE: 15-min frames to f18; a 48 h run's f19–f48 are hourly and only listed when extended (the user's call:
	/// f00–f18 by default, "Extend to f48" in Short Range).
	/// ⚠️ Opens at the frame NEAREST NOW; Stop returns there (radar's Stop returns to the newest frame — the same idea).
	/// ⚠️ UI thread only: every await resumes on it (the service's own work is ConfigureAwait(false) inside).
	/// </remarks>
	public sealed class ModelPlaybackViewModel : ObservableObject
	{
		/// <summary>How long a frame shows while playing, and the hold on the last one before it wraps.</summary>
		internal const int FrameMs = 220, EndHoldMs = 1500;

		/// <summary>Frames built at once (plain ones; a swath builds one at a time).</summary>
		private const int Parallel = 3;

		private readonly IMapService _mapService;
		private readonly IHrrrService? _service;
		private readonly ILogger _logger;
		private readonly Action _checkNow;

		private CancellationTokenSource? _download;
		private string?[] _descriptors = Array.Empty<string?>();
		private int _current = -1;
		private bool _isPlaying;
		private int _playToken;

		public ModelPlaybackViewModel(IMapService mapService, IHrrrService? service, ILogger logger, Action checkNow)
		{
			_mapService = mapService;
			_service = service;
			_logger = logger;
			_checkNow = checkNow;
			UnitFormat.Changed += (_, _) => RaiseFrame(); // Settings → Units: the console's clock
		}

		// ── What is loaded ──

		public HrrrRunStatus? Run { get; private set; }
		public HrrrField? Field { get; private set; }
		public bool RunSoFar { get; private set; }

		/// <summary>The frames' leads (minutes after the run), in order — one per scrubber cell.</summary>
		public IReadOnlyList<int> Leads { get; private set; } = Array.Empty<int>();

		/// <summary>The scrubber's cells (the drawing scrubbers take RadarFrameSegments).</summary>
		public ObservableCollection<RadarFrameSegment> Segments { get; } = new();

		/// <summary>Raised when the frame ON THE MAP changes (ShortRange's card follows it).</summary>
		public event EventHandler? FrameShown;

		/// <summary>The lead on the map, or null before one is drawn.</summary>
		public int? ShownLead { get; private set; }

		/// <summary>
		/// Loads <paramref name="run"/>'s frames of <paramref name="field"/> (a different run / field / range rebuilds the
		/// scrubber; the same one only picks up hours that landed since). <paramref name="keepValid"/>: the moment to open
		/// at (the old frame's valid time on a run or product change), else the frame nearest now.
		/// </summary>
		public void Load(HrrrRunStatus run, HrrrField field, bool runSoFar, bool extended, DateTimeOffset? keepValid = null)
		{
			var leads = LeadsOf(run, field, extended);
			var same = Run?.RunUtc == run.RunUtc && Field == field && RunSoFar == runSoFar && Leads.SequenceEqual(leads);
			Run = run;
			OnPropertyChanged(nameof(RunText));
			if (same)
			{
				StartDownload(); // new hours may have landed
				return;
			}
			Field = field;
			RunSoFar = runSoFar;
			Leads = leads;
			_descriptors = new string?[leads.Count];
			Segments.Clear();
			foreach (var _ in leads) Segments.Add(new RadarFrameSegment());
			var at = keepValid ?? DateTimeOffset.UtcNow;
			_current = NearestIndex(leads, (at - run.RunUtc).TotalMinutes);
			ShownLead = null;
			IsBeforeRun = false;
			RaiseFrame();
			OnPropertyChanged(nameof(ProductText));
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(IsTransportEnabled));
			StartDownload();
		}

		/// <summary>Nothing loaded (the field stopped drawing).</summary>
		public void Clear()
		{
			_download?.Cancel();
			_download = null;
			IsPlaying = false;
			Run = null;
			Field = null;
			Leads = Array.Empty<int>();
			_descriptors = Array.Empty<string?>();
			Segments.Clear();
			_current = -1;
			ShownLead = null;
			IsBeforeRun = false;
			RaiseFrame();
			OnPropertyChanged(nameof(MaxFrameIndex));
			OnPropertyChanged(nameof(IsTransportEnabled));
		}

		/// <summary>The scrubber's leads: every 15 min to f18 (or the run's end), then — extended only — hourly to f48.
		/// An hourly max: every hour from f01.</summary>
		internal static IReadOnlyList<int> LeadsOf(HrrrRunStatus run, HrrrField field, bool extended)
		{
			var lastHour = extended ? run.LastHour : Math.Min(run.LastHour, HrrrFields.SubHourlyLastHour);
			var leads = new List<int>();
			for (var lead = field.IsHourlyMax ? 60 : 0; lead <= lastHour * 60; lead += field.StepMinutesAt(lead + 1))
			{
				leads.Add(lead);
			}
			return leads;
		}

		/// <summary>Whether <paramref name="lead"/> of <paramref name="field"/> has landed in <paramref name="s"/>
		/// (a swath needs every hour up to it).</summary>
		internal static bool HasLanded(HrrrRunStatus s, HrrrField field, int lead, bool runSoFar)
		{
			if (runSoFar && field.IsHourlyMax) { return Enumerable.Range(1, lead / 60).All(h => s.HourlyIn.Contains(h)); }
			var (kind, hour, _) = field.Locate(lead);
			return (kind == HrrrFields.HourlyFile ? s.HourlyIn : s.SubHourlyIn).Contains(hour);
		}

		internal static int NearestIndex(IReadOnlyList<int> leads, double minutes)
		{
			var best = 0;
			for (var i = 1; i < leads.Count; i++)
			{
				if (Math.Abs(leads[i] - minutes) < Math.Abs(leads[best] - minutes)) best = i;
			}
			return leads.Count == 0 ? -1 : best;
		}

		// ── The whole-run download ──

		private void StartDownload()
		{
			if (_service is null || Run is not { } run || Field is not { } field) { return; }
			_download?.Cancel();
			var cts = _download = new CancellationTokenSource();
			// Swath frames in order (each builds on the last); plain frames nearest the playhead first.
			var order = Enumerable.Range(0, Leads.Count)
				.Where(i => _descriptors[i] is null && HasLanded(run, field, Leads[i], RunSoFar))
				.OrderBy(i => RunSoFar ? i : Math.Abs(i - Math.Max(0, _current)))
				.ToList();
			if (order.Count == 0) { return; }
			_ = DownloadAsync(run.RunUtc, field, RunSoFar, order, cts.Token);
		}

		private async Task DownloadAsync(DateTimeOffset run, HrrrField field, bool runSoFar, List<int> order, CancellationToken ct)
		{
			var next = 0;
			async Task Worker()
			{
				while (!ct.IsCancellationRequested && next < order.Count)
				{
					var i = order[next++]; // UI thread: no race between workers
					try
					{
						var desc = await _service!.GetFrameAsync(run, field, Leads[i], runSoFar, ct);
						if (ct.IsCancellationRequested) { return; }
						_descriptors[i] = desc;
						Segments[i].Fill = 1;
						Segments[i].IsDecoded = true;
						Segments[i].IsReady = true;
						// the playhead's frame just landed (not while comparing before the run: that pane stays blank)
						if (i == _current && ShownLead != Leads[i] && !IsBeforeRun) { _ = ShowAsync(i); }
					}
					catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "HRRR frame {Lead} of {Run:yyyyMMddHH} failed", Leads[i], run);
					}
				}
			}
			await Task.WhenAll(Enumerable.Range(0, runSoFar ? 1 : Parallel).Select(_ => Worker()));
		}

		// ── The playhead ──

		/// <summary>The playhead (the scrubbers' CurrentIndex). Setting it shows that frame once it is built.</summary>
		public double CurrentFrameIndex
		{
			get => _current;
			set
			{
				var i = (int)Math.Round(value);
				if (i == _current || i < 0 || i >= Leads.Count) { return; }
				_current = i;
				RaiseFrame();
				_ = ShowAsync(i);
			}
		}

		public int MaxFrameIndex => Leads.Count - 1;

		public bool IsTransportEnabled => Leads.Count > 1;

		private async Task ShowAsync(int i)
		{
			if (_descriptors[i] is not { } desc) { return; } // not built yet: the map keeps the last frame until it is
			ShownLead = Leads[i];
			await _mapService.SetModelFieldAsync(desc);
			FrameShown?.Invoke(this, EventArgs.Empty);
		}

		public void StepFrame(int delta)
		{
			if (Leads.Count == 0) { return; }
			IsPlaying = false;
			CurrentFrameIndex = (_current + delta + Leads.Count) % Leads.Count;
		}

		public bool IsPlaying
		{
			get => _isPlaying;
			private set
			{
				if (!SetProperty(ref _isPlaying, value)) { return; }
				_playToken++;
				if (value) { _ = PlayAsync(_playToken); }
			}
		}

		/// <summary>Play ⇄ pause in place (a scrubber grab pauses through this too).</summary>
		public void TogglePlay() => IsPlaying = !IsPlaying && IsTransportEnabled;

		// ── Following radar (Compare with radar) ──

		/// <summary>Comparing, and radar's frame is from before this run started: the model pane is BLANK (it never borrows
		/// another run's frame). The card says "before this run".</summary>
		public bool IsBeforeRun { get; private set; }

		/// <summary>
		/// COMPARE WITH RADAR: radar's console sets the time — show the frame valid nearest <paramref name="radarTime"/>
		/// (radar scans ~5 min apart, model frames 15). Null = radar has no frame yet: leave the model where it is.
		/// </summary>
		public void Follow(DateTimeOffset? radarTime)
		{
			if (Run is not { } run || radarTime is not { } at || Leads.Count == 0) { return; }
			IsPlaying = false;
			if (at < run.RunUtc)
			{
				if (IsBeforeRun) { return; }
				IsBeforeRun = true;
				ShownLead = null;
				_ = _mapService.ClearModelFieldAsync();
				FrameShown?.Invoke(this, EventArgs.Empty);
				return;
			}
			var wasBefore = IsBeforeRun;
			IsBeforeRun = false;
			var i = NearestIndex(Leads, (at - run.RunUtc).TotalMinutes);
			if (i == _current && !wasBefore) { return; }
			_current = i;
			RaiseFrame();
			_ = ShowAsync(i);
		}

		/// <summary>Comparing ended: back to the console's own playhead (re-drawn if the pane was blank).</summary>
		public void EndFollow()
		{
			if (!IsBeforeRun) { return; }
			IsBeforeRun = false;
			if (_current >= 0) { _ = ShowAsync(_current); }
		}

		/// <summary>Stop: halt and return to the frame nearest now.</summary>
		public void Stop()
		{
			IsPlaying = false;
			if (Run is { } run) { CurrentFrameIndex = NearestIndex(Leads, (DateTimeOffset.UtcNow - run.RunUtc).TotalMinutes); }
		}

		// Advances through BUILT frames only (a cell not lit yet is skipped), holding on the last before it wraps.
		private async Task PlayAsync(int token)
		{
			while (token == _playToken)
			{
				var atEnd = _current == LastReady();
				await Task.Delay(atEnd ? EndHoldMs : FrameMs);
				if (token != _playToken) { return; }
				var next = NextReady(_current);
				if (next < 0) { continue; }
				CurrentFrameIndex = next;
			}
		}

		private int NextReady(int from)
		{
			for (var k = 1; k <= Leads.Count; k++)
			{
				var i = (from + k) % Leads.Count;
				if (_descriptors[i] is not null) return i;
			}
			return -1;
		}

		private int LastReady()
		{
			for (var i = Leads.Count - 1; i >= 0; i--) if (_descriptors[i] is not null) return i;
			return -1;
		}

		/// <summary>⟳: check for a newer run now (ShortRange debounces it).</summary>
		public void CheckNow() => _checkNow();

		// ── The console's words ──

		private int? CurrentLead => _current >= 0 && _current < Leads.Count ? Leads[_current] : null;

		private DateTimeOffset? CurrentValid => Run is { } r && CurrentLead is { } l ? r.RunUtc.AddMinutes(l) : null;

		/// <summary>The clock: the playhead's VALID time ("11:00 PM"); "—" before a run.</summary>
		public string ClockText => CurrentValid is { } v ? UnitFormat.Clock(v) : "—";

		/// <summary>Under the clock: how far from now, and the day — "in 5h 15m · Thu", "2h ago · Thu".</summary>
		public string WhenText => CurrentValid is { } v ? WhenWords(v - DateTimeOffset.UtcNow, v) : string.Empty;

		internal static string WhenWords(TimeSpan fromNow, DateTimeOffset valid)
		{
			var day = UnitFormat.Date(valid, "ddd");
			var m = (int)Math.Round(Math.Abs(fromNow.TotalMinutes));
			if (m < 1) return $"now · {day}";
			var span = m < 60 ? $"{m}m" : m % 60 == 0 ? $"{m / 60}h" : $"{m / 60}h {m % 60}m";
			return fromNow > TimeSpan.Zero ? $"in {span} · {day}" : $"{span} ago · {day}";
		}

		/// <summary>"f05:15 / f18:00" — the playhead's lead over the last one listed (a fixed length: LeadText).</summary>
		public string FrameCountText => CurrentLead is { } l && Leads.Count > 0
			? $"{ShortRangeViewModel.LeadText(l, Hourly)} / {ShortRangeViewModel.LeadText(Leads[^1], Hourly)}"
			: string.Empty;

		private bool Hourly => Field?.IsHourlyMax == true;

		public string ModelText => "HRRR";

		/// <summary>"16Z · 18/18" — the run and how much of it has landed.</summary>
		public string RunText => Run is { } r ? ShortRangeViewModel.Progress(r) : "—";

		public string ProductText => Field is null ? "—"
			: Field == HrrrFields.UpdraftHelicity25 ? (RunSoFar ? "UH run so far" : "UH this hour")
			: Field == HrrrFields.Reflectivity1Km ? "1 km refl." : "Composite";

		private void RaiseFrame()
		{
			OnPropertyChanged(nameof(CurrentFrameIndex));
			OnPropertyChanged(nameof(ClockText));
			OnPropertyChanged(nameof(WhenText));
			OnPropertyChanged(nameof(FrameCountText));
		}
	}
}
