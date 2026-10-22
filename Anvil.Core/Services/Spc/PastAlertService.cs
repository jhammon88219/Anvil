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
	/// COUNTIES (so PastCast draws watches the way NowCast does, county-filled, not SPC's parallelogram) —
	/// flood watches (FA/FL/FF) come back as their ZONES the same way.
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

		/// <summary><see cref="Lookback"/> for the watch request — a flood watch can be issued two days ahead.</summary>
		internal static readonly TimeSpan WatchLookback = TimeSpan.FromHours(48);

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
					end, cancellationToken).ConfigureAwait(false);
				// ⚠️ "-v2": the settled-window cache predates flood watches — the old name would serve a
				// TO/SV-only export forever. Longer lookback: a flood watch routinely runs 24–48 h.
				watchZip = await GetZipAsync($"watches-v2-{stem}.zip",
					$"{Endpoint}?accept=shapefile&sts={Iso(start - WatchLookback)}&ets={to}&limitps=1&phenomena=TO,SV,FA,FL,FF&significance=A,A,A,A,A&simple=1",
					end, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				return PastAlertFetch.Failed($"IEM warning archive unavailable ({ex.Message})");
			}

			// PERF PROBE (perf.ui "feedBy"): unzip + parse + build timed when on the UI thread. Grep RadarPerfCounters.
			RadarPerfCounters.NoteFeedRefresh("pastAlerts");
			var feedT = RadarPerfCounters.BeginFeed();
			List<IemShapefile.Record> warnRows, watchRows;
			try
			{
				warnRows = IemShapefile.Read(warnZip);
				watchRows = IemShapefile.Read(watchZip);
			}
			catch (Exception ex)
			{
				RadarPerfCounters.EndFeed(feedT, "pastAlerts");
				return PastAlertFetch.Failed($"IEM warning archive returned an unreadable file ({ex.Message})");
			}

			var (warnJson, warnings) = Build(warnRows, start, end, watches: false);
			var (watchJson, watches) = Build(watchRows, start, end, watches: true);
			RadarPerfCounters.EndFeed(feedT, "pastAlerts");
			await AtomicWriteAsync(Path.Combine(CacheDirectory, $"warnings-{stem}.geojson"), warnJson, cancellationToken).ConfigureAwait(false);
			await AtomicWriteAsync(Path.Combine(CacheDirectory, $"watches-{stem}.geojson"), watchJson, cancellationToken).ConfigureAwait(false);
			return new PastAlertFetch(true, warnings, watches,
				$"https://{CacheHostName}/warnings-{stem}.geojson", $"https://{CacheHostName}/watches-{stem}.geojson");
		}

		// ── The NowCast Warnings DAY LIST (IEM api/1 sbw_interval: every storm-based warning valid over an interval) ──
		// ⚠️ NOT watchwarn.py: that export selects by ISSUE time and has no place names; sbw_interval selects by OVERLAP and
		// carries "locations". Its geometry is the FIRST polygon only — fine for a fly-to.
		private const string DayEndpoint = "https://mesonet.agron.iastate.edu/api/1/vtec/sbw_interval.geojson";

		public async Task<IReadOnlyList<WarningTarget>?> FetchWarningDayAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
		{
			try
			{
				var json = await Http.GetStringAsync(
					$"{DayEndpoint}?begints={Iso(startUtc.ToUniversalTime())}&endts={Iso(endUtc.ToUniversalTime())}", cancellationToken).ConfigureAwait(false);
				return ParseWarningDay(json);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// <summary>sbw_interval GeoJSON → one <see cref="WarningTarget"/> per TO/SV/FF warning EVENT (advisories and the
		/// rest dropped). Null if the body isn't a FeatureCollection. Pure + internal for tests.</summary>
		internal static List<WarningTarget>? ParseWarningDay(string json)
		{
			if (JsonNode.Parse(json)?["features"] is not JsonArray features) { return null; }
			var byKey = new Dictionary<string, WarningTarget>();
			foreach (var f in features)
			{
				var p = f?["properties"];
				var phenom = Text(p?["phenomena"]);
				if (Text(p?["significance"]) != "W" || phenom is not ("TO" or "SV" or "FF")) { continue; }
				if (p?["eventid"] is not JsonValue idv || !idv.TryGetValue<int>(out var etn)) { continue; }
				var key = Vtec.KeyOf(Text(p["wfo"]), phenom, "W", etn);
				if (byKey.ContainsKey(key)) { continue; }
				if (!DateTimeOffset.TryParse(Text(p["utc_issue"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var issued)) { continue; }
				DateTimeOffset.TryParse(Text(p["utc_expire"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var ends);
				var geom = f!["geometry"];
				if (geom is null || !TryBox(geom["coordinates"], out var w, out var s, out var e, out var n)) { continue; }
				byKey[key] = new WarningTarget(key, phenom, 0, issued, FirstPlace(Text(p["locations"])),
					geom.ToJsonString(), w, s, e, n, key, ends);
			}
			return new List<WarningTarget>(byKey.Values);
		}

		// "Dorado [PR], Toa Alta [PR]" → "Dorado, PR" — CAP's areaDesc spelling, so both sources' rows read alike.
		internal static string FirstPlace(string locations)
		{
			var first = locations.Split(',')[0].Trim();
			var open = first.LastIndexOf('[');
			return open > 0 && first.EndsWith(']')
				? $"{first[..open].Trim()}, {first[(open + 1)..^1]}"
				: first;
		}

		private static bool TryBox(JsonNode? coords, out double w, out double s, out double e, out double n)
		{
			double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
			void Walk(JsonNode? c)
			{
				if (c is not JsonArray a || a.Count == 0) { return; }
				if (a[0] is JsonValue && a.Count >= 2)
				{
					var x = a[0]!.GetValue<double>();
					var y = a[1]!.GetValue<double>();
					x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
					return;
				}
				foreach (var child in a) { Walk(child); }
			}
			try { Walk(coords); } catch { x0 = double.MaxValue; }
			(w, s, e, n) = (x0, y0, x1, y1);
			return x0 <= x1 && y0 <= y1;
		}

		private static string Text(JsonNode? node) =>
			node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString() ?? string.Empty;

		// A settled window's export is final, so it is read from disk; a recent one is always re-fetched.
		private async Task<byte[]> GetZipAsync(string name, string url, DateTimeOffset end, CancellationToken ct)
		{
			var path = Path.Combine(CacheDirectory, name);
			if (DateTimeOffset.UtcNow - end > SettledAfter && File.Exists(path))
			{
				return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
			}
			var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
			// ⚠️ A bad query answers with an HTML page, not an HTTP error. A zip starts "PK".
			if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
			{
				throw new InvalidDataException("not a shapefile export");
			}
			await AtomicWriteAsync(path, async s => await s.WriteAsync(bytes, ct), ct).ConfigureAwait(false);
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
					if (sig != "A" || phenom is not ("TO" or "SV" or "FA" or "FL" or "FF")) { continue; }
					if (!TryTime(Get(f, "ISSUED"), out t0) || !TryTime(Get(f, "EXPIRED"), out t1)) { continue; }
					// TO/SV ETNs are national (SPC); a flood watch's ETN is per OFFICE, so the WFO joins its key.
					key = phenom is "TO" or "SV"
						? $"{phenom}.A.{Get(f, "ETN")}.{Get(f, "VTEC_YR")}"
						: $"{Get(f, "WFO")}.{phenom}.A.{Get(f, "ETN")}.{Get(f, "VTEC_YR")}";
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
