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
	/// ⚠️ COMPARE WITH RADAR (the user's call, 2026-10-21; <see cref="IsComparing"/>): while it is on, the field does NOT
	/// exclude NowCast — radar draws in pane 0, the model in pane 1 (MapViewModel owns the modes + the layout), and the
	/// RADAR console sets the time (<see cref="RadarFrameTime"/> → <see cref="ModelPlaybackViewModel.Follow"/>).
	/// ⚠️ THIS PICKS THE RUN, <see cref="Playback"/> PLAYS IT (the bottom console's model mode): "Latest" = the NEWEST run
	/// once its f00–f18 have landed (<see cref="HasShortRange"/>; the user's call, 2026-10-21) — until then the run before,
	/// with the arriving one listed in the Run box ("18Z · arriving 7/18") to pick by hand. So the run never changes
	/// mid-loop with two hours in. The playback opens at the frame nearest now and downloads the rest of the run.
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
		private HrrrRunStatus? _latest; // what Latest resolves to: _newest once it has f00–f18, else the run before
		private bool _extended;
		private bool _isComparing;
		private bool _wasCompareActive;
		private bool _hasRadarSite;
		private DateTimeOffset? _radarFrameTime;
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
			OnCompareInputsChanged();
			Reconcile();
		}

		// ── Compare with radar ──

		/// <summary>The "Compare with radar" box (persisted). Comparing needs the field drawing — <see cref="IsCompareActive"/>.</summary>
		public bool IsComparing
		{
			get => _isComparing;
			set
			{
				if (!SetProperty(ref _isComparing, value)) { return; }
				OnPropertyChanged(nameof(ProductHint));
				OnCompareInputsChanged();
			}
		}

		/// <summary>Comparing NOW: the box AND the field drawing. Radar in pane 0, the model in pane 1, radar's console.</summary>
		public bool IsCompareActive => _isComparing && IsDrawing;

		/// <summary>The bottom console's model mode shows: drawing, and not comparing (radar's console runs then).</summary>
		public bool IsModelConsoleShown => IsDrawing && !IsCompareActive;

		/// <summary>Raised when <see cref="IsCompareActive"/> flips — MapViewModel's modes + pane layout.</summary>
		public event EventHandler? CompareChanged;

		/// <summary>A radar site is loaded (set by MapViewModel) — the compare hint asks for one otherwise.</summary>
		public bool HasRadarSite
		{
			get => _hasRadarSite;
			set { if (SetProperty(ref _hasRadarSite, value)) { OnPropertyChanged(nameof(CompareHint)); } }
		}

		/// <summary>Radar's DISPLAYED frame time (set by MapViewModel on every frame change). While comparing, the model
		/// follows it.</summary>
		public DateTimeOffset? RadarFrameTime
		{
			get => _radarFrameTime;
			set
			{
				if (_radarFrameTime == value) { return; }
				_radarFrameTime = value;
				if (IsCompareActive) { Playback.Follow(value); }
			}
		}

		private void OnCompareInputsChanged()
		{
			OnPropertyChanged(nameof(IsCompareActive));
			OnPropertyChanged(nameof(IsModelConsoleShown));
			OnPropertyChanged(nameof(CompareHint));
			if (IsCompareActive == _wasCompareActive) { return; }
			_wasCompareActive = IsCompareActive;
			if (_isMapReady) { _ = _mapService.SetModelFieldPanesAsync(IsCompareActive ? ComparePanes : null); }
			if (IsCompareActive) { Playback.Follow(_radarFrameTime); }
			else { Playback.EndFollow(); }
			ShowCard();
			CompareChanged?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>While comparing, the model draws in pane 1 only (radar keeps pane 0).</summary>
		internal static readonly IReadOnlyList<int> ComparePanes = new[] { 1 };

		/// <summary>Whether pane <paramref name="pane"/> shows the MODEL (its notch the model form): every pane while the
		/// field draws, only <see cref="ComparePanes"/> while comparing. The page's split is the same rule (map.js).</summary>
		public static bool ModelInPane(bool drawing, bool compareActive, int pane) =>
			drawing && (!compareActive || ComparePanes.Contains(pane));

		/// <summary>Under the Compare box: what to do, or why it can't work yet, or the young-run caution.</summary>
		public string CompareHint =>
			!_isComparing ? string.Empty
			: !_hasRadarSite ? "Pick a radar site to compare."
			: Playback.Run is { } r && DateTimeOffset.UtcNow - r.RunUtc < TimeSpan.FromHours(2)
				? "A run's first hour is mostly radar data. An older run is the stronger test."
			: "Radar on the left, HRRR on the right. The radar console sets the time.";

		/// <summary>Under Product while comparing a product other than 1 km reflectivity.</summary>
		public string ProductHint => _isComparing && _product != HrrrFields.Reflectivity1Km
			? "1 km reflectivity looks most like radar's lowest scan"
			: string.Empty;

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
				OnPropertyChanged(nameof(ProductHint));
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
		private string _frameText = string.Empty;
		private string _cardFooter = string.Empty;

		/// <summary>"HRRR 16Z · all 18 hours in" — the run ON THE MAP and how much of it has landed.</summary>
		public string CardHeadline { get => _cardHeadline; private set => SetProperty(ref _cardHeadline, value); }

		/// <summary>The card's right side: what this range IS (fixed; the user's call, 2026-10-21 — it was a line under the card).</summary>
		public string CardContext => "Next 18 hr · CAM";

		/// <summary>"valid 11:00 PM (f05:00)" — the frame on the map. The line UNDER the card (it swapped places with the
		/// range note, 2026-10-21).</summary>
		public string FrameText { get => _frameText; private set => SetProperty(ref _frameText, value); }

		/// <summary>Loading / a problem; takes the Context's slot when set (DragMoveLayerExpanderHeaderCard).</summary>
		public string CardFooter { get => _cardFooter; private set => SetProperty(ref _cardFooter, value); }

		private string _modelPaneLabel = "HRRR";

		/// <summary>The model pane's corner label while comparing: "HRRR 15Z · valid 6:00 PM" / "… · before this run".</summary>
		public string ModelPaneLabel { get => _modelPaneLabel; private set => SetProperty(ref _modelPaneLabel, value); }

		// ── Lifecycle ──

		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetModelFieldOpacityAsync(_opacity);
			if (IsCompareActive) { await _mapService.SetModelFieldPanesAsync(ComparePanes); }
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
				// The run before the newest too: until the newest has its f00–f18, Latest is that one.
				HrrrRunStatus? before = null;
				if (newest is not null && !HasShortRange(newest))
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
			_newest = newest;
			_latest = ResolveLatest(newest, before);
			RebuildRunOptions(newest, _latest);
			Reconcile();
		}

		/// <summary>A run's SHORT RANGE has landed: f00–f18 (or its whole length if shorter) in both files — the bar Latest
		/// waits for (a 48 h run switches at f18 too; f19–f48 fill in after).</summary>
		internal static bool HasShortRange(HrrrRunStatus s)
		{
			var h = Math.Min(HrrrFields.SubHourlyLastHour, s.LastHour);
			return s.HourlyIn.Contains(h) && s.SubHourlyIn.Contains(h);
		}

		/// <summary>What Latest shows: the newest run once it has its f00–f18, else the run before (if known), else the newest.</summary>
		internal static HrrrRunStatus ResolveLatest(HrrrRunStatus newest, HrrrRunStatus? before) =>
			HasShortRange(newest) || before is null ? newest : before;

		// Latest (the run it resolves to) · the arriving newest, if Latest is holding the one before · the earlier runs.
		// Rebuilt only when a label changes (the arriving run's progress, a new run), keeping the pick by its run.
		private void RebuildRunOptions(HrrrRunStatus newest, HrrrRunStatus latestRun)
		{
			var list = RunOptionsFor(newest, latestRun, _selectedRun.RunUtc);
			if (list.Select(o => o.Label).SequenceEqual(RunOptions.Select(o => o.Label))) { return; }
			var held = _selectedRun.RunUtc;
			var pick = held is { } hh ? list.First(o => o.RunUtc == hh) : list[0];
			RunOptions.Clear();
			foreach (var o in list) { RunOptions.Add(o); }
			_selectedRun = pick;
			OnPropertyChanged(nameof(SelectedRun));
		}

		/// <summary>The Run list: Latest, then the newest <see cref="EarlierRuns"/> + 1 hours' runs except the one Latest is
		/// on (the newest marked "arriving" while Latest waits for it), then a held run that has scrolled off.</summary>
		internal static List<RunOption> RunOptionsFor(HrrrRunStatus newest, HrrrRunStatus latestRun, DateTimeOffset? held)
		{
			var list = new List<RunOption> { RunOption.Latest(latestRun.RunUtc) };
			for (var k = 0; k <= EarlierRuns; k++)
			{
				var run = newest.RunUtc.AddHours(-k);
				if (run == latestRun.RunUtc) { continue; }
				list.Add(k == 0 ? RunOption.Arriving(newest) : RunOption.Held(run));
			}
			// A held run that has scrolled off the list stays in it — it is what the map shows.
			if (held is { } h && list.All(o => o.RunUtc != h)) { list.Add(RunOption.Held(h)); }
			return list;
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
			if (IsCompareActive) { Playback.Follow(_radarFrameTime); } // comparing: radar's frame, not now
			OnPropertyChanged(nameof(CanExtend));
			OnPropertyChanged(nameof(CompareHint));
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
				// Latest: the run it resolves to (newest once it has f00–f18), the other one only as a fallback.
				candidates.Add(_latest ?? _newest!);
				if (!ReferenceEquals(candidates[0], _newest)) { candidates.Add(_newest!); }
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
			if (Playback.Run is not { } status) { return; }
			var run = status.RunUtc;
			var hoursIn = status.HourlyIn.Count == 0 ? 0 : status.HourlyIn[^1];
			CardHeadline = status.IsComplete
				? $"HRRR {run:HH}Z · all {status.LastHour} hours in"
				: $"HRRR {run:HH}Z · {hoursIn} of {status.LastHour} hours in";
			OnPropertyChanged(nameof(CompareHint));
			if (Playback.IsBeforeRun)
			{
				// Comparing, radar's frame is older than the run: the model pane is blank, and says why.
				FrameText = string.Create(CultureInfo.InvariantCulture, $"before this run ({run:HH}Z)");
				ModelPaneLabel = string.Create(CultureInfo.InvariantCulture, $"HRRR {run:HH}Z · before this run");
				CardFooter = string.Empty;
				return;
			}
			if (Playback.ShownLead is not { } lead) { return; }
			ModelPaneLabel = string.Create(CultureInfo.InvariantCulture, $"HRRR {run:HH}Z · valid ")
				+ run.AddMinutes(lead).ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
			var valid = run.AddMinutes(lead).ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
			// Run so far: the swath covers the run's start (f01's max begins at f00) to the frame — a clock time at BOTH ends.
			var from = run.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
			var hourly = _product.IsHourlyMax;
			FrameText = Playback.RunSoFar
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

		/// <summary>The newest run while Latest waits for its f00–f18: held if picked, labelled with how far in it is.</summary>
		internal static RunOption Arriving(HrrrRunStatus s) =>
			new(s.RunUtc, string.Create(CultureInfo.InvariantCulture,
				$"{s.RunUtc:HH}Z · arriving {(s.HourlyIn.Count == 0 ? 0 : s.HourlyIn[^1])}/{Math.Min(HrrrFields.SubHourlyLastHour, s.LastHour)}"));
	}
}
