using System;
using System.ComponentModel;
using Anvil.Models;

namespace Anvil.ViewModels
{
	/// <summary>
	/// PastCast's RADAR row and the table's title, read off the loop holding gate (<see cref="LoopHoldingGateViewModel"/>):
	/// "61 of 96 frames" while it builds, "96 frames" when done, "40 of 96 frames · cancelled", and the gate's event line
	/// ("KTLX · May 20, 2013 · 1:00 PM–3:00 PM") + "loaded 3:12 PM" as the title.
	/// </summary>
	/// <remarks>
	/// ⚠️ READ-ONLY OVER THE GATE: it listens to the gate's public counts and its <c>LoadMeasured</c>, and to
	/// <c>RadarViewModel.HasLoadedReplayWindow</c> — it changes nothing in the radar (the sealed engine is untouched).
	/// ⚠️ NO RETRY on this row: a radar load is the Timeframe card's Load button (the gate has its own Cancel / View).
	/// ⚠️ "Abandoned" covers a failure AND a load superseded by the next one: the next Begin overwrites it at once, and
	/// a load left for good (site change, Clear, PastCast off) drops the window, which idles the row.
	/// </remarks>
	internal sealed class PastRadarLoadRow
	{
		private readonly LoopHoldingGateViewModel _gate;
		private readonly RadarViewModel _radar;
		private readonly TemporalActivityViewModel _activity;

		private PastRadarLoadRow(LoopHoldingGateViewModel gate, RadarViewModel radar, PastLayerStatus row, TemporalActivityViewModel activity)
		{
			_gate = gate;
			_radar = radar;
			Row = row;
			_activity = activity;
			gate.PropertyChanged += OnGateChanged;
			gate.LoadMeasured += OnLoadMeasured;
			radar.PropertyChanged += OnRadarChanged;
		}

		public PastLayerStatus Row { get; }

		/// <summary>Watches <paramref name="radar"/>'s loop gate for <paramref name="row"/> and <paramref name="activity"/>'s title.</summary>
		public static PastRadarLoadRow Attach(RadarViewModel radar, PastLayerStatus row, TemporalActivityViewModel activity) =>
			new(radar.LoopGate, radar, row, activity);

		private void OnGateChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(LoopHoldingGateViewModel.IsLoading):
				case nameof(LoopHoldingGateViewModel.Built):
				case nameof(LoopHoldingGateViewModel.Total):
					if (_gate.IsLoading) { Row.Loading(LoadingText(_gate.Built, _gate.Total)); }
					break;
				case nameof(LoopHoldingGateViewModel.EventLine):
					if (_gate.IsLoading) { _activity.SetEvent(_gate.EventLine, null); }
					break;
			}
		}

		private void OnLoadMeasured(object? sender, LoopLoadTiming t)
		{
			switch (t.Outcome)
			{
				case LoopLoadOutcome.Finished:
					Row.Loaded(PastLayerStatus.Count(t.Frames, "frame", "frames"));
					_activity.SetEvent(_gate.EventLine, DateTimeOffset.Now);
					break;
				case LoopLoadOutcome.Cancelled:
					Row.Loaded($"{t.Kept} of {t.Frames} frames · cancelled");
					_activity.SetEvent(_gate.EventLine, DateTimeOffset.Now);
					break;
				default:
					Row.Failed("The radar load stopped before every frame was in.");
					break;
			}
		}

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(RadarViewModel.HasLoadedReplayWindow) or nameof(RadarViewModel.IsPastEventMode)
				&& !_gate.IsLoading && !(_radar.IsPastEventMode && _radar.HasLoadedReplayWindow))
			{
				Row.Idle();
				_activity.SetEvent(string.Empty, null);
			}
		}

		/// <summary>"finding volumes…" before the list is in, then "61 of 96 frames" (BUILT — what you can play). Internal for tests.</summary>
		internal static string LoadingText(int built, int total) =>
			total == 0 ? "finding volumes…" : $"{built} of {total} frames";
	}
}
