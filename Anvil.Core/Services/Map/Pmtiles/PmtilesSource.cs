using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>Byte ranges of a PMTiles archive — a remote one over HTTP, or bytes in memory (tests).</summary>
	public interface IPmtilesSource
	{
		/// <summary>Exactly <paramref name="length"/> bytes from <paramref name="offset"/> (fewer only at the archive's end).</summary>
		Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken ct);
	}

	/// <summary>
	/// A remote archive read with HTTP range requests — how the extractor reads Protomaps' ~130 GB planet build without
	/// downloading it. ⚠️ <c>ConfigureAwait(false)</c> throughout (the feed services' rule): this runs off the UI thread.
	/// </summary>
	public sealed class HttpPmtilesSource : IPmtilesSource
	{
		private readonly HttpClient _http;

		public HttpPmtilesSource(HttpClient http, Uri url)
		{
			_http = http;
			Url = url;
		}

		public Uri Url { get; }

		public async Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken ct)
		{
			if (length <= 0) return Array.Empty<byte>();
			using var request = new HttpRequestMessage(HttpMethod.Get, Url);
			request.Headers.Range = new RangeHeaderValue((long)offset, (long)offset + length - 1);
			using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
			if (response.StatusCode != HttpStatusCode.PartialContent)
			{
				// A 200 here would be the WHOLE planet — never read it.
				throw new HttpRequestException($"The map server didn't return the requested part ({(int)response.StatusCode}).");
			}
			return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
		}
	}
}
