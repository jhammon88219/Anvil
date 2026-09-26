using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The NWS warnings and SPC watches that were in effect during a PastCast window, from IEM's VTEC
	/// archive, written as two page files whose features carry their own time span (<c>t0</c>/<c>t1</c>,
	/// Unix ms) so the map can show exactly what was in effect at the displayed radar frame. The host maps
	/// <see cref="CacheDirectory"/> to the <c>pastalerts</c> virtual host.
	/// </summary>
	public interface IPastAlertService
	{
		string CacheDirectory { get; }

		/// <summary>Everything in effect at any moment of [start, end]. Never throws for a feed failure —
		/// that is <see cref="PastAlertFetch.Error"/>.</summary>
		Task<PastAlertFetch> FetchAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
	}

	/// <summary>The window's warning versions and watch counties (for the counts), and the two page files.</summary>
	public sealed record PastAlertFetch(bool Found,
		IReadOnlyList<PastAlert> Warnings, IReadOnlyList<PastAlert> Watches,
		string WarningsUrl, string WatchesUrl, string? Error = null)
	{
		public static PastAlertFetch Failed(string error) =>
			new(false, Array.Empty<PastAlert>(), Array.Empty<PastAlert>(), string.Empty, string.Empty, error);
	}
}
