using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>What a map key test found.</summary>
	public enum MapKeyCheck { Works, Rejected, Unreachable }

	/// <summary>Tests a Protomaps map key (map setup's Test / Use online map).</summary>
	public interface IMapKeyChecker
	{
		Task<MapKeyCheck> CheckAsync(string key, CancellationToken ct);
	}

	/// <summary>
	/// The online map's KEY, in and out of the one tile URL setting (<see cref="AppSettings.OnlineTilesUrl"/>): map setup
	/// shows the key alone, the map reads the whole TileJSON URL. Checked by fetching ONE tile, the world at zoom 0 —
	/// a bad key answers 403 "Invalid API key" (measured 2026-10-20), so a wrong key reads differently from no internet.
	/// ⚠️ A key limited to certain websites on protomaps.com can pass here (no website sent) yet fail in the map.
	/// </summary>
	public sealed class ProtomapsKeys : IMapKeyChecker
	{
		private const string TestTileUrl = "https://api.protomaps.com/tiles/v4/0/0/0.mvt?key=";

		private readonly HttpClient _http;

		public ProtomapsKeys() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
		{
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil (map key test)");
		}

		internal ProtomapsKeys(HttpClient http) => _http = http;

		/// <summary>The tile URL for a key (Protomaps' TileJSON, the form the map already reads).</summary>
		public static string UrlFor(string key) => AppSettings.DefaultOnlineTilesUrl + Uri.EscapeDataString(key.Trim());

		/// <summary>The key inside a Protomaps tile URL; "" for any other URL (a self-hosted source has none to show).</summary>
		public static string KeyOf(string? url)
		{
			if (string.IsNullOrEmpty(url) || !url.StartsWith("https://api.protomaps.com/", StringComparison.OrdinalIgnoreCase)) return "";
			var i = url.IndexOf("key=", StringComparison.OrdinalIgnoreCase);
			if (i < 0) return "";
			var key = url[(i + 4)..];
			var end = key.IndexOf('&');
			return Uri.UnescapeDataString(end < 0 ? key : key[..end]);
		}

		public async Task<MapKeyCheck> CheckAsync(string key, CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(key)) return MapKeyCheck.Rejected;
			try
			{
				using var response = await _http.GetAsync(TestTileUrl + Uri.EscapeDataString(key.Trim()), ct).ConfigureAwait(false);
				return response.IsSuccessStatusCode ? MapKeyCheck.Works
					: response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? MapKeyCheck.Rejected
					: MapKeyCheck.Unreachable;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
			{
				return MapKeyCheck.Unreachable;
			}
		}
	}
}
