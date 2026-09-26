using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>Every radar outage notice (FTM) for our sites over the uptime window — the Atlas's message history.</summary>
	public interface IRadarMessageHistoryService
	{
		/// <summary>All FTMs for the given sites issued in the last <see cref="RadarUptimeService.HistoryDays"/>
		/// (+1) days, newest first, each resolved to ONE site id (<see cref="RadarNwsStatusService.ResolveSiteId"/>).
		/// Throws only when nothing at all could be fetched; a failed older month just shortens the history.</summary>
		Task<IReadOnlyList<SiteMessage>> GetHistoryAsync(IReadOnlyList<RadarSite> sites, CancellationToken cancellationToken = default);
	}

	/// <summary>One historical FTM and the site it's listed under.</summary>
	/// <param name="FiledUnderSiteId">Null when the message is this site's own product. Set when it was filed
	/// under ANOTHER site but names this one — OUN posted the Sep 26 2026 KTLX 3° offset note as FTMOKC (TOKC).</param>
	public sealed record SiteMessage(string SiteId, RadarNwsMessage Message, string? FiledUnderSiteId = null);

	/// <summary>
	/// Message history from the Iowa Environmental Mesonet's text-product archive (IEM AFOS, <c>retrieve.py</c>),
	/// which keeps every FTM ever issued. ONE request per calendar month covers every site (208 codes ≈ 1.5 KB of
	/// URL; Sep 2026 = 758 messages, 124 KB, 0.3 s).
	/// </summary>
	/// <remarks>
	/// ⚠️ WHY PER CALENDAR MONTH: a message's time is its WMO header's ddhhmm (the rule the live service already
	/// follows — the free-text "Message Date:" line is unreliable), which has no month. Asking for exactly one
	/// month makes the month KNOWN, so the header dates are exact. Verified 2026-09-26: <c>edate</c> is EXCLUSIVE
	/// (sdate=09-24&amp;edate=09-25 returns only the 24th), so a month query can never pull in the 1st of the next.
	/// ⚠️ IEM's no-match answer is HTTP 200 "ERROR: Could not Find: …" — a real "no messages", not a failure.
	/// Products are SOH (\x01) … ETX (\x03) blocks, newest first: a sequence number, the WMO header, FTMxxx, text.
	/// CACHE: a month is FINAL a day after it ends → raw text on disk at
	/// <c>%LocalAppData%\Anvil\Network\Messages\{yyyyMM}-{codes hash}.txt</c> (the hash re-fetches if our site list
	/// changes). The current month is held in memory for <see cref="CurrentMonthTtl"/>.
	/// </remarks>
	public sealed class RadarMessageHistoryService : IRadarMessageHistoryService
	{
		private const string RetrieveUrl = "https://mesonet.agron.iastate.edu/cgi-bin/afos/retrieve.py";
		internal static readonly TimeSpan CurrentMonthTtl = TimeSpan.FromMinutes(15);

		private readonly HttpClient _http;
		private readonly ILogger<RadarMessageHistoryService> _logger;
		private readonly string _root;
		private readonly Func<DateTimeOffset> _clock;
		private readonly ConcurrentDictionary<string, (string text, DateTimeOffset at)> _current = new();

		public RadarMessageHistoryService(ILogger<RadarMessageHistoryService> logger) : this(logger, null, null, null) { }

		/// <param name="http">TESTS — a stubbed client. Null = a real one.</param>
		/// <param name="directory">TESTS/TOOLS — cache root. Null = <c>%LocalAppData%\Anvil\Network\Messages</c>.</param>
		internal RadarMessageHistoryService(ILogger<RadarMessageHistoryService> logger, HttpClient? http, string? directory, Func<DateTimeOffset>? clock)
		{
			_logger = logger;
			_http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
			if (http is null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0 (severe-weather app)");
			_root = directory ?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anvil", "Network", "Messages");
			_clock = clock ?? (() => DateTimeOffset.UtcNow);
			Directory.CreateDirectory(_root);
		}

		public async Task<IReadOnlyList<SiteMessage>> GetHistoryAsync(IReadOnlyList<RadarSite> sites, CancellationToken cancellationToken = default)
		{
			var now = _clock();
			var since = now.AddDays(-(RadarUptimeService.HistoryDays + 1));
			var codes = sites.Where(s => s.Id.Length == 4).Select(s => s.Id[1..].ToUpperInvariant()).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
			var resolveAgainst = sites.Select(s => (s.Id, s.Class == RadarSiteClass.Tdwr)).ToList();

			var messages = new List<RadarNwsMessage>();
			var failures = 0;
			var months = new List<(int y, int m)>();
			for (var m = new DateTime(since.Year, since.Month, 1); m <= now.UtcDateTime; m = m.AddMonths(1)) months.Add((m.Year, m.Month));
			foreach (var (y, m) in months)
			{
				try
				{
					messages.AddRange(ParseMonth(await GetMonthTextAsync(codes, y, m, now, cancellationToken), y, m));
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					failures++;
					_logger.LogWarning(ex, "Radar message history: {Year}-{Month:00} unavailable", y, m);
				}
			}
			if (failures == months.Count)
			{
				throw new HttpRequestException("Radar message history unavailable (IEM archive).");
			}

			// A message goes to the site it's filed under, AND to any other of our sites its text names by id.
			var ids = sites.Select(s => s.Id.ToUpperInvariant()).Distinct().ToList();
			var named = new Regex($@"\b({string.Join("|", ids.Select(Regex.Escape))})\b", RegexOptions.Compiled);
			var result = new List<SiteMessage>();
			foreach (var msg in messages.Where(x => x.IssuedUtc >= since && x.IssuedUtc <= now.AddMinutes(10)))
			{
				var own = RadarNwsStatusService.ResolveSiteId(msg, resolveAgainst);
				if (own is not null) result.Add(new SiteMessage(own, msg));
				foreach (var other in named.Matches(msg.Text).Select(m => m.Value).Distinct())
				{
					if (!string.Equals(other, own, StringComparison.OrdinalIgnoreCase)) result.Add(new SiteMessage(other, msg, own));
				}
			}
			return result.OrderByDescending(x => x.Message.IssuedUtc).ToList();
		}

		private async Task<string> GetMonthTextAsync(IReadOnlyList<string> codes, int year, int month, DateTimeOffset now, CancellationToken ct)
		{
			var monthStart = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
			var monthEnd = monthStart.AddMonths(1);
			var file = Path.Combine(_root, $"{year:0000}{month:00}-{CodesHash(codes)}.txt");
			var final = now >= monthEnd.AddDays(1);
			if (final && File.Exists(file))
			{
				return await File.ReadAllTextAsync(file, ct);
			}
			var key = $"{year:0000}{month:00}";
			if (!final && _current.TryGetValue(key, out var c) && now - c.at < CurrentMonthTtl)
			{
				return c.text;
			}

			var url = $"{RetrieveUrl}?pil={string.Join(",", codes.Select(x => "FTM" + x))}&limit=9999&fmt=text"
				+ $"&sdate={monthStart:yyyy-MM-dd}&edate={monthEnd:yyyy-MM-dd}";
			var text = await _http.GetStringAsync(url, ct);
			if (text.TrimStart().StartsWith('<'))
			{
				throw new InvalidOperationException("IEM returned HTML, not product text.");
			}
			if (final)
			{
				var temp = $"{file}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
				await File.WriteAllTextAsync(temp, text, ct);
				File.Move(temp, file, overwrite: true);
			}
			else
			{
				_current[key] = (text, now);
			}
			return text;
		}

		private static string CodesHash(IEnumerable<string> codes) =>
			Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join(",", codes))))[..8];

		/// <summary>
		/// Parses one calendar month of IEM product text. The month is GIVEN, so the header's ddhhmm is exact;
		/// a block whose day doesn't exist in that month, or with no WMO header + FTMxxx line, is skipped.
		/// </summary>
		internal static List<RadarNwsMessage> ParseMonth(string text, int year, int month)
		{
			var list = new List<RadarNwsMessage>();
			if (text.StartsWith("ERROR: Could not Find", StringComparison.Ordinal)) return list; // no messages that month

			foreach (var block in text.Replace("\r\n", "\n").Split('\u0001', '\u0003'))
			{
				var lines = block.Split('\n').Select(l => l.TrimEnd()).ToList();
				var h = lines.FindIndex(l => RadarNwsStatusService.WmoHeader.IsMatch(l));
				if (h < 0 || h + 1 >= lines.Count || RadarNwsStatusService.FtmLine.Match(lines[h + 1]) is not { Success: true } ftm)
				{
					continue;
				}
				var header = RadarNwsStatusService.WmoHeader.Match(lines[h]);
				var day = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
				var hour = int.Parse(header.Groups[3].Value, CultureInfo.InvariantCulture);
				var minute = int.Parse(header.Groups[4].Value, CultureInfo.InvariantCulture);
				if (day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
				{
					continue;
				}
				var issued = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
				list.Add(new RadarNwsMessage(ftm.Groups[1].Value, header.Groups[1].Value, issued,
					RadarNwsStatusService.BodyText(lines.Skip(h + 2))));
			}
			return list;
		}
	}
}
