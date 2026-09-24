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
	/// <summary>How bad the radar SAYS it is — drives the NWS status section's dot. Not availability.</summary>
	public enum RadarNwsLevel
	{
		Unknown,
		Ok,        // Operate + On-line
		Degraded,  // maintenance required/mandatory, or not in Operate (Start-Up)
		Down,      // Inoperable
	}

	/// <summary>One FTM as the Atlas shows it: a header line ("NWS RAH · Sep 23, 7:29 AM (27 hr ago)") and the text.</summary>
	public sealed record RadarNwsMessageRow(string Header, string Text);

	/// <summary>
	/// The NWS status section for ONE site, every word already chosen (the XAML only binds). Built fresh by
	/// <see cref="RadarNwsStatusViewModel.DetailFor"/> — immutable, so the Atlas re-raises ONE property.
	/// </summary>
	public sealed record RadarNwsSiteDetail(
		bool ShowTable,           // false → the section shows EmptyText instead of rows
		string EmptyText,
		bool HasStation,          // the three state rows (a site NWS lists)
		string StateText,
		RadarNwsLevel Level,
		string AlarmsText,
		string PowerText,
		RadarNwsMessageRow? Latest,
		string NoMessageText,
		IReadOnlyList<RadarNwsMessageRow> Earlier,
		string EarlierLabel)
	{
		public bool HasMessage => Latest is not null;
		public bool HasEarlier => Earlier.Count > 0;
	}

	/// <summary>
	/// The NWS's own account of every radar, for the Radar Atlas's NWS STATUS section: the RDA's self-reported
	/// state (status, alarms, power) and the Free Text Messages that say WHY a site is down. Checked ONCE at
	/// launch (<see cref="Start"/>, from <c>MapViewModel.OnMapsReadyAsync</c>) and on demand via the section's
	/// Re-check, which then cools down for <see cref="Cooldown"/> — one check covers every site (two requests).
	/// </summary>
	/// <remarks>
	/// ⚠️ INFORMATION, NOT AVAILABILITY. Nothing here writes <see cref="RadarSiteRow.Availability"/>: that dot means
	/// "data is reaching the bucket we load from" (RadarViewModel's SITE AVAILABILITY block, one writer), and a
	/// radar reporting "maintenance mandatory" is usually streaming fine. See <see cref="RadarNwsStation"/>.
	/// ⚠️ The cooldown runs from every ATTEMPT, failed ones included — it protects NWS, not our success rate.
	/// ⚠️ Each feed keeps its OWN last good answer: a failed FTM fetch doesn't blank the station rows, or vice versa.
	/// </remarks>
	public sealed class RadarNwsStatusViewModel : ObservableObject
	{
		public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

		private readonly IRadarNwsStatusService _service;
		private readonly Func<IEnumerable<(string Id, bool IsTdwr)>> _sites;
		private readonly Func<DateTimeOffset> _now;
		private readonly CancellationTokenSource _shutdown = new();

		private IReadOnlyDictionary<string, RadarNwsStation>? _stations;
		private Dictionary<string, List<RadarNwsMessage>>? _messagesBySite;
		private DateTimeOffset? _lastAttemptUtc;
		private DateTimeOffset? _stationsGoodUtc;
		private DateTimeOffset? _messagesGoodUtc;
		private bool _stationsFailed;
		private bool _messagesFailed;
		private bool _started;
		private long _lastMinute = -1;

		/// <param name="sites">OUR site list (id + whether it's a TDWR) — the only sites a message can land on.</param>
		public RadarNwsStatusViewModel(IRadarNwsStatusService service, Func<IEnumerable<(string Id, bool IsTdwr)>> sites,
			Func<DateTimeOffset>? now = null)
		{
			_service = service;
			_sites = sites;
			_now = now ?? (() => DateTimeOffset.UtcNow);
		}

		/// <summary>Raised when the data changes, and once a minute so relative times ("27 hr ago") stay true.</summary>
		public event EventHandler? Changed;

		/// <summary>The launch check + the 1 s clock that runs the cooldown countdown. Idempotent.</summary>
		public void Start()
		{
			if (_started)
			{
				return;
			}
			_started = true;
			_ = RunClockAsync();
			_ = CheckAsync();
		}

		public void Shutdown() => _shutdown.Cancel();

		private bool _isChecking;

		public bool IsChecking
		{
			get => _isChecking;
			private set
			{
				if (SetProperty(ref _isChecking, value))
				{
					RaiseClock();
				}
			}
		}

		private TimeSpan CooldownLeft => _lastAttemptUtc is { } at ? at + Cooldown - _now() : TimeSpan.Zero;

		/// <summary>Re-check enablement: not mid-check and the 5-minute cooldown has run out.</summary>
		public bool CanCheck => !_isChecking && CooldownLeft <= TimeSpan.Zero;

		/// <summary>"Re-check" · "Checking…" · "Re-check in 3:04".</summary>
		public string CheckButtonText
		{
			get
			{
				if (_isChecking) return "Checking…";
				var left = CooldownLeft;
				return left > TimeSpan.Zero
					? $"Re-check in {(int)left.TotalMinutes}:{left.Seconds:00}"
					: "Re-check";
			}
		}

		public string CheckToolTip => CanCheck
			? "Re-check NWS status for every radar site"
			: _isChecking
				? "Checking NWS status for every radar site"
				: "One check covers every site. Re-check is available 5 minutes after the last one.";

		/// <summary>The section header's small line: when it was checked, and what failed if anything did.</summary>
		public string CheckedText
		{
			get
			{
				if (_isChecking) return "checking…";
				if (_lastAttemptUtc is not { } at) return "not checked yet";
				var ago = Ago(at);
				if (_stationsFailed && _messagesFailed)
				{
					var good = Max(_stationsGoodUtc, _messagesGoodUtc);
					return good is { } g ? $"couldn't reach NWS · last good {Ago(g)}" : "couldn't reach NWS";
				}
				if (_stationsFailed) return $"checked {ago} · couldn't get radar states";
				if (_messagesFailed) return $"checked {ago} · couldn't get outage messages";
				return $"checked {ago}";
			}
		}

		/// <summary>One check of both feeds. No-op while one runs or during the cooldown.</summary>
		public async Task CheckAsync()
		{
			if (!CanCheck || _shutdown.IsCancellationRequested)
			{
				return;
			}
			_lastAttemptUtc = _now();
			IsChecking = true;

			var ct = _shutdown.Token;
			var stationsTask = TryAsync(() => _service.GetStationsAsync(ct), "stations");
			var messagesTask = TryAsync(() => _service.GetMessagesAsync(ct), "messages");
			var stations = await stationsTask;
			var messages = await messagesTask;

			_stationsFailed = stations is null;
			_messagesFailed = messages is null;
			if (stations is not null)
			{
				_stations = stations;
				_stationsGoodUtc = _lastAttemptUtc;
			}
			if (messages is not null)
			{
				_messagesBySite = GroupBySite(messages);
				_messagesGoodUtc = _lastAttemptUtc;
			}

			RadarDiagnostics.Log("vm", "nws.status",
				("stations", stations?.Count), ("messages", messages?.Count),
				("matched", _messagesBySite?.Count));

			IsChecking = false;
			Changed?.Invoke(this, EventArgs.Empty);
		}

		private static async Task<T?> TryAsync<T>(Func<Task<T>> fetch, string feed) where T : class
		{
			try
			{
				return await fetch();
			}
			catch (Exception ex)
			{
				RadarDiagnostics.Log("vm", "nws.status.failed", ("feed", feed), ("error", ex.GetType().Name), ("msg", ex.Message));
				return null;
			}
		}

		// Each message goes to the ONE site it's about (RadarNwsStatusService.ResolveSiteId); a message about a
		// radar we don't list (a profiler) is dropped. Lists stay newest first.
		private Dictionary<string, List<RadarNwsMessage>> GroupBySite(IReadOnlyList<RadarNwsMessage> messages)
		{
			var sites = _sites().ToList();
			var bySite = new Dictionary<string, List<RadarNwsMessage>>(StringComparer.OrdinalIgnoreCase);
			foreach (var message in messages.OrderByDescending(m => m.IssuedUtc))
			{
				if (RadarNwsStatusService.ResolveSiteId(message, sites) is not { } id)
				{
					continue;
				}
				if (!bySite.TryGetValue(id, out var list))
				{
					bySite[id] = list = new List<RadarNwsMessage>();
				}
				list.Add(message);
			}
			return bySite;
		}

		// ── The per-site detail ──────────────────────────────────────────────────────────────────

		/// <summary>The NWS STATUS section for <paramref name="row"/>, every word chosen.</summary>
		public RadarNwsSiteDetail DetailFor(RadarSiteRow? row)
		{
			var station = row is not null && _stations is not null && _stations.TryGetValue(row.Id, out var s) ? s : null;
			var messages = row is not null && _messagesBySite is not null && _messagesBySite.TryGetValue(row.Id, out var m)
				? m
				: new List<RadarNwsMessage>();
			var rows = messages.Select(ToRow).ToList();
			var hasData = _stations is not null || _messagesBySite is not null;

			string empty;
			if (!hasData)
			{
				empty = _isChecking || _lastAttemptUtc is null ? "Checking NWS…" : "Couldn't reach NWS.";
			}
			else
			{
				empty = "NWS doesn't report status for this site.";
			}

			var earlier = rows.Skip(1).ToList();
			return new RadarNwsSiteDetail(
				ShowTable: hasData && (station is not null || rows.Count > 0),
				EmptyText: empty,
				HasStation: station is not null,
				StateText: station is null ? string.Empty : StateWords(station),
				Level: station is null ? RadarNwsLevel.Unknown : LevelOf(station),
				AlarmsText: station is null ? string.Empty : AlarmWords(station.AlarmSummary),
				PowerText: station is null ? string.Empty : PowerWords(station.GeneratorState),
				Latest: rows.FirstOrDefault(),
				NoMessageText: _messagesBySite is null ? "Outage messages unavailable." : "No NWS messages in the last 24 hours.",
				Earlier: earlier,
				EarlierLabel: earlier.Count == 1 ? "1 earlier message" : $"{earlier.Count} earlier messages");
		}

		private RadarNwsMessageRow ToRow(RadarNwsMessage m)
		{
			var office = m.Office.Length == 4 ? m.Office[1..] : m.Office; // KRAH → RAH, PAFG → AFG
			var local = m.IssuedUtc.ToLocalTime();
			var when = local.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
			return new RadarNwsMessageRow($"NWS {office} · {when} ({Ago(m.IssuedUtc)})", m.Text);
		}

		/// <summary>"Operate · On-line", "Operate · Maintenance action mandatory", "Start-up · Inoperable".</summary>
		internal static string StateWords(RadarNwsStation s)
		{
			var status = string.IsNullOrWhiteSpace(s.Status) ? null : SentenceCase(s.Status!);
			var operability = string.IsNullOrWhiteSpace(s.Operability) ? null : SentenceCase(StripRda(s.Operability!));
			return (status, operability) switch
			{
				(null, null) => "Not reported",
				(null, { } o) => o,
				({ } st, null) => st,
				({ } st, { } o) => $"{st} · {o}",
			};
		}

		internal static RadarNwsLevel LevelOf(RadarNwsStation s)
		{
			var operability = s.Operability ?? string.Empty;
			if (operability.Contains("Inoperable", StringComparison.OrdinalIgnoreCase)
				|| operability.Contains("Off-line", StringComparison.OrdinalIgnoreCase))
			{
				return RadarNwsLevel.Down;
			}
			if (s.Status is null && s.Operability is null)
			{
				return RadarNwsLevel.Unknown;
			}
			var operating = string.Equals(s.Status, "Operate", StringComparison.OrdinalIgnoreCase);
			var online = operability.Contains("On-line", StringComparison.OrdinalIgnoreCase);
			return operating && online ? RadarNwsLevel.Ok : RadarNwsLevel.Degraded;
		}

		/// <summary>"No Alarms" → "None"; "Tower/Utilities|Transmitter" → "Tower/utilities · Transmitter".</summary>
		internal static string AlarmWords(string? summary)
		{
			if (string.IsNullOrWhiteSpace(summary)) return "Not reported";
			if (string.Equals(summary.Trim(), "No Alarms", StringComparison.OrdinalIgnoreCase)) return "None";
			return string.Join(" · ", summary.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(SentenceCase));
		}

		/// <summary>"Utility PWR Available" → "Utility power"; tokens joined by '|' are each worded and kept in order.</summary>
		internal static string PowerWords(string? generatorState)
		{
			if (string.IsNullOrWhiteSpace(generatorState)) return "Not reported";
			return string.Join(" · ", generatorState.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(token => token.ToUpperInvariant() switch
				{
					"UTILITY PWR AVAILABLE" => "Utility power",
					"GENERATOR ON" => "Generator running",
					"SWITCHED TO AUXILIARY POWER" => "On auxiliary power",
					_ => SentenceCase(token),
				}));
		}

		private static string StripRda(string value) =>
			value.StartsWith("RDA - ", StringComparison.OrdinalIgnoreCase) ? value[6..] : value;

		// "Maintenance Action Mandatory" → "Maintenance action mandatory". Words after the first are lowered.
		private static string SentenceCase(string value)
		{
			var trimmed = value.Trim();
			if (trimmed.Length == 0) return trimmed;
			return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
		}

		private string Ago(DateTimeOffset utc)
		{
			var span = _now() - utc;
			if (span < TimeSpan.FromMinutes(1)) return "just now";
			if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
			return $"{(int)span.TotalHours} hr ago";
		}

		private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
			a is null ? b : b is null ? a : (a > b ? a : b);

		// ── Clock: the countdown each second, the relative times each minute ─────────────────────

		private async Task RunClockAsync()
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
				while (await timer.WaitForNextTickAsync(_shutdown.Token))
				{
					RaiseClock();
					var minute = _now().ToUnixTimeSeconds() / 60;
					if (minute != _lastMinute)
					{
						_lastMinute = minute;
						Changed?.Invoke(this, EventArgs.Empty);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Window closed — this tick raises PropertyChanged at live bindings, so it must stop with it.
			}
		}

		private void RaiseClock()
		{
			OnPropertyChanged(nameof(CanCheck));
			OnPropertyChanged(nameof(CheckButtonText));
			OnPropertyChanged(nameof(CheckToolTip));
			OnPropertyChanged(nameof(CheckedText));
		}
	}
}
