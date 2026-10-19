using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Microsoft.Extensions.Logging;

namespace Anvil.Services
{
	/// <summary>How the chosen live source stands.</summary>
	public enum LiveLinkState
	{
		/// <summary>Polling only was chosen — no source runs.</summary>
		Off,
		/// <summary>Starting up, or switched to a new source and not yet receiving.</summary>
		Connecting,
		/// <summary>Receiving products: the pushed feeds' schedules are paused.</summary>
		Connected,
		/// <summary>Lost (or never made) its connection: every feed polls until it's back.</summary>
		Down,
		/// <summary>NOAA Weather Wire chosen without a saved account: every feed polls.</summary>
		NeedsAccount,
	}

	/// <summary>What the hub knows right now, for the activity expander's source line and Settings → Live Data.</summary>
	/// <param name="Since">Connected since (Connected) or lost at (Down).</param>
	/// <param name="LastProduct">The newest product that woke a feed this session.</param>
	/// <param name="Missed">Products the source's own numbering says it skipped this session (NWWS-OI only).</param>
	public sealed record LiveFeedSnapshot(string Source, LiveLinkState State, DateTimeOffset? Since, string? Reason,
		LiveProduct? LastProduct, int Missed);

	/// <summary>
	/// THE live-update hub: runs the ONE source chosen in Settings → Live Data, de-duplicates and routes its products, and
	/// owns every NowCast feed's schedule (<see cref="Channel"/>). The feed VMs only ask it how long to wait.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE POLICY (the user's, 2026-10-19 — details in <see cref="LiveFeedChannel"/>): while the source is CONNECTED a
	/// pushed feed fetches only when a product wakes it (+ re-checks + a 10-min safety); leaving Connected (a drop, a
	/// source switch, Polling only) wakes every pushed feed for a catch-up fetch, and they poll on the user's intervals
	/// until it's back; coming back from Down wakes them again (what was missed); an NWWS-OI numbering gap does too.
	/// ⚠️ A STALE RUN IS IGNORED: each <see cref="Restart"/> bumps a generation and a source's late reports carry the old one.
	/// ⚠️ Any thread; <see cref="Changed"/> is raised on whichever thread the change came from (the VM posts it over).
	/// </remarks>
	public sealed class LiveFeedHub
	{
		/// <summary>The Windows Credential Manager resource the NWWS-OI password is saved under.</summary>
		public const string NwwsCredentialResource = "Anvil NOAA Weather Wire";

		private const int RecentIdCap = 4096;

		private readonly ISettingsService _settings;
		private readonly ICredentialStore? _credentials;
		private readonly Func<string, ILiveProductSource?> _sourceFor;
		private readonly ILogger _logger;
		private readonly object _gate = new();
		private readonly Dictionary<LiveFeedKind, LiveFeedChannel> _channels = new();
		private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
		private readonly Queue<string> _recentOrder = new();
		private CancellationTokenSource? _run;
		private int _generation;
		private LiveFeedSnapshot _snapshot;
		private bool _shutdown;

		public LiveFeedHub(ISettingsService settings, ICredentialStore credentials, ILogger<LiveFeedHub> logger)
			: this(settings, credentials, null, logger)
		{
		}

		/// <param name="sourceFor">Builds the source for a token (null = the real ones). Internal for tests.</param>
		internal LiveFeedHub(ISettingsService settings, ICredentialStore? credentials, Func<string, ILiveProductSource?>? sourceFor, ILogger logger)
		{
			_settings = settings;
			_credentials = credentials;
			_logger = logger;
			var http = new Lazy<HttpClient>(() =>
			{
				var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
				c.DefaultRequestHeaders.UserAgent.ParseAdd("Anvil/1.0 (severe-weather app)");
				return c;
			});
			_sourceFor = sourceFor ?? (token => RealSource(token, http));
			_snapshot = new LiveFeedSnapshot(Source, LiveLinkState.Connecting, null, null, null, 0);
			foreach (var kind in LiveFeedKinds.All)
			{
				var k = kind;
				_channels[k] = new LiveFeedChannel(k, () => State == LiveLinkState.Connected,
					() => LiveFeedKinds.PollOf(k, _settings.Settings.PollSecondsOf(k)));
			}
		}

