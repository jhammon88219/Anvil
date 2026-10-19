using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Anvil.Models;
using Anvil.Services;
using Anvil.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Anvil.Tests.TemporalWindowPersistenceTests;

namespace Anvil.Tests
{
	/// <summary>
	/// The live-update subsystem (Settings → Live Data): which products wake which feed, the polling floors, IEM's room
	/// JSON, the per-feed schedule (live = no polling, a trigger's re-check run, the safety re-check), the hub's state
	/// rules (drops wake the feeds, stale runs are ignored, duplicates dropped), NWWS-OI's parsing and its no-read-ahead
	/// XML reader, and the activity expander's source line.
	/// </summary>
	public class LiveFeedTests
	{
		private sealed class InlineDispatcher : IDispatcher
		{
			public void Post(Action action) => action();
		}

		private static readonly IDispatcher Inline = new InlineDispatcher();

		// ── Routing + polling floors ──

		[Theory]
		[InlineData("SVROUN", LiveFeedKind.Warnings)]
		[InlineData("TORFWD", LiveFeedKind.Warnings)]
		[InlineData("SVSOUN", LiveFeedKind.Warnings)]
		[InlineData("FFWLZK", LiveFeedKind.Warnings)]
		[InlineData("SWOMCD", LiveFeedKind.Discussions)]
		[InlineData("FFGMPD", LiveFeedKind.Discussions)]
		[InlineData("LSROUN", LiveFeedKind.StormReports)]
		[InlineData("SEL5", LiveFeedKind.Watches)]
		[InlineData("WOU5", LiveFeedKind.Watches)]
		[InlineData("WCNOUN", LiveFeedKind.Watches)]
		[InlineData("FFAOUN", LiveFeedKind.Watches)]
		[InlineData("TCVAT1", LiveFeedKind.Tropical)]
		[InlineData("tcpat2", LiveFeedKind.Tropical)]
		public void Products_wake_their_feed(string awips, LiveFeedKind kind) =>
			Assert.Equal(kind, LiveFeedKinds.KindOfProduct(awips));

		[Theory]
		[InlineData("TAFINT")]
		[InlineData("AFDOUN")]
		[InlineData("SWOFOO")] // SPC's "SWO" prefix alone wakes nothing — only the whole outlook ids do
		[InlineData("")]
		[InlineData(null)]
		public void Other_products_wake_nothing(string? awips) => Assert.Null(LiveFeedKinds.KindOfProduct(awips));

		[Fact]
		public void Storm_cells_are_never_pushed() => Assert.False(LiveFeedKinds.IsPushed(LiveFeedKind.StormCells));

		[Theory]
		[InlineData(LiveFeedKind.Warnings, null, 30)]
		[InlineData(LiveFeedKind.Warnings, 5, 15)]       // below the floor → the floor
		[InlineData(LiveFeedKind.Warnings, 45, 45)]
		[InlineData(LiveFeedKind.Tropical, 60, 120)]
		[InlineData(LiveFeedKind.StormCells, 99999, 1800)] // above the ceiling → 30 min
		public void Poll_intervals_are_clamped(LiveFeedKind kind, int? stored, int expected) =>
			Assert.Equal(TimeSpan.FromSeconds(expected), LiveFeedKinds.PollOf(kind, stored));

		[Fact]
		public void Poll_row_stores_null_for_the_default_and_clamps()
		{
			var settings = new AppSettings();
			var hub = Hub(settings, _ => null);
			var vm = new LiveDataViewModel(hub, settings, Inline);
			var warnings = vm.Polling[0];
			Assert.Equal(LiveFeedKind.Warnings, warnings.Kind);

			warnings.Seconds = 5;
			Assert.Equal(15, warnings.Seconds);
			Assert.Equal(15, settings.PollWarningsSeconds);

			warnings.Seconds = 30; // the default
			Assert.Null(settings.PollWarningsSeconds);

			warnings.Seconds = 90;
			vm.RestoreDefaults();
			Assert.Null(settings.PollWarningsSeconds);
		}

		// ── IEM's room JSON ──

		[Fact]
		public void Iem_product_id_parses()
		{
			var p = IemBotSource.ParseProductId("202610191704-KOUN-WUUS54-SVROUN-AAA", DateTimeOffset.UnixEpoch)!;
			Assert.Equal("SVROUN", p.AwipsId);
			Assert.Equal("KOUN", p.Office);
			Assert.Equal(new DateTimeOffset(2026, 10, 19, 17, 4, 0, TimeSpan.Zero), p.Issued);
			Assert.Null(IemBotSource.ParseProductId("", DateTimeOffset.UnixEpoch));
			Assert.Null(IemBotSource.ParseProductId("garbage", DateTimeOffset.UnixEpoch));
		}

