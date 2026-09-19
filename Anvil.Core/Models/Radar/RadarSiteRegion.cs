using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// The coarse geography bucket a radar site falls in — the Atlas's top-level "where" filter, one step
	/// above the state. Six buckets, because a 200-row list sorted A–Z buries the OCONUS sites among the
	/// CONUS ones and there is no other way to say "show me Alaska".
	/// </summary>
	/// <remarks>
	/// Derived from <see cref="RadarSite.State"/> alone (see <see cref="RadarSiteRegions"/>) — there is no
	/// second source. A site with no state has no region either and is simply absent from both filters.
	/// </remarks>
	public enum RadarSiteRegion
	{
		/// <summary>The lower 48 plus DC.</summary>
		Conus,
		Alaska,
		Hawaii,
		/// <summary>Puerto Rico and the US Virgin Islands.</summary>
		Caribbean,
		/// <summary>Guam and the overseas Pacific military sites (Okinawa, Korea).</summary>
		Pacific,
		/// <summary>Lajes Field in the Azores — the one site on the far side of the Atlantic.</summary>
		Atlantic,
	}

	/// <summary>State code → <see cref="RadarSiteRegion"/>, and the display names both the filter and any
	/// future grouping read. The ONE mapping: nothing else decides what region a site is in.</summary>
	public static class RadarSiteRegions
	{
		// Everything not named here is CONUS — the 48 states plus DC are the default, so adding a state
		// never needs a code change, only a genuinely new OCONUS territory does.
		private static readonly Dictionary<string, RadarSiteRegion> ByState = new()
		{
			["AK"] = RadarSiteRegion.Alaska,
			["HI"] = RadarSiteRegion.Hawaii,
			["PR"] = RadarSiteRegion.Caribbean,
			["VI"] = RadarSiteRegion.Caribbean,
			["GU"] = RadarSiteRegion.Pacific,
			["MP"] = RadarSiteRegion.Pacific,
			["AS"] = RadarSiteRegion.Pacific,
			["JP"] = RadarSiteRegion.Pacific,
			["KR"] = RadarSiteRegion.Pacific,
			["PT"] = RadarSiteRegion.Atlantic,
		};

		/// <summary>The region for a state code, or null when the site has no state at all.</summary>
		public static RadarSiteRegion? For(string? state)
		{
			if (string.IsNullOrWhiteSpace(state))
			{
				return null;
			}
			return ByState.TryGetValue(state.Trim().ToUpperInvariant(), out var region)
				? region
				: RadarSiteRegion.Conus;
		}

		/// <summary>How a region is written in the filter. CONUS is an initialism and stays upper-case.</summary>
		public static string DisplayName(RadarSiteRegion region) => region switch
		{
			RadarSiteRegion.Conus => "CONUS",
			RadarSiteRegion.Alaska => "Alaska",
			RadarSiteRegion.Hawaii => "Hawaii",
			RadarSiteRegion.Caribbean => "Caribbean",
			RadarSiteRegion.Pacific => "Pacific",
			_ => "Atlantic",
		};
	}
}
