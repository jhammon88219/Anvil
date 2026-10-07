using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// Mesoscale discussions — SPC MDs and WPC MPDs (every <see cref="DiscussionKinds"/> kind) — as map areas
	/// AND as something to READ: a list of the window's discussions, a reader for the selected one (split
	/// into its sections), next/previous, and a click on the map selecting the discussion under it.
	/// NowCast and PastCast. Fetch/cache/text is <see cref="IMesoDiscussionService"/>.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE MAP DRAWS WHAT WAS IN EFFECT AT A MOMENT; THE LIST HOLDS THE WHOLE WINDOW. In PastCast the moment
	/// is the displayed radar frame (they follow the scrubber, like the alerts and storm cells); in NowCast it
	/// is now. The list keeps everything the window saw, so a discussion that expired an hour ago is still one
	/// click from being read — which live, on a quiet day, is most of them.
	/// <para>⚠️ KINDS ARE DATA: <see cref="Kinds"/> is built from <see cref="DiscussionKinds.All"/>, rendered by
	/// an ItemsControl, and persisted as ONE list of hidden ids — another kind needs no new property, row or
	/// setting.</para>
	/// </remarks>
	public sealed class MesoDiscussionsViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IMesoDiscussionService _service;
		private readonly RadarViewModel _radar;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<MesoDiscussionsViewModel> _logger;

		private static readonly TimeSpan LiveSpan = TimeSpan.FromHours(24);
		private static readonly TimeSpan LiveRefresh = TimeSpan.FromMinutes(2);

		private readonly CancellationTokenSource _shutdown = new();
		private bool _isMapReady;
		private int _applyToken;
		private int _textToken;
		private FetchKey? _loadedKey;
		private FetchKey? _inFlightKey;
		private DateTimeOffset? _moment;

		private sealed record FetchKey(bool Live, DateTimeOffset Start, DateTimeOffset End);

		public MesoDiscussionsViewModel(IMapService mapService, IMesoDiscussionService service, RadarViewModel radar,
			IDispatcher dispatcher, ILogger<MesoDiscussionsViewModel> logger)
		{
			_mapService = mapService;
			_service = service;
			_radar = radar;
			_dispatcher = dispatcher;
			_logger = logger;
			Kinds = new ReadOnlyCollection<DiscussionKindRow>(
				DiscussionKinds.All.Select(k => new DiscussionKindRow(k, OnKindToggled)).ToList());
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Kinds (one row each) ──

		/// <summary>One row per kind: its toggle, colour and in-effect count.</summary>
		public IReadOnlyList<DiscussionKindRow> Kinds { get; }

		public bool AnyShown => Kinds.Any(k => k.IsShown);

		/// <summary>The section header's tri-state.</summary>
		public bool? AllShown => Kinds.All(k => k.IsShown) ? true : AnyShown ? null : false;

		public void ToggleAll()
		{
			var on = AllShown != true;
			foreach (var k in Kinds) { k.SetSilently(on); }
			OnKindToggled();
		}

		/// <summary>The hidden kinds' ids — the persisted form (null/empty = all shown).</summary>
		public List<string> HiddenKindIds => Kinds.Where(k => !k.IsShown).Select(k => k.Kind.Id).ToList();

		/// <summary>Restores <see cref="HiddenKindIds"/>; unknown ids are ignored, unlisted kinds are shown.</summary>
		public void RestoreHidden(IEnumerable<string> hidden)
		{
			var set = new HashSet<string>(hidden, StringComparer.Ordinal);
			foreach (var k in Kinds) { k.SetSilently(!set.Contains(k.Kind.Id)); }
			OnKindToggled();
		}

		/// <summary>Raised when a kind is toggled (the persistence hook).</summary>
		public event EventHandler? KindsChanged;

		private void OnKindToggled()
		{
			OnPropertyChanged(nameof(AnyShown));
			OnPropertyChanged(nameof(AllShown));
			OnPropertyChanged(nameof(CardFooter));
			KindsChanged?.Invoke(this, EventArgs.Empty);
			_ = PushKindsAsync();
		}

		private Task PushKindsAsync() => !_isMapReady ? Task.CompletedTask :
			_mapService.SetDiscussionKindsAsync(_isModeActive ? string.Join(",", Kinds.Where(k => k.IsShown).Select(k => k.Kind.Id)) : string.Empty);

		private bool _isModeActive;

		/// <summary>Whether NowCast or PastCast is running — set by <c>MapViewModel</c>.</summary>
		public bool IsModeActive
		{
			get => _isModeActive;
			set
			{
				if (!SetProperty(ref _isModeActive, value)) { return; }
				_ = PushKindsAsync();
				Reconcile();
			}
		}

		private double _opacity = 1.0;
		public double Opacity
		{
			get => _opacity;
			set { if (SetProperty(ref _opacity, value) && _isMapReady) { _ = _mapService.SetDiscussionsOpacityAsync(value); } }
		}

		// ── The list ──

		/// <summary>Every discussion of the window, oldest first.</summary>
		public ObservableCollection<DiscussionRow> Discussions { get; } = new();

		public bool HasDiscussions => Discussions.Count > 0;

		private DiscussionRow? _selected;

		/// <summary>The discussion the reader shows (and the map outlines). Two-way with the list.</summary>
		public DiscussionRow? Selected
		{
			get => _selected;
			set
			{
				// A live refresh rebuilds the rows; re-selecting the SAME discussion must not reload (and flash)
				// the text being read.
				var sameDiscussion = _selected?.Discussion.Key == value?.Discussion.Key;
				var previous = _selected;
				if (!SetProperty(ref _selected, value)) { return; }
				if (previous is not null) { previous.IsSelected = false; }
				if (value is not null) { value.IsSelected = true; }
				OnPropertyChanged(nameof(HasSelection));
				OnPropertyChanged(nameof(CanStepPrevious));
				OnPropertyChanged(nameof(CanStepNext));
				if (sameDiscussion) { return; }
				if (_isMapReady) { _ = _mapService.SetDiscussionSelectedAsync(value?.Discussion.Key ?? string.Empty); }
				_ = LoadTextAsync(value);
			}
		}

		public bool HasSelection => _selected is not null;

		public bool CanStepPrevious => _selected is not null && Discussions.IndexOf(_selected) > 0;
		public bool CanStepNext => _selected is not null && Discussions.IndexOf(_selected) < Discussions.Count - 1;

		/// <summary>Reads the previous (−1) or next (+1) discussion in time order.</summary>
		public void Step(int delta)
		{
			if (Discussions.Count == 0) { return; }
			var i = _selected is null ? (delta > 0 ? -1 : Discussions.Count) : Discussions.IndexOf(_selected);
			var j = Math.Clamp(i + delta, 0, Discussions.Count - 1);
			Selected = Discussions[j];
		}

		/// <summary>A click on the map (discussions.js → WebMessageRouter): read the discussion under it.</summary>
		public void SelectByKey(string key)
		{
			var row = Discussions.FirstOrDefault(d => d.Discussion.Key == key);
			if (row is null) { return; }
			OpenReader(row);
		}

		/// <summary>
		/// Open the reader (the Mesoscale Discussions WINDOW) on <paramref name="row"/>; with none, keep what is
		/// selected, else start on the first discussion in effect, else the first listed.
		/// </summary>
		public void OpenReader(DiscussionRow? row = null)
		{
			Selected = row ?? _selected ?? InEffect.FirstOrDefault() ?? Discussions.FirstOrDefault();
			ReaderRequested?.Invoke(this, EventArgs.Empty);
		}

		/// <summary>Frame the selected discussion's area on the map.</summary>
		public void ShowSelectedOnMap()
		{
			if (_selected is not null && _isMapReady) { _ = _mapService.FocusDiscussionAsync(_selected.Discussion.Key); }
		}

		/// <summary>Raised to OPEN the reader window — a map click, a section row, or the section's button.
		/// MapViewModel opens <c>IsMesoDiscussionOpen</c> on it.</summary>
		public event EventHandler? ReaderRequested;

		// ── The groups (the window's list): relative to the moment the map shows ──
		// ⚠️ Re-filled only when their MEMBERSHIP changes: a live refresh re-runs ApplyMoment every two minutes,
		// and a reset list would flicker under the pointer.

		/// <summary>In effect at the moment the map shows (also the section's short list).</summary>
		public ObservableCollection<DiscussionRow> InEffect { get; } = new();

		/// <summary>Expired before that moment (newest first).</summary>
		public ObservableCollection<DiscussionRow> Earlier { get; } = new();

		/// <summary>Issued after that moment — PastCast only (live, "now" has no later).</summary>
		public ObservableCollection<DiscussionRow> Later { get; } = new();

		public bool HasInEffect => InEffect.Count > 0;
		public bool HasEarlier => Earlier.Count > 0;
		public bool HasLater => Later.Count > 0;

		/// <summary>The section's list heading: "In effect now" (NowCast) or "In effect at 4:51 PM" (PastCast).</summary>
		public string InEffectHeading => _loadedKey is { Live: true } || _moment is null
			? "In effect now"
			: $"In effect at {_moment.Value.ToLocalTime():h:mm tt}";

		/// <summary>The section's one button.</summary>
		public string OpenReaderText => Discussions.Count switch
		{
			0 => "Open reader",
			1 => "1 discussion · Open reader",
			var n => $"All {n} discussions · Open reader",
		};

		private void RefillGroups()
		{
			var t = _moment;
			Refill(InEffect, Discussions.Where(r => r.IsInEffect));
			Refill(Earlier, t is { } at ? Discussions.Where(r => r.Discussion.Expires <= at).Reverse() : Enumerable.Empty<DiscussionRow>());
			Refill(Later, t is { } at2 ? Discussions.Where(r => r.Discussion.Issued > at2) : Enumerable.Empty<DiscussionRow>());
			OnPropertyChanged(nameof(HasInEffect));
			OnPropertyChanged(nameof(HasEarlier));
			OnPropertyChanged(nameof(HasLater));
			OnPropertyChanged(nameof(InEffectHeading));
			OnPropertyChanged(nameof(OpenReaderText));
		}

		private static void Refill(ObservableCollection<DiscussionRow> target, IEnumerable<DiscussionRow> rows)
		{
			var list = rows.ToList();
			if (target.SequenceEqual(list)) { return; }
			target.Clear();
			foreach (var r in list) { target.Add(r); }
		}

		// ── The reader: the selected discussion's text, by section ──

		private MesoDiscussionText? _text;
		private string _readerStatus = string.Empty;

		public string ReaderTitle => _selected is { } s ? $"{s.Discussion.Label} · {s.Discussion.Kind.Issuer}" : string.Empty;
		public string ReaderValid => _selected is { } s ? $"{s.Discussion.Issued.ToLocalTime():ddd MMM d · h:mm tt} → {s.Discussion.Expires.ToLocalTime():h:mm tt}" : string.Empty;
		public string ReaderAreas => _text?.AreasAffected ?? string.Empty;
		public string ReaderConcerning => _text is { Concerning.Length: > 0 } t ? t.Concerning : _selected?.Discussion.Concerning ?? string.Empty;
		public string ReaderWatch => _selected?.Discussion.WatchProbability is int p ? $"{p}%" : string.Empty;
		/// <summary>The watch probability as 0–100 for the reader's meter (0 when the discussion carries none).</summary>
		public double ReaderWatchPercent => _selected?.Discussion.WatchProbability ?? 0;
		public bool HasReaderWatch => _selected?.Discussion.WatchProbability is not null;
		public string ReaderSummary => _text?.Summary ?? string.Empty;
		public string ReaderDiscussion => _text?.Discussion ?? string.Empty;
		public string ReaderSignature =>
			_text is null ? string.Empty :
			string.Join(" · ", new[] { _text.Forecasters, _text.Attn.Length > 0 ? "ATTN " + _text.Attn : string.Empty }.Where(s => s.Length > 0));
		public string ReaderStatus => _readerStatus;
		public Uri? ReaderUrl => _selected is { } s ? new Uri(s.Discussion.WebUrl) : null;

		private async Task LoadTextAsync(DiscussionRow? row)
		{
			var token = ++_textToken;
			_text = null;
			_readerStatus = row is null ? string.Empty : "Loading the discussion…";
			RaiseReader();
			if (row is null) { return; }

			MesoDiscussionText? text;
			try { text = await _service.GetTextAsync(row.Discussion); }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Discussion text failed for {Product}", row.Discussion.ProductId);
				text = null;
			}
			if (token != _textToken) { return; }
			_text = text;
			_readerStatus = text is null ? "The text isn't available — open it on the issuer's site." : string.Empty;
			RaiseReader();
		}

		private void RaiseReader()
		{
			foreach (var name in new[] { nameof(ReaderTitle), nameof(ReaderValid), nameof(ReaderAreas), nameof(ReaderConcerning),
				nameof(ReaderWatch), nameof(ReaderWatchPercent), nameof(HasReaderWatch),
				nameof(ReaderSummary), nameof(ReaderDiscussion), nameof(ReaderSignature),
				nameof(ReaderStatus), nameof(ReaderUrl) })
			{
				OnPropertyChanged(name);
			}
		}

		// ── The card ──

		private string _cardHeadline = "No discussions";
		private string _cardContext = string.Empty;
		private string _cardMessage = string.Empty;

		public string CardHeadline { get => _cardHeadline; private set => SetProperty(ref _cardHeadline, value); }
		public string CardContext { get => _cardContext; private set => SetProperty(ref _cardContext, value); }

		public string CardFooter =>
			_cardMessage.Length > 0 ? _cardMessage :
			!AnyShown ? "None shown — pick a kind below" :
			string.Empty;

		private void SetMessage(string message)
		{
			if (_cardMessage == message) { return; }
			_cardMessage = message;
			OnPropertyChanged(nameof(CardFooter));
		}

		// ── Lifecycle ──

		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetDiscussionsOpacityAsync(_opacity);
			await PushKindsAsync();
			Reconcile();
		}

		/// <summary>Starts the live refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = BackgroundRefresh.RunAdaptiveAsync(first =>
		{
			if (!first)
			{
				_dispatcher.Post(() =>
				{
					if (DesiredKey() is { Live: true }) { _ = EnsureAsync(refresh: true); }
				});
			}
			return Task.FromResult(LiveRefresh);
		}, _shutdown.Token);

		public void Shutdown() => _shutdown.Cancel();

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(RadarViewModel.DisplayedFrameTimeUtc):
					if (_loadedKey is { Live: false }) { ApplyMoment(_radar.DisplayedFrameTimeUtc); }
					break;
				// ⚠️ A LOAD, not a picker move — the same two names every PastCast overlay keys on.
				case nameof(RadarViewModel.IsPastEventMode):
				case nameof(RadarViewModel.HasLoadedReplayWindow):
					Reconcile();
					break;
			}
		}

		// ── Core ──

		private FetchKey? DesiredKey()
		{
			if (!_isModeActive) { return null; }
			if (_radar.IsPastEventMode)
			{
				return _radar.LoadedReplayStartUtc is { } s && _radar.LoadedReplayEndUtc is { } e ? new FetchKey(false, s, e) : null;
			}
			return new FetchKey(true, default, default);
		}

		private void Reconcile()
		{
			if (!_isMapReady) { return; }
			var want = DesiredKey();
			if (want is null)
			{
				if (_loadedKey is not null) { _ = ClearAsync(); }
				CardHeadline = "No discussions";
				CardContext = string.Empty;
				SetMessage(_isModeActive && _radar.IsPastEventMode ? "Load a timeframe to see its discussions" : string.Empty);
				return;
			}
			if (!Equals(want, _loadedKey)) { _ = EnsureAsync(refresh: false); }
		}

		private async Task EnsureAsync(bool refresh)
		{
			if (!_isMapReady || DesiredKey() is not { } key) { return; }
			// ⚠️⚠️ DEDUPE BEFORE TAKING A TOKEN, AND NEVER TOUCH _applyToken ON A CALL THAT BAILS.
			if (Equals(_inFlightKey, key)) { return; }
			if (!refresh && Equals(_loadedKey, key)) { return; }

			var token = ++_applyToken;
			if (!refresh) { SetMessage("Loading…"); }
			var end = key.Live ? DateTimeOffset.UtcNow : key.End;
			var start = key.Live ? end - LiveSpan : key.Start;

			MesoDiscussionFetch fetch;
			_inFlightKey = key;
			try { fetch = await _service.FetchAsync(start, end, key.Live); }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Discussions fetch failed");
				fetch = MesoDiscussionFetch.Failed("Discussions unavailable.");
			}
			finally { _inFlightKey = null; }

			if (token != _applyToken) { return; }
			if (!fetch.Found)
			{
				if (!refresh) { await ClearAsync(); }
				SetMessage(fetch.Error ?? "Discussions unavailable.");
				return;
			}

			var keepKey = _selected?.Discussion.Key;
			var sameWindow = Equals(_loadedKey, key);
			_loadedKey = key;
			// ⚠️ Rebuild the rows ONLY when the list changed: clearing a ListView's source drops its selection,
			// which would blank the reader every two minutes while you read a live discussion.
			if (!sameWindow || !Discussions.Select(r => r.Discussion.Key).SequenceEqual(fetch.Discussions.Select(d => d.Key)))
			{
				Discussions.Clear();
				foreach (var d in fetch.Discussions) { Discussions.Add(new DiscussionRow(d)); }
				OnPropertyChanged(nameof(HasDiscussions));
				// A refresh keeps what you were reading; a new window starts with nothing selected.
				Selected = keepKey is null || !sameWindow ? null : Discussions.FirstOrDefault(r => r.Discussion.Key == keepKey);
			}

			await _mapService.SetDiscussionsSourceAsync(fetch.Url);
			await PushKindsAsync();
			await _mapService.SetDiscussionsOpacityAsync(_opacity);
			if (_selected is not null) { await _mapService.SetDiscussionSelectedAsync(_selected.Discussion.Key); }
			SetMessage(fetch.Error ?? string.Empty);
			ApplyMoment(key.Live ? DateTimeOffset.UtcNow : _radar.DisplayedFrameTimeUtc, force: true);
		}

		// The moment the map shows and the counts describe.
		private void ApplyMoment(DateTimeOffset? t, bool force = false)
		{
			if (!force && t == _moment) { return; }
			_moment = t;
			if (_isMapReady) { _ = _mapService.SetDiscussionTimeAsync(t?.ToUnixTimeMilliseconds()); }

			foreach (var row in Discussions) { row.IsInEffect = t is { } at && row.Discussion.IsInEffectAt(at); }
			foreach (var k in Kinds) { k.Count = Discussions.Count(r => r.IsInEffect && r.Discussion.Kind == k.Kind); }

			var inEffect = Discussions.Count(r => r.IsInEffect);
			CardHeadline = inEffect switch
			{
				0 => "No discussions in effect",
				1 => "1 discussion in effect",
				_ => $"{inEffect} discussions in effect",
			};
			var listed = Discussions.Count == 1 ? "1 listed" : $"{Discussions.Count} listed";
			CardContext = _loadedKey is { Live: true }
				? $"Now · {listed} from the last 24 hours"
				: t is { } at2 ? $"At {at2.ToLocalTime():h:mm tt} · {listed} for this window" : listed;
			RefillGroups();
		}

		private async Task ClearAsync()
		{
			++_applyToken;
			_loadedKey = null;
			Selected = null;
			Discussions.Clear();
			OnPropertyChanged(nameof(HasDiscussions));
			foreach (var k in Kinds) { k.Count = 0; }
			RefillGroups();
			await _mapService.ClearDiscussionsAsync();
		}
	}

	/// <summary>One kind's row in the section: toggle, colour, in-effect count.</summary>
	public sealed class DiscussionKindRow : ObservableObject
	{
		private readonly Action _changed;
		private bool _isShown = true;
		private int _count;

		internal DiscussionKindRow(DiscussionKind kind, Action changed)
		{
			Kind = kind;
			_changed = changed;
		}

		public DiscussionKind Kind { get; }
		public string Label => Kind.RowLabel;
		public string Fill => Kind.Fill;
		public string ToolTip => $"Show {Kind.Issuer} {Kind.ProductName.ToLowerInvariant()}s";

		public bool IsShown
		{
			get => _isShown;
			set { if (SetProperty(ref _isShown, value)) { _changed(); } }
		}

		internal void SetSilently(bool shown)
		{
			if (_isShown == shown) { return; }
			_isShown = shown;
			OnPropertyChanged(nameof(IsShown));
		}

		public int Count { get => _count; internal set => SetProperty(ref _count, value); }
	}

	/// <summary>One discussion in the list.</summary>
	public sealed class DiscussionRow : ObservableObject
	{
		private bool _isInEffect;

		internal DiscussionRow(MesoDiscussion d) => Discussion = d;

		public MesoDiscussion Discussion { get; }
		public string Label => Discussion.Label;
		public string Fill => Discussion.Kind.Fill;

		/// <summary>The CONCERNING line, or the product name when the index carried none (older SPC MDs).</summary>
		public string Headline => Discussion.Concerning.Length > 0 ? Discussion.Concerning : Discussion.Kind.ProductName;

		public string TimeText => $"{Discussion.Issued.ToLocalTime():h:mm tt} → {Discussion.Expires.ToLocalTime():h:mm tt}";
		public string WatchText => Discussion.WatchProbability is int p ? $"{p}%" : string.Empty;

		/// <summary>In effect at the moment the map shows — the list dims the rest.</summary>
		public bool IsInEffect { get => _isInEffect; internal set => SetProperty(ref _isInEffect, value); }

		private bool _isSelected;

		/// <summary>The one the reader shows — the window's list highlights it (set by the VM's Selected).</summary>
		public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }
	}
}
