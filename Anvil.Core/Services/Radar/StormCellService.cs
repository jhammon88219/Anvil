using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IStormCellService"/>: IEM's parsed WSR-88D storm-attribute table
	/// (<c>cgi-bin/request/gis/nexrad_storm_attrs.py</c>), one CSV per site per window.
	/// </summary>
	/// <remarks>
	/// ⚠️ WHY IEM AND NOT THE AWS LEVEL III BUCKET we already read NVW from: since 2023 that bucket carries
	/// only NST (tracks) and NMD (mesocyclones) — NTV (TVS), NHI (hail) and NSS (cell structure) stopped in
	/// 2022 (checked 2026-09-26 across TLX/FWS/LOT/DMX), and it is real-time only. IEM ingests the full
	/// NOAAPORT feed, joins all five products per cell, computes each cell's lat/lon, and keeps the archive
	/// (2013-05-20 KTLX answers with the Moore TVS), so ONE source serves NowCast and PastCast alike.
	/// Live it runs ~3 min behind the volume's end — see <c>StormCellThresholds.MaxFrameLag</c>.
	/// <para>⚠️ IEM's archive endpoint caps a request at 1000 (radars × days); one site over one window is
	/// nowhere near that.</para>
	/// </remarks>
	public sealed class StormCellService : CachingHttpService, IStormCellService
	{
		/// <summary>WebView virtual host the page files are served under (the view owns the mapping).</summary>
		public const string CacheHostName = "stormcells";

		private const string Endpoint = "https://mesonet.agron.iastate.edu/cgi-bin/request/gis/nexrad_storm_attrs.py";

		// A historical window ending this long ago is complete, so its cached CSV is final.
		private static readonly TimeSpan SettledAfter = TimeSpan.FromHours(1);

		public StormCellService() : base("StormCells", "Anvil/1.0 (severe-weather app)") { }

		public async Task<StormCellFetch> FetchAsync(string siteId, DateTimeOffset startUtc, DateTimeOffset endUtc, bool live,
			CancellationToken cancellationToken = default)
		{
			var site3 = Level3NvwProvider.ToThreeLetterSite(siteId);
			if (site3 is null) { return StormCellFetch.Failed("No radar site."); }

			// Reach back far enough that the window's FIRST scan still has a past track.
			var from = startUtc.ToUniversalTime() - StormCellThresholds.PastTrack;
			var to = endUtc.ToUniversalTime();
			var name = live
				? $"cells-{site3}-live"
				: $"cells-{site3}-{from:yyyyMMddHHmm}-{to:yyyyMMddHHmm}";
			var csvPath = Path.Combine(CacheDirectory, name + ".csv");

			string csv;
			var settled = !live && DateTimeOffset.UtcNow - to > SettledAfter;
			if (settled && File.Exists(csvPath))
			{
				csv = await File.ReadAllTextAsync(csvPath, cancellationToken);
			}
			else
			{
				try
				{
					var url = $"{Endpoint}?fmt=csv&radar={site3}&sts={Iso(from)}&ets={Iso(to)}";
					csv = await Http.GetStringAsync(url, cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					return StormCellFetch.Failed($"IEM storm attributes unavailable ({ex.Message})");
				}

				// ⚠️ An unknown site or a bad query comes back as an HTML error page, not an HTTP error.
				if (!csv.StartsWith("VALID", StringComparison.OrdinalIgnoreCase))
				{
					return StormCellFetch.Failed("IEM storm attributes unavailable for this site.");
				}
				await AtomicWriteAsync(csvPath, csv, cancellationToken);
			}

			var scans = StormCellTracks.ParseCsv(csv);
			var jsonName = name + ".json";
			await AtomicWriteAsync(Path.Combine(CacheDirectory, jsonName), StormCellTracks.ToPageJson(site3, scans), cancellationToken);
			return new StormCellFetch(true, scans, $"https://{CacheHostName}/{jsonName}");
		}

		private static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
	}
}
