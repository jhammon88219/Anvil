using System;
using System.Threading.Tasks;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the SPC watch-box subsystem — active Tornado / Severe Thunderstorm Watches. These
	/// are current-conditions alerts (not a forecast), so they live in their OWN subsystem VM (surfaced
	/// under NowCast in the UI) rather than on <see cref="OutlookViewModel"/>. The show/hide toggle,
	/// opacity, map-ready latch, and source push come from <see cref="MapOverlayViewModel"/>; this class
	/// adds the ~2-min background refresh loop. Fetch/cache is in <see cref="ISpcWatchService"/>; the map
	/// is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class WatchesViewModel : MapOverlayViewModel
	{
		private readonly IMapService _mapService;
		private readonly ISpcWatchService _watchService;
		private readonly IDispatcher _dispatcher;

		public WatchesViewModel(IMapService mapService, ISpcWatchService watchService, IDispatcher dispatcher)
		{
			_mapService = mapService;
			_watchService = watchService;
			_dispatcher = dispatcher;
		}

		protected override string SourceUrl => _watchService.WatchesUrl;
		protected override Task SetVisibleAsync(bool visible) => _mapService.SetWatchesVisibleAsync(visible);
		protected override Task SetOpacityAsync(double opacity) => _mapService.SetWatchesOpacityAsync(opacity);
		protected override Task SetSourceAsync(string url) => _mapService.SetWatchSourceAsync(url);

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
				System.Diagnostics.Debug.WriteLine($"[SPC] watches refresh: {result.Status} active={result.ActiveCount} {result.Message}");

				// Re-point the page at the cache so it reloads — on launch (first-run empty cache) and
				// whenever a cycle pulled fresh data.
				if (first || result.Status is SpcWatchFetchStatus.Updated)
				{
					_dispatcher.Post(RepushSource);
				}
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[SPC] watches refresh aborted: {ex.Message}");
			}
		});
	}
}
