using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Tropical-cyclone watches and warnings (<see cref="TropicalProducts"/>) as forecast ZONES, written to one page
	/// file per fetch. LIVE = the NWS WWA map service (the only live source with both the zone shapes and the VTEC
	/// codes — CAP has no geometry for zone products); PAST = IEM's VTEC archive for a window. Storms are named from
	/// NHC's ATCF storm list. The host maps <see cref="CacheDirectory"/> to the <c>tropical</c> virtual host.
	/// </summary>
	public interface ITropicalService
	{
		string CacheDirectory { get; }

		/// <summary>Every tropical zone in effect now. One file, rewritten each call.</summary>
		Task<TropicalFetch> FetchLiveAsync(CancellationToken cancellationToken = default);

		/// <summary>Every tropical zone in effect at any moment of [start, end] (features carry t0/t1). Cached by
		/// its bounds once the window has settled.</summary>
		Task<TropicalFetch> FetchPastAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default);
	}

	/// <summary>One zone of one product. <see cref="T0"/>/<see cref="T1"/> are null live (everything is in effect).</summary>
	public sealed record TropicalZone(string ProductId, string StormKey, DateTimeOffset? T0, DateTimeOffset? T1)
	{
		public bool IsInEffectAt(DateTimeOffset? t) =>
			t is not { } at || T0 is null || T1 is null || (T0 <= at && at < T1);
	}

	/// <param name="Storms">Storm key ("AL092026") → its words ("Tropical Storm Isaias").</param>
	public sealed record TropicalFetch(bool Found, IReadOnlyList<TropicalZone> Zones,
		IReadOnlyDictionary<string, string> Storms, string Url, string? Error = null)
	{
		public static TropicalFetch Failed(string error) =>
			new(false, Array.Empty<TropicalZone>(), new Dictionary<string, string>(), string.Empty, error);
	}
}
