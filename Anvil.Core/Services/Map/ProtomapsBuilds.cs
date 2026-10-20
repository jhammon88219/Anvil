using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Where Protomaps' daily planet builds are: the build list (<see cref="BuildListUrl"/>, a JSON array of
	/// <c>{ "key": "yyyyMMdd.pmtiles", "size": … }</c>) and the files themselves under <see cref="BuildsBaseUrl"/>.
	/// An offline map is extracted from the NEWEST one.
	/// </summary>
	public static class ProtomapsBuilds
	{
		public const string BuildListUrl = "https://build-metadata.protomaps.dev/builds.json";
		public const string BuildsBaseUrl = "https://build.protomaps.com/";

		/// <summary>The newest planet build's URL. Keys are dates, so the newest is the greatest key.</summary>
		public static async Task<Uri> LatestAsync(HttpClient http, CancellationToken ct)
		{
			var json = await http.GetStringAsync(BuildListUrl, ct).ConfigureAwait(false);
			return LatestFromList(json);
		}

		/// <summary>Pick the newest build out of the list's JSON (split out so the rule is tested without a network).</summary>
		public static Uri LatestFromList(string json)
		{
			using var doc = JsonDocument.Parse(json);
			var newest = doc.RootElement.EnumerateArray()
				.Select(b => b.TryGetProperty("key", out var k) ? k.GetString() : null)
				.Where(k => k is not null && k.EndsWith(".pmtiles", StringComparison.Ordinal))
				.Max(StringComparer.Ordinal);
			return newest is null
				? throw new InvalidOperationException("Protomaps' build list has no map builds.")
				: new Uri(BuildsBaseUrl + newest);
		}
	}
}
