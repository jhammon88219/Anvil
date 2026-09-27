using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// SPC mesoscale discussions and WPC precipitation discussions (every <see cref="DiscussionKinds"/> kind)
	/// for a window, from IEM: the index + polygons as one page file (features carry t0/t1 so the map can
	/// show what was in effect at a moment), and each product's full text on demand. The host maps
	/// <see cref="CacheDirectory"/> to the <c>discussions</c> virtual host.
	/// </summary>
	public interface IMesoDiscussionService
	{
		string CacheDirectory { get; }

		/// <summary>
		/// Every discussion in effect at any moment of [start, end], oldest first. <paramref name="live"/> =
		/// a rolling window re-fetched every call into ONE file; otherwise cached by its bounds once settled.
		/// A kind that fails is reported in <see cref="MesoDiscussionFetch.Error"/> while the others still load.
		/// </summary>
		Task<MesoDiscussionFetch> FetchAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, bool live,
			CancellationToken cancellationToken = default);

		/// <summary>The product's text, split into sections; null if it can't be had. Cached forever — a
		/// product never changes once issued.</summary>
		Task<MesoDiscussionText?> GetTextAsync(MesoDiscussion discussion, CancellationToken cancellationToken = default);
	}

	public sealed record MesoDiscussionFetch(bool Found, IReadOnlyList<MesoDiscussion> Discussions, string Url, string? Error = null)
	{
		public static MesoDiscussionFetch Failed(string error) => new(false, Array.Empty<MesoDiscussion>(), string.Empty, error);
	}
}
