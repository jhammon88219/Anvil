using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the watch subsystem — active Tornado / Severe Thunderstorm Watches plus NWS Flood
	/// Watches (the third, flood row; <see cref="WatchFlood"/>). These
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

		/// <summary>This feed's refresh timing for the NowCast activity expander (FeedStatus).</summary>
		public FeedStatus Status { get; }

		/// <param name="live">This feed's schedule from the live-feed hub (null = poll at the default interval).</param>
		public WatchesViewModel(IMapService mapService, ISpcWatchService watchService, IDispatcher dispatcher, ILogger<WatchesViewModel> logger,
			LiveFeedChannel? live = null)
		{
			Status = new FeedStatus(Models.LayerOrder.Watches, "Watches", dispatcher);
			_live = live ?? LiveFeedChannel.Polling(Models.LiveFeedKind.Watches, Models.LiveFeedKinds.DefaultPoll(Models.LiveFeedKind.Watches));
			_mapService = mapService;
			_watchService = watchService;
			_dispatcher = dispatcher;
			_logger = logger;
		}

		protected override string SourceUrl => _watchService.WatchesUrl;
		protected override Task SetVisibleAsync(bool visible) => _mapService.SetWatchesVisibleAsync(visible);
		protected override Task SetOpacityAsync(double opacity) => _mapService.SetWatchesOpacityAsync(opacity);
		protected override Task SetSourceAsync(string url) => _mapService.SetWatchSourceAsync(url);
		protected override Task SetKindsAsync(bool tornado, bool severe, bool flashFlood, bool marine) => _mapService.SetWatchKindsAsync(tornado, severe, flashFlood);

		public override bool SupportsFlashFlood => true;
		public override string FloodLabel => WatchFlood.Label;
		public override string FloodColor => WatchFlood.Color;

		protected override string ItemNounSingular => "watch";
		protected override string ItemNounPlural => "watches";

		// Cancels this VM's app-lifetime loops when the window closes. See Shutdown().
		private readonly CancellationTokenSource _shutdown = new();

		/// <summary>Stops this subsystem's app-lifetime loops. Called from <see cref="MapViewModel.Shutdown"/>
		/// on the main window's Closed, so no loop ticks into a XAML runtime that is being torn down.</summary>
		public void Shutdown() => _shutdown.Cancel();

		/// <summary>Kicks off the watch background refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = RefreshWatchesInBackgroundAsync();

		// SPC watches change on roughly hourly scales but expire continuously (the service re-filters to in-effect
		// watches each cycle). WHEN to fetch is the live-feed hub's (LiveFeedChannel): at once when a SEL/WOU/WCN/WWP/FFA
		// is issued while a live source is connected, else the user's polling interval (Settings → Live Data).
		private readonly LiveFeedChannel _live;

		private Task RefreshWatchesInBackgroundAsync() => BackgroundRefresh.RunLiveAsync(_live, async first =>
		{
			try
			{
				Status.Checking();
				var result = await _watchService.RefreshAsync();
				_logger.LogInformation("Watches refresh: {Status} active={Active} {Message}", result.Status, result.ActiveCount, result.Message);
				if (result.Status is SpcWatchFetchStatus.Updated) { Status.Succeeded(); } else { Status.Failed(); }

				// Re-point the page at the cache so it reloads — on launch (first-run empty cache) and
				// whenever a cycle pulled fresh data. The counts ride along on the same UI-thread hop:
				// they describe the file we just cached, so they must not land before it.
				if (result.Status is SpcWatchFetchStatus.Updated)
				{
					_dispatcher.Post(() =>
					{
						ApplyRefreshed(result.ActiveCount, result.TornadoCount, result.SevereCount, result.FloodCount);
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
				Status.Failed();
				_logger.LogWarning(ex, "Watches refresh aborted");
			}
			return _live.Schedule(Status);
		}, _shutdown.Token);
	}
}
