using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Default <see cref="ISpcWatchService"/>. Downloads the active Tornado / Severe Thunderstorm
	/// Watch polygons (plus NWS Flood Watches, zone-filled) and caches them on disk as GeoJSON; no WebView2 here — MainWindow maps the
	/// cache folder to the "spcwatches" virtual host so the page can fetch
	/// https://spcwatches/watches.geojson.
	///
	/// Source: the NWS WWA event-driven map service (`watch_warn_adv` MapServer, the
	/// `WatchesWarnings` layer 1). It serves the **official county-aggregated** watch geometry — the
	/// polygon is the union of the watch's counties, so it FOLLOWS COUNTY LINES (matching RadarScope)
	/// rather than the older SPC parallelogram box. We query only watches (`sig='A'`) of the two
	/// convective phenomena (`phenom` TO = tornado, SV = severe thunderstorm), as GeoJSON. The
	/// service is current-events-only, so everything returned is active — no client-side expiry
	/// filtering needed. Each feature carries `prod_type` (label), `phenom` (TO/SV — the page colors
	/// by this), and `expiration`.
	/// </summary>
	public sealed class SpcWatchService : CachingHttpService, ISpcWatchService
	{
		/// <summary>WebView virtual host the cached file is served under (shared contract; the
		/// view owns the actual mapping).</summary>
		public const string CacheHostName = "spcwatches";
		private const string CacheFileName = "watches.geojson";

		// WWA MapServer, layer 1 = "WatchesWarnings" (county-aggregated polygons), filtered to WATCHES (sig
		// 'A') of phenom TO/SV + the flood family: FA (Flood Watch, one feature PER ZONE), FL (river Flood
		// Watch) and FF (the pre-2023 Flash Flood Watch — gone from the live feed, kept so it never needs a
		// re-add), WGS84 (outSR=4326). `wfo` + `event` (the ETN) are what count flood watches per WATCH
		// rather than per zone. Split into a base + format so we can also ask for JUST the count to
		// corroborate a suspicious empty GeoJSON — see the RefreshAsync guard.
		private const string QueryBase =
			"https://mapservices.weather.noaa.gov/eventdriven/rest/services/WWA/watch_warn_adv/MapServer/1/query" +
			"?where=sig%3D%27A%27%20AND%20phenom%20IN%20%28%27TO%27%2C%27SV%27%2C%27FA%27%2C%27FL%27%2C%27FF%27%29" +
			"&outFields=prod_type%2Cphenom%2Cexpiration%2Cwfo%2Cevent&returnGeometry=true&outSR=4326";
		private const string GeoJsonUrl = QueryBase + "&f=geojson";
		private const string CountUrl = QueryBase + "&returnCountOnly=true&f=json";

		public SpcWatchService() : base("SpcWatches")
		{
			// ⚠️ MUST send an Accept header — this NOAA endpoint (Akamai-fronted) caches keyed on Accept
			// (Vary: Accept). HttpClient sends none by default, landing in a rarely-populated cache
			// partition that can serve a stale EMPTY FeatureCollection for a whole TTL after an origin
			// republish, while browsers (Accept: */*) get real data. See WarningService for the full story.
			Http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
		}

		public string WatchesUrl => $"https://{CacheHostName}/{CacheFileName}";

		public async Task<SpcWatchFetchResult> RefreshAsync(CancellationToken cancellationToken = default)
		{
			var cacheFile = Path.Combine(CacheDirectory, CacheFileName);
			var cacheExists = File.Exists(cacheFile);

			try
			{
				var json = await Http.GetStringAsync(GeoJsonUrl, cancellationToken);

				// Only cache a real FeatureCollection. An ArcGIS error object lacks a features array; in
				// that case keep the last-known-good cache instead of blanking it.
				if (!TryGetFeatureCounts(json, out var features, out var count, out var tornado, out var severe, out var flood))
				{
					return Failed(cacheExists, "Response was not a GeoJSON FeatureCollection.");
				}

				// ⚠️ An EMPTY GeoJSON is SUSPECT, not trusted — this event-driven origin intermittently
				// emits a spurious empty set while the count endpoint still reports active features.
				// Corroborate before caching an empty (which would blank the map): only accept it if the
				// lighter count endpoint AGREES the set is really zero. See WarningService for the story.
				if (features == 0 && await RemoteCountAsync(cancellationToken) is > 0)
				{
					return Failed(cacheExists, "Empty GeoJSON contradicted by a non-zero count — kept last-known-good.");
				}

				// Atomic write (temp then move) so a partial/failed write never blanks the last-known-good cache.
				await AtomicWriteAsync(cacheFile, json, cancellationToken);

				return new SpcWatchFetchResult(SpcWatchFetchStatus.Updated, count, tornado, severe, flood);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				// Never throw to the caller: keep last-known-good on disk and report it.
				return Failed(cacheExists, ex.Message);
			}
		}

		// Asks the server for JUST the matching feature count (returnCountOnly, Esri JSON — a different,
		// lighter code path than the GeoJSON export). Used to sanity-check a suspiciously-empty GeoJSON.
		// Returns the count, or -1 if the check itself failed (so "unknown" doesn't block accepting empty).
		private async Task<int> RemoteCountAsync(CancellationToken cancellationToken)
		{
			try
			{
				var json = await Http.GetStringAsync(CountUrl, cancellationToken);
				if (JsonNode.Parse(json)?["count"] is JsonValue v && v.TryGetValue<int>(out var n))
				{
					return n;
				}
			}
			catch
			{
				// Count check failed — fall through to -1 ("unknown"); the caller won't block on it.
			}
			return -1;
		}

		private static SpcWatchFetchResult Failed(bool cacheExists, string message) =>
			new(cacheExists ? SpcWatchFetchStatus.FailedCacheKept : SpcWatchFetchStatus.FailedNoCache,
				Message: message);

		// Confirms the body is a GeoJSON FeatureCollection and returns its raw feature count, plus the
		// WATCH counts by `phenom` for the per-type NowCast rows and the card's total. A missing "features"
		// array (e.g. an ArcGIS {"error":...} object) returns false.
		// ⚠️ A FLOOD WATCH IS ONE FEATURE PER ZONE (one watch today = dozens of features), so flood counts
		// DISTINCT wfo+phenom+event; TO/SV stay one per feature as before. The total is the rows' sum plus any
		// unexpected phenomenon per feature, so a stray type still shows as a total the rows don't add to.
		internal static bool TryGetFeatureCounts(string geoJson, out int features, out int count, out int tornado, out int severe, out int flood)
		{
			features = 0;
			count = 0;
			tornado = 0;
			severe = 0;
			flood = 0;
			try
			{
				if (JsonNode.Parse(geoJson)?["features"] is not JsonArray list)
				{
					return false;
				}

				features = list.Count;
				var other = 0;
				var floods = new System.Collections.Generic.HashSet<string>();
				foreach (var feature in list)
				{
					// ⚠️ TryGetValue, not GetValue: a feature carrying a non-string phenom would THROW, and
					// the catch below turns any throw into "not a FeatureCollection" — i.e. one odd
					// property would discard a perfectly good fetch. An unreadable phenom just goes
					// uncounted.
					var props = feature?["properties"];
					if (props?["phenom"] is not JsonValue value ||
						!value.TryGetValue<string>(out var phenom))
					{
						continue;
					}

					if (phenom == "TO") { tornado++; }
					else if (phenom == "SV") { severe++; }
					else if (phenom is "FA" or "FL" or "FF") { floods.Add($"{Str(props, "wfo")}.{phenom}.{Str(props, "event")}"); }
					else { other++; }
				}
				flood = floods.Count;
				count = tornado + severe + flood + other;
				return true;
			}
			catch
			{
				// Malformed JSON — treat as a failed fetch (keep last-known-good).
			}
			return false;
		}

		private static string Str(JsonNode? props, string name) =>
			props?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
	}
}
