using System;
using System.Collections.Generic;
using System.Globalization;

namespace Anvil.Models
{
	/// <summary>
	/// A family of mesoscale discussions — who issues it, where IEM serves it, how it is drawn and named.
	/// ⚠️ THE ONE PLACE A KIND IS DEFINED. <see cref="DiscussionKinds.All"/> drives the fetch, the section's
	/// rows, the counts and the page colours (mirrored in <c>discussions.js KIND_COLORS</c> — change both), so
	/// another IEM-served discussion family is one entry here plus its colour there.
	/// </summary>
	/// <param name="Id">Stable id: the page's feature <c>kind</c>, the settings key, the cache-file stem.</param>
	/// <param name="Prefix">Short product name before the number ("MD 0725").</param>
	/// <param name="RowLabel">The section row's name.</param>
	/// <param name="ApiPath">IEM API path (<c>api/1/…</c>) taking <c>valid</c> + <c>hours</c>: everything ISSUED in
	/// [valid − hours, valid].</param>
	/// <param name="Fill">Area colour. DATA, never themed.</param>
	/// <param name="WebUrlFormat">The issuer's own page; {0} = year, {1} = number.</param>
	public sealed record DiscussionKind(
		string Id, string Prefix, string RowLabel, string ProductName, string Issuer,
		string ApiPath, string Fill, string Outline, string WebUrlFormat);

	public static class DiscussionKinds
	{
		/// <summary>SPC Mesoscale Discussion — severe/winter/fire concerns, often "watch likely". Colours are
		/// NWS's own renderer for SPC MDs (mapservices spc_mesoscale_discussion drawingInfo, 2026-09-27).</summary>
		public static readonly DiscussionKind Spc = new(
			"mcd", "MD", "SPC mesoscale", "Mesoscale Discussion", "Storm Prediction Center",
			"nws/spc_mcd.geojson", "#A900E6", "#8400A8",
			"https://www.spc.noaa.gov/products/md/{0}/md{1:0000}.html");

		/// <summary>WPC Mesoscale Precipitation Discussion — heavy rain / flash-flood concerns. WPC publishes no
		/// GIS renderer; teal keeps it apart from the MD purple AND the flash-flood-warning green.</summary>
		public static readonly DiscussionKind Wpc = new(
			"mpd", "MPD", "WPC precipitation", "Mesoscale Precipitation Discussion", "Weather Prediction Center",
			"nws/wpc_mpd.geojson", "#00C7A8", "#008F78",
			"https://www.wpc.ncep.noaa.gov/metwatch/metwatch_mpd_multi.php?md={1}&yr={0}");

		/// <summary>Every kind, in row order.</summary>
		public static readonly IReadOnlyList<DiscussionKind> All = new[] { Spc, Wpc };

		public static DiscussionKind? ById(string id)
		{
			foreach (var k in All) { if (k.Id == id) { return k; } }
			return null;
		}
	}

	/// <summary>
	/// One discussion as its index lists it (from IEM's GeoJSON properties; the polygon lives in the page
	/// file). The full text is <see cref="MesoDiscussionText"/>, fetched separately and cached forever.
	/// </summary>
	/// <param name="WatchProbability">SPC's "probability of watch issuance", percent; null when none was given
	/// (and always for WPC).</param>
	/// <param name="Concerning">The CONCERNING line ("SEVERE POTENTIAL...TORNADO WATCH LIKELY"), or empty.</param>
	public sealed record MesoDiscussion(
		DiscussionKind Kind, int Year, int Number, string ProductId,
		DateTimeOffset Issued, DateTimeOffset Expires, int? WatchProbability, string Concerning)
	{
		/// <summary>Stable id across both kinds: "mcd-2013-725".</summary>
		public string Key => $"{Kind.Id}-{Year}-{Number}";

		/// <summary>"MD 0725" — the name forecasters use.</summary>
		public string Label => $"{Kind.Prefix} {Number:0000}";

		public string WebUrl => string.Format(CultureInfo.InvariantCulture, Kind.WebUrlFormat, Year, Number);

		public bool IsInEffectAt(DateTimeOffset t) => Issued <= t && t < Expires;
	}

	/// <summary>
	/// A discussion's text, split into the sections every MD and MPD carries. Each field is the section's
	/// prose with its line wrapping undone (paragraph breaks kept); a missing section is "". <see cref="Raw"/>
	/// is the product verbatim, for anything the split doesn't name.
	/// </summary>
	public sealed record MesoDiscussionText(
		string AreasAffected, string Concerning, string Valid, string WatchProbability,
		string Summary, string Discussion, string Forecasters, string Attn, string Raw);
}
