using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>How many sampled hours a site spent on one VCP.</summary>
	public sealed record ScanPatternShare(int Vcp, int Hours);

	/// <summary>A site's scan-pattern mix over the uptime window, from an HOURLY sample. Partial while
	/// <see cref="DaysDone"/> &lt; <see cref="DaysTotal"/> (the sample fills newest day first).</summary>
	public sealed record SiteScanPatternReport(string SiteId, IReadOnlyList<ScanPatternShare> Shares, int DaysDone, int DaysTotal)
	{
		public int SampledHours => Shares.Sum(s => s.Hours);
	}

	public interface IRadarScanPatternService
	{
		/// <summary>The site's VCP mix over the last <see cref="RadarUptimeService.HistoryDays"/> UTC days.
		/// <paramref name="progress"/> gets the growing report after each day (newest first). A failed day is
		/// simply missing from the sample — never an exception.</summary>
		Task<SiteScanPatternReport> GetReportAsync(string siteId, IProgress<SiteScanPatternReport>? progress = null, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// The Atlas's scan-pattern bar: ONE volume per hour (the hour's first), its VCP read from an ~8 KB range
	/// request (<see cref="IArchiveVolumeLister.ReadVcpAsync"/>). ~720 reads / ~6 MB the first time a site is
	/// opened; finished days are cached, so after that it's one day's worth (≤24).
	/// </summary>
	/// <remarks>
	/// ⚠️ A SAMPLE, and the share is of sampled HOURS, not of volumes — say "share of time" in the UI, never
	/// "of volumes". A switch shorter than an hour can be missed; chosen 2026-09-26 over 3-hourly (misses a
	/// short storm) and over only-loaded-frames (biased toward when you were watching).
	/// CACHE: <c>%LocalAppData%\Anvil\Network\Vcp\{SITE}\{yyyyMMdd}.txt</c>, one "HH vcp" per sampled hour,
	/// written only when the day is FINAL (listed ≥ <see cref="RadarUptimeService.FinalGrace"/> after its UTC
	/// end) AND every read in it succeeded — a day with a failed read is retried next time. A 0 VCP is stored
	/// (an unreadable volume stays unreadable) and left out of the shares. Today lives in memory for
	/// <see cref="PartialTtl"/>.
	/// </remarks>
	public sealed class RadarScanPatternService : IRadarScanPatternService
	{
		internal static readonly TimeSpan PartialTtl = TimeSpan.FromMinutes(30);
		private const int MaxConcurrentReads = 4;

		private readonly IArchiveVolumeLister _lister;
		private readonly ILogger<RadarScanPatternService> _logger;
		private readonly string _root;
		private readonly Func<DateTimeOffset> _clock;
		private readonly SemaphoreSlim _budget = new(MaxConcurrentReads);
		private readonly ConcurrentDictionary<string, (IReadOnlyList<(int hour, int vcp)> samples, DateTimeOffset at)> _partial = new();

		public RadarScanPatternService(IArchiveVolumeLister lister, ILogger<RadarScanPatternService> logger)
			: this(lister, logger, null, null) { }

		internal RadarScanPatternService(IArchiveVolumeLister lister, ILogger<RadarScanPatternService> logger, string? directory, Func<DateTimeOffset>? clock)
		{
			_lister = lister;
			_logger = logger;
			_root = directory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Network", "Vcp");
			_clock = clock ?? (() => DateTimeOffset.UtcNow);
			Directory.CreateDirectory(_root);
		}

		public async Task<SiteScanPatternReport> GetReportAsync(string siteId, IProgress<SiteScanPatternReport>? progress = null, CancellationToken cancellationToken = default)
		{
			siteId = siteId.ToUpperInvariant();
			var now = _clock();
			var today = DateOnly.FromDateTime(now.UtcDateTime);
			var days = Enumerable.Range(0, RadarUptimeService.HistoryDays).Select(i => today.AddDays(-i)).ToList(); // newest first
			var counts = new Dictionary<int, int>();
			var done = 0;

			SiteScanPatternReport Snapshot() => new(siteId,
				counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => new ScanPatternShare(kv.Key, kv.Value)).ToList(),
				done, days.Count);

			foreach (var day in days)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var samples = await GetDayAsync(siteId, day, now, cancellationToken);
				foreach (var (_, vcp) in samples ?? Array.Empty<(int, int)>())
				{
					if (vcp > 0) counts[vcp] = counts.GetValueOrDefault(vcp) + 1;
				}
				done++;
				progress?.Report(Snapshot());
			}
			return Snapshot();
		}

		// One day's hourly samples: disk (final) → memory (today) → list + read. Null = the listing failed.
		private async Task<IReadOnlyList<(int hour, int vcp)>?> GetDayAsync(string siteId, DateOnly day, DateTimeOffset now, CancellationToken ct)
		{
			var file = Path.Combine(_root, siteId, $"{day:yyyyMMdd}.txt");
			if (File.Exists(file) && TryRead(file) is { } cached) return cached;
			var key = $"{siteId}/{day:yyyyMMdd}";
			if (_partial.TryGetValue(key, out var p) && now - p.at < PartialTtl) return p.samples;

			IReadOnlyList<(DateTimeOffset Time, string Key)> volumes;
			var listedAt = _clock();
			await _budget.WaitAsync(ct);
			try
			{
				volumes = await _lister.ListVolumesAsync(siteId, day, ct);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Scan-pattern listing failed for {Key}", key);
				return null;
			}
			finally
			{
				_budget.Release();
			}

			var firstPerHour = volumes.GroupBy(v => v.Time.UtcDateTime.Hour).Select(g => (hour: g.Key, key: g.First().Key)).ToList();
			var anyFailed = false;
			var samples = await Task.WhenAll(firstPerHour.Select(async h =>
			{
				await _budget.WaitAsync(ct);
				try
				{
					return (h.hour, vcp: await _lister.ReadVcpAsync(h.key, ct));
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
				catch (Exception ex)
				{
					anyFailed = true;
					_logger.LogDebug(ex, "VCP read failed for {Key}", h.key);
					return (h.hour, vcp: -1); // failed: not stored, not counted
				}
				finally
				{
					_budget.Release();
				}
			}));
			var good = samples.Where(s => s.vcp >= 0).OrderBy(s => s.hour).ToList();

			var dayEnd = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
			if (!anyFailed && listedAt >= dayEnd + RadarUptimeService.FinalGrace)
			{
				Write(file, good);
			}
			else
			{
				_partial[key] = (good, listedAt);
			}
			return good;
		}

		private static IReadOnlyList<(int hour, int vcp)>? TryRead(string file)
		{
			try
			{
				var list = new List<(int, int)>();
				foreach (var line in File.ReadAllLines(file))
				{
					var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length == 2
						&& int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
						&& int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
					{
						list.Add((h, v));
					}
				}
				return list;
			}
			catch (IOException)
			{
				return null;
			}
		}

		private void Write(string file, IEnumerable<(int hour, int vcp)> samples)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				var temp = $"{file}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
				File.WriteAllLines(temp, samples.Select(s => $"{s.hour:00} {s.vcp}"));
				File.Move(temp, file, overwrite: true);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not cache scan-pattern day {File}", file);
			}
		}
	}
}
