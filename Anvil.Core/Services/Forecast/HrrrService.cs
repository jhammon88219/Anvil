using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The HRRR from NOAA's open-data bucket on AWS (<c>noaa-hrrr-bdp-pds</c>, the NODD copy NCEP publishes to as each
	/// hour finishes). Reads ONE message per request: the file's <c>.idx</c> names every message's byte offset, so a
	/// 100+ MB file costs one ~200 KB range read for composite reflectivity.
	/// </summary>
	/// <remarks>
	/// ⚠️ A forecast hour has LANDED when its <c>.idx</c> exists — NCEP writes the index after the GRIB file, so an
	/// index never points into a half-written file. The bucket listing (prefix <c>hrrr.tHHz.wrfs</c>, ~200 keys) is
	/// how a run's progress is read: one request, no per-hour probing.
	/// ⚠️ Cached by RUN folder (<c>Hrrr\{yyyyMMddHH}\{field}-{lead}.grib2</c>): a landed message never changes, so a
	/// cached one is never re-fetched. Run folders older than <see cref="KeepRuns"/> are swept at construction
	/// (the base class's sweep only looks at top-level files).
	/// ⚠️ <c>ConfigureAwait(false)</c> throughout — the decode is ~2 M points and must stay off the UI thread.
	/// Real-data check: <c>TiltCheck -- --hrrr</c>.
	/// </remarks>
	public sealed class HrrrService : CachingHttpService, IHrrrService
	{
		private const string Bucket = "https://noaa-hrrr-bdp-pds.s3.amazonaws.com";
		private static readonly TimeSpan KeepRuns = TimeSpan.FromDays(2);
		private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

		// A landed hour's index never changes; keyed "yyyyMMddHH/wrfsfc/f01".
		private readonly ConcurrentDictionary<string, string> _indexes = new();

		public HrrrService() : base("Hrrr", "Anvil/1.0 (severe-weather app)")
		{
			Http.Timeout = TimeSpan.FromSeconds(60);
			SweepOldRuns();
		}

		public async Task<HrrrRunStatus?> FindLatestRunAsync(CancellationToken cancellationToken = default)
		{
			var now = DateTimeOffset.UtcNow;
			var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
			for (var back = 0; back <= 6; back++)
			{
				var status = await GetRunStatusAsync(hour.AddHours(-back), cancellationToken).ConfigureAwait(false);
				if (status.HourlyIn.Contains(0)) return status;
			}
			return null;
		}

		public async Task<HrrrRunStatus> GetRunStatusAsync(DateTimeOffset runUtc, CancellationToken cancellationToken = default)
		{
			var prefix = $"{DayFolder(runUtc)}/hrrr.t{runUtc:HH}z.wrfs";
			var hourly = new SortedSet<int>();
			var subHourly = new SortedSet<int>();
			string? token = null;
			do
			{
				var url = $"{Bucket}/?list-type=2&prefix={Uri.EscapeDataString(prefix)}"
					+ (token is null ? "" : $"&continuation-token={Uri.EscapeDataString(token)}");
				var xml = XDocument.Parse(await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false));
				foreach (var key in xml.Descendants(S3 + "Key").Select(k => k.Value))
				{
					// …/hrrr.t15z.wrfsfcf01.grib2.idx
					if (!key.EndsWith(".grib2.idx", StringComparison.Ordinal)) continue;
					var name = key[(key.LastIndexOf('/') + 1)..];
					if (TryHour(name, HrrrFields.HourlyFile, out var h)) hourly.Add(h);
					else if (TryHour(name, HrrrFields.SubHourlyFile, out h)) subHourly.Add(h);
				}
				token = xml.Root?.Element(S3 + "IsTruncated")?.Value == "true"
					? xml.Root.Element(S3 + "NextContinuationToken")?.Value
					: null;
			}
			while (token is not null);
			return new HrrrRunStatus(runUtc, HrrrFields.LastHourOf(runUtc), hourly.ToList(), subHourly.ToList());
		}

		public async Task<GribField> GetFieldAsync(DateTimeOffset runUtc, HrrrField field, int leadMinutes, CancellationToken cancellationToken = default)
		{
			var folder = Path.Combine(CacheDirectory, runUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));
			var path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{field.Id}-{leadMinutes:D4}.grib2"));
			byte[] bytes;
			if (File.Exists(path))
			{
				bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				bytes = await DownloadMessageAsync(runUtc, field, leadMinutes, cancellationToken).ConfigureAwait(false);
				Directory.CreateDirectory(folder);
				await AtomicWriteAsync(path, s => s.WriteAsync(bytes, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
			}
			return await Task.Run(() => Grib2Decoder.Decode(bytes), cancellationToken).ConfigureAwait(false);
		}

		private async Task<byte[]> DownloadMessageAsync(DateTimeOffset runUtc, HrrrField field, int leadMinutes, CancellationToken ct)
		{
			var (kind, fileHour, idxTime) = field.Locate(leadMinutes);
			var fileUrl = FileUrl(runUtc, kind, fileHour);
			var key = $"{runUtc:yyyyMMddHH}/{kind}/f{fileHour:D2}";
			if (!_indexes.TryGetValue(key, out var idx))
			{
				using var idxResponse = await Http.GetAsync(fileUrl + ".idx", ct).ConfigureAwait(false);
				if (idxResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
					throw new HrrrNotAvailableException($"HRRR {runUtc:HH}Z f{fileHour:D2} ({kind}) has not landed yet.");
				idxResponse.EnsureSuccessStatusCode();
				idx = await idxResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
				_indexes[key] = idx;
			}

			var range = HrrrIndex.Find(idx, field.IdxVariable, field.IdxLevel, idxTime)
				?? throw new HrrrNotAvailableException($"HRRR {runUtc:HH}Z f{fileHour:D2} has no {field.IdxVariable} {field.IdxLevel} at \"{idxTime}\".");
			using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
			request.Headers.Range = new RangeHeaderValue(range.Offset, range.Length is { } n ? range.Offset + n - 1 : null);
			using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			if (response.StatusCode != HttpStatusCode.PartialContent)
				throw new HttpRequestException($"Expected a partial response for {fileUrl}, got {(int)response.StatusCode}.");
			return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
		}

		private static string DayFolder(DateTimeOffset runUtc) => $"hrrr.{runUtc:yyyyMMdd}/conus";

		private static string FileUrl(DateTimeOffset runUtc, string kind, int hour) =>
			$"{Bucket}/{DayFolder(runUtc)}/hrrr.t{runUtc:HH}z.{kind}f{hour:D2}.grib2";

		// "hrrr.t15z.wrfsfcf01.grib2.idx" → 1 for kind "wrfsfc" (and not for "wrfsubh").
		private static bool TryHour(string name, string kind, out int hour)
		{
			hour = 0;
			var at = name.IndexOf("." + kind + "f", StringComparison.Ordinal);
			if (at < 0) return false;
			var digits = name.AsSpan(at + kind.Length + 2, 2);
			return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out hour);
		}

		private void SweepOldRuns()
		{
			_ = Task.Run(() =>
			{
				try
				{
					var cutoff = DateTime.UtcNow - KeepRuns;
					foreach (var dir in Directory.EnumerateDirectories(CacheDirectory))
					{
						if (!DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMddHH", CultureInfo.InvariantCulture,
								DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var run) || run >= cutoff) continue;
						try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
					}
				}
				catch { /* tidying is never worth a throw */ }
			});
		}
	}
}
