using System;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Small shared helper for the app's launch-then-interval background refresh loops (SPC outlooks,
	/// SPC watches). Kept in one place so each subsystem VM runs the exact same loop shape rather than
	/// duplicating the timer plumbing.
	/// </summary>
	/// <remarks>
	/// ⚠️ EVERY LOOP TAKES A CancellationToken, AND IT IS NOT OPTIONAL — it is the shutdown token its
	/// subsystem VM cancels from <c>MapViewModel.Shutdown()</c> (MainWindow's Closed handler). These loops
	/// resume on the UI thread's DispatcherQueue, so one still ticking after the window closes is
	/// re-entering a XAML runtime that is being torn down. Deliberately has no default value: a new loop
	/// must state its lifetime rather than inherit "forever" by omission.
	/// ⚠️ Cancellation lands at the loop BOUNDARY. A cycle already inside <c>work</c> runs to completion —
	/// the token is not threaded into the fetches — so the last thing a cancelled loop does may still be a
	/// map push. What it cannot do is start another cycle.
	/// </remarks>
	internal static class BackgroundRefresh
	{
		/// <summary>
		/// Runs <paramref name="work"/> once immediately, then every <paramref name="interval"/> until
		/// <paramref name="ct"/> is cancelled. <c>first</c> is true only on the launch cycle. The caller owns
		/// its try/catch so one bad cycle can't kill the loop.
		/// </summary>
		public static async Task RunPeriodicAsync(TimeSpan interval, Func<bool, Task> work, CancellationToken ct)
		{
			var first = true;
			try
			{
				using var timer = new PeriodicTimer(interval);
				do
				{
					if (ct.IsCancellationRequested) { return; }
					await work(first);
					first = false;
				}
				while (await timer.WaitForNextTickAsync(ct));
			}
			catch (OperationCanceledException)
			{
				// The app is closing. A shutdown is not a failed cycle — swallow it so it can't reach the
				// unobserved-task handler and log itself as a crash on every clean exit.
			}
		}

		/// <summary>
		/// Like <see cref="RunPeriodicAsync"/>, but the cadence is DYNAMIC: <paramref name="work"/> returns
		/// the delay to wait before the next cycle, so it can speed up or slow down based on what it just
		/// found (e.g. poll faster while warnings are active). Runs once immediately (<c>first</c> = true),
		/// then waits the returned delay and repeats until <paramref name="ct"/> is cancelled. The caller owns
		/// its try/catch so one bad cycle can't kill the loop; a returned delay is clamped to a small floor so
		/// a bug can't spin the loop hot.
		/// </summary>
		public static async Task RunAdaptiveAsync(Func<bool, Task<TimeSpan>> work, CancellationToken ct)
		{
			var first = true;
			try
			{
				while (!ct.IsCancellationRequested)
				{
					var next = await work(first);
					first = false;
					if (next < TimeSpan.FromSeconds(1)) { next = TimeSpan.FromSeconds(1); }
					await Task.Delay(next, ct);
				}
			}
			catch (OperationCanceledException)
			{
				// See RunPeriodicAsync — a cancelled loop is a clean exit, not an error.
			}
		}
	}
}
