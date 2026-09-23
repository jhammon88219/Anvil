using System;
using System.Text.Json.Serialization;

namespace Anvil.Models
{
	/// <summary>
	/// How much the user has used ONE radar site — the Radar Atlas's "Your use" strip. Written only by
	/// <c>SiteUsageStore</c> (fed by <c>SiteUsageTracker</c>); persisted as JSON, keyed by site ICAO.
	/// </summary>
	/// <remarks>
	/// ⚠️ A LOAD is a loop the user asked for, not a selection: a live pick counts once, a PastCast site pick
	/// counts only when its replay window actually LOADS. The Debug site sweep never counts.
	/// ⚠️ SITE HOURS run from a load until the selection moves off the site or the app closes, and PAUSE while
	/// the main window is minimized. The rule's words live in <c>RadarGlossary.SiteHours</c> — change both.
	/// </remarks>
	public sealed class SiteUsage
	{
		/// <summary>Live-loop loads (NowCast site picks).</summary>
		public int LiveLoads { get; set; }

		/// <summary>PastCast replay windows loaded at this site.</summary>
		public int ReplayLoads { get; set; }

		/// <summary>Total seconds this site has been the loaded radar (the tile's "site hours").</summary>
		public double SecondsLoaded { get; set; }

		/// <summary>The first load ever recorded for this site (UTC).</summary>
		public DateTimeOffset? FirstUsedUtc { get; set; }

		/// <summary>The latest load, or the last moment it was on the map, whichever is later (UTC).</summary>
		public DateTimeOffset? LastUsedUtc { get; set; }

		[JsonIgnore]
		public int TotalLoads => LiveLoads + ReplayLoads;
	}
}
