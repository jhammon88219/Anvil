using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// DEV-ONLY automated LOAD-TIME SEEDING RUN (Settings → Dev). Plays every built-in saved event through the real
	/// PastCast load path at each ticked window length, in up to three passes (<see cref="SeedPass"/>): COLD (that window's
	/// cached files and the page's decoded frames forgotten first), WARM (straight after, everything cached — a reload in
	/// the same session) and REVISIT (files kept, decoded frames forgotten — coming back to an event another day), so the
	/// load-time log (<see cref="LoopLoadLog"/>, Debug-only) fills with comparable sets. You watch it run; it waits for
	/// each loop to finish, presses "View event" itself, pauses, and moves on.
	/// </summary>
	/// <remarks>
	/// ⚠️ Order per event: windows SHORTEST first, each cold → warm → revisit — a longer window's cold pass forgets the
	/// shorter one's files too, so every cold pass really is cold. The window starts at the event's default leg start.
	/// ⚠️ The speed cap (<see cref="DevBandwidthLimit"/>) is process-wide and stays on after the run until set back to 0.
	/// ⚠️ Same Debug-only lifetime as SiteSweepViewModel: MainWindow builds it under #if DEBUG; the Dev tab is its only door.
	/// Threading: driven from the UI thread, never ConfigureAwait(false) — it writes the pickers and the site.
	/// </remarks>
	public sealed class LoadTimeSeedingViewModel : ObservableObject
	{
		/// <summary>The window lengths offered (the Timeframe picker's list) and their labels.</summary>
		public static readonly IReadOnlyList<int> WindowChoices = SavedEventLeg.AllowedDurationMinutes;

		private readonly RadarViewModel _radar;
		private readonly ISavedEventLibrary _library;
		private readonly ILevel2RadarService _radarService;
		private readonly IMapService _mapService;
		private readonly LoopLoadRecorder? _recorder;
		private bool _stop;

		public LoadTimeSeedingViewModel(RadarViewModel radar, ISavedEventLibrary library, ILevel2RadarService radarService,
			IMapService mapService, LoopLoadRecorder? recorder)
		{
			_radar = radar;
			_library = library;
			_radarService = radarService;
			_mapService = mapService;
			_recorder = recorder;
			Windows = WindowChoices.Select(m => new SeedWindowOption(m, m is 60 or 120 or 180, OnChoiceChanged)).ToList();
		}

		// ── Parameters ──

		/// <summary>One box per window length (default 1, 2 and 3 hours).</summary>
		public IReadOnlyList<SeedWindowOption> Windows { get; }

		private bool _cold = true, _warm = true, _revisit = true, _tornado = true, _hurricane = true, _derecho = true;
		public bool Cold { get => _cold; set { if (SetProperty(ref _cold, value)) OnChoiceChanged(); } }
		public bool Warm { get => _warm; set { if (SetProperty(ref _warm, value)) OnChoiceChanged(); } }
		/// <summary>Files on disk, nothing decoded: the WARM pass keeps the page's decoded frames, so it measured a reload
		/// in the same session (most frames already drawn), not the usual return to an event (2026-10-02 run).</summary>
		public bool Revisit { get => _revisit; set { if (SetProperty(ref _revisit, value)) OnChoiceChanged(); } }
		public bool Tornado { get => _tornado; set { if (SetProperty(ref _tornado, value)) OnChoiceChanged(); } }
		public bool Hurricane { get => _hurricane; set { if (SetProperty(ref _hurricane, value)) OnChoiceChanged(); } }
		public bool Derecho { get => _derecho; set { if (SetProperty(ref _derecho, value)) OnChoiceChanged(); } }

		private double _speedCapMbps;
		/// <summary>Simulated connection speed for the run's downloads, Mbps (0 = your own). Applied at Start; stays on
		/// after the run until set back to 0.</summary>
		public double SpeedCapMbps { get => _speedCapMbps; set => SetProperty(ref _speedCapMbps, Math.Clamp(value, 0, 10_000)); }

		private int _pauseSeconds = 10;
		/// <summary>The settle between loads, so the last load's background downloads don't land in the next one's numbers.</summary>
		public int PauseSeconds { get => _pauseSeconds; set => SetProperty(ref _pauseSeconds, Math.Clamp(value, 0, 600)); }

		private int _timeoutMinutes = 20;
		/// <summary>Give up on one load after this long and move on (it's logged as abandoned).</summary>
		public int TimeoutMinutes { get => _timeoutMinutes; set => SetProperty(ref _timeoutMinutes, Math.Clamp(value, 1, 240)); }

		// ── State ──

		private bool _isRunning;
		public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) OnPropertyChanged(nameof(IsIdle)); } }
		public bool IsIdle => !_isRunning;

		private string _status = "Turn on PastCast, pick the options, then Start.";
		public string StatusText { get => _status; private set => SetProperty(ref _status, value); }

		/// <summary>"28 events × 3 windows × 2 passes = 168 loads".</summary>
		public string PlanText
		{
			get
			{
				var events = Events().Count;
				var windows = Windows.Count(w => w.IsChecked);
				var passes = Passes().Count;
				return $"{events} events × {windows} windows × {passes} pass{(passes == 1 ? "" : "es")} = {events * windows * passes} loads";
			}
		}

		private void OnChoiceChanged() => OnPropertyChanged(nameof(PlanText));

		// The ticked passes in run order: cold first (it empties the cache the other two then use).
		private List<SeedPass> Passes()
		{
			var passes = new List<SeedPass>();
			if (_cold) passes.Add(SeedPass.Cold);
			if (_warm) passes.Add(SeedPass.Warm);
			if (_revisit) passes.Add(SeedPass.Revisit);
			return passes;
		}

		private List<SavedEvent> Events() => _library.GetEvents()
			.Where(e => e.IsBuiltIn && e.Kind switch
			{
				SavedEventKind.Tornado => _tornado,
				SavedEventKind.Hurricane => _hurricane,
				SavedEventKind.Derecho => _derecho,
				_ => false,
			})
			.ToList();

		// ── The run ──

		public void Stop()
		{
			_stop = true;
			if (_isRunning) StatusText = "Stopping after this load…";
		}

		public async Task StartAsync()
		{
			if (_isRunning) return;
			if (!_radar.IsPastEventMode)
			{
				StatusText = "Turn on PastCast first.";
				return;
			}
			var events = Events();
			var windows = Windows.Where(w => w.IsChecked).Select(w => w.Minutes).OrderBy(m => m).ToList();
			var passes = Passes();
			var total = events.Count * windows.Count * passes.Count;
			if (total == 0)
			{
				StatusText = "Nothing to run: tick at least one window, one pass and one type.";
				return;
			}

			_stop = false;
			IsRunning = true;
			DevBandwidthLimit.Mbps = _speedCapMbps;
			var runId = $"seed-{DateTimeOffset.Now:yyyyMMdd-HHmm}";
			var done = 0;
			try
			{
				foreach (var ev in events)
				{
					var leg = ev.DefaultLeg;
					var option = _radar.RadarOptions.FirstOrDefault(o =>
						string.Equals(o.Site?.Id, leg.SiteId, StringComparison.OrdinalIgnoreCase));
					if (option?.Site is null)
					{
						done += windows.Count * passes.Count;
						continue; // a window-only event, or a site not in the list — nothing to time
					}
					foreach (var minutes in windows)
					{
						foreach (var pass in passes)
						{
							if (_stop) return;
							done++;
							var name = pass.ToString().ToLowerInvariant(); // the log's run tag: "cold" / "warm" / "revisit"
							var label = $"{done}/{total} · {ev.Name} · {option.Site.Id} · {Hours(minutes)} · {name}";
							await LoadOneAsync(ev, leg, option, minutes, pass, $"{runId}|{name}|{minutes}m", label);
							if (_stop) return;
							await Pause(label);
						}
					}
				}
				StatusText = $"Done: {done} loads ({runId}).";
			}
			finally
			{
				if (_stop) StatusText = $"Stopped at {done}/{total} ({runId}).";
				if (_recorder is not null) _recorder.RunTag = null;
				IsRunning = false;
			}
		}

		private async Task LoadOneAsync(SavedEvent ev, SavedEventLeg leg, RadarOption option, int minutes, SeedPass pass, string tag, string label)
		{
			var start = leg.StartUtc;
			if (pass == SeedPass.Cold)
			{
				var forgotten = _radarService.ForgetCachedRange(option.Site!.Id, start, start.AddMinutes(minutes));
				StatusText = $"{label} · forgot {forgotten} cached files";
			}
			if (pass != SeedPass.Warm)
			{
				await _mapService.ForgetRadarDecodesAsync(); // cold + revisit: nothing already drawn
			}

			_radar.ApplyReplayWindow(start, minutes);
			// After the window write (it drops the saved-event pick, which clears these) — the gate's title and the log.
			_radar.LoopGate.EventName = $"{start.ToLocalTime():MMM d, yyyy} {ev.Name}";
			_radar.LoopGate.EventId = ev.Id;
			if (_recorder is not null) _recorder.RunTag = tag;

			StatusText = $"{label} · loading…";
			var clock = System.Diagnostics.Stopwatch.StartNew();
			// ⚠️ The same site + a clean window would return at once without loading (the warm pass), so a site that's
			// already selected goes straight to the Load path.
			var load = ReferenceEquals(_radar.SelectedRadarOption, option)
				? _radar.LoadSelectedPastEventAsync()
				: _radar.LoadReplayAtSiteAsync(option);
			await load;

			var limit = TimeSpan.FromMinutes(_timeoutMinutes);
			while (_radar.LoopGate.IsLoading && clock.Elapsed < limit && !_stop)
			{
				var g = _radar.LoopGate;
				StatusText = $"{label} · {g.Built}/{g.Total} built · {clock.Elapsed:m\\:ss}";
				await Task.Delay(500);
			}
			if (_radar.LoopGate.IsReadyShown) _radar.LoopGate.ViewReady();
			StatusText = clock.Elapsed >= limit ? $"{label} · timed out" : $"{label} · {_radar.LoopGate.ElapsedText}";
		}

		private async Task Pause(string label)
		{
			for (var s = _pauseSeconds; s > 0 && !_stop; s--)
			{
				StatusText = $"{label} · next in {s}s";
				await Task.Delay(1000);
			}
		}

		private static string Hours(int minutes) => minutes < 60 ? $"{minutes} min" : $"{minutes / 60} hr";
	}

	/// <summary>A seeding pass — what is already on hand when the load starts.</summary>
	public enum SeedPass
	{
		/// <summary>Nothing: the window's files and the page's decoded frames are forgotten first.</summary>
		Cold,
		/// <summary>Everything: files on disk AND frames still decoded in the page (a reload in the same session).</summary>
		Warm,
		/// <summary>Files on disk, decoded frames forgotten (coming back to an event another day).</summary>
		Revisit,
	}

	/// <summary>One window-length box on the seeding card.</summary>
	public sealed class SeedWindowOption : ObservableObject
	{
		private readonly Action _changed;
		private bool _isChecked;

		public SeedWindowOption(int minutes, bool isChecked, Action changed)
		{
			Minutes = minutes;
			_isChecked = isChecked;
			_changed = changed;
		}

		public int Minutes { get; }
		public string Label => Minutes < 60 ? $"{Minutes} min" : $"{Minutes / 60} hr";
		public bool IsChecked { get => _isChecked; set { if (SetProperty(ref _isChecked, value)) _changed(); } }
	}
}
