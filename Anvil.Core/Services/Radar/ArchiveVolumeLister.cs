using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Anvil.Services
{
	/// <summary>Lists one site's archive volumes for one UTC day — the raw input to data uptime and the
	/// scan-pattern sample — and reads one volume's VCP from its first few KB.</summary>
	public interface IArchiveVolumeLister
	{
		/// <summary>Volume start times, ascending. Throws on a failed listing — the caller must tell "couldn't
		/// look" (unknown) from "looked, found nothing" (an empty list = down all day).</summary>
		Task<IReadOnlyList<DateTimeOffset>> ListVolumeTimesAsync(string siteId, DateOnly utcDay, CancellationToken cancellationToken);

		/// <summary>Volume (start time, bucket key) pairs, ascending. Same failure rule as the times.</summary>
		Task<IReadOnlyList<(DateTimeOffset Time, string Key)>> ListVolumesAsync(string siteId, DateOnly utcDay, CancellationToken cancellationToken);

		/// <summary>The VCP a volume ran, from its metadata record alone (~8 KB range request, no volume
		/// download). 0 when it can't be read (legacy .gz, a bad prefix) — a failed read, not a pattern.</summary>
		Task<int> ReadVcpAsync(string key, CancellationToken cancellationToken);
	}

	/// <summary>
	/// The archive bucket behind data uptime and the scan-pattern bar: LISTING (one request per site-day, a day
	/// is ~300 keys, under the 1000-key page) and a tiny RANGE read of a volume's first record for its VCP.
	/// Never a whole-volume download.
	/// </summary>
	/// <remarks>
	/// ⚠️ Same key rules as <c>Level2RadarService.KeysForDayAsync</c> — it reuses <c>IsVolumeKey</c> (no _MDM
	/// sidecars) and <c>MinVolumeBytes</c> (a 4 KB aborted scan is not data), so "a volume" means the same
	/// thing to the uptime strip as to the loop. It does NOT log each degenerate volume the way the loop does:
	/// a backfill lists hundreds of site-days and would flood the diagnostics run.
	/// ⚠️ VCP READ: a volume file opens with the 24-byte AR2V header + the METADATA record (control word +
	/// bzip2), the same layout as a live S chunk, so it goes through <c>Level2RadarService.DecompressChunk</c>
	/// and <c>Level2Format.ReadVcpFromMetadata</c> — the loop's own parse, unlisted-VCP gate included. Measured
	/// 2026-09-26: that record is 2.3–3.9 KB compressed (KTLX/KVNX/KLOT), so an 8 KB prefix holds it; a bigger
	/// one costs a second request sized to the record.
	/// Its own HttpClient so a background sample never queues behind a loop's downloads.
	/// </remarks>
	public sealed class ArchiveVolumeLister : IArchiveVolumeLister
	{
		private const int VcpPrefixBytes = 8 * 1024;
		private const int MaxMetadataRecordBytes = 512 * 1024; // sanity cap on the record-size word

		private readonly HttpClient _http;

		public ArchiveVolumeLister()
		{
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0");
		}

		public async Task<IReadOnlyList<DateTimeOffset>> ListVolumeTimesAsync(string siteId, DateOnly utcDay, CancellationToken cancellationToken) =>
			(await ListVolumesAsync(siteId, utcDay, cancellationToken)).Select(v => v.Time).ToList();

		public async Task<IReadOnlyList<(DateTimeOffset Time, string Key)>> ListVolumesAsync(string siteId, DateOnly utcDay, CancellationToken cancellationToken)
		{
			var prefix = $"{utcDay:yyyy/MM/dd}/{siteId}/";
			var volumes = new List<(DateTimeOffset Time, string Key)>();
			string? continuation = null;
			do
			{
				var url = $"{Level2RadarService.BucketBase}?list-type=2&prefix={Uri.EscapeDataString(prefix)}&max-keys=1000";
				if (continuation is not null)
				{
					url += $"&continuation-token={Uri.EscapeDataString(continuation)}";
				}
				var doc = XDocument.Parse(await _http.GetStringAsync(url, cancellationToken));
				var s3 = Level2RadarService.S3;
				foreach (var contents in doc.Descendants(s3 + "Contents"))
				{
					var key = contents.Element(s3 + "Key")?.Value;
					if (key is null || !Level2RadarService.IsVolumeKey(key)) continue;
					if (long.TryParse(contents.Element(s3 + "Size")?.Value, out var size) && size < Level2RadarService.MinVolumeBytes) continue;
					if (Level2RadarService.ParseVolumeTime(key) is { } t) volumes.Add((t, key));
				}
				continuation = doc.Root?.Element(s3 + "IsTruncated")?.Value == "true"
					? doc.Root?.Element(s3 + "NextContinuationToken")?.Value
					: null;
			}
			while (continuation is not null);
			volumes.Sort((a, b) => a.Time.CompareTo(b.Time));
			return volumes;
		}

		public async Task<int> ReadVcpAsync(string key, CancellationToken cancellationToken)
		{
			if (key.EndsWith(".gz", StringComparison.Ordinal)) return 0; // gzip-wrapped: no readable prefix

			var prefix = await GetRangeAsync(key, VcpPrefixBytes, cancellationToken);
			if (prefix.Length < 28) return 0;
			var recordBytes = Math.Abs((prefix[24] << 24) | (prefix[25] << 16) | (prefix[26] << 8) | prefix[27]);
			if (recordBytes is <= 0 or > MaxMetadataRecordBytes) return 0;
			if (28 + recordBytes > prefix.Length)
			{
				prefix = await GetRangeAsync(key, 28 + recordBytes, cancellationToken);
			}
			var metadata = Level2RadarService.DecompressChunk(prefix, isS: true);
			return metadata is null ? 0 : Level2Format.ReadVcpFromMetadata(new List<(byte[] block, int elev)> { (metadata, 0) });
		}

		private async Task<byte[]> GetRangeAsync(string key, int bytes, CancellationToken ct)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, Level2RadarService.BucketBase + key);
			request.Headers.Range = new RangeHeaderValue(0, bytes - 1);
			using var response = await _http.SendAsync(request, ct);
			response.EnsureSuccessStatusCode();
			return await response.Content.ReadAsByteArrayAsync(ct);
		}
	}
}
