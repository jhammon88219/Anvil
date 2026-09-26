using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="IPastAlertService"/>: IEM's VTEC archive export
	/// (<c>cgi-bin/request/gis/watchwarn.py?accept=shapefile</c>), two requests per window — the storm-based
	/// warning polygons with every follow-up version (<c>limit1</c> + <c>addsvs</c>), and the watch
	/// COUNTIES (so PastCast draws watches the way NowCast does, county-filled, not SPC's parallelogram).
	/// </summary>
	/// <remarks>
	/// ⚠️ THE QUERY SELECTS BY ISSUE TIME, NOT OVERLAP (checked: Moore's watch 191, issued 18:10Z, is absent
	/// from a 20:00–20:30Z query). So each request reaches back <see cref="Lookback"/> before the window and
	/// <see cref="Build"/> keeps what OVERLAPS it.
	/// ⚠️ A WARNING IS DRAWN PER POLYGON VERSION: a follow-up statement reshapes (and can re-tag) it, and the
	/// row carries that version's own POLY_BEG→POLY_END. EXP/CAN rows are the "it's over" statements, not
	/// areas in effect, and are dropped. A watch county runs ISSUED→EXPIRED, which a cancellation shortens
	/// per county — so a watch shrinks on the map as the real one did.
	/// </remarks>
	public sealed class PastAlertService : CachingHttpService, IPastAlertService
	{
		public const string CacheHostName = "pastalerts";

		private const string Endpoint = "https://mesonet.agron.iastate.edu/cgi-bin/request/gis/watchwarn.py";

		/// <summary>How far before the window an alert may have been ISSUED and still be in effect in it. A
		/// watch runs up to ~10 h; a flash-flood warning rarely past 6.</summary>
		internal static readonly TimeSpan Lookback = TimeSpan.FromHours(12);

		// A window that ended this long ago has every follow-up statement it will ever get.
		private static readonly TimeSpan SettledAfter = TimeSpan.FromHours(3);

		public PastAlertService() : base("PastAlerts", "Anvil/1.0 (severe-weather app)") { }

		public async Task<PastAlertFetch> FetchAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
		{
			var start = startUtc.ToUniversalTime();
			var end = endUtc.ToUniversalTime();
			var stem = $"{start:yyyyMMddHHmm}-{end:yyyyMMddHHmm}";
			var from = Iso(start - Lookback);
			var to = Iso(end);

			byte[] warnZip, watchZip;
			try
			{
				warnZip = await GetZipAsync($"warnings-{stem}.zip",
					$"{Endpoint}?accept=shapefile&sts={from}&ets={to}&limit1=1&addsvs=1&limitps=1&phenomena=TO,SV,FF&significance=W,W,W",
					end, cancellationToken);
				watchZip = await GetZipAsync($"watches-{stem}.zip",
					$"{Endpoint}?accept=shapefile&sts={from}&ets={to}&limitps=1&phenomena=TO,SV&significance=A,A&simple=1",
					end, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				return PastAlertFetch.Failed($"IEM warning archive unavailable ({ex.Message})");
			}

			List<IemShapefile.Record> warnRows, watchRows;
			try
			{
				warnRows = IemShapefile.Read(warnZip);
				watchRows = IemShapefile.Read(watchZip);
			}
			catch (Exception ex)
			{
				return PastAlertFetch.Failed($"IEM warning archive returned an unreadable file ({ex.Message})");
			}

			var (warnJson, warnings) = Build(warnRows, start, end, watches: false);
			var (watchJson, watches) = Build(watchRows, start, end, watches: true);
			await AtomicWriteAsync(Path.Combine(CacheDirectory, $"warnings-{stem}.geojson"), warnJson, cancellationToken);
			await AtomicWriteAsync(Path.Combine(CacheDirectory, $"watches-{stem}.geojson"), watchJson, cancellationToken);
			return new PastAlertFetch(true, warnings, watches,
				$"https://{CacheHostName}/warnings-{stem}.geojson", $"https://{CacheHostName}/watches-{stem}.geojson");
		}

		// A settled window's export is final, so it is read from disk; a recent one is always re-fetched.
		private async Task<byte[]> GetZipAsync(string name, string url, DateTimeOffset end, CancellationToken ct)
		{
			var path = Path.Combine(CacheDirectory, name);
			if (DateTimeOffset.UtcNow - end > SettledAfter && File.Exists(path))
			{
				return await File.ReadAllBytesAsync(path, ct);
			}
			var bytes = await Http.GetByteArrayAsync(url, ct);
			// ⚠️ A bad query answers with an HTML page, not an HTTP error. A zip starts "PK".
			if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
			{
				throw new InvalidDataException("not a shapefile export");
			}
			await AtomicWriteAsync(path, async s => await s.WriteAsync(bytes, ct), ct);
			return bytes;
		}

		/// <summary>
		/// Rows → the page's FeatureCollection (render schema shared with the NowCast overlays: <c>phenom</c>,
		/// <c>threat_tier</c>, plus <c>t0</c>/<c>t1</c>) and the alert list the counts are taken from. Keeps
		/// only rows whose span overlaps [start, end]. Pure + internal for tests.
		/// </summary>
		internal static (string Json, List<PastAlert> Alerts) Build(IReadOnlyList<IemShapefile.Record> rows,
			DateTimeOffset start, DateTimeOffset end, bool watches)
		{
			var features = new JsonArray();
			var alerts = new List<PastAlert>();
			foreach (var row in rows)
			{
				var f = row.Fields;
				var phenom = Get(f, "PHENOM");
				var sig = Get(f, "SIG");
				if (row.Polygons.Count == 0) { continue; }

				DateTimeOffset t0, t1;
				string key;
				int tier = 0;
				string threat = string.Empty;
				if (watches)
				{
					if (sig != "A" || phenom is not ("TO" or "SV")) { continue; }
					if (!TryTime(Get(f, "ISSUED"), out t0) || !TryTime(Get(f, "EXPIRED"), out t1)) { continue; }
					key = $"{phenom}.A.{Get(f, "ETN")}.{Get(f, "VTEC_YR")}";
				}
				else
				{
					if (sig != "W" || phenom is not ("TO" or "SV" or "FF")) { continue; }
					if (Get(f, "STATUS") is "EXP" or "CAN") { continue; }
					if (!TryTime(Get(f, "POLY_BEG"), out t0) || !TryTime(Get(f, "POLY_END"), out t1)) { continue; }
					key = $"{Get(f, "WFO")}.{phenom}.W.{Get(f, "ETN")}.{Get(f, "VTEC_YR")}";
					threat = Get(f, "DAMAGTAG").ToLowerInvariant();
					tier = WarningService.ThreatTier(threat);
					// Pre-tag years (and the phrasing-only emergencies) carry just the EMERGENC flag.
					if (Get(f, "EMERGENC") is "T" or "t" or "Y" or "y") { tier = 2; }
				}
				if (t1 <= t0 || t1 <= start || t0 >= end) { continue; }

				alerts.Add(new PastAlert(key, phenom, tier, t0, t1));
				features.Add(new JsonObject
				{
					["type"] = "Feature",
					["geometry"] = Geometry(row.Polygons),
					["properties"] = new JsonObject
					{
						["phenom"] = phenom,
						["key"] = key,
						["threat"] = threat,
						["threat_tier"] = tier,
						["t0"] = t0.ToUnixTimeMilliseconds(),
						["t1"] = t1.ToUnixTimeMilliseconds(),
					},
				});
			}
			var json = new JsonObject { ["type"] = "FeatureCollection", ["features"] = features }.ToJsonString();
			return (json, alerts);
		}

		private static JsonNode Geometry(List<List<List<double[]>>> polygons)
		{
			var coords = new JsonArray();
			foreach (var polygon in polygons)
			{
				var rings = new JsonArray();
				foreach (var ring in polygon)
				{
					var pts = new JsonArray();
					foreach (var p in ring) { pts.Add(new JsonArray(p[0], p[1])); }
					rings.Add(pts);
				}
				coords.Add(rings);
			}
			return new JsonObject { ["type"] = "MultiPolygon", ["coordinates"] = coords };
		}

		private static string Get(IReadOnlyDictionary<string, string> f, string name) =>
			f.TryGetValue(name, out var v) ? v : string.Empty;

		private static bool TryTime(string s, out DateTimeOffset t) =>
			DateTimeOffset.TryParseExact(s, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);

		private static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);
	}
}
