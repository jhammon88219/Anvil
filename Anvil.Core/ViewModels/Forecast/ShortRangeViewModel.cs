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
	/// ⚠️ ONE FRAME, THE ONE NEAREST NOW, until the console plays model hours (step 3): instant fields every 15 min to f18
	/// (then hourly), hourly maxes on the hour. "Latest" draws the NEWEST run that already has that frame — a run that is
	/// still landing falls back to the one before, so the card always names the run on the map.
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
		private FrameKey? _drawn, _inFlight;
		private int _applyToken;

		private sealed record FrameKey(DateTimeOffset Run, string FieldId, int Lead, bool RunSoFar);

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
				_dispatcher.Post(() => { if (_drawn is null) { CardFooter = "HRRR unavailable — will retry"; } });
			}
		}

		// UI thread.
		private void ApplyRuns(HrrrRunStatus? newest, HrrrRunStatus? before)
		{
			if (newest is null)
			{
				if (_drawn is null) { CardFooter = "No HRRR run found in the last 6 hours"; }
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
				if (_drawn is not null || _inFlight is not null) { _ = ClearAsync(); }
				return;
			}
			_ = EnsureAsync();
		}

		private async Task EnsureAsync()
		{
			var held = _selectedRun.RunUtc;
			if (held is null && _newest is null) { CardFooter = "Finding the newest HRRR run…"; return; }

			FrameKey? key;
			HrrrRunStatus? status;
			try { (key, status) = await PickFrameAsync(held).ConfigureAwait(true); }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "HRRR run status failed");
				CardFooter = "HRRR unavailable — will retry";
				return;
			}
			if (key is null || status is null)
			{
				CardFooter = held is null ? "The newest HRRR runs have no frame for now yet" : "That run has no frame for now yet";
				return;
			}
			// ⚠️ DEDUPE BEFORE TAKING A TOKEN, AND NEVER TOUCH _applyToken ON A CALL THAT BAILS (TropicalViewModel's rule).
			if (Equals(_inFlight, key) || (Equals(_drawn, key) && _inFlight is null)) { ShowCard(key, status); return; }

			var token = ++_applyToken;
			_inFlight = key;
			CardFooter = "Loading…";
			string descriptor;
			try { descriptor = await _service!.GetFrameAsync(key.Run, _product, key.Lead, key.RunSoFar, _shutdown.Token); }
			catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
			catch (Exception ex)
			{
				if (token == _applyToken) { _inFlight = null; }
				_logger.LogWarning(ex, "HRRR frame failed ({Run:yyyyMMddHH} {Field} {Lead})", key.Run, key.FieldId, key.Lead);
				if (token == _applyToken) { CardFooter = ex is HrrrNotAvailableException ? ex.Message : "HRRR frame unavailable — will retry"; }
				return;
			}
			if (token != _applyToken || !IsDrawing) { return; }
			_inFlight = null;
			_drawn = key;
			await _mapService.SetModelFieldAsync(descriptor);
			await _mapService.SetModelFieldOpacityAsync(_opacity);
			ShowCard(key, status);
		}

		// The frame nearest now, in the held run or (Latest) the newest run that has it.
		private async Task<(FrameKey?, HrrrRunStatus?)> PickFrameAsync(DateTimeOffset? held)
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
				if (lead is { } l && HasLanded(s, l)) { return (new FrameKey(s.RunUtc, _product.Id, l, RunSoFar), s); }
			}
			return (null, null);
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

		private bool HasLanded(HrrrRunStatus s, int lead)
		{
			if (RunSoFar) { return Enumerable.Range(1, lead / 60).All(h => s.HourlyIn.Contains(h)); }
			var (kind, hour, _) = _product.Locate(lead);
			return (kind == HrrrFields.HourlyFile ? s.HourlyIn : s.SubHourlyIn).Contains(hour);
		}

		private void ShowCard(FrameKey key, HrrrRunStatus status)
		{
			var hoursIn = status.HourlyIn.Count == 0 ? 0 : status.HourlyIn[^1];
			CardHeadline = status.IsComplete
				? $"HRRR {key.Run:HH}Z · all {status.LastHour} hours in"
				: $"HRRR {key.Run:HH}Z · {hoursIn} of {status.LastHour} hours in";
			var valid = key.Run.AddMinutes(key.Lead).ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
			CardContext = key.RunSoFar ? $"f01–{LeadText(key.Lead)} · to {valid}" : $"valid {valid} ({LeadText(key.Lead)})";
			CardFooter = string.Empty;
		}

		/// <summary>"f05" on the hour, "f05:15" between.</summary>
		internal static string LeadText(int leadMinutes) => leadMinutes % 60 == 0
			? string.Create(CultureInfo.InvariantCulture, $"f{leadMinutes / 60:D2}")
			: string.Create(CultureInfo.InvariantCulture, $"f{leadMinutes / 60:D2}:{leadMinutes % 60:D2}");

		/// <summary>The activity row's LAST: "17Z · 7/18".</summary>
		internal static string Progress(HrrrRunStatus s) =>
			string.Create(CultureInfo.InvariantCulture, $"{s.RunUtc:HH}Z · {(s.HourlyIn.Count == 0 ? 0 : s.HourlyIn[^1])}/{s.LastHour}");

		private async Task ClearAsync()
		{
			++_applyToken;
			_drawn = null;
			_inFlight = null;
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
