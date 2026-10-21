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
	/// ForeCast's SHORT RANGE section: a convection-allowing model (the HRRR, for now) drawn on the map as one field —
	/// future radar, or updraft helicity tracks. Data is <see cref="IHrrrService"/>; the map layer is <c>model-field.js</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE BOX IS EXCLUSIVE WITH NOWCAST (the user's call, 2026-10-21): while the field DRAWS (box ticked AND ForeCast
	/// on — <see cref="IsDrawing"/>) live radar is off. <c>MapViewModel</c> owns that rule (it owns the modes); this VM
	/// only says when drawing starts and stops (<see cref="DrawingChanged"/>).
	/// ⚠️ THIS PICKS THE RUN, <see cref="Playback"/> PLAYS IT (the bottom console's model mode): "Latest" = the NEWEST run
	/// that already has the frame nearest now — a run still landing falls back to the one before, so the card always names
	/// the run on the map. The playback opens at that frame and downloads the rest of the run.
	/// ⚠️ WHEN to check is the live-feed hub's (<see cref="LiveFeedKind.Hrrr"/>, polled — nothing announces a model hour).
	/// A check is ONE bucket listing; it only runs while the field draws.
	/// ⚠️ CLOCK TIMES, never countdowns (FeedStatus). The activity row's LAST is the newest run's progress ("17Z · 7/18").
	/// </remarks>
	public sealed class ShortRangeViewModel : ObservableObject
	{
		/// <summary>The products, in the Product list's order. The tracks one is an hourly max.</summary>
		public static IReadOnlyList<HrrrField> ProductFields { get; } = new[]
		{
			HrrrFields.CompositeReflectivity, HrrrFields.Reflectivity1Km, HrrrFields.UpdraftHelicity25,
		};

		/// <summary>How many runs before the newest the Run list offers.</summary>
		private const int EarlierRuns = 6;

		private readonly IMapService _mapService;
		private readonly IHrrrService? _service;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<ShortRangeViewModel> _logger;
		private readonly LiveFeedChannel _live;
		private readonly Func<RadarRampInfo?> _reflectivityRamp;
		private readonly CancellationTokenSource _shutdown = new();
		private readonly Dictionary<DateTimeOffset, HrrrRunStatus> _runs = new();

		private bool _isMapReady;
		private bool _isShown;
		private bool _isModeActive;
		private double _opacity = 0.8;
		private HrrrField _product = HrrrFields.CompositeReflectivity;
		private int _tracksIndex = 1;
		private RunOption _selectedRun;
		private HrrrRunStatus? _newest;
		private bool _extended;
		private int _pickToken;
		private DateTimeOffset _lastCheckNow;

		/// <param name="reflectivityRamp">Radar's reflectivity ramp (the page's table, pushed at load) — the legend for
		/// future radar, which draws in radar's own colours.</param>
		public ShortRangeViewModel(IMapService mapService, IHrrrService? service, IDispatcher dispatcher,
			ILogger<ShortRangeViewModel> logger, Func<RadarRampInfo?> reflectivityRamp, LiveFeedChannel? live = null)
		{
			_mapService = mapService;
			_service = service;
			_dispatcher = dispatcher;
			_logger = logger;
			_reflectivityRamp = reflectivityRamp;
			_live = live ?? LiveFeedChannel.Polling(LiveFeedKind.Hrrr, LiveFeedKinds.DefaultPoll(LiveFeedKind.Hrrr));
			Status = new FeedStatus(LayerOrder.ModelField, "HRRR", dispatcher);
			Products = ProductFields.Select(f => new ProductOption(f)).ToList();
			_selectedRun = RunOption.Latest(null);
			RunOptions = new ObservableCollection<RunOption> { _selectedRun };
			Playback = new ModelPlaybackViewModel(mapService, service, logger, CheckNow);
			Playback.FrameShown += (_, _) => ShowCard();
		}

		/// <summary>The bottom console's model mode: the drawn run's frames, playhead and download.</summary>
		public ModelPlaybackViewModel Playback { get; }

		/// <summary>⟳ on the console: check for a newer run now (at most every 5 s).</summary>
		public void CheckNow()
		{
			if (DateTimeOffset.UtcNow - _lastCheckNow < TimeSpan.FromSeconds(5)) { return; }
			_lastCheckNow = DateTimeOffset.UtcNow;
			_live.Wake();
		}

		/// <summary>This section's row in the ForeCast activity expander (MapViewModel shows it only while ticked).</summary>
		public FeedStatus Status { get; }

		// ── The box ──

		/// <summary>The header box: the user's tick (persisted). Drawing needs ForeCast on too — <see cref="IsDrawing"/>.</summary>
		public bool IsShown
		{
			get => _isShown;
			set
			{
				if (!SetProperty(ref _isShown, value)) { return; }
				OnDrawingInputsChanged();
			}
		}

		public void ToggleShown() => IsShown = !IsShown;

		/// <summary>Whether ForeCast is running — set by <c>MapViewModel</c>.</summary>
		public bool IsModeActive
		{
			get => _isModeActive;
			set
			{
				if (!SetProperty(ref _isModeActive, value)) { return; }
				OnDrawingInputsChanged();
			}
		}

		/// <summary>The field is on the map: ticked AND ForeCast on. Excludes NowCast (MapViewModel).</summary>
		public bool IsDrawing => _isShown && _isModeActive;

		/// <summary>Raised when <see cref="IsDrawing"/> flips — MapViewModel's NowCast rule.</summary>
		public event EventHandler? DrawingChanged;

		private bool _wasDrawing;

		private void OnDrawingInputsChanged()
		{
			OnPropertyChanged(nameof(IsDrawing));
			if (IsDrawing != _wasDrawing)
			{
				_wasDrawing = IsDrawing;
				DrawingChanged?.Invoke(this, EventArgs.Empty);
				if (IsDrawing) { _live.Wake(); } // check the runs now, not at the next poll
			}
			Reconcile();
		}

		public double Opacity
		{
			get => _opacity;
			set { if (SetProperty(ref _opacity, value) && _isMapReady) { _ = _mapService.SetModelFieldOpacityAsync(value); } }
		}

		// ── Model (one for now: the slot where NAM 3 km / RRFS will go) ──

		public IReadOnlyList<string> ModelLabels { get; } = new[] { "HRRR" };

		public int ModelIndex => 0;

		// ── Product ──

		public IReadOnlyList<ProductOption> Products { get; }

		public ProductOption SelectedProduct
		{
			get => Products.First(p => p.Field == _product);
			set
			{
				if (value is null || value.Field == _product) { return; }
				_product = value.Field;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsTracksProduct));
				OnPropertyChanged(nameof(Legend));
				ProductChanged?.Invoke(this, EventArgs.Empty);
				Reconcile();
			}
		}

		/// <summary>The product's id — the persisted form.</summary>
		public string ProductId => _product.Id;

		public void RestoreProduct(string? id)
		{
			if (id is not null && HrrrFields.ById(id) is { } f && ProductFields.Contains(f)) { SelectedProduct = Products.First(p => p.Field == f); }
		}

		/// <summary>Raised when the product or the tracks mode changes (the persistence hook).</summary>
		public event EventHandler? ProductChanged;

		/// <summary>The Tracks row shows: the product is an hourly max (UH).</summary>
		public bool IsTracksProduct => _product.IsHourlyMax;

		// ── Tracks (UH only) ──

		public IReadOnlyList<string> TracksLabels { get; } = new[] { "This hour", "Run so far" };

		/// <summary>0 = this hour's max, 1 = the max of every hour so far (default — the classic swath).</summary>
		public int TracksIndex
		{
			get => _tracksIndex;
			set
			{
				var v = Math.Clamp(value, 0, 1);
				if (!SetProperty(ref _tracksIndex, v)) { return; }
				ProductChanged?.Invoke(this, EventArgs.Empty);
				Reconcile();
			}
		}

		/// <summary>The tracks mode token — the persisted form ("hour" | "run").</summary>
		public string TracksToken
		{
			get => _tracksIndex == 0 ? "hour" : "run";
			set => TracksIndex = value == "hour" ? 0 : 1;
		}

		private bool RunSoFar => _product.IsHourlyMax && _tracksIndex == 1;

		// ── Extend to f48 (00/06/12/18Z runs only) ──

		/// <summary>List the whole 48 h run on the scrubber (hourly past f18) — else f00–f18 (the user's call, 2026-10-21).
		/// Persisted.</summary>
		public bool IsExtended
		{
			get => _extended;
			set
			{
				if (!SetProperty(ref _extended, value)) { return; }
				ProductChanged?.Invoke(this, EventArgs.Empty);
				Reconcile();
			}
		}

		/// <summary>The row shows: the run on the map goes to 48 h.</summary>
		public bool CanExtend => Playback.Run is { LastHour: > HrrrFields.SubHourlyLastHour };

		// ── Run ──

		/// <summary>Latest (follows each new run) + the <see cref="EarlierRuns"/> runs before the newest (held).</summary>
		public ObservableCollection<RunOption> RunOptions { get; }

		public RunOption SelectedRun
		{
			get => _selectedRun;
			set
			{
				if (value is null || ReferenceEquals(value, _selectedRun)) { return; }
				_selectedRun = value;
				OnPropertyChanged();
				Reconcile();
			}
		}

		// ── Legend ──

		/// <summary>The legend's ramp: radar's reflectivity for future radar, the UH table for tracks.</summary>
		public RadarRampInfo? Legend => ModelRamps.ById(_product.RampId) ?? _reflectivityRamp();

		/// <summary>The radar ramp table landed (the page pushes it at load) — the legend may have been empty.</summary>
		public void RefreshLegend() => OnPropertyChanged(nameof(Legend));

		// ── The card ──

		private string _cardHeadline = "HRRR";
		private string _cardContext = string.Empty;
		private string _cardFooter = string.Empty;

		/// <summary>"HRRR 16Z · all 18 hours in" — the run ON THE MAP and how much of it has landed.</summary>
		public string CardHeadline { get => _cardHeadline; private set => SetProperty(ref _cardHeadline, value); }

		/// <summary>"valid 11:00 PM (f05)" — the frame on the map.</summary>
		public string CardContext { get => _cardContext; private set => SetProperty(ref _cardContext, value); }

		/// <summary>Loading / a problem; takes the Context's slot when set (DragMoveLayerExpanderHeaderCard).</summary>
		public string CardFooter { get => _cardFooter; private set => SetProperty(ref _cardFooter, value); }

		// ── Lifecycle ──

		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetModelFieldOpacityAsync(_opacity);
			Reconcile();
		}

		/// <summary>Starts the run-checking loop (called once at launch). It only checks while the field draws.</summary>
		public void StartBackgroundRefresh() => _ = BackgroundRefresh.RunLiveAsync(_live, async _ =>
		{
			if (_service is not null && IsDrawing) { await CheckRunsAsync().ConfigureAwait(false); }
			return _live.Schedule(Status);
		}, _shutdown.Token);

		public void Shutdown() => _shutdown.Cancel();

		// ── Runs ──

		private async Task CheckRunsAsync()
		{
			Status.Checking();
			try
			{
				var newest = await _service!.FindLatestRunAsync(_shutdown.Token).ConfigureAwait(false);
				// The run before the newest too: while the newest is landing, it is the one with the frame.
				HrrrRunStatus? before = null;
				if (newest is { IsComplete: false })
				{
					before = await _service.GetRunStatusAsync(newest.RunUtc.AddHours(-1), _shutdown.Token).ConfigureAwait(false);
				}
				_dispatcher.Post(() => ApplyRuns(newest, before));
				if (newest is null) { Status.Failed(); }
				else { Status.SucceededWith(Progress(newest)); }
			}
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "HRRR run check failed");
				Status.Failed();
				_dispatcher.Post(() => { if (Playback.ShownLead is null) { CardFooter = "HRRR unavailable — will retry"; } });
			}
		}

		// UI thread.
		private void ApplyRuns(HrrrRunStatus? newest, HrrrRunStatus? before)
		{
			if (newest is null)
			{
				if (Playback.ShownLead is null) { CardFooter = "No HRRR run found in the last 6 hours"; }
				return;
			}
			_runs[newest.RunUtc] = newest;
			if (before is not null) { _runs[before.RunUtc] = before; }
			if (_newest?.RunUtc != newest.RunUtc) { RebuildRunOptions(newest.RunUtc); }
			_newest = newest;
			Reconcile();
		}

		private void RebuildRunOptions(DateTimeOffset newestRun)
		{
			var held = _selectedRun.RunUtc;
			var latest = RunOption.Latest(newestRun);
			var list = new List<RunOption> { latest };
			for (var k = 1; k <= EarlierRuns; k++) { list.Add(RunOption.Held(newestRun.AddHours(-k))); }
			// A held run that has scrolled off the list stays in it — it is what the map shows.
			if (held is { } h && list.All(o => o.RunUtc != h)) { list.Add(RunOption.Held(h)); }
			var pick = held is { } hh ? list.First(o => o.RunUtc == hh) : latest;
			RunOptions.Clear();
			foreach (var o in list) { RunOptions.Add(o); }
			_selectedRun = pick;
			OnPropertyChanged(nameof(SelectedRun));
		}

		// ── Drawing ──

		private void Reconcile()
		{
			if (!_isMapReady) { return; }
			if (!IsDrawing || _service is null)
			{
				if (Playback.Run is not null) { _ = ClearAsync(); }
				return;
			}
			_ = EnsureAsync();
		}

		// Picks the RUN and hands it to the playback (which opens at the frame nearest now and downloads the rest). The
		// same run again only lets the playback pick up hours that landed since.
		private async Task EnsureAsync()
		{
			var held = _selectedRun.RunUtc;
			if (held is null && _newest is null) { CardFooter = "Finding the newest HRRR run…"; return; }

			var token = ++_pickToken;
			HrrrRunStatus? status;
			try { status = await PickRunAsync(held).ConfigureAwait(true); }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "HRRR run status failed");
				if (token == _pickToken) { CardFooter = "HRRR unavailable — will retry"; }
				return;
			}
			if (token != _pickToken || !IsDrawing) { return; }
			if (status is null)
			{
				CardFooter = held is null ? "The newest HRRR runs have no frame for now yet" : "That run has no frame for now yet";
				return;
			}
			// A run or product change keeps the MOMENT on screen (the old frame's valid time), not the lead.
			var keep = Playback.Run is { } was && Playback.ShownLead is { } lead ? was.RunUtc.AddMinutes(lead) : (DateTimeOffset?)null;
			var firstDraw = Playback.Run is null;
			Playback.Load(status, _product, RunSoFar, _extended && status.LastHour > HrrrFields.SubHourlyLastHour, keep);
			OnPropertyChanged(nameof(CanExtend));
			if (firstDraw) { await _mapService.SetModelFieldOpacityAsync(_opacity); }
			if (Playback.ShownLead is null) { CardFooter = "Loading…"; }
			else { ShowCard(); }
		}

		// The held run, or (Latest) the newest run that already has the frame nearest now.
		private async Task<HrrrRunStatus?> PickRunAsync(DateTimeOffset? held)
		{
			var now = DateTimeOffset.UtcNow;
			var candidates = new List<HrrrRunStatus>();
			if (held is { } run)
			{
				if (!_runs.TryGetValue(run, out var s) || !s.IsComplete)
				{
					s = await _service!.GetRunStatusAsync(run, _shutdown.Token);
					_runs[run] = s;
				}
				candidates.Add(s);
			}
			else
			{
				candidates.Add(_newest!);
				if (_runs.TryGetValue(_newest!.RunUtc.AddHours(-1), out var before)) { candidates.Add(before); }
			}
			foreach (var s in candidates)
			{
				var lead = LeadNearest(s, now);
				if (lead is { } l && ModelPlaybackViewModel.HasLanded(s, _product, l, RunSoFar)) { return s; }
			}
			return null;
		}

		// The lead whose valid time is nearest now, on the field's step, inside the run.
		private int? LeadNearest(HrrrRunStatus s, DateTimeOffset now)
		{
			var minutes = (now - s.RunUtc).TotalMinutes;
			var last = s.LastHour * 60;
			if (minutes > last) { minutes = last; }
			if (minutes < 0) { return null; }
			if (_product.IsHourlyMax)
			{
				var h = Math.Max(1, (int)Math.Ceiling(minutes / 60));
				return Math.Min(h * 60, last);
			}
			var step = _product.StepMinutesAt((int)minutes);
			return Math.Min((int)(Math.Round(minutes / step) * step), last);
		}

		// The card follows the frame ON THE MAP (the playback's), whatever the scrubber is doing.
		private void ShowCard()
		{
			if (Playback.Run is not { } status || Playback.ShownLead is not { } lead) { return; }
			var run = status.RunUtc;
			var hoursIn = status.HourlyIn.Count == 0 ? 0 : status.HourlyIn[^1];
			CardHeadline = status.IsComplete
				? $"HRRR {run:HH}Z · all {status.LastHour} hours in"
				: $"HRRR {run:HH}Z · {hoursIn} of {status.LastHour} hours in";
			var valid = run.AddMinutes(lead).ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
			// Run so far: the swath covers the run's start (f01's max begins at f00) to the frame — a clock time at BOTH ends.
			var from = run.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
			var hourly = _product.IsHourlyMax;
			CardContext = Playback.RunSoFar
				? $"{from} – {valid} (f01–{LeadText(lead, hourly)})"
				: $"valid {valid} ({LeadText(lead, hourly)})";
			CardFooter = string.Empty;
		}

		/// <summary>"f05:00" / "f05:15" — ALWAYS hours AND minutes for a 15-minute product (past f18 too), so text that
		/// ticks with the playhead never changes length (the user's call, 2026-10-21); "f05" for an hourly-only field (UH),
		/// whose frames are never between hours.</summary>
		internal static string LeadText(int leadMinutes, bool hourly = false) => hourly
			? string.Create(CultureInfo.InvariantCulture, $"f{leadMinutes / 60:D2}")
			: string.Create(CultureInfo.InvariantCulture, $"f{leadMinutes / 60:D2}:{leadMinutes % 60:D2}");

		/// <summary>The activity row's LAST: "17Z · 7/18".</summary>
		internal static string Progress(HrrrRunStatus s) =>
			string.Create(CultureInfo.InvariantCulture, $"{s.RunUtc:HH}Z · {(s.HourlyIn.Count == 0 ? 0 : s.HourlyIn[^1])}/{s.LastHour}");

		private async Task ClearAsync()
		{
			++_pickToken;
			Playback.Clear();
			OnPropertyChanged(nameof(CanExtend));
			CardFooter = string.Empty;
			await _mapService.ClearModelFieldAsync();
		}
	}

	/// <summary>One Product list entry.</summary>
	public sealed class ProductOption
	{
		internal ProductOption(HrrrField field) => Field = field;
		public HrrrField Field { get; }
		public string Label => Field.Name;
	}

	/// <summary>One Run list entry: Latest (follows each new run) or one held run.</summary>
	public sealed class RunOption
	{
		private RunOption(DateTimeOffset? runUtc, string label)
		{
			RunUtc = runUtc;
			Label = label;
		}

		/// <summary>The held run; null = Latest.</summary>
		public DateTimeOffset? RunUtc { get; }

		public string Label { get; }

		internal static RunOption Latest(DateTimeOffset? newest) =>
			new(null, newest is { } n ? string.Create(CultureInfo.InvariantCulture, $"Latest ({n:HH}Z)") : "Latest");

		internal static RunOption Held(DateTimeOffset run) =>
			new(run, string.Create(CultureInfo.InvariantCulture, $"{run:HH}Z · {run.ToLocalTime():ddd h tt}"));
	}
}