		/// <summary>Raised whenever <see cref="Snapshot"/> changes (any thread).</summary>
		public event EventHandler? Changed;

		/// <summary>The chosen source's token (normalized).</summary>
		public string Source => LiveSources.Normalize(_settings.Settings.LiveSource);

		public LiveFeedSnapshot Snapshot { get { lock (_gate) { return _snapshot; } } }

		public LiveLinkState State { get { lock (_gate) { return _snapshot.State; } } }

		/// <summary>The schedule of one feed — handed to its VM once, at construction.</summary>
		public LiveFeedChannel Channel(LiveFeedKind kind) => _channels[kind];

		/// <summary>Starts the chosen source. Called once at launch, BEFORE the feeds' loops start (so their first
		/// cycle can already find the source connected).</summary>
		public void Start() => Restart();

		/// <summary>(Re)starts from the current settings — a source pick, a saved account. Cancels the old run first.</summary>
		public void Restart()
		{
			CancellationTokenSource? old;
			CancellationTokenSource? run = null;
			ILiveProductSource? source = null;
			int generation;
			var token = Source;
			var state = LiveLinkState.Connecting;
			lock (_gate)
			{
				if (_shutdown) { return; }
				old = _run;
				_run = null;
				generation = ++_generation;
				if (token == LiveSources.Poll) { state = LiveLinkState.Off; }
				else if ((source = _sourceFor(token)) is null) { state = LiveLinkState.NeedsAccount; }
				else { _run = run = new CancellationTokenSource(); }
			}
			old?.Cancel();
			old?.Dispose();
			_logger.LogInformation("Live feed: source {Source} → {State}", token, state);
			SetState(generation, state, null, newSource: token);
			if (source is not null && run is not null)
			{
				var sink = new Sink(this, generation);
				var ct = run.Token;
				_ = Task.Run(async () =>
				{
					try { await source.RunAsync(sink, ct).ConfigureAwait(false); }
					catch (OperationCanceledException) { }
					catch (Exception ex) { _logger.LogWarning(ex, "Live feed source {Source} stopped", token); sink.Down("stopped unexpectedly"); }
				});
			}
		}

		/// <summary>Stops the source for good (MapViewModel.Shutdown).</summary>
		public void Shutdown()
		{
			CancellationTokenSource? run;
			lock (_gate)
			{
				_shutdown = true;
				run = _run;
				_run = null;
				_generation++;
			}
			run?.Cancel();
		}

		/// <summary>Signs in to NOAA Weather Wire and back out — Settings' "Test connection". Plain-words result.</summary>
		public Task<string> TestNwwsAsync(string user, string password, CancellationToken ct) =>
			NwwsOiSource.TestAsync(user, password, _logger, ct);

		/// <summary>The saved NWWS-OI password for the settings' account name, or null.</summary>
		public string? SavedNwwsPassword()
		{
			var user = _settings.Settings.NwwsUsername;
			return string.IsNullOrWhiteSpace(user) ? null : _credentials?.Read(NwwsCredentialResource, user.Trim());
		}

		/// <summary>Saves the account (name in settings, password in Credential Manager). False = the store refused it.</summary>
		public bool SaveNwwsAccount(string user, string password)
		{
			user = user.Trim();
			_settings.Settings.NwwsUsername = user;
			if (_credentials is null) { return false; }
			_credentials.Delete(NwwsCredentialResource); // one account at a time
			return _credentials.Write(NwwsCredentialResource, user, password);
		}

