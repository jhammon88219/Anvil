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
	/// Default <see cref="IMesoDiscussionService"/>: IEM's <c>api/1/nws/spc_mcd</c> / <c>wpc_mpd</c> GeoJSON
	/// (index + polygon, live AND archive) and <c>api/1/nwstext/{product_id}</c> (the text).
	/// </summary>
	/// <remarks>
	/// ⚠️ THE INDEX SELECTS BY ISSUE TIME (<c>valid</c> + <c>hours</c> = issued in [valid − hours, valid];
	/// checked on 2024-05-06), so each request reaches back <see cref="Lookback"/> before the window and
	/// <see cref="Build"/> keeps what OVERLAPS it — the same rule as the PastCast alerts.
	/// ⚠️ LIVE IS A WINDOW TOO, not "what is active": MDs are hours apart on a quiet day (one in the 36 h
	/// before this was written), so NowCast lists the last day's and draws only the ones in effect.
	/// </remarks>
	public sealed class MesoDiscussionService : CachingHttpService, IMesoDiscussionService
	{
		public const string CacheHostName = "discussions";

		private const string ApiRoot = "https://mesonet.agron.iastate.edu/api/1/";

		/// <summary>How long before the window a discussion may have been ISSUED and still be in effect in it.
		/// MDs run 1–3 h, MPDs up to ~6.</summary>
		internal static readonly TimeSpan Lookback = TimeSpan.FromHours(8);

		// A window that ended this long ago will never gain another discussion.
		private static readonly TimeSpan SettledAfter = TimeSpan.FromHours(3);

		public MesoDiscussionService() : base("Discussions", "Anvil/1.0 (severe-weather app)")
		{
			Directory.CreateDirectory(Path.Combine(CacheDirectory, "text"));
		}

		public async Task<MesoDiscussionFetch> FetchAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, bool live,
			CancellationToken cancellationToken = default)
		{
			var start = startUtc.ToUniversalTime();
			var end = endUtc.ToUniversalTime();
			var stem = live ? "live" : $"{start:yyyyMMddHHmm}-{end:yyyyMMddHHmm}";
			var hours = (int)Math.Ceiling((end - start + Lookback).TotalHours);
			var settled = !live && DateTimeOffset.UtcNow - end > SettledAfter;

			var bodies = new List<(DiscussionKind Kind, string Json)>();
			var errors = new List<string>();
			foreach (var kind in DiscussionKinds.All)
			{
				var path = Path.Combine(CacheDirectory, $"{kind.Id}-{stem}.json");
				try
				{
					string json;
					if (settled && File.Exists(path))
					{
						json = await File.ReadAllTextAsync(path, cancellationToken);
					}
					else
					{
						json = await Http.GetStringAsync(
							$"{ApiRoot}{kind.ApiPath}?valid={Iso(end)}&hours={hours}", cancellationToken);
						if (JsonNode.Parse(json)?["features"] is not JsonArray) { throw new InvalidDataException("not a FeatureCollection"); }
						await AtomicWriteAsync(path, json, cancellationToken);
					}
					bodies.Add((kind, json));
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					errors.Add($"{kind.Issuer} discussions unavailable ({ex.Message})");
				}
			}

			if (bodies.Count == 0) { return MesoDiscussionFetch.Failed(string.Join(" ", errors)); }

			var (page, list) = Build(bodies, start, end);
			var file = $"discussions-{stem}.geojson";
			await AtomicWriteAsync(Path.Combine(CacheDirectory, file), page, cancellationToken);
			return new MesoDiscussionFetch(true, list, $"https://{CacheHostName}/{file}",
				errors.Count > 0 ? string.Join(" ", errors) : null);
		}

		public async Task<MesoDiscussionText?> GetTextAsync(MesoDiscussion discussion, CancellationToken cancellationToken = default)
		{
			var path = Path.Combine(CacheDirectory, "text", discussion.ProductId + ".txt");
			try
			{
				string text;
				if (File.Exists(path))
				{
					text = await File.ReadAllTextAsync(path, cancellationToken);
				}
				else
				{
					text = await Http.GetStringAsync($"{ApiRoot}nwstext/{Uri.EscapeDataString(discussion.ProductId)}", cancellationToken);
					if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith("<", StringComparison.Ordinal) || text.TrimStart().StartsWith("{", StringComparison.Ordinal))
					{
						return null; // an error page / JSON error, not a product
					}
					await AtomicWriteAsync(path, text, cancellationToken);
				}
				return MesoDiscussionTextParser.Parse(text);
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

		/// <summary>
		/// The kinds' GeoJSON bodies → the page's FeatureCollection (<c>key</c>, <c>kind</c>, <c>label</c>,
		/// <c>t0</c>/<c>t1</c>, <c>prob</c>; the polygon as given) and the index, both limited to what OVERLAPS
		/// [start, end] and ordered by issue time. Pure + internal for tests.
		/// </summary>
		internal static (string Json, List<MesoDiscussion> List) Build(
			IEnumerable<(DiscussionKind Kind, string Json)> bodies, DateTimeOffset start, DateTimeOffset end)
		{
			var rows = new List<(MesoDiscussion D, JsonNode Geometry)>();
			foreach (var (kind, body) in bodies)
			{
				if (JsonNode.Parse(body)?["features"] is not JsonArray features) { continue; }
				foreach (var f in features)
				{
					var p = f?["properties"];
					var geom = f?["geometry"];
					if (p is null || geom is null) { continue; }
					if (!TryTime(p["issue"], out var issued) || !TryTime(p["expire"], out var expires)) { continue; }
					if (expires <= start || issued > end || expires <= issued) { continue; }
					var num = Int(p["num"]);
					var year = Int(p["year"]);
					var pid = Str(p["product_id"]);
					if (num <= 0 || year <= 0 || pid.Length == 0) { continue; }

					int? prob = p["watch_confidence"] is JsonValue pv && pv.TryGetValue<double>(out var pd) ? (int)Math.Round(pd) : null;
					var d = new MesoDiscussion(kind, year, num, pid, issued, expires, prob, Str(p["concerning"]).Trim());
					rows.Add((d, geom.DeepClone()));
				}
			}

			// A product can appear twice if IEM re-lists a correction; the latest issuance of a number wins.
			var unique = rows.GroupBy(r => r.D.Key).Select(g => g.OrderByDescending(r => r.D.Issued).First())
				.OrderBy(r => r.D.Issued).ThenBy(r => r.D.Kind.Id).ToList();

			var page = new JsonArray();
			foreach (var (d, geom) in unique)
			{
				page.Add(new JsonObject
				{
					["type"] = "Feature",
					["geometry"] = geom,
					["properties"] = new JsonObject
					{
						["key"] = d.Key,
						["kind"] = d.Kind.Id,
						["label"] = d.Label,
						["t0"] = d.Issued.ToUnixTimeMilliseconds(),
						["t1"] = d.Expires.ToUnixTimeMilliseconds(),
						["prob"] = d.WatchProbability ?? -1,
					},
				});
			}
			var json = new JsonObject { ["type"] = "FeatureCollection", ["features"] = page }.ToJsonString();
			return (json, unique.Select(r => r.D).ToList());
		}

		private static bool TryTime(JsonNode? n, out DateTimeOffset t)
		{
			t = default;
			return n is JsonValue v && v.TryGetValue<string>(out var s) &&
				DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);
		}

		private static int Int(JsonNode? n) =>
			n is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : 0) : 0;

		private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;

		private static string Iso(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
	}
}
