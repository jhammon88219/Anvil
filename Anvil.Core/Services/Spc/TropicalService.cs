using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="ITropicalService"/>. Both sources end in ONE page schema — a FeatureCollection of zones,
	/// each with <c>pid</c> ("HU.A"), <c>prio</c> + <c>fill</c> (the NWS's, from <see cref="TropicalProducts"/>),
	/// <c>name</c>, <c>storm</c> (words), <c>wfo</c>, and (past only) <c>t0</c>/<c>t1</c> — so tropical.js carries
	/// no colour table of its own.
	/// </summary>
	/// <remarks>
	/// ⚠️ LIVE comes from the WWA map service (the watches' source), NOT CAP: tropical alerts are ZONE products, and
	/// CAP's active feed carries no geometry for them (WarningService skips polygon-less alerts). Measured
	/// 2026-10-07: 76 zones for TS Isaias (HU.A 40, SS.A 21, TR.A 15), every one with a polygon.
	/// ⚠️ PAST comes from IEM's watchwarn export, which selects by ISSUE time — a tropical product is issued days
	/// ahead and runs for days, so the request reaches back <see cref="Lookback"/> and keeps what OVERLAPS.
	/// ⚠️ STORM NAMES: a tropical VTEC ETN is 1000 + the Atlantic storm number (1009 = AL09; checked against NHC's
	/// list for Isaias 2026 and Helene 2024). Other basins' ETN ranges are NOT verified, so they go unnamed. Storm
	/// surge zones are NHC's own and carry NO ETN live — they take the storm when exactly one is in the file.
	/// </remarks>
	public sealed class TropicalService : CachingHttpService, ITropicalService
	{
		public const string CacheHostName = "tropical";
		private const string LiveFileName = "tropical-live.geojson";
		private const string StormListFileName = "storm_list.txt";

		// WWA MapServer layer 1, every tropical phenomenon, watches AND warnings. Zone outlines are coastal and
		// detailed (~2.2 MB raw): maxAllowableOffset ≈ 500 m + 4 decimals trims that without visible loss at the
		// zooms tropical zones are read at.
		private const string LiveQueryBase =
			"https://mapservices.weather.noaa.gov/eventdriven/rest/services/WWA/watch_warn_adv/MapServer/1/query" +
			"?where=phenom%20IN%20%28%27HU%27%2C%27TR%27%2C%27SS%27%2C%27EW%27%29" +
			"&outFields=prod_type%2Cphenom%2Csig%2Cwfo%2Cevent%2Cissuance%2Cexpiration&returnGeometry=true&outSR=4326";
		private const string LiveUrl = LiveQueryBase + "&maxAllowableOffset=0.005&geometryPrecision=4&f=geojson";
		private const string LiveCountUrl = LiveQueryBase + "&returnCountOnly=true&f=json";

		private const string IemEndpoint = "https://mesonet.agron.iastate.edu/cgi-bin/request/gis/watchwarn.py";
		private const string StormListUrl = "https://ftp.nhc.noaa.gov/atcf/index/storm_list.txt";

		/// <summary>How far before a window a tropical product may have been ISSUED and still be in effect in it.</summary>
		internal static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

		private static readonly TimeSpan SettledAfter = TimeSpan.FromHours(3);
		private static readonly TimeSpan StormListMaxAge = TimeSpan.FromHours(6);

		public TropicalService() : base("Tropical", "Anvil/1.0 (severe-weather app)")
		{
			// ⚠️ The WWA endpoint caches keyed on Accept (Vary: Accept) — see SpcWatchService.
			Http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
		}

		public async Task<TropicalFetch> FetchLiveAsync(CancellationToken cancellationToken = default)
		{
			string json;
			try { json = await Http.GetStringAsync(LiveUrl, cancellationToken); }
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception ex) { return TropicalFetch.Failed($"NWS tropical alerts unavailable ({ex.Message})"); }

			var storms = await GetStormNamesAsync(cancellationToken);
			if (!TryRewriteLive(json, storms, DateTimeOffset.UtcNow.Year, out var page, out var zones))
			{
				return TropicalFetch.Failed("NWS tropical alerts: the response was not a feature collection.");
			}
			// ⚠️ An EMPTY set is suspect on this origin (SpcWatchService) — keep the last file unless the count agrees.
			if (zones.Count == 0 && await RemoteCountAsync(cancellationToken) is > 0)
			{
				return TropicalFetch.Failed("NWS tropical alerts came back empty while the count says otherwise — kept the last ones.");
			}
			await AtomicWriteAsync(Path.Combine(CacheDirectory, LiveFileName), page, cancellationToken);
			return new TropicalFetch(true, zones, NamesUsed(zones, storms), $"https://{CacheHostName}/{LiveFileName}");
		}

		public async Task<TropicalFetch> FetchPastAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
		{
			var start = startUtc.ToUniversalTime();
			var end = endUtc.ToUniversalTime();
			var stem = $"{start:yyyyMMddHHmm}-{end:yyyyMMddHHmm}";
			// IEM pairs phenomena[i] with significance[i].
			var url = $"{IemEndpoint}?accept=shapefile&sts={Iso(start - Lookback)}&ets={Iso(end)}&limitps=1&simple=1" +
				"&phenomena=EW,SS,HU,TR,SS,HU,TR&significance=W,W,W,W,A,A,A";

			List<IemShapefile.Record> rows;
			try
			{
				var zip = await GetZipAsync($"tropical-{stem}.zip", url, end, cancellationToken);
				rows = IemShapefile.Read(zip);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception ex) { return TropicalFetch.Failed($"IEM tropical archive unavailable ({ex.Message})"); }

			var storms = await GetStormNamesAsync(cancellationToken);
			var (page, zones) = BuildPast(rows, start, end, storms);
			var name = $"tropical-{stem}.geojson";
			await AtomicWriteAsync(Path.Combine(CacheDirectory, name), page, cancellationToken);
			return new TropicalFetch(true, zones, NamesUsed(zones, storms), $"https://{CacheHostName}/{name}");
		}

		// ── Live: WWA GeoJSON → the page schema ──

		internal static bool TryRewriteLive(string json, IReadOnlyDictionary<string, string> storms, int year,
			out string page, out List<TropicalZone> zones)
		{
			page = string.Empty;
			zones = new List<TropicalZone>();
			JsonNode? root;
			try { root = JsonNode.Parse(json); }
			catch { return false; }
			if (root?["features"] is not JsonArray features) { return false; }

			var picked = new List<(TropicalProduct Product, string Storm, string Wfo, JsonNode? Geometry)>();
			foreach (var f in features)
			{
				var props = f?["properties"];
				var product = TropicalProducts.Find(Str(props?["phenom"]), Str(props?["sig"]));
				if (product is null || f?["geometry"] is null) { continue; }
				picked.Add((product, StormKey(Str(props?["event"]), year), Str(props?["wfo"]), f["geometry"]!.DeepClone()));
			}
			// Surge zones have no ETN live: with exactly one storm in the file, they are its.
			var only = picked.Select(p => p.Storm).Where(s => s.Length > 0).Distinct().ToList();
			var fallback = only.Count == 1 ? only[0] : string.Empty;

			var outFeatures = new JsonArray();
			foreach (var (product, storm0, wfo, geometry) in picked)
			{
				var storm = storm0.Length > 0 ? storm0 : fallback;
				zones.Add(new TropicalZone(product.Id, storm, null, null));
				outFeatures.Add(Feature(product, storm, storms, wfo, geometry, null, null));
			}
			page = new JsonObject { ["type"] = "FeatureCollection", ["features"] = outFeatures }.ToJsonString();
			return true;
		}

		// ── Past: IEM rows → the page schema (keeps what OVERLAPS the window) ──

		internal static (string Page, List<TropicalZone> Zones) BuildPast(IReadOnlyList<IemShapefile.Record> rows,
			DateTimeOffset start, DateTimeOffset end, IReadOnlyDictionary<string, string> storms)
		{
			var picked = new List<(TropicalProduct Product, string Storm, string Wfo, IemShapefile.Record Row, DateTimeOffset T0, DateTimeOffset T1)>();
			foreach (var row in rows)
			{
				var f = row.Fields;
				var product = TropicalProducts.Find(Get(f, "PHENOM"), Get(f, "SIG"));
				if (product is null || row.Polygons.Count == 0) { continue; }
				if (!TryTime(Get(f, "ISSUED"), out var t0) || !TryTime(Get(f, "EXPIRED"), out var t1)) { continue; }
				if (t1 <= t0 || t1 <= start || t0 >= end) { continue; }
				int.TryParse(Get(f, "VTEC_YR"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year);
				picked.Add((product, StormKey(Get(f, "ETN"), year), Get(f, "WFO"), row, t0, t1));
			}
			// An Extreme Wind Warning's ETN is the OFFICE's own count, not 1000 + the storm (Helene 2024: 25 EW zones,
			// unnamed): with exactly one storm in the window, it is that storm's — the live surge rule.
			var only = picked.Select(p => p.Storm).Where(s => s.Length > 0).Distinct().ToList();
			var fallback = only.Count == 1 ? only[0] : string.Empty;

			var features = new JsonArray();
			var zones = new List<TropicalZone>();
			foreach (var (product, storm0, wfo, row, t0, t1) in picked)
			{
				var storm = storm0.Length > 0 ? storm0 : fallback;
				zones.Add(new TropicalZone(product.Id, storm, t0, t1));
				features.Add(Feature(product, storm, storms, wfo, Geometry(row.Polygons), t0, t1));
			}
			return (new JsonObject { ["type"] = "FeatureCollection", ["features"] = features }.ToJsonString(), zones);
		}

		private static JsonObject Feature(TropicalProduct product, string storm, IReadOnlyDictionary<string, string> storms,
			string wfo, JsonNode? geometry, DateTimeOffset? t0, DateTimeOffset? t1)
		{
			var props = new JsonObject
			{
				["pid"] = product.Id,
				["prio"] = product.Priority,
				["fill"] = product.Fill,
				["name"] = product.Name,
				["storm"] = storms.TryGetValue(storm, out var words) ? words : string.Empty,
				["wfo"] = wfo,
			};
			if (t0 is { } a) { props["t0"] = a.ToUnixTimeMilliseconds(); }
			if (t1 is { } b) { props["t1"] = b.ToUnixTimeMilliseconds(); }
			return new JsonObject { ["type"] = "Feature", ["geometry"] = geometry, ["properties"] = props };
		}

		/// <summary>"1009" + 2026 → "AL092026" (Atlantic: ETN = 1000 + the storm number). Anything else → "".</summary>
		internal static string StormKey(string etn, int year) =>
			int.TryParse(etn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is > 1000 and < 1100 && year > 0
				? $"AL{n - 1000:00}{year}"
				: string.Empty;

		// ── Storm names: NHC's ATCF storm list (every storm since the 1850s, live ones included) ──

		private async Task<IReadOnlyDictionary<string, string>> GetStormNamesAsync(CancellationToken ct)
		{
			var path = Path.Combine(CacheDirectory, StormListFileName);
			var fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < StormListMaxAge;
			if (!fresh)
			{
				try
				{
					var text = await Http.GetStringAsync(StormListUrl, ct);
					if (text.Contains(',')) { await AtomicWriteAsync(path, text, ct); }
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
				catch { /* keep the old list (or none): an unnamed storm is still drawn */ }
			}
			try { return File.Exists(path) ? ParseStormList(await File.ReadAllTextAsync(path, ct)) : new Dictionary<string, string>(); }
			catch { return new Dictionary<string, string>(); }
		}

		/// <summary>ATCF storm_list.txt → "AL092026" → "Tropical Storm Isaias". Column 0 = name, 9 = the storm's
		/// (highest so far) class, the last = the storm id.</summary>
		internal static Dictionary<string, string> ParseStormList(string text)
		{
			var map = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var line in text.Split('\n'))
			{
				var cols = line.Split(',').Select(c => c.Trim()).ToArray();
				if (cols.Length < 11) { continue; }
				var id = cols[^1];
				var name = cols[0];
				if (id.Length != 8 || name.Length == 0 || name is "INVEST" or "GENESIS") { continue; }
				var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
				var prefix = cols[9] switch
				{
					"HU" => "Hurricane ",
					"TS" => "Tropical Storm ",
					"TD" => "Tropical Depression ",
					"SS" => "Subtropical Storm ",
					"SD" => "Subtropical Depression ",
					_ => string.Empty,
				};
				map[id] = prefix + title;
			}
			return map;
		}

		private static IReadOnlyDictionary<string, string> NamesUsed(IEnumerable<TropicalZone> zones, IReadOnlyDictionary<string, string> storms) =>
			zones.Select(z => z.StormKey).Where(k => k.Length > 0).Distinct()
				.ToDictionary(k => k, k => storms.TryGetValue(k, out var w) ? w : k);

		// ── Plumbing ──

		private async Task<int> RemoteCountAsync(CancellationToken ct)
		{
			try
			{
				var json = await Http.GetStringAsync(LiveCountUrl, ct);
				if (JsonNode.Parse(json)?["count"] is JsonValue v && v.TryGetValue<int>(out var n)) { return n; }
			}
			catch { /* unknown — don't block */ }
			return -1;
		}

		// A settled window's export is final, so it is read from disk; a recent one is always re-fetched.
		private async Task<byte[]> GetZipAsync(string name, string url, DateTimeOffset end, CancellationToken ct)
		{
			var path = Path.Combine(CacheDirectory, name);
			if (DateTimeOffset.UtcNow - end > SettledAfter && File.Exists(path)) { return await File.ReadAllBytesAsync(path, ct); }
			var bytes = await Http.GetByteArrayAsync(url, ct);
			// ⚠️ A bad query answers with an HTML page, not an HTTP error. A zip starts "PK".
			if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K') { throw new InvalidDataException("not a shapefile export"); }
			await AtomicWriteAsync(path, async s => await s.WriteAsync(bytes, ct), ct);
			return bytes;
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

		private static string Str(JsonNode? node) =>
			node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString() ?? string.Empty;

		private static string Get(IReadOnlyDictionary<string, string> f, string name) => f.TryGetValue(name, out var v) ? v : string.Empty;

		private static bool TryTime(string s, out DateTimeOffset t) =>
			DateTimeOffset.TryParseExact(s, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);

		private static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);
	}
}
