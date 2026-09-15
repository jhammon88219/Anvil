using System;

namespace Anvil.Services
{
	/// <summary>
	/// THE freshness rule for "is this radar site up": its newest volume is no older than <see cref="Staleness"/>.
	/// One rule for every piece of evidence — the 10-minute archive status pass
	/// (<c>Level2RadarService.GetLiveSiteIdsAsync</c>), a scan time the Radar Atlas fetched, and a loaded
	/// loop's newest frame — so no two parts of the app can grade the same site differently.
	/// </summary>
	/// <remarks>
	/// Why 30 min: the pass measures the ARCHIVE bucket, which lags real time by ~10 min. Stacked on a clear-air
	/// VCP's ~10-min volume cadence, a healthy quiet site's newest archive volume is routinely ~20-25 min old, so
	/// the threshold must clear that or clear-air sites false-flag as down (the KMQT/KIWA/KSGF/KINX false
	/// positives). 30 min clears the worst healthy case while a genuine outage keeps climbing past it.
	/// Evidence taken from the chunks bucket is FRESHER than the archive, so the same threshold applied to it can
	/// only err toward Online — it never flags a healthy site down.
	/// </remarks>
	public static class RadarSiteStatus
	{
		public static readonly TimeSpan Staleness = TimeSpan.FromMinutes(30);

		public static bool IsFresh(DateTimeOffset newestScanUtc, DateTimeOffset nowUtc) =>
			nowUtc - newestScanUtc <= Staleness;
	}
}
