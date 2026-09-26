using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// IEM message history: the month-scoped date rule, IEM's framing and "no match" answer, site resolution,
	/// and the final-month disk cache. The product text is copied from a real IEM response (2026-09-26).
	/// </summary>
	public class RadarMessageHistoryTests
	{
		private static string Block(string seq, string header, string ftm, string body) =>
			$"\u0001\n{seq} \n{header}\n{ftm}\n\nMESSAGE DATE: SEP 26 2026 0107 UTC\n\n{body}\n\n\u0003\n";

		private static readonly string August =
			Block("490", "NOUS64 KOUN 121526", "FTMTLX", "THE KTLX WSR-88D RADAR HAS RETURNED TO NORMAL OPERATING STATUS.\n\nPW/WFO OUN")
			+ Block("914", "NOUS64 KOUN 121340", "FTMTLX", "THE KTLX WSR-88D WILL BE DOWN FOR MAINTENANCE THROUGH ROUGHLY NOON \nTODAY.\n\nPW/WFO OUN");

		private static readonly string September =
			Block("192", "NOUS64 KOUN 260108", "FTMTLX", "DUE TO A MAPPING OFFSET IN KTLX RADAR DATA, KTLX HAS BEEN PUT INTO \nSTANDBY.\n\nDS/WFO OUN")
			+ Block("098", "NOUS64 KOUN 260015", "FTMOKC", "NOTE TO USERS: IN THE AFTERMATH OF THE KTLX OUTAGE THIS WEEK, IT HAS \nBEEN OBSERVED THAT THE LOCATION OF ECHOES FROM KTLX ARE NOW OFFSET.")
			+ Block("300", "NOUS63 KEAX 201500", "FTMMCI", "THE KANSAS CITY TDWR (TMCI) IS DOWN.")
			+ Block("301", "NOUS64 KOUN 311200", "FTMTLX", "IMPOSSIBLE DAY — SEPTEMBER HAS 30.");

		[Fact]
		public void TheMonthIsGivenSoHeaderDatesAreExact()
		{
			// Today's day-of-month (26) is AFTER the 12th, so the live 24-h rule would put this on Sep 12.
			var msgs = RadarMessageHistoryService.ParseMonth(August, 2026, 8);
			Assert.Equal(2, msgs.Count);
			Assert.Equal(new DateTimeOffset(2026, 8, 12, 15, 26, 0, TimeSpan.Zero), msgs[0].IssuedUtc);
			Assert.Equal("KOUN", msgs[0].Office);
			Assert.Equal("THE KTLX WSR-88D WILL BE DOWN FOR MAINTENANCE THROUGH ROUGHLY NOON TODAY.\nPW/WFO OUN", msgs[1].Text);
		}

		[Fact]
		public void ADayTheMonthDoesNotHaveIsSkipped() =>
			Assert.DoesNotContain(RadarMessageHistoryService.ParseMonth(September, 2026, 9), m => m.IssuedUtc.Day == 31 || m.Text.Contains("IMPOSSIBLE"));

		[Fact]
		public void IemsNoMatchAnswerIsAnEmptyMonth() =>
			Assert.Empty(RadarMessageHistoryService.ParseMonth("ERROR: Could not Find: FTMAAA,FTMZZZ", 2026, 9));

		// ── the service ──

		private static readonly DateTimeOffset Now = new(2026, 9, 26, 15, 0, 0, TimeSpan.Zero);

		private static readonly RadarSite[] Sites =
		{
			new("KTLX", "Norman", 35.3, -97.3),
			new("KMCI", "Kansas City", 39.3, -94.7),
			new("TMCI", "Kansas City TDWR", 39.5, -94.7, RadarSiteClass.Tdwr),
			new("TOKC", "Oklahoma City TDWR", 35.3, -97.6, RadarSiteClass.Tdwr),
		};

		private sealed class Stub : HttpMessageHandler
		{
			public int Requests;
			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
			{
				Interlocked.Increment(ref Requests);
				var q = request.RequestUri!.Query;
				var body = q.Contains("sdate=2026-08-01") ? August : q.Contains("sdate=2026-09-01") ? September : "ERROR: Could not Find: x";
				return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
			}
		}

		private static RadarMessageHistoryService Service(Stub stub, string dir) =>
			new(NullLogger<RadarMessageHistoryService>.Instance, new HttpClient(stub), dir, () => Now);

		[Fact]
		public async Task HistoryResolvesEachMessageToOneSiteNewestFirst()
		{
			var dir = Path.Combine(Path.GetTempPath(), "anvil-ftm-" + Guid.NewGuid().ToString("N"));
			var h = await Service(new Stub(), dir).GetHistoryAsync(Sites);
			// Aug 12 is outside the 31-day window (since Aug 26) and is dropped. The FTMOKC note is TOKC's
			// product but names KTLX, so it's listed under both — under KTLX as a mention filed under TOKC.
			Assert.Equal(new[] { "KTLX", "TOKC", "KTLX", "TMCI" }, h.Select(m => m.SiteId));
			var mention = h.Single(m => m.SiteId == "KTLX" && m.FiledUnderSiteId is not null);
			Assert.Equal("TOKC", mention.FiledUnderSiteId);
			Assert.Null(h.Single(m => m.SiteId == "TOKC").FiledUnderSiteId);
		}

		[Fact]
		public async Task AFinishedMonthIsFetchedOnceTheCurrentMonthEachTime()
		{
			var dir = Path.Combine(Path.GetTempPath(), "anvil-ftm-" + Guid.NewGuid().ToString("N"));
			var first = new Stub();
			await Service(first, dir).GetHistoryAsync(Sites);
			Assert.Equal(2, first.Requests); // August + September

			var relaunch = new Stub();
			await Service(relaunch, dir).GetHistoryAsync(Sites);
			Assert.Equal(1, relaunch.Requests); // August from disk; September (unfinished) re-asked
		}
	}
}