		private const string RoomJson = """
			{"messages": [
			 {"seqnum": 9, "ts": "2026-10-19 12:19:04", "author": "iembot", "product_id": "202610191218-KOUN-WUUS54-SVROUN", "message": ""},
			 {"seqnum": 11, "ts": "2026-10-19 12:19:04", "author": "iembot", "product_id": "", "message": "daily summary"},
			 {"seqnum": 14, "ts": "2026-10-19 12:20:21", "author": "iembot", "product_id": "202610191219-KWNS-ACUS11-SWOMCD", "message": ""}
			]}
			""";

		[Fact]
		public void Iem_room_reports_only_whats_new_and_skips_blank_ids()
		{
			var sink = new RecordingSink();
			Assert.Equal(14, IemBotSource.Read(RoomJson, after: 10, sink));
			var only = Assert.Single(sink.Products);
			Assert.Equal("SWOMCD", only.AwipsId);
			Assert.Equal(new DateTimeOffset(2026, 10, 19, 12, 20, 21, TimeSpan.Zero), only.Received);
		}

		[Fact]
		public void Iem_first_ask_only_sets_the_starting_number() =>
			Assert.Equal(14, IemBotSource.Read(RoomJson, after: 0, sink: null));

		// ── One feed's schedule ──

		[Fact]
		public void Polling_schedule_is_the_interval_and_a_clock_time()
		{
			var status = new FeedStatus(LayerOrder.Warnings, "Warnings", null);
			var channel = LiveFeedChannel.Polling(LiveFeedKind.Warnings, TimeSpan.FromSeconds(30));
			Assert.Equal(TimeSpan.FromSeconds(30), channel.Schedule(status));
			Assert.Equal("30 s", status.Every);
			Assert.Matches(@"^\d{1,2}:\d\d:\d\d", status.NextText);
		}

		[Fact]
		public void Live_schedule_runs_the_rechecks_after_a_trigger_then_the_safety()
		{
			var live = true;
			var status = new FeedStatus(LayerOrder.Warnings, "Warnings", null);
			var channel = new LiveFeedChannel(LiveFeedKind.Warnings, () => live, () => TimeSpan.FromSeconds(30));

			Assert.Equal(LiveFeedKinds.LiveSafetyInterval, channel.Schedule(status)); // no news: only the safety net
			Assert.Equal("live", status.Every);
			Assert.Equal("when issued", status.NextText);

			channel.Trigger();
			foreach (var gap in LiveFeedKinds.ChaseDelays) { Assert.Equal(gap, channel.Schedule(status)); }
			Assert.Equal(LiveFeedKinds.LiveSafetyInterval, channel.Schedule(status));

			live = false; // the source dropped: back to polling, and the row says so
			Assert.Equal(TimeSpan.FromSeconds(30), channel.Schedule(status));
			Assert.Equal("30 s", status.Every);
			Assert.NotEqual("when issued", status.NextText);
		}

		[Fact]
		public void Storm_cells_poll_even_while_live()
		{
			var status = new FeedStatus(LayerOrder.Cells, "Storm cells", null);
			var channel = new LiveFeedChannel(LiveFeedKind.StormCells, () => true, () => TimeSpan.FromSeconds(60));
			Assert.False(channel.IsLive);
			Assert.Equal(TimeSpan.FromSeconds(60), channel.Schedule(status));
		}

