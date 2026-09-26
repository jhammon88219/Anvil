using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The radar's own storm-cell attributes (cell tracks, TVS, mesocyclones, hail) for one site over a time
	/// window, fetched from IEM and cached as the file the page draws. The host (MainWindow) maps
	/// <see cref="CacheDirectory"/> to the <c>stormcells</c> virtual host.
	/// </summary>
	public interface IStormCellService
	{
		/// <summary>Folder the page files are written to (mapped to the virtual host).</summary>
		string CacheDirectory { get; }

		/// <summary>
		/// Fetches every scan for <paramref name="siteId"/> in [start − past-track reach, end] and writes the
		/// page file. <paramref name="live"/> = a rolling window that is re-fetched every call and written to
		/// ONE file per site; otherwise the window is historical, cached by its bounds and fetched once.
		/// Never throws for a feed failure — that is <see cref="StormCellFetch.Error"/>.
		/// </summary>
		Task<StormCellFetch> FetchAsync(string siteId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool live,
			CancellationToken cancellationToken = default);
	}

	/// <summary>A fetch's scans (oldest first), the page URL of the written file, or why there is none.</summary>
	public sealed record StormCellFetch(bool Found, IReadOnlyList<StormCellScan> Scans, string Url, string? Error = null)
	{
		public static StormCellFetch Failed(string error) => new(false, Array.Empty<StormCellScan>(), string.Empty, error);
	}
}
