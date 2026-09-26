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
	/// <summary>Per-site data uptime over the last <see cref="RadarUptimeService.HistoryDays"/> days.</summary>
	public interface IRadarUptimeService
	{
		/// <summary>One site's report — lists whatever days aren't cached yet (≈31 requests the first time,
		/// ~1-2 after). Never throws for a failed day: that day comes back unknown.</summary>
		Task<SiteUptimeReport> GetReportAsync(string siteId, CancellationToken cancellationToken = default);

		/// <summary>Fills the day cache for many sites in the background, a few requests at a time.
		/// <paramref name="progress"/> gets each site id as its days are all cached.</summary>
		Task BackfillAsync(IReadOnlyList<string> siteIds, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Data uptime from the archive: lists each site-day once (<see cref="IArchiveVolumeLister"/>), keeps FINAL
	/// days on disk forever-ish, and hands the times to <see cref="UptimeCalculator"/>.
	/// </summary>
	/// <remarks>
	/// ⚠️ A DAY IS FINAL only once it's been listed at least <see cref="FinalGrace"/> after its UTC end — the
	/// archive lags real time and late uploads trickle in, so a listing taken sooner is kept in memory for
	/// <see cref="PartialTtl"/> and never written. Final days are immutable: fetched once, never again (same
	/// idea as past outlooks). Files: <c>%LocalAppData%\Anvil\Network\Archive\{SITE}\{yyyyMMdd}.txt</c>, one
	/// "HHmmss" per line; an EMPTY file is a real answer (no data that day), a MISSING file means not fetched.
	/// ⚠️ Concurrency is capped at <see cref="MaxConcurrentListings"/> ACROSS the service, so a network backfill
	/// and an Atlas click share one polite budget. Concurrent asks for the same site-day share one request.
	/// </remarks>
	public sealed class RadarUptimeService : IRadarUptimeService
	{
		public const int HistoryDays = 30;
		internal static readonly TimeSpan FinalGrace = TimeSpan.FromHours(3);
		internal static readonly TimeSpan PartialTtl = TimeSpan.FromMinutes(5);
		private const int MaxConcurrentListings = 4;

		private readonly IArchiveVolumeLister _lister;
		private readonly ILogger<RadarUptimeService> _logger;
		private readonly string _root;
		private readonly Func<DateTimeOffset> _clock;
		private readonly TimeZoneInfo _dayZone;
		private readonly SemaphoreSlim _budget = new(MaxConcurrentListings);
		private readonly ConcurrentDictionary<string, (IReadOnlyList<DateTimeOffset> times, DateTimeOffset at)> _partial = new();
		private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<DateTimeOffset>?>>> _inFlight = new();

		public RadarUptimeService(IArchiveVolumeLister lister, ILogger<RadarUptimeService> logger)
			: this(lister, logger, null, null, null) { }

		/// <param name="directory">TESTS/TOOLS — cache root. Null = <c>%LocalAppData%\Anvil\Network\Archive</c>.</param>
		/// <param name="clock">TESTS — "now". Null = the wall clock.</param>
		/// <param name="dayZone">Zone the strip's days are cut in. Null = the user's local zone.</param>
		internal RadarUptimeService(IArchiveVolumeLister lister, ILogger<RadarUptimeService> logger,
			string? directory, Func<DateTimeOffset>? clock, TimeZoneInfo? dayZone)
		{
			_lister = lister;
			_logger = logger;
			_root = directory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Network", "Archive");
			_clock = clock ?? (() => DateTimeOffset.UtcNow);
			_dayZone = dayZone ?? TimeZoneInfo.Local;
			Directory.CreateDirectory(_root);
		}

		public async Task<SiteUptimeReport> GetReportAsync(string siteId, CancellationToken cancellationToken = default)
		{
			siteId = siteId.ToUpperInvariant();
			var now = _clock();
			var windowStart = WindowStart(now);
			var days = UtcDaysFor(windowStart, now);

			var results = await Task.WhenAll(days.Select(d => GetDayAsync(siteId, d, now, cancellationToken)));
			var known = new HashSet<DateOnly>();
			var times = new List<DateTimeOffset>();
			for (var i = 0; i < days.Count; i++)
			{
				if (results[i] is not { } dayTimes) continue;
				known.Add(days[i]);
				times.AddRange(dayTimes);
			}
			return UptimeCalculator.Compute(siteId, times, known, windowStart, now, _dayZone);
		}

		public async Task BackfillAsync(IReadOnlyList<string> siteIds, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
		{
			var now = _clock();
			var days = UtcDaysFor(WindowStart(now), now);
			var failed = 0;
			foreach (var site in siteIds.Select(s => s.ToUpperInvariant()))
			{
				cancellationToken.ThrowIfCancellationRequested();
				// Days of one site in parallel (the shared budget still caps it); sites one after another, so a
				// half-finished backfill leaves whole sites done rather than every site half done.
				var results = await Task.WhenAll(days.Select(d => GetDayAsync(site, d, now, cancellationToken)));
				failed += results.Count(r => r is null);
				progress?.Report(site);
			}
			_logger.LogInformation("Uptime backfill: {Sites} site(s) x {Days} day(s), {Failed} listing(s) failed",
				siteIds.Count, days.Count, failed);
			PruneOldDays(now);
		}

		// Local midnight HistoryDays-1 days ago → the strip is exactly HistoryDays local days, today included.
		private DateTimeOffset WindowStart(DateTimeOffset now)
		{
			var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _dayZone).DateTime);
			var first = today.AddDays(-(HistoryDays - 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
			return new DateTimeOffset(first, _dayZone.GetUtcOffset(first)).ToUniversalTime();
		}

		// Every UTC day the window touches, plus the day BEFORE it (the first interval needs a predecessor).
		private static List<DateOnly> UtcDaysFor(DateTimeOffset windowStart, DateTimeOffset now)
		{
			var list = new List<DateOnly>();
			for (var d = DateOnly.FromDateTime(windowStart.UtcDateTime).AddDays(-1); d <= DateOnly.FromDateTime(now.UtcDateTime); d = d.AddDays(1))
			{
				list.Add(d);
			}
			return list;
		}

		// A day's times: disk (final) → memory (recent partial) → one shared listing. Null = couldn't list.
		private async Task<IReadOnlyList<DateTimeOffset>?> GetDayAsync(string siteId, DateOnly day, DateTimeOffset now, CancellationToken ct)
		{
			var file = DayFile(siteId, day);
			if (File.Exists(file))
			{
				var fromDisk = TryRead(file, day);
				if (fromDisk is not null) return fromDisk;
			}
			var key = $"{siteId}/{day:yyyyMMdd}";
			if (_partial.TryGetValue(key, out var p) && now - p.at < PartialTtl) return p.times;

			var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<DateTimeOffset>?>>(() => ListAsync(siteId, day, key, file, ct)));
			try
			{
				return await lazy.Value;
			}
			finally
			{
				_inFlight.TryRemove(key, out _);
			}
		}

		private async Task<IReadOnlyList<DateTimeOffset>?> ListAsync(string siteId, DateOnly day, string key, string file, CancellationToken ct)
		{
			await _budget.WaitAsync(ct);
			try
			{
				var listedAt = _clock();
				var times = await _lister.ListVolumeTimesAsync(siteId, day, ct);
				var dayEnd = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
				if (listedAt >= dayEnd + FinalGrace)
				{
					Write(file, times);
				}
				else
				{
					_partial[key] = (times, listedAt);
				}
				return times;
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				// One bad listing costs one day's knowledge (shown unknown), never the report.
				_logger.LogDebug(ex, "Uptime listing failed for {Key}", key);
				return null;
			}
			finally
			{
				_budget.Release();
			}
		}

		private string DayFile(string siteId, DateOnly day) => Path.Combine(_root, siteId, $"{day:yyyyMMdd}.txt");

		private static IReadOnlyList<DateTimeOffset>? TryRead(string file, DateOnly day)
		{
			try
			{
				var baseTime = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
				var list = new List<DateTimeOffset>();
				foreach (var line in File.ReadAllLines(file))
				{
					if (TimeOnly.TryParseExact(line.Trim(), "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
					{
						list.Add(baseTime + t.ToTimeSpan());
					}
				}
				return list;
			}
			catch (IOException)
			{
				return null; // re-list rather than trust a file we can't read
			}
		}

		private void Write(string file, IReadOnlyList<DateTimeOffset> times)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				var temp = $"{file}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
				File.WriteAllLines(temp, times.Select(t => t.UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture)));
				File.Move(temp, file, overwrite: true);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not cache uptime day {File}", file);
			}
		}

		// Days older than the window (+ a week of slack) are never read again.
		private void PruneOldDays(DateTimeOffset now)
		{
			var cutoff = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(HistoryDays + 7));
			try
			{
				foreach (var f in Directory.EnumerateFiles(_root, "*.txt", SearchOption.AllDirectories))
				{
					if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
						&& d < cutoff)
					{
						File.Delete(f);
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Uptime cache prune failed");
			}
		}
	}
}
