using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the SPC watch-box subsystem — active Tornado / Severe Thunderstorm Watches. These
	/// are current-conditions alerts (not a forecast), so they live in their OWN subsystem VM (surfaced
	/// under NowCast in the UI) rather than on <see cref="OutlookViewModel"/>. Visibility, opacity, the
	/// map-ready latch and the source push come from <see cref="MapOverlayViewModel"/>; the per-type
	/// toggles, the live counts and the section card come from <see cref="PhenomOverlayViewModel"/>,
	/// shared with its sibling <see cref="WarningsViewModel"/>. This class adds only what is specific to
	/// watches: the ~2-min background refresh loop and the service it drives. Fetch/cache is in
	/// <see cref="ISpcWatchService"/>; the map is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class WatchesViewModel : PhenomOverlayViewModel
	{
		private readonly IMapService _mapService;
		private readonly ISpcWatchService _watchService;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<WatchesViewModel> _logger;

		public WatchesViewModel(IMapService mapService, ISpcWatchService watchService, IDispatcher dispatcher, ILogger<WatchesViewModel> logger)
		{
			_mapService = mapService;
			_watchService = watchService;
			_dispatcher = dispatcher;
			_logger = logger;
		}

		protected override string SourceUrl => _watchService.WatchesUrl;
		protected override Task SetVisibleAsync(bool visible) => _mapService.SetWatchesVisibleAsync(visible);
		protected override Task SetOpacityAsync(double opacity) => _mapService.SetWatchesOpacityAsync(opacity);
		protected override Task SetSourceAsync(string url) => _mapService.SetWatchSourceAsync(url);
		protected override Task SetKindsAsync(bool tornado, bool severe) => _mapService.SetWatchKindsAsync(tornado, severe);

		protected override string ItemNounSingular => "watch";
		protected override string ItemNounPlural => "watches";

		/// <summary>Kicks off the watch background refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = RefreshWatchesInBackgroundAsync();

		// SPC watches change on roughly hourly scales but expire continuously; a few-minute refresh
		// keeps the active set current (the service re-filters to in-effect watches each cycle).
		private static readonly TimeSpan WatchRefreshInterval = TimeSpan.FromMinutes(2);

		private Task RefreshWatchesInBackgroundAsync() => BackgroundRefresh.RunPeriodicAsync(WatchRefreshInterval, async first =>
		{
			try
			{
				var result = await _watchService.RefreshAsync();
				_logger.LogInformation("Watches refresh: {Status} active={Active} {Message}", result.Status, result.ActiveCount, result.Message);

				// Re-point the page at the cache so it reloads — on launch (first-run empty cache) and
				// whenever a cycle pulled fresh data. The counts ride along on the same UI-thread hop:
				// they describe the file we just cached, so they must not land before it.
				if (result.Status is SpcWatchFetchStatus.Updated)
				{
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
				_logger.LogWarning(ex, "Watches refresh aborted");
			}
		});
	}
}
