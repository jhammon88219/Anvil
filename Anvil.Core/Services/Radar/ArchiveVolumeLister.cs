using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Anvil.Services
{
	/// <summary>Lists one site's archive volume times for one UTC day — the raw input to data uptime.</summary>
	public interface IArchiveVolumeLister
	{
		/// <summary>Volume start times, ascending. Throws on a failed listing — the caller must tell "couldn't
		/// look" (unknown) from "looked, found nothing" (an empty list = down all day).</summary>
		Task<IReadOnlyList<DateTimeOffset>> ListVolumeTimesAsync(string siteId, DateOnly utcDay, CancellationToken cancellationToken);
	}

	/// <summary>
	/// The archive-bucket listing behind data uptime: LISTING ONLY, never a volume download — one S3 request per
	/// site-day (a day is ~300 keys, under the 1000-key page).
	/// </summary>
	/// <remarks>
	/// ⚠️ Same key rules as <c>Level2RadarService.KeysForDayAsync</c> — it reuses <c>IsVolumeKey</c> (no _MDM
	/// sidecars) and <c>MinVolumeBytes</c> (a 4 KB aborted scan is not data), so "a volume" means the same
	/// thing to the uptime strip as to the loop. It does NOT log each degenerate volume the way the loop does:
	/// a network backfill lists thousands of site-days and would flood the diagnostics run.
	/// Its own HttpClient so a background backfill never queues behind a loop's downloads.
	/// </remarks>
	public sealed class ArchiveVolumeLister : IArchiveVolumeLister
	{
		private readonly HttpClient _http;

		public ArchiveVolumeLister()
		{
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0");
		}

		public async Task<IReadOnlyList<DateTimeOffset>> ListVolumeTimesAsync(string siteId, DateOnly utcDay, CancellationToken cancellationToken)
		{
			var prefix = $"{utcDay:yyyy/MM/dd}/{siteId}/";
			var times = new List<DateTimeOffset>();
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
					if (Level2RadarService.ParseVolumeTime(key) is { } t) times.Add(t);
				}
				continuation = doc.Root?.Element(s3 + "IsTruncated")?.Value == "true"
					? doc.Root?.Element(s3 + "NextContinuationToken")?.Value
					: null;
			}
			while (continuation is not null);
			times.Sort();
			return times;
		}
	}
}
