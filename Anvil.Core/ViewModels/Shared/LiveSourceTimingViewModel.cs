using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// DEV → Live source timing (Debug-only card, <c>Composites/DevSettingsTab</c>): listens to IEM AND NOAA Weather Wire at
	/// the same time, pairs the same product heard on both (<see cref="LiveSourceTiming"/>), and shows how much sooner each
	/// one had it — the number Settings → Live Data's Weather Wire hint is waiting for.
	/// </summary>
	/// <remarks>
	/// ⚠️ Its sources are its OWN (<see cref="LiveFeedHub.TimingSources"/>), not the hub's run, and nothing they hear wakes
	/// a feed. ⚠️ One Weather Wire sign-in per account: it refuses to start while Live Data is set to Weather Wire.
	/// Arrivals are stamped the moment a source hands a product over (IEM's own poll every 6 s is part of IEM's number —
	/// that is what a user of IEM gets).
	/// </remarks>
	public sealed class LiveSourceTimingViewModel : ObservableObject
	{
		private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(2);

		private readonly LiveFeedHub _hub;
		private readonly IDispatcher _dispatcher;
		private CancellationTokenSource? _run;
		private LiveSourceTiming? _timing;
		private DateTimeOffset _started;
		private string _statusText = "Not running.";
		private string _resultText = string.Empty;
		private string _iemState = "—", _nwwsState = "—";

		public LiveSourceTimingViewModel(LiveFeedHub hub, IDispatcher dispatcher)
		{
			_hub = hub;
			_dispatcher = dispatcher;
		}

		public bool IsRunning => _run is not null;
		public bool IsIdle => _run is null;

		public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

		/// <summary>The findings so far, several lines (selectable in the card, to paste into a note).</summary>
		public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }

		public void Start()
		{
			if (_run is not null) { return; }
			var (iem, nwws, refusal) = _hub.TimingSources();
			if (refusal is not null || iem is null || nwws is null)
			{
				StatusText = refusal ?? "Couldn't start.";
				return;
			}
			_timing = new LiveSourceTiming();
			_started = DateTimeOffset.Now;
			_iemState = _nwwsState = "connecting";
			var run = _run = new CancellationTokenSource();
			Raise();
			_ = RunAsync(iem, new Recorder(this, TimedSource.Iem), run.Token);
			_ = RunAsync(nwws, new Recorder(this, TimedSource.Nwws), run.Token);
			_ = RefreshLoopAsync(run.Token);
		}

		public void Stop()
		{
			var run = _run;
			if (run is null) { return; }
			_run = null;
			run.Cancel();
			run.Dispose();
			Refresh();
			StatusText = $"Stopped after {Elapsed()}.";
			Raise();
		}

		private static async Task RunAsync(ILiveProductSource source, ILiveSourceSink sink, CancellationToken ct)
		{
			try { await Task.Run(() => source.RunAsync(sink, ct), ct).ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			catch (Exception ex) { sink.Down($"stopped: {ex.Message}"); }
		}

		private async Task RefreshLoopAsync(CancellationToken ct)
		{
			try
			{
				while (!ct.IsCancellationRequested)
				{
					_dispatcher.Post(Refresh);
					await Task.Delay(RefreshEvery, ct).ConfigureAwait(false);
				}
			}
			catch (OperationCanceledException) { }
		}

		private void Refresh()
		{
			if (_timing is null) { return; }
			if (_run is not null) { StatusText = $"Running {Elapsed()} · IEM {_iemState} · Weather Wire {_nwwsState}"; }
			ResultText = Describe(_timing.Summary());
		}

		/// <summary>The summary in words. Internal for tests.</summary>
		internal static string Describe(LiveSourceTimingSummary s)
		{
			var heard = $"Heard {s.HeardIem} on IEM · {s.HeardNwws} on Weather Wire · {s.Pairs} matched on both";
			if (s.Pairs == 0) { return heard + "\nNo product heard on both yet — give it a few minutes."; }
			var first = s.NwwsFirstShare is { } share ? $"Weather Wire first {share:P0} of the time" : "";
			var lead = $"by {Seconds(s.MedianLeadSeconds)} typically (10–90%: {Seconds(s.P10LeadSeconds)} to {Seconds(s.P90LeadSeconds)})";
			var lag = $"After the issue time (to the minute): IEM ~{Seconds(s.MedianIemLagSeconds)} · Weather Wire ~{Seconds(s.MedianNwwsLagSeconds)}";
			return $"{heard}\n{first}, {lead}\n{lag}";
		}

		private static string Seconds(double? s) => s is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + " s" : "—";

		private string Elapsed()
		{
			var t = DateTimeOffset.Now - _started;
			return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)t.TotalMinutes} min {t.Seconds} s";
		}

		private void Raise()
		{
			OnPropertyChanged(nameof(IsRunning));
			OnPropertyChanged(nameof(IsIdle));
		}

		private void SetState(TimedSource source, string state)
		{
			if (source == TimedSource.Iem) { _iemState = state; } else { _nwwsState = state; }
		}

		/// <summary>Stamps each product the moment its source hands it over. Any thread.</summary>
		private sealed class Recorder : ILiveSourceSink
		{
			private readonly LiveSourceTimingViewModel _vm;
			private readonly TimedSource _source;

			public Recorder(LiveSourceTimingViewModel vm, TimedSource source)
			{
				_vm = vm;
				_source = source;
			}

			public void Connected() => _vm.SetState(_source, "connected");
			public void Down(string reason) => _vm.SetState(_source, "down (" + reason + ")");
			public void Product(LiveProduct product) => _vm._timing?.Heard(_source, product, DateTimeOffset.UtcNow);
			public void Gap(int missed) { }
		}
	}
}
