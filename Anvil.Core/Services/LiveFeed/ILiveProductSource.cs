using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// A live PUSH source of NWS text products (IEM's iembot feed, NOAA Weather Wire). <see cref="LiveFeedHub"/> runs
	/// ONE at a time and owns everything after a product arrives — dedupe, routing, the feeds' schedules.
	/// </summary>
	/// <remarks>
	/// ⚠️ A SOURCE NEVER GIVES UP: <see cref="RunAsync"/> reconnects (with its own back-off) until cancelled, and reports
	/// every change through the sink. It never touches UI and is <c>ConfigureAwait(false)</c> throughout.
	/// ⭐ A fourth source = one more implementation + a <c>LiveSources</c> token + a case in the hub's factory.
	/// </remarks>
	public interface ILiveProductSource
	{
		/// <summary>Runs until <paramref name="ct"/> is cancelled. Throws only <see cref="System.OperationCanceledException"/>.</summary>
		Task RunAsync(ILiveSourceSink sink, CancellationToken ct);
	}

	/// <summary>What a source reports to the hub (any thread).</summary>
	public interface ILiveSourceSink
	{
		/// <summary>The source is receiving products (first connect or back after a drop).</summary>
		void Connected();

		/// <summary>The source lost its connection (or can't make one); <paramref name="reason"/> is plain words.</summary>
		void Down(string reason);

		/// <summary>One product arrived.</summary>
		void Product(LiveProduct product);

		/// <summary>The source's own message numbers skipped <paramref name="missed"/> products (NWWS-OI only).</summary>
		void Gap(int missed);
	}
}
