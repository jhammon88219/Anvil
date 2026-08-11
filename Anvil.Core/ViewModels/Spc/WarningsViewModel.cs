using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the storm-based WARNING subsystem — active Tornado / Severe Thunderstorm Warnings
	/// (the modern forecaster-drawn polygons). Sibling of <see cref="WatchesViewModel"/>: watches are the
	/// large outlook areas, warnings are the imminent-threat polygons, so each gets its own layer, toggle,
	/// and refresh loop. Surfaced under NowCast in the UI (current-conditions alerts, not a forecast).
	/// The show/hide toggle, opacity, map-ready latch, and source push come from
	/// <see cref="MapOverlayViewModel"/>; this class adds the ~1-min ADAPTIVE background refresh loop and
	/// the per-type active-warning counts. Fetch/cache is in <see cref="IWarningService"/>; the map is
	/// driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class WarningsViewModel : MapOverlayViewModel
	{
		private readonly IMapService _mapService;
		private readonly IWarningService _warningService;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<WarningsViewModel> _logger;

		public WarningsViewModel(IMapService mapService, IWarningService warningService, IDispatcher dispatcher, ILogger<WarningsViewModel> logger)
		{
			_mapService = mapService;
			_warningService = warningService;
			_dispatcher = dispatcher;
			_logger = logger;
		}

		protected override string SourceUrl => _warningService.WarningsUrl;
		protected override Task SetVisibleAsync(bool visible) => _mapService.SetWarningsVisibleAsync(visible);
		protected override Task SetOpacityAsync(double opacity) => _mapService.SetWarningsOpacityAsync(opacity);
		protected override Task SetSourceAsync(string url) => _mapService.SetWarningSourceAsync(url);

		// Live per-type active-warning counts for the NowCast readout, updated each refresh (UI thread).
		private int _tornadoWarningCount;
		private int _severeWarningCount;

		/// <summary>Number of active Tornado Warnings (NowCast readout). Updated each refresh cycle.</summary>
		public int TornadoWarningCount
		{
			get => _tornadoWarningCount;
			private set => SetProperty(ref _tornadoWarningCount, value);
		}

		/// <summary>Number of active Severe Thunderstorm Warnings (NowCast readout). Updated each cycle.</summary>
		public int SevereWarningCount
		{
			get => _severeWarningCount;
			private set => SetProperty(ref _severeWarningCount, value);
		}

		/// <summary>Kicks off the warning background refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = RefreshWarningsInBackgroundAsync();

		// Warnings are short-fused (issued/expiring on ~30-45 min cycles, new ones appearing continuously).
		// The endpoint sends Cache-Control: max-age=0 (revalidated per request, no edge-TTL floor), so
		// polling faster genuinely gets fresher data. We poll ADAPTIVELY: fast while warnings are active
		// (rapid updates as an event unfolds — new warnings, polygon changes), and slower when quiet. The
		// quiet interval also bounds how late the FIRST new warning can appear, so it isn't set too high.
		// A ~19 KB GeoJSON per cycle makes even the fast rate cheap; tune here.
		private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(15); // warnings on screen
		private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);   // none active

		// Best-known "are there active warnings right now" signal, updated only on a definitive fetch
		// (a cycle that actually pulled a fresh feature count). Failures/kept-last-known-good leave it
		// unchanged, so a transient hiccup doesn't drop us back to the slow cadence while a storm is on.
		private bool _hasActiveWarnings;

		private Task RefreshWarningsInBackgroundAsync() => BackgroundRefresh.RunAdaptiveAsync(async first =>
		{
			try
			{
				var result = await _warningService.RefreshAsync();
				_logger.LogInformation("Warnings refresh: {Status} active={Active} {Message}", result.Status, result.ActiveCount, result.Message);

				// A completed fetch tells us the current active state; a failure leaves the prior state.
				if (result.Status is WarningFetchStatus.Updated)
				{
					_hasActiveWarnings = result.ActiveCount > 0;

					// Push the per-type counts to the NowCast readout on the UI thread, then reload the map.
					_dispatcher.Post(() =>
					{
						TornadoWarningCount = result.TornadoCount;
						SevereWarningCount = result.SevereCount;
						RepushSource();
					});
				}
				else if (first)
				{
					// First cycle with no data yet — still point the page at the (empty) cache.
					_dispatcher.Post(RepushSource);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Warnings refresh aborted");
			}

			// Poll fast while anything is active, slow when the map is clear.
			return _hasActiveWarnings ? ActiveInterval : IdleInterval;
		});
	}
}
