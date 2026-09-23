using System;
using System.Text.Json.Serialization;

namespace Anvil.Models
{
	/// <summary>
	/// How much the user has used ONE radar site — the Radar Atlas's "Your use" strip. Written only by
	/// <c>SiteUsageStore</c> (fed by <c>SiteUsageTracker</c>); persisted as JSON, keyed by site ICAO.
	/// Loads AND load time are split by mode: NowCast (live) and PastCast (replay).
	/// </summary>
	/// <remarks>
	/// ⚠️ A LOAD is a loop the user asked for, not a selection: a live pick counts once, a PastCast site pick
	/// counts only when its replay window actually LOADS. The Debug site sweep never counts.
	/// ⚠️ SITE LOAD TIME runs from a load until the selection moves off the site or the app closes, and PAUSES
	/// while the main window is minimized; it lands in the bucket of the mode that load was in. The rule's
	/// words live in <c>RadarGlossary.SiteLoadTime</c> — change both.
	/// </remarks>
	public sealed class SiteUsage
	{
		/// <summary>NowCast (live-loop) loads.</summary>
		public int LiveLoads { get; set; }

		/// <summary>PastCast replay windows loaded at this site.</summary>
		public int ReplayLoads { get; set; }

		/// <summary>Seconds this site has been the loaded radar in NowCast.</summary>
		public double LiveSeconds { get; set; }

		/// <summary>Seconds this site has been the loaded radar in PastCast.</summary>
		public double ReplaySeconds { get; set; }

		/// <summary>
		/// LEGACY — the single, unsplit total the first build wrote. Read-only in practice:
		/// <c>SiteUsageStore</c> folds it into <see cref="LiveSeconds"/> on load (every load that build could
		/// time before the split was live in the files that exist) and it is never written again.
		/// </summary>
		[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
		public double? SecondsLoaded { get; set; }

		/// <summary>The first load ever recorded for this site (UTC).</summary>
		public DateTimeOffset? FirstUsedUtc { get; set; }

		/// <summary>The latest load, or the last moment it was on the map, whichever is later (UTC).</summary>
		public DateTimeOffset? LastUsedUtc { get; set; }

		[JsonIgnore]
		public int TotalLoads => LiveLoads + ReplayLoads;

		[JsonIgnore]
		public double TotalSeconds => LiveSeconds + ReplaySeconds;
	}
}
