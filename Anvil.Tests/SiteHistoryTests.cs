using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The Atlas history sections: the hourly VCP sample (<see cref="RadarScanPatternService"/>) and the rules
	/// <see cref="RadarSiteHistoryViewModel"/> adds on top — nothing fetched while the Atlas is closed, a site
	/// with no archive data reads as one line (not 0%), and a hole is linked to the message that explains it.
	/// </summary>
	public class SiteHistoryTests
	{
		private static readonly DateTimeOffset Now = new(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);

		// ── the sampler ──

		private sealed class VcpLister : IArchiveVolumeLister
		{
			public int Reads;
			public string? FailKey;
			public Task<IReadOnlyList<DateTimeOffset>> ListVolumeTimesAsync(string s, DateOnly d, CancellationToken ct) => throw new NotSupportedException();

			// Every 5 min; the day's first 12 hours on VCP 212, the rest on 35.
			public Task<IReadOnlyList<(DateTimeOffset Time, string Key)>> ListVolumesAsync(string siteId, DateOnly day, CancellationToken ct)
			{
				var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
				IReadOnlyList<(DateTimeOffset, string)> v = Enumerable.Range(0, 288)
					.Select(i => start.AddMinutes(5 * i)).Where(t => t <= Now)
					.Select(t => (t, $"{siteId}/{t:yyyyMMdd_HHmmss}/{(t.Hour < 12 ? 212 : 35)}")).ToList();
				return Task.FromResult(v);
			}

			public Task<int> ReadVcpAsync(string key, CancellationToken ct)
			{
				Interlocked.Increment(ref Reads);
				if (key == FailKey) throw new System.Net.Http.HttpRequestException("boom");
				return Task.FromResult(int.Parse(key[(key.LastIndexOf('/') + 1)..]));
			}
		}

		private static string TempDir() => Path.Combine(Path.GetTempPath(), "anvil-hist-" + Guid.NewGuid().ToString("N"));

		private static RadarScanPatternService Sampler(VcpLister lister, string dir) =>
			new(lister, NullLogger<RadarScanPatternService>.Instance, dir, () => Now);

		[Fact]
		public async Task OneReadPerHourAndSharesAreOfHours()
		{
			var lister = new VcpLister();
			var r = await Sampler(lister, TempDir()).GetReportAsync("KTLX");
			// 29 full days x 24 hours + today's 16 hours (00Z-15Z).
			Assert.Equal(29 * 24 + 16, lister.Reads);
			Assert.Equal(29 * 12 + 12, r.Shares.Single(s => s.Vcp == 212).Hours);
			Assert.Equal(29 * 12 + 4, r.Shares.Single(s => s.Vcp == 35).Hours);
			Assert.Equal(30, r.DaysDone);
		}

		[Fact]
		public async Task FinishedDaysAreReadOnceAndADayWithAFailedReadIsRetried()
		{
			var dir = TempDir();
			var failing = new VcpLister { FailKey = "KTLX/20260910_030000/212" };
			await Sampler(failing, dir).GetReportAsync("KTLX");

			var relaunch = new VcpLister();
			await Sampler(relaunch, dir).GetReportAsync("KTLX");
			// Only today (unfinished) and Sep 10 (had a failed read) are sampled again.
			Assert.Equal(16 + 24, relaunch.Reads);
		}

		// ── the view model ──

		private sealed class FakeUptime : IRadarUptimeService
		{
			public int Calls;
			public SiteUptimeReport? Report;
			public Task<SiteUptimeReport> GetReportAsync(string siteId, CancellationToken ct = default)
			{
				Calls++;
				return Task.FromResult(Report!);
			}
			public Task BackfillAsync(IReadOnlyList<string> ids, IProgress<string>? p = null, CancellationToken ct = default) => Task.CompletedTask;
		}

		private sealed class FakeMessages : IRadarMessageHistoryService
		{
			public IReadOnlyList<SiteMessage> Messages = Array.Empty<SiteMessage>();
			public Task<IReadOnlyList<SiteMessage>> GetHistoryAsync(IReadOnlyList<RadarSite> sites, CancellationToken ct = default) =>
				Task.FromResult(Messages);
		}

		private sealed class FakeScan : IRadarScanPatternService
		{
			public Task<SiteScanPatternReport> GetReportAsync(string siteId, IProgress<SiteScanPatternReport>? p = null, CancellationToken ct = default) =>
				Task.FromResult(new SiteScanPatternReport(siteId, new[] { new ScanPatternShare(35, 30), new ScanPatternShare(212, 10) }, 30, 30));
		}

		private sealed class InlineDispatcher : IDispatcher
		{
			public void Post(Action action) => action();
		}

		private static readonly RadarSite Ktlx = new("KTLX", "Norman", 35.3, -97.3);

		private static (RadarSiteHistoryViewModel vm, FakeUptime uptime, FakeMessages messages) Build()
		{
			var uptime = new FakeUptime();
			var messages = new FakeMessages();
			var vm = new RadarSiteHistoryViewModel(uptime, messages, new FakeScan(),
				new NonStandardVcpLog(NullLogger<NonStandardVcpLog>.Instance, TempDir()),
				() => new[] { Ktlx }, new InlineDispatcher());
			return (vm, uptime, messages);
		}

		private static SiteUptimeReport Report(IEnumerable<DateTimeOffset> times) =>
			UptimeCalculator.Compute("KTLX", times,
				Enumerable.Range(0, 32).Select(i => DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-i)).ToHashSet(),
				new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc);

		private static IEnumerable<DateTimeOffset> Every5Min(DateTimeOffset until)
		{
			for (var t = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero); t <= until; t = t.AddMinutes(5)) yield return t;
		}

		[Fact]
		public void NothingIsFetchedWhileTheAtlasIsClosed()
		{
			var (vm, uptime, _) = Build();
			uptime.Report = Report(Every5Min(Now));
			vm.Show(Ktlx);
			Assert.Equal(0, uptime.Calls);

			vm.IsActive = true;   // opening the Atlas loads the shown site
			Assert.Equal(1, uptime.Calls);
			vm.IsActive = false;
			vm.IsActive = true;   // re-opening on the SAME site doesn't re-fetch
			Assert.Equal(1, uptime.Calls);
		}

		[Fact]
		public void ASiteWithNoArchiveDataIsOneLineNotZeroPercent()
		{
			var (vm, uptime, _) = Build();
			uptime.Report = Report(Array.Empty<DateTimeOffset>());
			vm.IsActive = true;
			vm.Show(Ktlx);
			Assert.False(vm.HasUptime);
			Assert.Contains("No data from KTLX has reached the public archive", vm.UptimeEmptyText);
		}

		[Fact]
		public void AnOutageCarriesTheMessageThatExplainsIt()
		{
			var (vm, uptime, messages) = Build();
			var down = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);
			uptime.Report = Report(Every5Min(down));
			messages.Messages = new[]
			{
				new SiteMessage("KTLX", new RadarNwsMessage("TLX", "KOUN", down.AddMinutes(8),
					"KTLX HAS BEEN PUT INTO STANDBY. TECHNICIANS HOPE TO RECALIBRATE SATURDAY.")),
			};
			vm.IsActive = true;
			vm.Show(Ktlx);

			var hole = vm.HoleRows.First();
			Assert.StartsWith("Down 18 hr", hole.What);
			Assert.EndsWith(", ongoing", hole.What);
			Assert.True(hole.HasMessage);
			Assert.Equal("KTLX HAS BEEN PUT INTO STANDBY.", hole.MessageHint);
			Assert.Equal("1", vm.OutagesValue);
			Assert.True(vm.DayCells.Single(c => c.ToolTip.StartsWith("Fri, Sep 25", StringComparison.Ordinal)).HasMessage);
		}

		[Fact]
		public void ScanSharesAreOfSampledHours()
		{
			var (vm, uptime, _) = Build();
			uptime.Report = Report(Every5Min(Now));
			vm.IsActive = true;
			vm.Show(Ktlx);
			Assert.Equal(new[] { "35 sz-2 clear air · 75%", "212 sz-2 severe convective · 25%" }, vm.ScanShares.Select(s => s.Label));
			Assert.StartsWith("share of time", vm.ScanCaption);
		}

		[Theory]
		[InlineData(20, "20 min")]
		[InlineData(60, "1 hr")]
		[InlineData(807, "13 hr 27 min")]
		[InlineData(3000, "2 d 2 hr")]
		public void DurationsReadNaturally(int minutes, string expected) =>
			Assert.Equal(expected, RadarSiteHistoryViewModel.Duration(TimeSpan.FromMinutes(minutes)));
	}
}
