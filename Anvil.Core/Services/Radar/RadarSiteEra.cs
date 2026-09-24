using System;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// THE rule for whether a site existed in the era being viewed. A working site always did. A RETIRED id (a
	/// moved/renamed radar — KLIX → KHDC, TPBI → TDJT; see <see cref="RadarSite.RetiredOn"/>) has archive data
	/// only up to its retired day, so it's useless live but the only way to replay the old radar: hidden live,
	/// shown in PastCast while the replay window STARTS on or before that day (UTC).
	/// </summary>
	public static class RadarSiteEra
	{
		/// <param name="replayStartUtc">The replay window's start, or null live.</param>
		public static bool IsInEra(RadarSite site, DateTimeOffset? replayStartUtc)
		{
			if (site.RetiredOn is not { } retired)
			{
				return true;
			}
			return replayStartUtc is { } start && DateOnly.FromDateTime(start.UtcDateTime) <= retired;
		}
	}
}
