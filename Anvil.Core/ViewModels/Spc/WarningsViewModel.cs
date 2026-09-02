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
	/// Visibility, opacity, the map-ready latch and the source push come from
	/// <see cref="MapOverlayViewModel"/>; the per-type toggles, the live counts and the section card come
	/// from <see cref="PhenomOverlayViewModel"/>, shared with <see cref="WatchesViewModel"/>. This class
	/// adds only what is specific to warnings: the ~1-min ADAPTIVE background refresh loop. Fetch/cache is
	/// in <see cref="IWarningService"/>; the map is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class WarningsViewModel : PhenomOverlayViewModel
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
		protected override Task SetKindsAsync(bool tornado, bool severe) => _mapService.SetWarningKindsAsync(tornado, severe);

		protected override string ItemNounSingular => "warning";
		protected override string ItemNounPlural => "warnings";

		// ⚠️ The one overlay that states its cadence on the card. Warnings are the short-fused layer and
		// the poll is ADAPTIVE, so "how current is this number" has a genuinely variable answer; watches
		// tick along at a fixed 2 min and saying so would just be noise.
		protected override string CadenceSuffix =>
			$" · checking every {(_hasActiveWarnings ? ActiveInterval : IdleInterval).TotalSeconds:0}s";

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
						ApplyRefreshed(result.ActiveCount, result.TornadoCount, result.SevereCount);
						RepushSource();
					});
				}
				else if (first)
				{
					// First cycle with no data yet — still point the page at the (empty) cache.
					_dispatcher.Post(() =>
					{
						ApplyRefreshFailed(result.Message);
						RepushSource();
					});
				}
				else
				{
					_dispatcher.Post(() => ApplyRefreshFailed(result.Message));
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
