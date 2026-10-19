using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>
	/// IEM's iembot feed as a live source: the JSON of weather.im's <c>botstalk</c> room (every product iembot relays),
	/// asked for everything AFTER the last message number seen, every <see cref="PollEvery"/>. No account.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE ROOM ENDPOINT: <c>https://weather.im/iembot-json/room/botstalk?seqnum=N</c> → <c>{"messages":[{seqnum, ts,
	/// author, product_id, message}]}</c>, the messages AFTER N (seqnum=0 = about the last two hours, checked 2026-10-19).
	/// <c>product_id</c> = "202610191704-KOUN-WUUS54-SVROUN[-AAA]" (issued UTC · office · WMO header · AWIPS id · an
	/// optional correction tag); a blank one (daily summaries) is skipped.
	/// ⚠️ NO GAP DETECTION: the room's numbers skip on their own (one number space across every iembot room), so a skip
	/// means nothing. Missed products can't happen while it answers — each ask returns all after the last number — and a
	/// drop longer than the room's backlog is covered by the hub's catch-up on reconnect.
	/// ⚠️ The FIRST ask only sets the starting number: its backlog is history the feeds' own launch fetches already hold.
	/// </remarks>
	public sealed class IemBotSource : ILiveProductSource
	{
		/// <summary>How often the room is asked for news. The answer is small (only what's new).</summary>
		internal static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(6);

		// Consecutive failed asks before the source reports itself down (one blip is not an outage).
		private const int FailuresBeforeDown = 2;

		private const string RoomUrl = "https://weather.im/iembot-json/room/botstalk?seqnum=";

		private readonly HttpClient _http;
		private readonly ILogger _logger;

		public IemBotSource(HttpClient http, ILogger logger)
		{
			_http = http;
			_logger = logger;
		}

		public async Task RunAsync(ILiveSourceSink sink, CancellationToken ct)
		{
			long? last = null;
			var failures = 0;
			var down = false;
			var connected = false;
			while (true)
			{
				ct.ThrowIfCancellationRequested();
				var wait = PollEvery;
				try
				{
					var json = await _http.GetStringAsync(RoomUrl + (last ?? 0).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
					var first = last is null;
					last = Read(json, last ?? 0, first ? null : sink);
					failures = 0;
					if (!connected || down)
					{
						connected = true;
						down = false;
						sink.Connected();
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					failures++;
					_logger.LogInformation("IEM live feed ask failed ({Count}): {Message}", failures, ex.Message);
					if (failures >= FailuresBeforeDown && !down)
					{
						down = true;
						sink.Down("IEM isn't answering");
					}
					// Back off while down: 15 s, 30 s, then every minute.
					if (down) { wait = TimeSpan.FromSeconds(Math.Min(60, 15 * Math.Pow(2, Math.Max(0, failures - FailuresBeforeDown)))); }
				}
				await Task.Delay(wait, ct).ConfigureAwait(false);
			}
		}

		/// <summary>Reports each new product to <paramref name="sink"/> (null = just find the newest number) and returns
		/// the newest message number seen. Internal for tests.</summary>
		internal static long Read(string json, long after, ILiveSourceSink? sink)
		{
			var newest = after;
			using var doc = JsonDocument.Parse(json);
			if (!doc.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
			{
				return newest;
			}
			foreach (var m in messages.EnumerateArray())
			{
				if (!m.TryGetProperty("seqnum", out var seq) || !seq.TryGetInt64(out var n) || n <= after) { continue; }
				if (n > newest) { newest = n; }
				if (sink is null) { continue; }
				var id = m.TryGetProperty("product_id", out var p) ? p.GetString() : null;
				var received = m.TryGetProperty("ts", out var ts) ? ParseStamp(ts.GetString()) : null;
				if (ParseProductId(id, received ?? DateTimeOffset.UtcNow) is { } product) { sink.Product(product); }
			}
			return newest;
		}

		/// <summary>"202610191704-KOUN-WUUS54-SVROUN-AAA" → the product, or null when it isn't one. Internal for tests.</summary>
		internal static LiveProduct? ParseProductId(string? id, DateTimeOffset received)
		{
			if (string.IsNullOrWhiteSpace(id)) { return null; }
			var parts = id.Split('-');
			if (parts.Length < 4) { return null; }
			var issued = DateTimeOffset.TryParseExact(parts[0], "yyyyMMddHHmm", CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : received;
			return new LiveProduct(id, parts[3], parts[1], issued, received);
		}

		// The room's "2026-10-19 12:20:21" (UTC).
		private static DateTimeOffset? ParseStamp(string? s) =>
			DateTimeOffset.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
	}
}