		private ILiveProductSource? RealSource(string token, Lazy<HttpClient> http)
		{
			if (token == LiveSources.Nwws)
			{
				var user = _settings.Settings.NwwsUsername.Trim();
				var password = SavedNwwsPassword();
				return string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) ? null : new NwwsOiSource(user, password, _logger);
			}
			return new IemBotSource(http.Value, _logger);
		}

		// ── The source's reports (Sink → here) ──

		private void OnConnected(int generation)
		{
			var wasDown = State == LiveLinkState.Down;
			if (!SetState(generation, LiveLinkState.Connected, null)) { return; }
			_logger.LogInformation("Live feed: {Source} connected", Source);
			// Back from a drop: fetch what was missed. (A first connect needs none — the launch fetches hold it.)
			if (wasDown) { WakePushed(); }
		}

		private void OnDown(int generation, string reason)
		{
			if (!SetState(generation, LiveLinkState.Down, reason)) { return; }
			_logger.LogInformation("Live feed: {Source} down ({Reason})", Source, reason);
		}

		private void OnProduct(int generation, LiveProduct product)
		{
			if (LiveFeedKinds.KindOfProduct(product.AwipsId) is not { } kind || !LiveFeedKinds.IsPushed(kind)) { return; }
			lock (_gate)
			{
				if (generation != _generation) { return; }
				if (!_recentIds.Add(product.ProductId)) { return; }
				_recentOrder.Enqueue(product.ProductId);
				if (_recentOrder.Count > RecentIdCap) { _recentIds.Remove(_recentOrder.Dequeue()); }
				_snapshot = _snapshot with { LastProduct = product };
			}
			_logger.LogInformation("Live feed: {Product} → {Kind}", product.ProductId, kind);
			// The outlooks re-fetch only the files the product announces (a SWODY1 = Day 1's four convective files).
			_channels[kind].Trigger(kind == LiveFeedKind.Outlooks ? SpcOutlookCatalog.IdsForProduct(product.AwipsId) : null);
			Changed?.Invoke(this, EventArgs.Empty);
		}

		private void OnGap(int generation, int missed)
		{
			lock (_gate)
			{
				if (generation != _generation) { return; }
				_snapshot = _snapshot with { Missed = _snapshot.Missed + missed };
			}
			_logger.LogInformation("Live feed: {Missed} product(s) missed — catching up", missed);
			WakePushed();
			Changed?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>Moves to <paramref name="state"/>; false when the report is from a stale run or changes nothing.
		/// Leaving Connected wakes every pushed feed so it fetches now and falls back to polling.</summary>
		private bool SetState(int generation, LiveLinkState state, string? reason, string? newSource = null)
		{
			bool leftConnected;
			lock (_gate)
			{
				if (generation != _generation) { return false; }
				var old = _snapshot;
				if (newSource is null && old.State == state) { return false; }
				leftConnected = old.State == LiveLinkState.Connected && state != LiveLinkState.Connected;
				var since = state is LiveLinkState.Connected or LiveLinkState.Down ? DateTimeOffset.Now : (DateTimeOffset?)null;
				_snapshot = newSource is null
					? old with { State = state, Since = since, Reason = reason }
					: new LiveFeedSnapshot(newSource, state, since, reason, null, 0);
			}
			if (leftConnected) { WakePushed(); }
			Changed?.Invoke(this, EventArgs.Empty);
			return true;
		}

		private void WakePushed()
		{
			foreach (var c in _channels.Values.Where(c => LiveFeedKinds.IsPushed(c.Kind))) { c.Wake(); }
		}

		private sealed class Sink : ILiveSourceSink
		{
			private readonly LiveFeedHub _hub;
			private readonly int _generation;

			public Sink(LiveFeedHub hub, int generation)
			{
				_hub = hub;
				_generation = generation;
			}

			public void Connected() => _hub.OnConnected(_generation);
			public void Down(string reason) => _hub.OnDown(_generation, reason);
			public void Product(LiveProduct product) => _hub.OnProduct(_generation, product);
			public void Gap(int missed) => _hub.OnGap(_generation, missed);
		}
	}
}
