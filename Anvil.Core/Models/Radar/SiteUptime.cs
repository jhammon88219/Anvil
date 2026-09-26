using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>How bad a hole in a site's archive data is.</summary>
	public enum UptimeHoleKind
	{
		/// <summary>Two or more volumes missing, but under the offline threshold — amber, not counted as down.</summary>
		Gap,
		/// <summary>No volume for longer than <c>RadarSiteStatus.Staleness</c> — red, counted as downtime.</summary>
		Down,
	}

	/// <summary>
	/// One hole in a site's archive data. <see cref="StartUtc"/> is when the next volume was DUE (the last
	/// volume plus the site's normal cadence), <see cref="EndUtc"/> when data resumed — or "now" while
	/// <see cref="Ongoing"/>.
	/// </summary>
	public sealed record UptimeHole(UptimeHoleKind Kind, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool Ongoing)
	{
		public TimeSpan Duration => EndUtc - StartUtc;
	}

	/// <summary>
	/// One UTC day of a site's data uptime. <see cref="UpFraction"/> is null when the day's listing couldn't
	/// be fetched — unknown, which is not the same as down, and is left out of every percentage.
	/// </summary>
	public sealed record UptimeDay(DateOnly Day, double? UpFraction, int Volumes);

	/// <summary>
	/// A site's DATA uptime over a window, reconstructed from gaps in the public Level II archive — see
	/// <c>UptimeCalculator</c>. ⚠️ Not the NWS's "operational availability" (that comes from the ROC's own
	/// maintenance records): a radar can be scanning while its data never reaches the archive.
	/// </summary>
	/// <param name="UpFraction">Share of the KNOWN part of the window with data flowing; null if nothing is known.</param>
	/// <param name="Holes">Every gap and outage in the window, newest first.</param>
	public sealed record SiteUptimeReport(
		string SiteId,
		DateTimeOffset WindowStartUtc,
		DateTimeOffset WindowEndUtc,
		double? UpFraction,
		IReadOnlyList<UptimeDay> Days,
		IReadOnlyList<UptimeHole> Holes);
}
