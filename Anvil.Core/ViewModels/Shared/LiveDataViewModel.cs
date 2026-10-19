using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Settings → Live Data (<c>Composites/LiveDataSettingsTab</c>) AND the NowCast activity expander's SOURCE LINE — both
	/// read the one <see cref="LiveFeedHub"/>. Source pick, the NOAA Weather Wire account, each feed's polling interval,
	/// and how the connection stands.
	/// </summary>
	/// <remarks>
	/// ⚠️ The password never lands on this VM's properties or in settings: the tab hands it over
	/// (<see cref="SetPendingPassword"/>) and Save puts it straight into Windows Credential Manager.
	/// ⚠️ The hub reports on any thread; every refresh here is posted to the UI thread.
	/// ⚠️ CLOCK TIMES ONLY, like the rest of the activity expander — nothing here ticks.
	/// </remarks>
	public sealed class LiveDataViewModel : ObservableObject
	{
		private readonly LiveFeedHub _hub;
		private readonly AppSettings _settings;
		private readonly IDispatcher _dispatcher;
		private string _pendingPassword = string.Empty;
		private string _accountMessage = string.Empty;
		private bool _isTesting;
		private string _sourceLine = string.Empty, _statusText = string.Empty, _lastProductText = string.Empty, _missedText = string.Empty;
		private LiveLinkState _linkState;

		public LiveDataViewModel(LiveFeedHub hub, AppSettings settings, IDispatcher dispatcher)
		{
			_hub = hub;
			_settings = settings;
			_dispatcher = dispatcher;
			Polling = new ReadOnlyCollection<LivePollRow>(LiveFeedKinds.All.Select(k => new LivePollRow(k, settings)).ToList());
			hub.Changed += (_, _) => _dispatcher.Post(Refresh);
			Refresh();
		}

		// ── Where live updates come from ──

		/// <summary>The picked source as an index into <see cref="LiveSources.All"/> (IEM · NOAA Weather Wire · Polling only).</summary>
		public int SourceIndex
		{
			get => Math.Max(0, LiveSources.All.ToList().IndexOf(LiveSources.Normalize(_settings.LiveSource)));
			set
			{
				if (value < 0 || value >= LiveSources.All.Count) { return; }
				var token = LiveSources.All[value];
				if (LiveSources.Normalize(_settings.LiveSource) == token) { return; }
				_settings.LiveSource = token;
				OnPropertyChanged();
				OnPropertyChanged(nameof(IsIem));
				OnPropertyChanged(nameof(IsNwws));
				OnPropertyChanged(nameof(IsPollOnly));
				_hub.Restart();
			}
		}

		// The three radio buttons (two-way; only a TRUE lands — a radio unticking itself is ignored).
		public bool IsIem { get => SourceIndex == 0; set { if (value) { SourceIndex = 0; } } }
		public bool IsNwws { get => SourceIndex == 1; set { if (value) { SourceIndex = 1; } } }
		public bool IsPollOnly { get => SourceIndex == 2; set { if (value) { SourceIndex = 2; } } }

		// ── The NOAA Weather Wire account ──

		/// <summary>The account name (saved as typed; the password only on Save).</summary>
		public string NwwsUsername
		{
			get => _settings.NwwsUsername;
			set
			{
				if (_settings.NwwsUsername == (value ?? "")) { return; }
				_settings.NwwsUsername = value ?? "";
				OnPropertyChanged();
				OnPropertyChanged(nameof(PasswordPlaceholder));
			}
		}

		/// <summary>"Saved" in the empty password box when Credential Manager holds one for this name.</summary>
		public string PasswordPlaceholder => _hub.SavedNwwsPassword() is null ? "Password" : "Saved — type to replace";

		/// <summary>The password box's text (from the tab's code-behind) — held only until Save or Test.</summary>
		public void SetPendingPassword(string password) => _pendingPassword = password ?? string.Empty;

		/// <summary>"Saved." · "Signed in. The account works." · what went wrong.</summary>
		public string AccountMessage { get => _accountMessage; private set => SetProperty(ref _accountMessage, value); }

		public bool IsTesting
		{
			get => _isTesting;
			private set { if (SetProperty(ref _isTesting, value)) { OnPropertyChanged(nameof(CanTest)); } }
		}

		public bool CanTest => !_isTesting;

		/// <summary>Saves the account (password → Credential Manager) and reconnects if NOAA Weather Wire is the source.</summary>
		/// <returns>True when the password was saved (the tab then clears its box).</returns>
		public bool SaveAccount()
		{
			if (string.IsNullOrWhiteSpace(NwwsUsername) || _pendingPassword.Length == 0)
			{
				AccountMessage = "Enter the user name and password first.";
				return false;
			}
			var saved = _hub.SaveNwwsAccount(NwwsUsername, _pendingPassword);
			AccountMessage = saved ? "Saved." : "Windows wouldn't save the password.";
			OnPropertyChanged(nameof(PasswordPlaceholder));
			if (saved && IsNwws) { _hub.Restart(); }
			return saved;
		}

		/// <summary>Signs in and back out with what's typed (or the saved password).</summary>
		public async Task TestConnectionAsync()
		{
			if (IsTesting) { return; }
			IsTesting = true;
			AccountMessage = "Signing in…";
			try
			{
				var password = _pendingPassword.Length > 0 ? _pendingPassword : _hub.SavedNwwsPassword() ?? string.Empty;
				AccountMessage = await _hub.TestNwwsAsync(NwwsUsername, password, CancellationToken.None);
			}
			finally
			{
				IsTesting = false;
			}
		}

		// ── Polling ──

		/// <summary>One row per feed (Settings order): its interval, floor, and what it does while connected.</summary>
		public ReadOnlyCollection<LivePollRow> Polling { get; }

		/// <summary>The tab's "Restore defaults": IEM, and every interval back to its default. The account is kept.</summary>
		public void RestoreDefaults()
		{
			foreach (var row in Polling) { row.Reset(); }
			IsIem = true;
		}

		// ── How the connection stands (Settings → Connection, and the expander's source line) ──

		public LiveLinkState LinkState { get => _linkState; private set => SetProperty(ref _linkState, value); }

		/// <summary>The activity expander's last line: "Live via IEM · connected since 11:02 AM" · "IEM lost at 12:08:31 PM ·
		/// polling until it's back" · "Polling only · Settings → Live Data" …</summary>
		public string SourceLine { get => _sourceLine; private set => SetProperty(ref _sourceLine, value); }

		/// <summary>Settings' Status row: "IEM · connected since 11:02:14 AM".</summary>
		public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

		/// <summary>Settings' Last product row: "12:04:31 PM · Severe thunderstorm warning, OUN" or "None yet".</summary>
		public string LastProductText { get => _lastProductText; private set => SetProperty(ref _lastProductText, value); }

		/// <summary>Settings' Missed products row.</summary>
		public string MissedText { get => _missedText; private set => SetProperty(ref _missedText, value); }

		private void Refresh()
		{
			var s = _hub.Snapshot;
			var name = LiveSources.NameOf(s.Source);
			LinkState = s.State;
			SourceLine = LineOf(s, name);
			StatusText = s.State switch
			{
				LiveLinkState.Off => "Polling only — no live source",
				LiveLinkState.Connecting => $"{name} · connecting…",
				LiveLinkState.Connected => $"{name} · connected since {Clock(s.Since)}",
				LiveLinkState.Down => $"{name} · {s.Reason ?? "down"} since {Clock(s.Since)} · polling",
				_ => $"{name} · needs an account · polling",
			};
			LastProductText = s.LastProduct is { } p
				? $"{FeedStatus.Clock(p.Received)} · {LiveFeedKinds.DescribeProduct(p.AwipsId)}{OfficeOf(p.Office)}"
				: "None yet";
			MissedText = s.Source != LiveSources.Nwws
				? "Not tracked (only NOAA Weather Wire numbers its products)"
				: s.Missed == 0 ? "None this session" : $"{s.Missed} this session · each one caught up";
		}

		/// <summary>The source line's words. Internal for tests.</summary>
		internal static string LineOf(LiveFeedSnapshot s, string name) => s.State switch
		{
			LiveLinkState.Connected => $"Live via {name} · connected since {ClockMinutes(s.Since)}",
			LiveLinkState.Connecting => $"Connecting to {name}… · polling meanwhile",
			LiveLinkState.Down when s.Reason?.Contains("refused", StringComparison.OrdinalIgnoreCase) == true =>
				$"{name} refused the sign-in · polling",
			LiveLinkState.Down => $"{name} lost at {Clock(s.Since)} · polling until it's back",
			LiveLinkState.NeedsAccount => $"{name} needs an account (Settings → Live Data) · polling",
			_ => "Polling only · Settings → Live Data",
		};

		// "KOUN" → ", OUN" (the NWS office id people know); blank → nothing.
		private static string OfficeOf(string office) =>
			string.IsNullOrWhiteSpace(office) ? string.Empty : ", " + (office.Length == 4 && office[0] == 'K' ? office[1..] : office);

		private static string Clock(DateTimeOffset? t) => t is { } v ? FeedStatus.Clock(v) : "—";

		private static string ClockMinutes(DateTimeOffset? t) => t is { } v ? FeedStatus.ClockMinutes(v) : "—";
	}

	/// <summary>One feed's row in Settings → Live Data → Polling.</summary>
	public sealed class LivePollRow : ObservableObject
	{
		private readonly AppSettings _settings;

		internal LivePollRow(LiveFeedKind kind, AppSettings settings)
		{
			Kind = kind;
			_settings = settings;
		}

		public LiveFeedKind Kind { get; }

		public string Name => LiveFeedKinds.NameOf(Kind);

		/// <summary>The WHEN CONNECTED column: "live" (fetched when a product is issued) or "polled" (storm cells).</summary>
		public string WhenConnected => LiveFeedKinds.IsPushed(Kind) ? "live" : "polled";

		/// <summary>The interval in seconds (the NumberBox, two-way). Clamped to the floor and the ceiling.</summary>
		public double Seconds
		{
			get => LiveFeedKinds.PollOf(Kind, _settings.PollSecondsOf(Kind)).TotalSeconds;
			set
			{
				if (double.IsNaN(value)) { OnPropertyChanged(); return; } // a cleared box snaps back
				var clamped = LiveFeedKinds.PollOf(Kind, (int)Math.Round(value));
				var seconds = (int)clamped.TotalSeconds;
				var stored = clamped == LiveFeedKinds.DefaultPoll(Kind) ? (int?)null : seconds;
				if (_settings.PollSecondsOf(Kind) != stored) { _settings.SetPollSeconds(Kind, stored); }
				OnPropertyChanged();
			}
		}

		public double MinimumSeconds => LiveFeedKinds.MinimumPoll(Kind).TotalSeconds;

		public double MaximumSeconds => LiveFeedKinds.MaximumPoll.TotalSeconds;

		/// <summary>The MINIMUM column: "15 s" / "2 min".</summary>
		public string MinimumText => FeedStatus.EveryWords(LiveFeedKinds.MinimumPoll(Kind));

		internal void Reset()
		{
			_settings.SetPollSeconds(Kind, null);
			OnPropertyChanged(nameof(Seconds));
		}
	}
}
