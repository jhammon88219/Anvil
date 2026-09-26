using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The data-uptime rules (<see cref="UptimeCalculator"/>): cadence read from the data, gap vs down, the live
	/// tail, and unknown days. All in UTC days so the arithmetic is readable.
	/// </summary>
	public class UptimeCalculatorTests
	{
		private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
		private static readonly DateTimeOffset WindowStart = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

		// Every UTC day from the lead-in day through today.
		private static HashSet<DateOnly> AllKnown()
		{
			var set = new HashSet<DateOnly>();
			for (var d = DateOnly.FromDateTime(WindowStart.UtcDateTime).AddDays(-1); d <= DateOnly.FromDateTime(Now.UtcDateTime); d = d.AddDays(1)) set.Add(d);
			return set;
		}

		// Volumes every `step` from the lead-in day to `until`, skipping any in [holeFrom, holeTo).
		private static List<DateTimeOffset> Volumes(TimeSpan step, DateTimeOffset until, DateTimeOffset? holeFrom = null, DateTimeOffset? holeTo = null)
		{
			var list = new List<DateTimeOffset>();
			for (var t = WindowStart.AddDays(-1); t <= until; t += step)
			{
				if (holeFrom is { } a && holeTo is { } b && t >= a && t < b) continue;
				list.Add(t);
			}
			return list;
		}

		private static SiteUptimeReport Run(IEnumerable<DateTimeOffset> times, HashSet<DateOnly>? known = null) =>
			UptimeCalculator.Compute("KTLX", times, known ?? AllKnown(), WindowStart, Now, TimeZoneInfo.Utc);

		[Fact]
		public void SteadyDataIsFullyUp()
		{
			var r = Run(Volumes(TimeSpan.FromMinutes(5), Now.AddMinutes(-3)));
			Assert.Equal(1.0, r.UpFraction);
			Assert.Empty(r.Holes);
			Assert.Equal(30, r.Days.Count);
		}

		[Fact]
		public void ATwentyMinuteHoleInPrecipCadenceIsAGapNotDowntime()
		{
			var at = new DateTimeOffset(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
			var r = Run(Volumes(TimeSpan.FromMinutes(5), Now.AddMinutes(-3), at, at.AddMinutes(15)));
			var hole = Assert.Single(r.Holes);
			Assert.Equal(UptimeHoleKind.Gap, hole.Kind);
			Assert.Equal(1.0, r.UpFraction); // gaps are listed, never counted down
		}

		[Fact]
		public void ClearAirSpacingNeverFlags()
		{
			// 10-min cadence with one 20-min interval: under 2.5 x 10 = 25 min.
			var at = new DateTimeOffset(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
			var r = Run(Volumes(TimeSpan.FromMinutes(10), Now.AddMinutes(-3), at, at.AddMinutes(10)));
			Assert.Empty(r.Holes);
		}

		[Fact]
		public void ATwoHourHoleIsDownFromWhenTheNextVolumeWasDue()
		{
			var at = new DateTimeOffset(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
			var r = Run(Volumes(TimeSpan.FromMinutes(5), Now.AddMinutes(-3), at, at.AddHours(2)));
			var hole = Assert.Single(r.Holes);
			Assert.Equal(UptimeHoleKind.Down, hole.Kind);
			Assert.Equal(at, hole.StartUtc);                    // last volume 05:55 + 5-min cadence
			Assert.Equal(at.AddHours(2), hole.EndUtc);
			var day = r.Days.Single(d => d.Day == new DateOnly(2026, 9, 10));
			Assert.Equal(1 - 2.0 / 24, day.UpFraction!.Value, 6);
		}

		[Fact]
		public void AStaleTailIsAnOngoingOutage()
		{
			var r = Run(Volumes(TimeSpan.FromMinutes(5), Now.AddMinutes(-45)));
			var hole = Assert.Single(r.Holes);
			Assert.True(hole.Ongoing);
			Assert.Equal(UptimeHoleKind.Down, hole.Kind);
			Assert.Equal(Now, hole.EndUtc);
		}

		[Fact]
		public void AShortTailIsArchiveLagNotAGap()
		{
			var r = Run(Volumes(TimeSpan.FromMinutes(5), Now.AddMinutes(-20)));
			Assert.Empty(r.Holes);
		}

		[Fact]
		public void AnUnlistedDayIsUnknownNotDown()
		{
			var missing = new DateOnly(2026, 9, 10);
			var known = AllKnown();
			known.Remove(missing);
			var dayStart = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
			// The day's volumes are absent because we never listed it — not because the radar was down.
			var r = Run(Volumes(TimeSpan.FromMinutes(5), Now.AddMinutes(-3), dayStart, dayStart.AddDays(1)), known);
			Assert.Empty(r.Holes);
			Assert.Null(r.Days.Single(d => d.Day == missing).UpFraction);
			Assert.Equal(1.0, r.UpFraction);
		}

		[Fact]
		public void AListedEmptyDayIsDownAllDay()
		{
			var dayStart = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
			var r = Run(Volumes(TimeSpan.FromMinutes(5), Now.AddMinutes(-3), dayStart, dayStart.AddDays(1)));
			Assert.Equal(0.0, r.Days.Single(d => d.Day == new DateOnly(2026, 9, 10)).UpFraction!.Value, 2);
		}

		[Fact]
		public void NoDataAtAllIsOneOngoingOutageOverTheWholeWindow()
		{
			var r = Run(Array.Empty<DateTimeOffset>());
			var hole = Assert.Single(r.Holes);
			Assert.True(hole.Ongoing);
			Assert.Equal(WindowStart, hole.StartUtc);
			Assert.Equal(0.0, r.UpFraction);
		}

		// ── the service's day cache ──

		private sealed class FakeLister : IArchiveVolumeLister
		{
			public int Calls;
			public DateOnly? Fails;
			public Task<IReadOnlyList<DateTimeOffset>> ListVolumeTimesAsync(string siteId, DateOnly utcDay, CancellationToken ct)
			{
				Interlocked.Increment(ref Calls);
				if (utcDay == Fails) throw new System.Net.Http.HttpRequestException("boom");
				var start = new DateTimeOffset(utcDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
				IReadOnlyList<DateTimeOffset> times = Enumerable.Range(0, 288).Select(i => start.AddMinutes(5 * i)).Where(t => t <= Now).ToList();
				return Task.FromResult(times);
			}
		}

		private static RadarUptimeService Service(FakeLister lister, string dir) =>
			new(lister, NullLogger<RadarUptimeService>.Instance, dir, () => Now, TimeZoneInfo.Utc);

		[Fact]
		public async Task FinalDaysAreListedOnceAndTodayIsNotCachedToDisk()
		{
			var dir = Path.Combine(Path.GetTempPath(), "anvil-uptime-" + Guid.NewGuid().ToString("N"));
			var lister = new FakeLister();
			await Service(lister, dir).GetReportAsync("ktlx");
			Assert.Equal(31, lister.Calls); // 30 window days + the lead-in day

			// A fresh service (a relaunch) reads final days from disk: only today (partial) is listed again.
			var again = new FakeLister();
			var r = await Service(again, dir).GetReportAsync("KTLX");
			Assert.Equal(1, again.Calls);
			Assert.False(File.Exists(Path.Combine(dir, "KTLX", "20260926.txt")));
			Assert.Equal(1.0, r.UpFraction);
		}

		[Fact]
		public async Task AFailedListingLeavesThatDayUnknown()
		{
			var dir = Path.Combine(Path.GetTempPath(), "anvil-uptime-" + Guid.NewGuid().ToString("N"));
			var lister = new FakeLister { Fails = new DateOnly(2026, 9, 10) };
			var r = await Service(lister, dir).GetReportAsync("KTLX");
			Assert.Null(r.Days.Single(d => d.Day == new DateOnly(2026, 9, 10)).UpFraction);
			Assert.Empty(r.Holes);
			Assert.False(File.Exists(Path.Combine(dir, "KTLX", "20260910.txt"))); // retried next time
		}
	}
}
