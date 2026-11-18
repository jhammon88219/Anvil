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
		private bool _isTesting, _hasSavedAccount, _isEditingUser, _isEditingPassword, _isDetailsOpen;
		private string _draftUsername;
		private string _sourceLine = string.Empty, _statusText = string.Empty, _lastProductText = string.Empty, _missedText = string.Empty;
		private LiveLinkState _linkState;

		public LiveDataViewModel(LiveFeedHub hub, AppSettings settings, IDispatcher dispatcher)
		{
			_hub = hub;
			_settings = settings;
			_dispatcher = dispatcher;
			Polling = new ReadOnlyCollection<LivePollRow>(LiveFeedKinds.All.Select(k => new LivePollRow(k, settings)).ToList());
			Timing = new LiveSourceTimingViewModel(hub, dispatcher);
			_draftUsername = settings.NwwsUsername.Trim();
			_hasSavedAccount = hub.SavedNwwsPassword() is not null;
			hub.Changed += (_, _) => _dispatcher.Post(Refresh);
			Refresh();
		}

		/// <summary>DEV → Live source timing: IEM and Weather Wire side by side (does nothing until started).</summary>
		public LiveSourceTimingViewModel Timing { get; }

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
				OnPropertyChanged(nameof(SourceHint));
				OnPropertyChanged(nameof(SourceAbout));
				OnPropertyChanged(nameof(AccountHint));
				OnPropertyChanged(nameof(DetailsConnection));
				_hub.Restart();
			}
		}

		/// <summary>Settings → Live Data → Source: the picker's segment names, in <see cref="LiveSources.All"/> order.</summary>
		public System.Collections.Generic.IReadOnlyList<string> SourceLabels { get; } = new[] { "IEM live feed", "NOAA Weather Wire", "Polling only" };

		/// <summary>The source's one-line hint — what the pick gives you.</summary>
		public string SourceHint => Services.SettingsText.ChoiceOf("live.source", SourceIndex is 1 or 2 ? SourceIndex : 0);

		/// <summary>The Updates from row's About text — what the PICKED source is, gives and costs (follows the pick).</summary>
		public string SourceAbout => Services.SettingsText.ChoiceAboutOf("live.source", SourceIndex is 1 or 2 ? SourceIndex : 0);

		/// <summary>The Source card's DEFAULT column.</summary>
		public string DefaultSourceText => SourceLabels[0];

		/// <summary>The Source card's Restore defaults: IEM. The account is kept.</summary>
		public void ResetSource() => IsIem = true;

		/// <summary>The Polling card's Restore defaults: every interval back to its default.</summary>
		public void ResetPolling()
		{
			foreach (var row in Polling) { row.Reset(); }
		}

		// The three radio buttons (two-way; only a TRUE lands — a radio unticking itself is ignored).
		public bool IsIem { get => SourceIndex == 0; set { if (value) { SourceIndex = 0; } } }
		public bool IsNwws { get => SourceIndex == 1; set { if (value) { SourceIndex = 1; } } }
		public bool IsPollOnly { get => SourceIndex == 2; set { if (value) { SourceIndex = 2; } } }

		// ── The NOAA Weather Wire account ──

		// A SAVED account shows LOCKED: both boxes grey, each with an Edit key that unlocks it (and turns into Cancel).
		// The user name box is a DRAFT — nothing reaches settings until Sign in (SaveAccount); it lights only when something changed.

		/// <summary>The NWS page to ask for an account (an email request; the NWS says up to 10 days or more).</summary>
		public const string RequestAccountUrl = "https://www.weather.gov/nwws/nwws_oi_request";

		/// <summary>The user name box (two-way) — a draft until Save.</summary>
		public string NwwsUsername
		{
			get => _draftUsername;
			set
			{
				if (SetProperty(ref _draftUsername, value ?? string.Empty))
				{
					AccountMessage = string.Empty;
					RaiseAccount();
				}
			}
		}

		/// <summary>The account name as SAVED (settings), for the details and the hint.</summary>
		public string SavedUsername => _settings.NwwsUsername.Trim();

		/// <summary>True once Credential Manager holds a password for the saved name.</summary>
		public bool HasSavedAccount => _hasSavedAccount;

		public bool IsEditingUser { get => _isEditingUser; private set { if (SetProperty(ref _isEditingUser, value)) { RaiseAccount(); } } }

		public bool IsEditingPassword { get => _isEditingPassword; private set { if (SetProperty(ref _isEditingPassword, value)) { RaiseAccount(); } } }

		/// <summary>The user name box takes typing: no saved account yet, or its Edit was pressed.</summary>
		public bool IsUserOpen => !_hasSavedAccount || _isEditingUser;

		/// <summary>The password box takes typing: no saved account yet, or its Edit was pressed.</summary>
		public bool IsPasswordOpen => !_hasSavedAccount || _isEditingPassword;

		public string UserEditLabel => _isEditingUser ? "Cancel" : "Edit";

		public string PasswordEditLabel => _isEditingPassword ? "Cancel" : "Edit";

		/// <summary>The password box's placeholder: dots for a saved one, a prompt while editing.</summary>
		public string PasswordPlaceholder => !_hasSavedAccount ? "Password" : _isEditingPassword ? "Type the new password" : "●●●●●●●●●●";

		/// <summary>The password box's text (from the tab's code-behind) — held only until Save, Test or Cancel.</summary>
		public void SetPendingPassword(string password)
		{
			var was = _pendingPassword.Length > 0;
			_pendingPassword = password ?? string.Empty;
			if (was != _pendingPassword.Length > 0) { AccountMessage = string.Empty; RaiseAccount(); }
		}

		/// <summary>Something differs from what's saved (a first account: both boxes filled).</summary>
		public bool HasChanges => _hasSavedAccount
			? !string.Equals(_draftUsername.Trim(), SavedUsername, StringComparison.Ordinal) || _pendingPassword.Length > 0
			: _draftUsername.Trim().Length > 0 && _pendingPassword.Length > 0;

		public bool CanSave => HasChanges && _draftUsername.Trim().Length > 0;

		/// <summary>"Saved." · "Signed in. The account works." · what went wrong — the last action's words.</summary>
		public string AccountMessage
		{
			get => _accountMessage;
			private set { if (SetProperty(ref _accountMessage, value)) { OnPropertyChanged(nameof(AccountHint)); } }
		}

		/// <summary>The buttons row's hint: the last action's words, else where the account stands.</summary>
		public string AccountHint =>
			_accountMessage.Length > 0 ? _accountMessage
			: _hasSavedAccount && HasChanges ? "Changed · Sign in to save it · Test connection tries it"
			: _hasSavedAccount && IsNwws ? LinkState switch
			{
				LiveLinkState.Connected => $"Signed in as {SavedUsername}",
				LiveLinkState.Connecting => $"Signing in as {SavedUsername}…",
				_ => $"Saved as {SavedUsername} · not signed in",
			}
			: _hasSavedAccount ? $"Saved as {SavedUsername} · used when Weather Wire is the source"
			: "Fill in both to test or sign in";

		public bool IsTesting
		{
			get => _isTesting;
			private set { if (SetProperty(ref _isTesting, value)) { OnPropertyChanged(nameof(CanTest)); } }
		}

		/// <summary>A name and a password (typed, or the saved one) to try, and no test running.</summary>
		public bool CanTest => !_isTesting && _draftUsername.Trim().Length > 0 && (_pendingPassword.Length > 0 || _hasSavedAccount);

		/// <summary>The user name's Edit / Cancel. Cancel puts the saved name back.</summary>
		public void ToggleEditUser()
		{
			if (_isEditingUser) { NwwsUsername = SavedUsername; }
			AccountMessage = string.Empty;
			IsEditingUser = !_isEditingUser;
		}

		/// <summary>The password's Edit / Cancel. On Cancel the tab clears its box (→ <see cref="SetPendingPassword"/>).</summary>
		public void ToggleEditPassword()
		{
			AccountMessage = string.Empty;
			IsEditingPassword = !_isEditingPassword;
		}

		/// <summary>Saves the account (password → Credential Manager) and reconnects if NOAA Weather Wire is the source.
		/// A rename alone keeps the saved password.</summary>
		/// <returns>True when it was saved (the tab then clears its password box; both boxes lock again).</returns>
		public bool SaveAccount()
		{
			// Read the saved password BEFORE the save moves the name it's filed under.
			var password = _pendingPassword.Length > 0 ? _pendingPassword : _hub.SavedNwwsPassword() ?? string.Empty;
			if (_draftUsername.Trim().Length == 0 || password.Length == 0)
			{
				AccountMessage = "Enter the user name and password first";
				return false;
			}
			var saved = _hub.SaveNwwsAccount(_draftUsername, password);
			// Saved → no message, so the hint follows the connection ("Signing in as …" → "Signed in as …").
			AccountMessage = saved ? string.Empty : "Windows wouldn't save the password";
			if (saved)
			{
				_draftUsername = SavedUsername;
				OnPropertyChanged(nameof(NwwsUsername));
				_isEditingUser = _isEditingPassword = false;
				if (IsNwws) { _hub.Restart(); }
			}
			RefreshSaved();
			return saved;
		}

		/// <summary>Signs in and back out with what's typed (or the saved password).</summary>
		public async Task TestConnectionAsync()
		{
			if (!CanTest) { return; }
			IsTesting = true;
			AccountMessage = "Signing in…";
			try
			{
				var password = _pendingPassword.Length > 0 ? _pendingPassword : _hub.SavedNwwsPassword() ?? string.Empty;
				AccountMessage = (await _hub.TestNwwsAsync(_draftUsername.Trim(), password, CancellationToken.None)).TrimEnd('.');
			}
			finally
			{
				IsTesting = false;
			}
		}

		// ── Account details (facts only — the password is NEVER shown in Anvil) ──

		public bool IsDetailsOpen
		{
			get => _isDetailsOpen;
			private set { if (SetProperty(ref _isDetailsOpen, value)) { OnPropertyChanged(nameof(DetailsButtonLabel)); } }
		}

		public string DetailsButtonLabel => _isDetailsOpen ? "Hide details" : "Account details";

		public void ToggleDetails() => IsDetailsOpen = !_isDetailsOpen;

		public string DetailsStoredIn => $"Windows Credential Manager · Web Credentials · \"{LiveFeedHub.NwwsCredentialResource}\"";

		public string DetailsServer => NwwsOiSource.Host;

		/// <summary>How the account stands right now: the Connection plate's status while it's the source.</summary>
		public string DetailsConnection => IsNwws ? StatusText : $"Not in use · updates come from {LiveSources.NameOf(LiveSources.Normalize(_settings.LiveSource))}";

		/// <summary>Forget account (the tab asks first): password out of Credential Manager, name out of settings.</summary>
		public void ForgetAccount()
		{
			_hub.ForgetNwwsAccount();
			_draftUsername = string.Empty;
			OnPropertyChanged(nameof(NwwsUsername));
			_isEditingUser = _isEditingPassword = false;
			IsDetailsOpen = false;
			AccountMessage = "Account forgotten";
			RefreshSaved();
		}

		private void RefreshSaved()
		{
			_hasSavedAccount = _hub.SavedNwwsPassword() is not null;
			RaiseAccount();
		}

		private void RaiseAccount()
		{
			OnPropertyChanged(nameof(HasSavedAccount));
			OnPropertyChanged(nameof(SavedUsername));
			OnPropertyChanged(nameof(IsEditingUser));
			OnPropertyChanged(nameof(IsEditingPassword));
			OnPropertyChanged(nameof(IsUserOpen));
			OnPropertyChanged(nameof(IsPasswordOpen));
			OnPropertyChanged(nameof(UserEditLabel));
			OnPropertyChanged(nameof(PasswordEditLabel));
			OnPropertyChanged(nameof(PasswordPlaceholder));
			OnPropertyChanged(nameof(HasChanges));
			OnPropertyChanged(nameof(CanSave));
			OnPropertyChanged(nameof(CanTest));
			OnPropertyChanged(nameof(AccountHint));
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
			OnPropertyChanged(nameof(AccountHint));     // "Signed in as …" follows the link
			OnPropertyChanged(nameof(DetailsConnection));
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

		/// <summary>Beside the number box: "seconds · min 15 s".</summary>
		public string UnitText => "seconds · min " + MinimumText;

		/// <summary>True = a live source carries it while connected (the "Live" tag); false = always polled.</summary>
		public bool IsLive => LiveFeedKinds.IsPushed(Kind);

		/// <summary>The hint after the tag.</summary>
		public string LiveHint => Services.SettingsText.ChoiceOf("polling.feed", IsLive ? 0 : 1);

		/// <summary>The DEFAULT column: "30 s" / "2 min".</summary>
		public string DefaultText => FeedStatus.EveryWords(LiveFeedKinds.DefaultPoll(Kind));

		internal void Reset()
		{
			_settings.SetPollSeconds(Kind, null);
			OnPropertyChanged(nameof(Seconds));
		}
	}
}
