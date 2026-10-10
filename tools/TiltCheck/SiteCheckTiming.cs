// --sitecheck: how long the app's LIVE SITE CHECK (Level2RadarService.GetLiveSiteIdsAsync — ~200 S3 day listings, one per
// site) keeps the UI THREAD busy. The app starts it from the UI thread (RadarViewModel.RefreshLiveSiteStatusAsync), so
// every continuation that doesn't ConfigureAwait(false) runs THERE. Measured 2026-10-10 because the map froze for
// 1-1.7 s at a time right after launch, while that pass ran (radar-diag perf.ui: no ticks, ~one core busy, no WebView
// messages).
//
// It runs the real service call on a single-threaded SynchronizationContext standing in for the UI thread (a Progress
// built on it, like the app's) and times every callback that thread runs: total busy time, the longest single callback,
// and how many ran over 50 ms. Read-only: listings only, nothing cached.

using System.Collections.Concurrent;
using System.Diagnostics;
using Anvil.Services;

internal static class SiteCheckTiming
{
    public static async Task<int> RunAsync()
    {
        var settings = new SettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsService>.Instance,
            Path.Combine(Path.GetTempPath(), "anvil-sitecheck-settings"));
        var svc = new Level2RadarService(Microsoft.Extensions.Logging.Abstractions.NullLogger<Level2RadarService>.Instance, settings,
            new NonStandardVcpLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<NonStandardVcpLog>.Instance,
                Path.Combine(Path.GetTempPath(), "anvil-sitecheck-vcp")));

        using var ui = new UiThread();
        var reports = 0;
        var wall = Stopwatch.StartNew();
        var result = await ui.Run(async () =>
        {
            var progress = new Progress<Anvil.Models.SiteCheckResult>(_ => reports++); // built ON the "UI thread", as the app does
            return await svc.GetLiveSiteIdsAsync(progress);
        });
        wall.Stop();
        await Task.Delay(500); // let the last progress posts drain

        Console.WriteLine($"== live site check: {result.Count} live, {reports} progress reports, {wall.Elapsed.TotalSeconds:0.0} s wall");
        Console.WriteLine($"   UI thread busy {ui.BusyMs:0} ms in {ui.Callbacks} callbacks; longest {ui.LongestMs:0} ms; " +
                          $"{ui.Over50} over 50 ms, {ui.Over100} over 100 ms");
        return 0;
    }

    // A single dedicated thread with a SynchronizationContext that queues work to it and times each item.
    private sealed class UiThread : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = new();
        private readonly Thread _thread;
        public double BusyMs, LongestMs;
        public int Callbacks, Over50, Over100;

        public UiThread()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (cb, state) in _queue.GetConsumingEnumerable())
                {
                    var sw = Stopwatch.StartNew();
                    cb(state);
                    var ms = sw.Elapsed.TotalMilliseconds;
                    BusyMs += ms;
                    Callbacks++;
                    if (ms > LongestMs) LongestMs = ms;
                    if (ms > 50) Over50++;
                    if (ms > 100) Over100++;
                }
            }) { IsBackground = true, Name = "fake UI" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task<T> Run<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { done.SetResult(await work()); }
                catch (Exception ex) { done.SetException(ex); }
            }, null);
            return done.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}