		[Fact]
		public async Task A_trigger_cuts_the_wait_short()
		{
			var channel = new LiveFeedChannel(LiveFeedKind.Warnings, () => true, () => TimeSpan.FromSeconds(30));
			var wait = channel.WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			await Task.Delay(50);
			Assert.False(wait.IsCompleted);
			channel.Trigger();
			Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(2000)));
		}

		[Fact]
		public async Task A_wake_mid_cycle_is_kept_for_the_next_wait()
		{
			var channel = new LiveFeedChannel(LiveFeedKind.Warnings, () => true, () => TimeSpan.FromSeconds(30));
			channel.Wake(); // nobody waiting — the feed is mid-fetch
			var wait = channel.WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(2000)));
			// …and only once: the wait after that waits.
			var next = channel.WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			await Task.Delay(50);
			Assert.False(next.IsCompleted);
			channel.Wake();
			await next;
		}

		// ── The hub ──

		private sealed class FakeSource : ILiveProductSource
		{
			public ILiveSourceSink? Sink;

			public async Task RunAsync(ILiveSourceSink sink, CancellationToken ct)
			{
				Sink = sink;
				await Task.Delay(Timeout.Infinite, ct);
			}
		}

		private static LiveFeedHub Hub(AppSettings settings, Func<string, ILiveProductSource?> sourceFor) =>
			new(Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => settings }), null, sourceFor, NullLogger.Instance);

		private static async Task<ILiveSourceSink> SinkOf(FakeSource source)
		{
			for (var i = 0; i < 200 && source.Sink is null; i++) { await Task.Delay(10); }
			return source.Sink ?? throw new TimeoutException("the source never ran");
		}

		[Fact]
		public async Task Connected_source_makes_pushed_feeds_live_and_a_drop_wakes_them()
		{
			var source = new FakeSource();
			var hub = Hub(new AppSettings(), _ => source);
			hub.Start();
			Assert.Equal(LiveLinkState.Connecting, hub.State);
			var sink = await SinkOf(source);
			var warnings = hub.Channel(LiveFeedKind.Warnings);
			Assert.False(warnings.IsLive);

			sink.Connected();
			Assert.Equal(LiveLinkState.Connected, hub.State);
			Assert.True(warnings.IsLive);
			Assert.False(hub.Channel(LiveFeedKind.StormCells).IsLive);

			var wait = warnings.WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			sink.Down("IEM isn't answering");
			Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(2000))); // the catch-up fetch
			Assert.Equal(LiveLinkState.Down, hub.State);
			Assert.False(warnings.IsLive);
			Assert.Equal("IEM isn't answering", hub.Snapshot.Reason);
			hub.Shutdown();
		}

		[Fact]
		public async Task A_product_wakes_only_its_feed_once()
		{
			var source = new FakeSource();
			var hub = Hub(new AppSettings(), _ => source);
			hub.Start();
			var sink = await SinkOf(source);
			sink.Connected();

			var reports = hub.Channel(LiveFeedKind.StormReports).WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			var warnings = hub.Channel(LiveFeedKind.Warnings).WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			var product = new LiveProduct("p1", "SVROUN", "KOUN", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
			sink.Product(product);
			Assert.Same(warnings, await Task.WhenAny(warnings, Task.Delay(2000)));
			Assert.False(reports.IsCompleted);
			Assert.Equal(product, hub.Snapshot.LastProduct);

			// The same id again (a duplicate relay) wakes nothing.
			var again = hub.Channel(LiveFeedKind.Warnings).WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			sink.Product(product);
			await Task.Delay(50);
			Assert.False(again.IsCompleted);
			hub.Shutdown();
		}

		[Fact]
		public async Task A_stale_run_is_ignored_after_a_source_switch()
		{
			var settings = new AppSettings();
			var first = new FakeSource();
			var second = new FakeSource();
			var hub = Hub(settings, token => token == LiveSources.Iem ? first : second);
			hub.Start();
			var oldSink = await SinkOf(first);

			settings.LiveSource = LiveSources.Nwws;
			hub.Restart();
			await SinkOf(second);
			oldSink.Connected(); // the cancelled IEM run reporting late
			Assert.Equal(LiveLinkState.Connecting, hub.State);
			Assert.Equal(LiveSources.Nwws, hub.Snapshot.Source);
			hub.Shutdown();
		}

		[Fact]
		public void Polling_only_runs_no_source_and_nwws_without_an_account_waits()
		{
			var settings = new AppSettings { LiveSource = LiveSources.Poll };
			var hub = Hub(settings, _ => throw new InvalidOperationException("no source should be built"));
			hub.Start();
			Assert.Equal(LiveLinkState.Off, hub.State);

			// The real factory with no saved password → no source.
			var nwws = new LiveFeedHub(Null<ISettingsService>.Create(new() { ["get_Settings"] = _ => new AppSettings { LiveSource = LiveSources.Nwws } }),
				null, null, NullLogger.Instance);
			nwws.Start();
			Assert.Equal(LiveLinkState.NeedsAccount, nwws.State);
		}

		[Fact]
		public async Task A_numbering_gap_counts_and_wakes_the_feeds()
		{
			var source = new FakeSource();
			var hub = Hub(new AppSettings(), _ => source);
			hub.Start();
			var sink = await SinkOf(source);
			sink.Connected();
			var wait = hub.Channel(LiveFeedKind.Watches).WaitAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
			sink.Gap(3);
			Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(2000)));
			Assert.Equal(3, hub.Snapshot.Missed);
			hub.Shutdown();
		}

		// ── NOAA Weather Wire ──

		[Fact]
		public void Nwws_product_and_sequence_parse()
		{
			var x = XElement.Parse("<x xmlns='nwws-oi' cccc='KOUN' ttaaii='WUUS54' issue='2026-10-19T17:04:00Z' awipsid='svroun' id='14425.39811'>text</x>");
			var p = NwwsOiSource.ProductOf(x, DateTimeOffset.UnixEpoch)!;
			Assert.Equal("SVROUN", p.AwipsId);
			Assert.Equal("KOUN", p.Office);
			Assert.Equal(new DateTimeOffset(2026, 10, 19, 17, 4, 0, TimeSpan.Zero), p.Issued);
			Assert.True(NwwsOiSource.TryParseSequence(p.ProductId, out var process, out var sequence));
			Assert.Equal(14425, process);
			Assert.Equal(39811, sequence);
			Assert.False(NwwsOiSource.TryParseSequence("nodot", out _, out _));
		}

		// A stream that hands over its bytes, then never answers again — like a server waiting for our reply.
		private sealed class StallingStream : Stream
		{
			private readonly byte[] _data;
			private int _at;

			public StallingStream(string text) => _data = Encoding.UTF8.GetBytes(text);

			public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
			{
				if (_at >= _data.Length) { await Task.Delay(Timeout.Infinite, ct); }
				var n = Math.Min(buffer.Length, _data.Length - _at);
				_data.AsMemory(_at, n).CopyTo(buffer);
				_at += n;
				return n;
			}

			public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
				ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("sync read");
			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}

		[Fact]
		public async Task Nwws_reader_returns_an_element_without_reading_past_it()
		{
			// ⚠️ The server sends nothing after <proceed/> (or a features list) until we answer: a reader that peeked one
			// node further would hang here forever.
			var stream = new StallingStream("<?xml version='1.0'?><stream:stream xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>"
				+ "<stream:features><starttls xmlns='urn:ietf:params:xml:ns:xmpp-tls'><required/></starttls></stream:features>");
			var r = XmlReader.Create(stream, new XmlReaderSettings { Async = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
			var read = Task.Run(async () =>
			{
				while (await r.ReadAsync())
				{
					if (r.NodeType == XmlNodeType.Element && r.Depth == 1) { return await NwwsOiSource.ReadElementAsync(r); }
				}
				throw new IOException("ended");
			});
			Assert.Same(read, await Task.WhenAny(read, Task.Delay(3000)));
			var features = await read;
			Assert.Equal(XName.Get("features", "http://etherx.jabber.org/streams"), features.Name);
			Assert.NotNull(features.Element(XName.Get("starttls", "urn:ietf:params:xml:ns:xmpp-tls"))?.Element(XName.Get("required", "urn:ietf:params:xml:ns:xmpp-tls")));
		}

		// ── ForeCast's outlooks ──

		[Theory]
		[InlineData("SWODY1")]
		[InlineData("SWODY2")]
		[InlineData("SWODY3")]
		[InlineData("SWOD48")]
		[InlineData("FWDDY1")]
		[InlineData("FWDDY2")]
		[InlineData("FWDD38")]
		public void Outlook_products_wake_the_outlooks(string awips) =>
			Assert.Equal(LiveFeedKind.Outlooks, LiveFeedKinds.KindOfProduct(awips));

		[Fact]
		public void An_mcd_is_still_a_discussion_not_an_outlook() =>
			Assert.Equal(LiveFeedKind.Discussions, LiveFeedKinds.KindOfProduct("SWOMCD"));

		[Theory]
		[InlineData("SWODY1", "day1-categorical,day1-tornado,day1-wind,day1-hail")]
		[InlineData("SWODY3", "day3-categorical,day3-probabilistic")]
		[InlineData("SWOD48", "day4-probabilistic,day5-probabilistic,day6-probabilistic,day7-probabilistic,day8-probabilistic")]
		[InlineData("FWDDY1", "day1-fire")]
		[InlineData("FWDD38", "day3-fire,day4-fire,day5-fire,day6-fire,day7-fire,day8-fire")]
		[InlineData("SVROUN", "")]
		public void Each_outlook_product_wakes_only_its_own_files(string awips, string ids) =>
			Assert.Equal(ids, string.Join(",", SpcOutlookCatalog.IdsForProduct(awips)));

		[Fact]
		public void Outlook_trigger_scopes_its_files_until_each_is_caught()
		{
			var status = new FeedStatus(LayerOrder.Outlook, "Outlooks", null);
			var channel = new LiveFeedChannel(LiveFeedKind.Outlooks, () => true, () => TimeSpan.FromMinutes(5));
			Assert.Null(channel.Scope()); // no trigger: a poll / safety cycle fetches everything

			channel.Trigger(new[] { "day1-categorical", "day1-tornado" });
			Assert.Equal(new[] { "day1-categorical", "day1-tornado" }, channel.Scope()!.OrderBy(k => k));
			Assert.Equal(LiveFeedKinds.OutlookChaseDelays[0], channel.Schedule(status)); // the LONG run

			channel.Caught(new[] { "day1-categorical" }); // one file landed: only the other is re-asked
			Assert.Equal(new[] { "day1-tornado" }, channel.Scope());
			Assert.Equal(LiveFeedKinds.OutlookChaseDelays[1], channel.Schedule(status));

			channel.Caught(new[] { "day1-tornado" }); // all caught: the run ends early
			Assert.Null(channel.Scope());
			Assert.Equal(LiveFeedKinds.LiveSafetyInterval, channel.Schedule(status));
		}

		[Fact]
		public void A_catch_up_wake_fetches_every_outlook_file()
		{
			var channel = new LiveFeedChannel(LiveFeedKind.Outlooks, () => true, () => TimeSpan.FromMinutes(5));
			channel.Trigger(new[] { "day1-fire" });
			channel.Wake(); // the source dropped
			Assert.Null(channel.Scope());                            // this cycle: everything…
			Assert.Equal(new[] { "day1-fire" }, channel.Scope());   // …then the run carries on for its file
		}

		[Fact]
		public void The_outlook_chase_outlasts_the_fire_maps_lag()
		{
			// Measured 2026-10-19: the fire MapServer ~11.5 min after FWDDY1. The run must reach well past it.
			var total = LiveFeedKinds.OutlookChaseDelays.Aggregate(TimeSpan.Zero, (a, d) => a + d);
			Assert.True(total >= TimeSpan.FromMinutes(15), $"outlook re-checks end after {total}");
			Assert.Same(LiveFeedKinds.ChaseDelays, LiveFeedKinds.ChaseDelaysOf(LiveFeedKind.Warnings));
		}

		[Theory]
		[InlineData("https://www.spc.noaa.gov/products/outlook/day1otlk_cat.lyr.geojson", "?anvil=")]
		[InlineData("https://x/MapServer/1/query?where=1%3D1&f=geojson", "&anvil=")]
		public void Cdn_bypass_adds_a_fresh_query(string url, string joint) =>
			Assert.StartsWith(url + joint, SpcOutlookService.BypassCdn(url));

		[Fact]
		public void One_feed_summary_names_it()
		{
			var outlooks = new FeedStatus(LayerOrder.Outlook, "Outlooks", Inline);
			var vm = new TemporalActivityViewModel(null, new[] { outlooks }, () => "hidden", _ => { });
			Assert.Equal("Outlooks starting", vm.Summary);
			outlooks.Succeeded();
			Assert.StartsWith("Outlooks current · last ", vm.Summary);
			outlooks.Failed();
			Assert.StartsWith("Outlooks failed at ", vm.Summary);
		}

		// ── The activity expander's source line ──

		[Fact]
		public void Source_line_words()
		{
			var at = DateTimeOffset.Now;
			Assert.StartsWith("Live via IEM · connected since ",
				LiveDataViewModel.LineOf(new LiveFeedSnapshot(LiveSources.Iem, LiveLinkState.Connected, at, null, null, 0), "IEM"));
			Assert.EndsWith("· polling until it's back",
				LiveDataViewModel.LineOf(new LiveFeedSnapshot(LiveSources.Iem, LiveLinkState.Down, at, "IEM isn't answering", null, 0), "IEM"));
			Assert.Equal("NOAA Weather Wire refused the sign-in · polling",
				LiveDataViewModel.LineOf(new LiveFeedSnapshot(LiveSources.Nwws, LiveLinkState.Down, at, "NOAA Weather Wire refused the sign-in", null, 0), "NOAA Weather Wire"));
			Assert.Equal("Polling only · Settings → Live Data",
				LiveDataViewModel.LineOf(new LiveFeedSnapshot(LiveSources.Poll, LiveLinkState.Off, null, null, null, 0), "Polling only"));
		}

		private sealed class RecordingSink : ILiveSourceSink
		{
			public readonly List<LiveProduct> Products = new();
			public void Connected() { }
			public void Down(string reason) { }
			public void Product(LiveProduct product) => Products.Add(product);
			public void Gap(int missed) { }
		}
	}
}
