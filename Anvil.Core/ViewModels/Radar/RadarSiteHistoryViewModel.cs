using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>One day of the uptime strip.</summary>
	public enum UptimeCellLevel { Up, Partial, Down, Unknown }

	/// <summary>One cell of the 30-day strip. <see cref="HasMessage"/> = an NWS message was issued that day.</summary>
	public sealed record UptimeDayCell(UptimeCellLevel Level, bool HasMessage, string ToolTip);

	/// <summary>One gap or outage under the strip: "Sep 25, 8:09 PM" · "Down 13 hr 27 min, ongoing".</summary>
	public sealed record UptimeHoleRow(string When, string What, UptimeHoleKind Kind, bool HasMessage, string MessageHint);

	/// <summary>One segment of the scan-pattern bar and its legend entry. <see cref="Index"/> 0 = the most-used
	/// pattern (picks the segment's shade, largest first).</summary>
	public sealed record ScanShareRow(int Vcp, double Share, int Index, string Label);

	/// <summary>One unlisted pattern seen at this site.</summary>
	public sealed record NonStandardVcpRow(string Vcp, string Detail, string When);

	/// <summary>
	/// The Atlas site page's history sections — DATA UPTIME, MESSAGE HISTORY, SCAN PATTERNS — for the selected
	/// site, every word chosen here (the XAML only binds). Owned by <see cref="RadarAtlasViewModel"/>.
	/// </summary>
	/// <remarks>
	/// ⚠️ LOADS ONLY WHILE THE ATLAS IS OPEN (<see cref="IsActive"/>). The Atlas selection follows the map's site
	/// even with the window closed, and a cold site costs ~750 requests (31 listings + ~720 VCP reads); nobody
	/// looking = nothing fetched. Opening the Atlas loads whatever is selected.
	/// ⚠️ A new selection CANCELS the previous site's fetches (finished days are cached, so nothing is lost)
	/// and a token drops any late result, the same guard the scan tile uses.
	/// Information, never availability: nothing here touches the Online/Offline pill.
	/// </remarks>
	public sealed class RadarSiteHistoryViewModel : ObservableObject
	{
		private const int HoleRowsShown = 6;
		private const int MessagesFolded = 4;

		private readonly IRadarUptimeService _uptime;
		private readonly IRadarMessageHistoryService _messages;
		private readonly IRadarScanPatternService _scanPatterns;
		private readonly NonStandardVcpLog _nonStandard;
		private readonly Func<IReadOnlyList<RadarSite>> _allSites;
		private readonly IDispatcher _dispatcher;

		private RadarSite? _site;
		private bool _isActive;
		private string? _loadedFor;
		private int _token;
		private CancellationTokenSource? _cts;

		private SiteUptimeReport? _report;
		private IReadOnlyList<SiteMessage>? _allMessages;
		private SiteScanPatternReport? _scan;

		public RadarSiteHistoryViewModel(IRadarUptimeService uptime, IRadarMessageHistoryService messages,
			IRadarScanPatternService scanPatterns, NonStandardVcpLog nonStandard,
			Func<IReadOnlyList<RadarSite>> allSites, IDispatcher dispatcher)
		{
			_uptime = uptime;
			_messages = messages;
			_scanPatterns = scanPatterns;
			_nonStandard = nonStandard;
			_allSites = allSites;
			_dispatcher = dispatcher;
			// Fires on a frame-fetch thread — marshal, then re-read only if it's about the site on screen.
			_nonStandard.Changed += (_, id) => _dispatcher.Post(() =>
			{
				if (string.Equals(id, _site?.Id, StringComparison.OrdinalIgnoreCase)) RaiseNonStandard();
			});
		}

		/// <summary>True while the Atlas window is open. Turning it on loads the shown site if it isn't loaded.</summary>
		public bool IsActive
		{
			get => _isActive;
			set
			{
				if (SetProperty(ref _isActive, value) && value) LoadIfNeeded();
			}
		}

		/// <summary>The Atlas's selected site changed.</summary>
		public void Show(RadarSite? site)
		{
			if (site?.Id == _site?.Id) return;
			_site = site;
			_cts?.Cancel();
			_loadedFor = null;
			_report = null;
			_scan = null;
			IsMessagesExpanded = false;
			RaiseAll();
			LoadIfNeeded();
		}

		private void LoadIfNeeded()
		{
			if (!_isActive || _site is null || _loadedFor == _site.Id) return;
			_loadedFor = _site.Id;
			_cts?.Cancel();
			_cts = new CancellationTokenSource();
			var token = ++_token;
			_ = LoadUptimeAsync(_site, token, _cts.Token);
			_ = LoadMessagesAsync(token, _cts.Token);
			_ = LoadScanAsync(_site, token, _cts.Token);
		}

		// ── DATA UPTIME ─────────────────────────────────────────────────────────────────────────

		private bool _isLoadingUptime;
		private bool _uptimeFailed;

		public bool IsLoadingUptime
		{
			get => _isLoadingUptime;
			private set => SetProperty(ref _isLoadingUptime, value);
		}

		private async Task LoadUptimeAsync(RadarSite site, int token, CancellationToken ct)
		{
			IsLoadingUptime = true;
			_uptimeFailed = false;
			RaiseUptime();
			try
			{
				var report = await _uptime.GetReportAsync(site.Id, ct);
				if (token != _token) return;
				_report = report;
			}
			catch (OperationCanceledException) { return; }
			catch
			{
				if (token != _token) return;
				_uptimeFailed = true;
			}
			if (token == _token)
			{
				IsLoadingUptime = false;
				RaiseUptime();
			}
		}

		/// <summary>True once a report with ANY known day is in — shows tiles, strip and holes.</summary>
		public bool HasUptime => _report is { UpFraction: not null } && !NoArchiveData;

		// Every known day listed and not one volume in any of them: the radar isn't publishing (KCRI), which
		// "0% uptime" would misstate as 30 days broken.
		private bool NoArchiveData => _report is { } r && r.Days.All(d => d.Volumes == 0) && r.Days.Any(d => d.UpFraction is not null);

		/// <summary>The one line shown in place of the tiles, or empty when the tiles show.</summary>
		public string UptimeEmptyText =>
			_site is null ? string.Empty
			: HasUptime ? string.Empty
			: _isLoadingUptime ? "Reading 30 days of the archive…"
			: NoArchiveData ? $"No data from {_site.Id} has reached the public archive in {RadarUptimeService.HistoryDays} days."
			: _uptimeFailed || _report is not null ? "The archive couldn't be reached. Uptime will show next time the Atlas opens."
			: string.Empty;

		public string Uptime7Value => Percent(UpOver(7));
		public string Uptime30Value => Percent(_report?.UpFraction);
		public string OutagesValue => _report is null ? "—" : Outages.Count.ToString(CultureInfo.InvariantCulture);
		public string OutagesLabel => $"OUTAGES · {RadarUptimeService.HistoryDays} D";

		public RadarGlossaryCard UptimeHint => RadarGlossary.DataUptime(_report?.UpFraction);

		private IReadOnlyList<UptimeHole> Outages => _report?.Holes.Where(h => h.Kind == UptimeHoleKind.Down).ToList()
			?? (IReadOnlyList<UptimeHole>)Array.Empty<UptimeHole>();

		// Mean over the last N KNOWN days (today counts as a day; unknown days are skipped, not zeros).
		private double? UpOver(int days)
		{
			var known = _report?.Days.TakeLast(days).Where(d => d.UpFraction is not null).Select(d => d.UpFraction!.Value).ToList();
			return known is { Count: > 0 } ? known.Average() : null;
		}

		private static string Percent(double? f) => f is double v ? $"{v * 100:0.0}%" : "—";

		/// <summary>The 30 day cells, oldest first.</summary>
		public IReadOnlyList<UptimeDayCell> DayCells => _report is null ? Array.Empty<UptimeDayCell>()
			: _report.Days.Select(d =>
			{
				var level = d.UpFraction switch
				{
					null => UptimeCellLevel.Unknown,
					>= 0.995 => UptimeCellLevel.Up,
					>= 0.9 => UptimeCellLevel.Partial,
					_ => UptimeCellLevel.Down,
				};
				var messages = MessagesOn(d.Day);
				var words = d.UpFraction is double f ? $"{f * 100:0.#}% up" : "Not checked";
				var tip = $"{d.Day.ToDateTime(TimeOnly.MinValue):ddd, MMM d} · {words}"
					+ (messages > 0 ? $" · {messages} NWS message{(messages == 1 ? "" : "s")}" : string.Empty);
				return new UptimeDayCell(level, messages > 0, tip);
			}).ToList();

		public string StripStartLabel => _report?.Days.FirstOrDefault() is { } d ? d.Day.ToDateTime(TimeOnly.MinValue).ToString("MMM d", CultureInfo.CurrentCulture) : string.Empty;
		public string StripEndLabel => _report is null ? string.Empty : "Today";

		/// <summary>The newest holes, outages and gaps together.</summary>
		public IReadOnlyList<UptimeHoleRow> HoleRows => _report is null ? Array.Empty<UptimeHoleRow>()
			: _report.Holes.Take(HoleRowsShown).Select(h =>
			{
				var msg = MessageFor(h);
				var what = h.Kind == UptimeHoleKind.Down
					? $"Down {Duration(h.Duration)}{(h.Ongoing ? ", ongoing" : string.Empty)}"
					: $"Gap {Duration(h.Duration)}";
				return new UptimeHoleRow(Clock(h.StartUtc), what, h.Kind, msg is not null, msg is null ? string.Empty : Snippet(msg.Message.Text));
			}).ToList();

		public bool HasHoles => _report is { Holes.Count: > 0 } && HasUptime;
		public string NoHolesText => HasUptime && _report is { Holes.Count: 0 } ? "No gaps or outages in 30 days." : string.Empty;
		public string MoreHolesText => _report is { } r && r.Holes.Count > HoleRowsShown
			? $"{r.Holes.Count - HoleRowsShown} older gap{(r.Holes.Count - HoleRowsShown == 1 ? "" : "s")} and outages not shown" : string.Empty;

		// The message that explains a hole: issued from 12 h before it opened to 1 h after it closed, the newest.
		private SiteMessage? MessageFor(UptimeHole h) => SiteMessages
			.Where(m => m.Message.IssuedUtc >= h.StartUtc.AddHours(-12) && m.Message.IssuedUtc <= h.EndUtc.AddHours(1))
			.OrderByDescending(m => m.Message.IssuedUtc).FirstOrDefault();

		private int MessagesOn(DateOnly localDay) => SiteMessages.Count(m => DateOnly.FromDateTime(m.Message.IssuedUtc.ToLocalTime().DateTime) == localDay);

		private void RaiseUptime()
		{
			OnPropertyChanged(nameof(HasUptime));
			OnPropertyChanged(nameof(UptimeEmptyText));
			OnPropertyChanged(nameof(Uptime7Value));
			OnPropertyChanged(nameof(Uptime30Value));
			OnPropertyChanged(nameof(OutagesValue));
			OnPropertyChanged(nameof(UptimeHint));
			OnPropertyChanged(nameof(DayCells));
			OnPropertyChanged(nameof(StripStartLabel));
			OnPropertyChanged(nameof(StripEndLabel));
			OnPropertyChanged(nameof(HoleRows));
			OnPropertyChanged(nameof(HasHoles));
			OnPropertyChanged(nameof(NoHolesText));
			OnPropertyChanged(nameof(MoreHolesText));
		}

		// ── MESSAGE HISTORY ─────────────────────────────────────────────────────────────────────

		private bool _messagesFailed;

		private async Task LoadMessagesAsync(int token, CancellationToken ct)
		{
			_messagesFailed = false;
			try
			{
				// Network-wide in one go (one request per month, cached) — every later site is free.
				_allMessages ??= await _messages.GetHistoryAsync(_allSites(), ct);
			}
			catch (OperationCanceledException) { return; }
			catch
			{
				_messagesFailed = true;
			}
			if (token != _token) return;
			RaiseMessages();
			RaiseUptime(); // the strip's message markers and the holes' reasons read the messages too
		}

		private IEnumerable<SiteMessage> SiteMessages => _site is null || _allMessages is null
			? Enumerable.Empty<SiteMessage>()
			: _allMessages.Where(m => string.Equals(m.SiteId, _site.Id, StringComparison.OrdinalIgnoreCase));

		private List<RadarNwsMessageRow> AllRows => SiteMessages.Select(m => new RadarNwsMessageRow(
			$"NWS {m.Message.Office[1..]} · {Clock(m.Message.IssuedUtc)}"
				+ (m.FiledUnderSiteId is { } other ? $" · filed under {other}" : string.Empty),
			m.Message.Text)).ToList();

		public IReadOnlyList<RadarNwsMessageRow> MessageRows => IsMessagesExpanded ? AllRows : AllRows.Take(MessagesFolded).ToList();

		public bool HasMessages => SiteMessages.Any();

		public string MessagesEmptyText =>
			_site is null || HasMessages ? string.Empty
			: _allMessages is null && !_messagesFailed ? "Reading the NWS message archive…"
			: _messagesFailed ? "The NWS message archive couldn't be reached."
			: $"No NWS messages about {_site.Id} in {RadarUptimeService.HistoryDays} days.";

		public bool HasMoreMessages => AllRows.Count > MessagesFolded;
		public string MoreMessagesLabel => IsMessagesExpanded ? "Show fewer" : $"{AllRows.Count - MessagesFolded} older message{(AllRows.Count - MessagesFolded == 1 ? "" : "s")}";

		private bool _isMessagesExpanded;

		public bool IsMessagesExpanded
		{
			get => _isMessagesExpanded;
			private set
			{
				if (SetProperty(ref _isMessagesExpanded, value))
				{
					OnPropertyChanged(nameof(MessageRows));
					OnPropertyChanged(nameof(MoreMessagesLabel));
				}
			}
		}

		public void ToggleMessages() => IsMessagesExpanded = !_isMessagesExpanded;

		private void RaiseMessages()
		{
			OnPropertyChanged(nameof(MessageRows));
			OnPropertyChanged(nameof(HasMessages));
			OnPropertyChanged(nameof(MessagesEmptyText));
			OnPropertyChanged(nameof(HasMoreMessages));
			OnPropertyChanged(nameof(MoreMessagesLabel));
		}

		// ── SCAN PATTERNS ───────────────────────────────────────────────────────────────────────

		private async Task LoadScanAsync(RadarSite site, int token, CancellationToken ct)
		{
			var progress = new Progress<SiteScanPatternReport>(r =>
			{
				if (token != _token) return;
				_scan = r;
				RaiseScan();
			});
			try
			{
				var report = await _scanPatterns.GetReportAsync(site.Id, progress, ct);
				if (token != _token) return;
				_scan = report;
			}
			catch (OperationCanceledException) { return; }
			catch { /* a failed sample leaves whatever it had */ }
			if (token == _token) RaiseScan();
		}

		public IReadOnlyList<ScanShareRow> ScanShares => _scan is not { SampledHours: > 0 } s ? Array.Empty<ScanShareRow>()
			: s.Shares.Select((x, i) =>
			{
				var share = (double)x.Hours / s.SampledHours;
				var name = VcpCatalog.Find(x.Vcp)?.Name.ToLowerInvariant() ?? "unlisted";
				return new ScanShareRow(x.Vcp, share, i, $"{x.Vcp} {name} · {share * 100:0}%");
			}).ToList();

		public bool HasScanShares => ScanShares.Count > 0;

		/// <summary>"hourly sample · share of time" once done; "sampling… 12 of 30 days" while it fills.</summary>
		public string ScanCaption => _scan is not { } s ? (_site is null ? string.Empty : "sampling…")
			: s.DaysDone < s.DaysTotal ? $"sampling… {s.DaysDone} of {s.DaysTotal} days"
			: $"share of time · hourly sample · {s.DaysTotal} days";

		public string ScanEmptyText => HasScanShares || _site is null ? string.Empty
			: _scan is { DaysDone: var d, DaysTotal: var t } && d >= t ? "No scan patterns could be read for this site."
			: "Reading scan patterns from the archive…";

		/// <summary>Unlisted patterns seen at this site (NonStandardVcpLog), newest first.</summary>
		public IReadOnlyList<NonStandardVcpRow> NonStandardRows => _site is null ? Array.Empty<NonStandardVcpRow>()
			: _nonStandard.ForSite(_site.Id).Select(s => new NonStandardVcpRow(
				$"VCP {s.Vcp}",
				$"{s.Frames} frame{(s.Frames == 1 ? "" : "s")}"
					+ (s.Tilts.Count > 0 ? $" · {s.Tilts.Count} tilts, {s.Tilts.Min():0.0#}–{s.Tilts.Max():0.0#}°" : string.Empty),
				s.FirstSeenUtc.ToLocalTime().Date == s.LastSeenUtc.ToLocalTime().Date
					? s.LastSeenUtc.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture)
					: $"{s.FirstSeenUtc.ToLocalTime():MMM d} – {s.LastSeenUtc.ToLocalTime():MMM d}")).ToList();

		public bool HasNonStandard => _site is not null && _nonStandard.ForSite(_site.Id).Count > 0;

		private void RaiseNonStandard()
		{
			OnPropertyChanged(nameof(NonStandardRows));
			OnPropertyChanged(nameof(HasNonStandard));
		}

		private void RaiseScan()
		{
			OnPropertyChanged(nameof(ScanShares));
			OnPropertyChanged(nameof(HasScanShares));
			OnPropertyChanged(nameof(ScanCaption));
			OnPropertyChanged(nameof(ScanEmptyText));
		}

		// ── shared ──────────────────────────────────────────────────────────────────────────────

		private void RaiseAll()
		{
			RaiseUptime();
			RaiseMessages();
			RaiseScan();
			RaiseNonStandard();
		}

		private static string Clock(DateTimeOffset utc) => utc.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);

		internal static string Duration(TimeSpan span)
		{
			if (span.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min";
			if (span.TotalHours < 24) return span.Minutes == 0 ? $"{(int)span.TotalHours} hr" : $"{(int)span.TotalHours} hr {span.Minutes} min";
			return span.Hours == 0 ? $"{(int)span.TotalDays} d" : $"{(int)span.TotalDays} d {span.Hours} hr";
		}

		// First sentence of a message, for a hole's tooltip.
		private static string Snippet(string text)
		{
			var flat = text.Replace('\n', ' ');
			var end = flat.IndexOf(". ", StringComparison.Ordinal);
			var first = end > 0 ? flat[..(end + 1)] : flat;
			return first.Length > 160 ? first[..157] + "…" : first;
		}
	}
}
