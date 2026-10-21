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
	/// ⚠️ Cached by RUN folder (<c>Forecast\hrrr\{yyyyMMddHH}\</c>): the GRIB message (<c>{field}-{lead}.grib2</c>) and
	/// the page's frame built from it (<c>{field}[-run]-{lead}.bin</c>, <see cref="ModelFieldFrames"/>). A landed message
	/// never changes, so nothing cached is ever re-fetched or rebuilt. Run folders older than <see cref="KeepRuns"/>
	/// are swept at construction (the base class's sweep only looks at top-level files). The <c>Forecast</c> folder is
	/// the page's <c>forecast</c> virtual host (<see cref="CacheHostName"/>), shared by every future model.
	/// ⚠️ <c>ConfigureAwait(false)</c> throughout — the decode is ~2 M points and must stay off the UI thread.
	/// Real-data check: <c>TiltCheck -- --hrrr</c>.
	/// </remarks>
	public sealed class HrrrService : CachingHttpService, IHrrrService
	{
		/// <summary>The page's virtual host for the <c>Forecast</c> cache folder (MainWindow maps it).</summary>
		public const string CacheHostName = "forecast";
		private const string ModelFolder = "hrrr";
		private const string MeshFileName = "mesh.json";

		private const string Bucket = "https://noaa-hrrr-bdp-pds.s3.amazonaws.com";
		private static readonly TimeSpan KeepRuns = TimeSpan.FromDays(2);
		private static readonly XNamespace S3 = "http://s3.amazonaws.com/doc/2006-03-01/";

		// A landed hour's index never changes; keyed "yyyyMMddHH/wrfsfc/f01".
		private readonly ConcurrentDictionary<string, string> _indexes = new();
		private readonly string _modelDirectory;
		private readonly SemaphoreSlim _frameGate = new(1, 1);
		private LambertConformalGrid? _meshWrittenFor;

		public HrrrService() : base("Forecast", "Anvil/1.0 (severe-weather app)")
		{
			Http.Timeout = TimeSpan.FromSeconds(60);
			_modelDirectory = Path.Combine(CacheDirectory, ModelFolder);
			Directory.CreateDirectory(_modelDirectory);
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
			var folder = RunFolder(runUtc);
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

		public async Task<string> GetFrameAsync(DateTimeOffset runUtc, HrrrField field, int leadMinutes, bool runSoFar,
			CancellationToken cancellationToken = default)
		{
			// One build at a time: a "run so far" frame decodes up to 48 hours, and two at once would double that for
			// the same answer when the frame is re-asked mid-build.
			await _frameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				var name = string.Create(CultureInfo.InvariantCulture, $"{field.Id}{(runSoFar ? "-run" : "")}-{leadMinutes:D4}.bin");
				var folder = RunFolder(runUtc);
				var path = Path.Combine(folder, name);
				LambertConformalGrid grid;
				if (File.Exists(path) && _meshWrittenFor is { } known)
				{
					grid = known;
				}
				else
				{
					float[] values;
					if (runSoFar && field.IsHourlyMax)
					{
						// Every hour up to this one, max'd cell by cell: the swath the storms painted so far.
						var hours = new List<float[]>();
						GribField? last = null;
						for (var h = 1; h <= leadMinutes / 60; h++)
						{
							last = await GetFieldAsync(runUtc, field, h * 60, cancellationToken).ConfigureAwait(false);
							hours.Add(last.Values);
						}
						grid = last!.Grid;
						values = await Task.Run(() => ModelFieldFrames.MaxOf(hours), cancellationToken).ConfigureAwait(false);
					}
					else
					{
						var f = await GetFieldAsync(runUtc, field, leadMinutes, cancellationToken).ConfigureAwait(false);
						grid = f.Grid;
						values = f.Values;
					}
					if (!File.Exists(path))
					{
						var bytes = await Task.Run(() => ModelFieldFrames.EncodeFrame(values, field.Encoding), cancellationToken).ConfigureAwait(false);
						Directory.CreateDirectory(folder);
						await AtomicWriteAsync(path, s => s.WriteAsync(bytes, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
					}
					await EnsureMeshAsync(grid, cancellationToken).ConfigureAwait(false);
				}
				var run = runUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
				return ModelFieldFrames.DescriptorJson($"https://{CacheHostName}/{ModelFolder}/{run}/{name}",
					$"https://{CacheHostName}/{ModelFolder}/{MeshFileName}", grid, field);
			}
			finally { _frameGate.Release(); }
		}

		// The mesh belongs to the GRID, which every HRRR run shares — written once, rewritten only if the grid changes.
		private async Task EnsureMeshAsync(LambertConformalGrid grid, CancellationToken ct)
		{
			if (Equals(_meshWrittenFor, grid)) return;
			var json = await Task.Run(() => ModelFieldFrames.MeshJson(grid), ct).ConfigureAwait(false);
			await AtomicWriteAsync(Path.Combine(_modelDirectory, MeshFileName), json, ct).ConfigureAwait(false);
			_meshWrittenFor = grid;
		}

		private string RunFolder(DateTimeOffset runUtc) =>
			Path.Combine(_modelDirectory, runUtc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));

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
					foreach (var dir in Directory.EnumerateDirectories(_modelDirectory))
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
