using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// The NWS radar status feeds (RadarNwsStatusService parsers) and the Atlas section's words + cooldown
	/// (RadarNwsStatusViewModel). The FTM text below is verbatim from the live ftm.txt of 2026-09-24.
	/// </summary>
	public class RadarNwsStatusTests
	{
		private static readonly DateTimeOffset Now = new(2026, 9, 24, 14, 35, 0, TimeSpan.Zero);

		private const string FtmFile =
			"NOUS62 KRAH 231129\n" +
			"FTMRAX\n" +
			"Message Date:  Sep 23 2026 11:29:13\n" +
			"THE KRAX WSR-88D RADAR WILL BE DOWN FOR MANDATORY SYSTEM MAINTENANCE TODAY FROM\n" +
			"1330Z UNTIL ABOUT 1730Z.\n" +
			"==================\n" +
			"NOUS61 KILN 241339\n" +
			"FTMCVG\n" +
			"MESSAGE DATE: SEP 24 2026 13:49Z\n" +
			"THE CINCINNATI TDWR (TCVG) DATA WILL BE UNAVAILABLE AT TIMES THROUGH\n" +
			"14:30Z FOR MAINTENANCE.\n" +
			"==================\n" +
			"NOUS61 KPBZ 241328\n" +
			"FTMPBZ\n" +
			"MESSAGE DATE:  AUG 12 2026 16:55 UTC\n" +
			"THE KPBZ WSR-88D WILL BE DOWN FOR MAITENANCE THROUGH 15Z SEPTEMBER\n" +
			"24TH, 2026.\n" +
			"MILCAREK\n" +
			"==================\n" +
			"NOUS63 KLOT 231551 RRA\n" +
			"FTMORD\n" +
			"Message Date:  Sep 23 2026 15:51:00\n" +
			"TORD back in service.\n" +
			"==================\n" +
			"garbage block with no header\n" +
			"==================\n";

		// ── FTM parsing ─────────────────────────────────────────────────────────────────────────

		[Fact]
		public void Messages_ParseEveryHeaderedBlock_NewestFirst()
		{
			var messages = RadarNwsStatusService.ParseMessages(FtmFile, Now);
			Assert.Equal(new[] { "CVG", "PBZ", "ORD", "RAX" }, messages.Select(m => m.Code));
		}

		[Fact]
		public void Message_TimeIsTheWmoHeader_NotTheMessageDateLine()
		{
			// KPBZ re-sent an Aug 12 notice on the 24th: the header (24 13:28Z) is when it was current.
			var pbz = RadarNwsStatusService.ParseMessages(FtmFile, Now).Single(m => m.Code == "PBZ");
			Assert.Equal(new DateTimeOffset(2026, 9, 24, 13, 28, 0, TimeSpan.Zero), pbz.IssuedUtc);
			Assert.Equal("KPBZ", pbz.Office);
		}

		[Fact]
		public void Message_DropsDateLine_AndReflowsTheTeletypeWrap()
		{
			var rax = RadarNwsStatusService.ParseMessages(FtmFile, Now).Single(m => m.Code == "RAX");
			Assert.Equal("THE KRAX WSR-88D RADAR WILL BE DOWN FOR MANDATORY SYSTEM MAINTENANCE TODAY FROM 1330Z UNTIL ABOUT 1730Z.", rax.Text);
		}

		[Fact]
		public void Message_CorrectionSuffixOnHeader_StillParses() =>
			Assert.Contains(RadarNwsStatusService.ParseMessages(FtmFile, Now), m => m.Code == "ORD");

		[Fact]
		public void Message_DayAfterToday_IsLastMonth()
		{
			// On Oct 1, a header dated the 30th is Sep 30 — ddhhmm carries no month.
			var oct1 = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero);
			var m = RadarNwsStatusService.ParseMessages("NOUS61 KBGM 302300\nFTMBGM\nTEST\n", oct1).Single();
			Assert.Equal(new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), m.IssuedUtc);
		}

		[Fact]
		public void Messages_CrlfFile_ParsesTheSame() =>
			Assert.Equal(4, RadarNwsStatusService.ParseMessages(FtmFile.Replace("\n", "\r\n"), Now).Count);

		// ── Which site a 3-letter FTM is about ──────────────────────────────────────────────────

		private static readonly (string Id, bool IsTdwr)[] Sites =
		{
			("KCVG", false), ("TCVG", true), ("KRAX", false), ("TRDU", true), ("KORD", false), ("TORD", true),
			("TJUA", false), ("TSJU", true), ("KPBZ", false),
		};

		private static RadarNwsMessage Msg(string code, string text) => new(code, "KXXX", Now, text);

		[Fact]
		public void Resolve_SingleCandidate() =>
			Assert.Equal("KRAX", RadarNwsStatusService.ResolveSiteId(Msg("RAX", "RADAR DOWN"), Sites));

		[Fact]
		public void Resolve_BodyNamesTheSite() =>
			Assert.Equal("TCVG", RadarNwsStatusService.ResolveSiteId(Msg("CVG", "THE CINCINNATI TDWR (TCVG) DATA..."), Sites));

		[Fact]
		public void Resolve_BodySaysTdwr_WithoutAnId() =>
			Assert.Equal("TORD", RadarNwsStatusService.ResolveSiteId(Msg("ORD", "THE CHICAGO TDWR IS DOWN"), Sites));

		[Fact]
		public void Resolve_NoHint_PrefersTheNexrad() =>
			Assert.Equal("KORD", RadarNwsStatusService.ResolveSiteId(Msg("ORD", "RADAR WILL BE DOWN"), Sites));

		// TJUA starts with T but is a WSR-88D: the network decides, never the first letter.
		[Fact]
		public void Resolve_ByNetwork_NotFirstLetter() =>
			Assert.Equal("TJUA", RadarNwsStatusService.ResolveSiteId(Msg("JUA", "SAN JUAN RADAR DOWN"), Sites));

		[Fact]
		public void Resolve_UnknownCode_IsNull() =>
			Assert.Null(RadarNwsStatusService.ResolveSiteId(Msg("XYZ", "PROFILER"), Sites));

		// ── Stations feed ───────────────────────────────────────────────────────────────────────

		private const string StationsJson = """
			{ "type": "FeatureCollection", "features": [
			  { "properties": { "id": "KSRX", "stationType": "WSR-88D",
			      "latency": { "levelTwoLastReceivedTime": "2026-09-24T14:31:48+00:00" },
			      "rda": { "properties": { "status": "Operate", "operabilityStatus": "RDA - Maintenance Action Mandatory",
			          "alarmSummary": "Tower/Utilities|Transmitter", "generatorState": "Utility PWR Available" } } } },
			  { "properties": { "id": "TOKC", "stationType": "TDWR",
			      "rda": { "properties": { "status": "Operate", "operabilityStatus": "RDA - On-line", "alarmSummary": "No Alarms" } } } },
			  { "properties": { "id": "RODN", "stationType": "WSR-88D", "rda": null } }
			] }
			""";

		[Fact]
		public void Stations_ParseRdaAndLatency()
		{
			var s = RadarNwsStatusService.ParseStations(StationsJson);
			Assert.Equal(3, s.Count);
			Assert.Equal("RDA - Maintenance Action Mandatory", s["ksrx"].Operability); // case-insensitive keys
			Assert.Equal(new DateTimeOffset(2026, 9, 24, 14, 31, 48, TimeSpan.Zero), s["KSRX"].LevelTwoLastReceivedUtc);
			Assert.Null(s["TOKC"].GeneratorState);
		}

		// A station with no rda block is still LISTED — "reports nothing" differs from "NWS doesn't know it".
		[Fact]
		public void Station_WithoutRda_IsListedWithNulls()
		{
			var rodn = RadarNwsStatusService.ParseStations(StationsJson)["RODN"];
			Assert.Null(rodn.Status);
			Assert.Equal(RadarNwsLevel.Unknown, RadarNwsStatusViewModel.LevelOf(rodn));
		}

		// ── Words + level ───────────────────────────────────────────────────────────────────────

		private static RadarNwsStation Station(string? status, string? operability) =>
			new("KXXX", status, operability, null, null, null);

		[Theory]
		[InlineData("Operate", "RDA - On-line", RadarNwsLevel.Ok)]
		[InlineData("Operate", "RDA - Maintenance Action Mandatory", RadarNwsLevel.Degraded)]
		[InlineData("Operate", "RDA - Maintenance Action Required", RadarNwsLevel.Degraded)]
		[InlineData("Start-Up", "RDA - On-line", RadarNwsLevel.Degraded)]
		[InlineData("Operate", "RDA - Inoperable", RadarNwsLevel.Down)]
		public void Level(string status, string operability, RadarNwsLevel expected) =>
			Assert.Equal(expected, RadarNwsStatusViewModel.LevelOf(Station(status, operability)));

		[Fact]
		public void StateWords_StripRdaPrefix_SentenceCase() =>
			Assert.Equal("Operate · Maintenance action mandatory",
				RadarNwsStatusViewModel.StateWords(Station("Operate", "RDA - Maintenance Action Mandatory")));

		[Theory]
		[InlineData("No Alarms", "None")]
		[InlineData("Tower/Utilities|Transmitter", "Tower/utilities · Transmitter")]
		[InlineData(null, "Not reported")]
		public void AlarmWords(string? summary, string expected) =>
			Assert.Equal(expected, RadarNwsStatusViewModel.AlarmWords(summary));

		[Theory]
		[InlineData("Utility PWR Available", "Utility power")]
		[InlineData("Switched to Auxiliary Power|Generator On", "On auxiliary power · Generator running")]
		[InlineData(null, "Not reported")]
		public void PowerWords(string? state, string expected) =>
			Assert.Equal(expected, RadarNwsStatusViewModel.PowerWords(state));

		// ── The view model: detail, failure halves, cooldown ────────────────────────────────────

		private sealed class FakeService : IRadarNwsStatusService
		{
			public int Calls;
			public bool FailStations;
			public bool FailMessages;
			public string Ftm = FtmFile;

			public Task<IReadOnlyDictionary<string, RadarNwsStation>> GetStationsAsync(CancellationToken ct = default)
			{
				Calls++;
				return FailStations
					? Task.FromException<IReadOnlyDictionary<string, RadarNwsStation>>(new HttpRequestExceptionStub())
					: Task.FromResult<IReadOnlyDictionary<string, RadarNwsStation>>(RadarNwsStatusService.ParseStations(StationsJson));
			}

			public Task<IReadOnlyList<RadarNwsMessage>> GetMessagesAsync(CancellationToken ct = default) =>
				FailMessages
					? Task.FromException<IReadOnlyList<RadarNwsMessage>>(new HttpRequestExceptionStub())
					: Task.FromResult<IReadOnlyList<RadarNwsMessage>>(RadarNwsStatusService.ParseMessages(Ftm, Now));
		}

		private sealed class HttpRequestExceptionStub : Exception { }

		private static (RadarNwsStatusViewModel Vm, FakeService Service, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance) Build()
		{
			var now = Now;
			var service = new FakeService();
			var sites = new (string, bool)[] { ("KSRX", false), ("TOKC", true), ("KRAX", false), ("TCVG", true), ("KCVG", false), ("KCRI", false) };
			var vm = new RadarNwsStatusViewModel(service, () => sites, () => now);
			return (vm, service, () => now, d => now += d);
		}

		private static RadarSiteRow Row(string id) => new(new RadarSite(id, id, 35, -97));

		[Fact]
		public async Task Detail_StationAndMessage()
		{
			var (vm, _, _, _) = Build();
			await vm.CheckAsync();

			var srx = vm.DetailFor(Row("KSRX"));
			Assert.True(srx.ShowTable);
			Assert.Equal(RadarNwsLevel.Degraded, srx.Level);
			Assert.Equal("Tower/utilities · Transmitter", srx.AlarmsText);
			Assert.False(srx.HasMessage);
			Assert.Equal("No NWS messages in the last 24 hours.", srx.NoMessageText);

			var rax = vm.DetailFor(Row("KRAX"));
			Assert.False(rax.HasStation); // not in the stations fixture, but has a message → still a table
			Assert.True(rax.ShowTable);
			Assert.StartsWith("THE KRAX WSR-88D", rax.Latest!.Text);
			Assert.StartsWith("NWS RAH · ", rax.Latest.Header);
		}

		[Fact]
		public async Task Detail_SiteNwsDoesntList_IsTheOneLine()
		{
			var (vm, _, _, _) = Build();
			await vm.CheckAsync();
			var cri = vm.DetailFor(Row("KCRI"));
			Assert.False(cri.ShowTable);
			Assert.Equal("NWS doesn't report status for this site.", cri.EmptyText);
		}

		[Fact]
		public async Task Detail_OlderMessagesFoldUnderTheNewest()
		{
			var (vm, service, _, _) = Build();
			service.Ftm = "NOUS62 KRAH 231129\nFTMRAX\nDOWN\n====\nNOUS62 KRAH 231729\nFTMRAX\nBACK UP\n====\n";
			await vm.CheckAsync();
			var rax = vm.DetailFor(Row("KRAX"));
			Assert.Equal("BACK UP", rax.Latest!.Text);
			Assert.Equal("DOWN", Assert.Single(rax.Earlier).Text);
			Assert.Equal("1 earlier message", rax.EarlierLabel);
		}

		[Fact]
		public async Task Cooldown_BlocksAReCheck_ForFiveMinutes()
		{
			var (vm, service, _, advance) = Build();
			await vm.CheckAsync();
			Assert.Equal(1, service.Calls);
			Assert.False(vm.CanCheck);
			Assert.Equal("Re-check in 5:00", vm.CheckButtonText);

			advance(TimeSpan.FromSeconds(116));
			Assert.Equal("Re-check in 3:04", vm.CheckButtonText);
			await vm.CheckAsync(); // swallowed by the cooldown
			Assert.Equal(1, service.Calls);

			advance(TimeSpan.FromSeconds(184));
			Assert.True(vm.CanCheck);
			Assert.Equal("Re-check", vm.CheckButtonText);
			await vm.CheckAsync();
			Assert.Equal(2, service.Calls);
		}

		// The cooldown protects NWS, not our success rate: a failed attempt still starts it.
		[Fact]
		public async Task Cooldown_StartsOnAFailedAttemptToo()
		{
			var (vm, service, _, _) = Build();
			service.FailStations = service.FailMessages = true;
			await vm.CheckAsync();
			Assert.False(vm.CanCheck);
			Assert.Equal("couldn't reach NWS", vm.CheckedText);
			Assert.Equal("Couldn't reach NWS.", vm.DetailFor(Row("KSRX")).EmptyText);
		}

		// Each feed keeps its own last good answer — a failed FTM fetch mustn't blank the station rows.
		[Fact]
		public async Task FailedHalf_KeepsItsLastGoodAnswer()
		{
			var (vm, service, _, advance) = Build();
			await vm.CheckAsync();
			advance(RadarNwsStatusViewModel.Cooldown);
			service.FailMessages = true;
			await vm.CheckAsync();

			Assert.Equal("checked just now · couldn't get outage messages", vm.CheckedText);
			Assert.StartsWith("THE KRAX", vm.DetailFor(Row("KRAX")).Latest!.Text);
			Assert.True(vm.DetailFor(Row("KSRX")).HasStation);
		}
	}
}
