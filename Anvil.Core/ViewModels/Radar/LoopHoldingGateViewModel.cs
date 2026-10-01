using System;
using System.Threading.Tasks;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>The gate's screens. <see cref="Cancelling"/> covers the moment between the click and the
	/// loop being truncated, so a load that bails in that window can't hide the gate under the popup.</summary>
	public enum LoopGateState { Hidden, Holding, ConfirmingEscape, Cancelling, Cancelled }

	/// <summary>
	/// The LOOP HOLDING GATE: while a PastCast loop loads, the map is dimmed behind a progress screen and takes
	/// no input. Sub-VM of <see cref="RadarViewModel"/> (<c>Radar.LoopGate</c>); drawn by
	/// <c>Controls/Composites/LoopHoldingGate</c> over the map band.
	/// </summary>
	/// <remarks>
	/// <code>
	///   Load ─▶ Begin ─▶ Holding ──(every frame settled)──▶ Hidden
	///                     │  ▲
	///        "Use the map"│  │"Keep waiting"
	///                     ▼  │
	///               ConfirmingEscape ──"Use the map"──▶ Hidden  (+ "Don't hold future loads" → setting off)
	///   Holding ──"Cancel load"──▶ Cancelling ──(loop cut to its lit run)──▶ Cancelled ──"Got it"──▶ Hidden
	/// </code>
	/// WHY (2026-10-01): a PastCast load keeps a 4-core machine near 100% CPU, and WinUI 3 hands mouse input to the
	/// WebView2 THROUGH the UI thread, so the map lags until the loop is built — measured, chased, and parked
	/// (docs/performance-measurement.md). The gate turns that into an honest wait: real counts, an exit, and a
	/// way to accept the lag. The user's call: EVERY PastCast load is held, even light ones.
	/// ⚠️ TRUTH ONLY: the counts are the engine's (frames whose volume arrived; scrubber cells lit), never a
	/// timer or an estimate. The engine pushes them through <see cref="Report"/>.
	/// ⚠️ The escape is deliberately the quiet path: a borderless button, then a confirm that says what you
	/// get. The permanent opt-out lives INSIDE that confirm and in Settings → Radar → PastCast.
	/// </remarks>
	public sealed class LoopHoldingGateViewModel : ObservableObject
	{
		/// <summary>The confirm's sentence (wording lives here, not in XAML).</summary>
		public const string EscapeWarning = "The map and the app will be sluggish until the loop finishes loading.";

		private readonly ISettingsService _settings;
		private readonly Func<Task> _cancelLoad;
		private LoopGateState _state;
		private bool _armed;     // the loop has actually begun — counts before this are a previous loop's
		private string _eventLine = string.Empty;
		private int _total, _downloaded, _built;
		private string _keptText = string.Empty;
		private bool _neverHoldAgain;

		public LoopHoldingGateViewModel(ISettingsService settings, Func<Task> cancelLoad)
		{
			_settings = settings;
			_cancelLoad = cancelLoad;
		}

		public LoopGateState State
		{
			get => _state;
			private set
			{
				if (SetProperty(ref _state, value))
				{
					OnPropertyChanged(nameof(IsShown));
					OnPropertyChanged(nameof(IsProgressShown));
					OnPropertyChanged(nameof(IsActionsShown));
					OnPropertyChanged(nameof(IsConfirmShown));
					OnPropertyChanged(nameof(IsCancelledShown));
					OnPropertyChanged(nameof(AreActionsEnabled));
				}
			}
		}

		/// <summary>The gate is up (dimming the map and swallowing its input).</summary>
		public bool IsShown => _state != LoopGateState.Hidden;
		/// <summary>Title, event line and bars — every screen but the cancelled popup.</summary>
		public bool IsProgressShown => _state is LoopGateState.Holding or LoopGateState.ConfirmingEscape or LoopGateState.Cancelling;
		public bool IsActionsShown => _state is LoopGateState.Holding or LoopGateState.Cancelling;
		public bool IsConfirmShown => _state == LoopGateState.ConfirmingEscape;
		public bool IsCancelledShown => _state == LoopGateState.Cancelled;
		/// <summary>False while a cancel is being carried out (one click is enough).</summary>
		public bool AreActionsEnabled => _state == LoopGateState.Holding;

		public string Title => "Loading the loop";

		/// <summary>"KTBW · Sep 28, 2022 · 1:00 PM–4:00 PM" (local time, like the rest of PastCast).</summary>
		public string EventLine { get => _eventLine; private set => SetProperty(ref _eventLine, value); }

		public int Total => _total;
		public int Downloaded => _downloaded;
		public int Built => _built;

		/// <summary>0–1, the thin top bar.</summary>
		public double DownloadedFraction => _total > 0 ? Math.Clamp((double)_downloaded / _total, 0, 1) : 0;
		/// <summary>0–1, the thick bar — the scrubber's lit cells.</summary>
		public double BuiltFraction => _total > 0 ? Math.Clamp((double)_built / _total, 0, 1) : 0;

		public string DownloadedText =>
			_total == 0 ? "Finding volumes…"
			: _downloaded >= _total ? "All downloaded"
			: $"Downloaded {_downloaded} of {_total}";

		public string BuiltText => _total == 0 ? string.Empty : $"{_built} of {_total} frames built";

		/// <summary>The cancelled popup's sentence.</summary>
		public string KeptText { get => _keptText; private set => SetProperty(ref _keptText, value); }

		/// <summary>The confirm's "Don't hold future loads" box; applied only if the escape is taken.</summary>
		public bool NeverHoldAgain { get => _neverHoldAgain; set => SetProperty(ref _neverHoldAgain, value); }

		/// <summary>PERSISTED (AppSettings.HoldPastCastLoads), default on. Off = loads never raise the gate.</summary>
		public bool HoldEnabled
		{
			get => _settings.Settings.HoldPastCastLoads;
			set
			{
				if (_settings.Settings.HoldPastCastLoads == value) return;
				_settings.Settings.HoldPastCastLoads = value; // persists (auto-save)
				OnPropertyChanged();
			}
		}

		// ── Engine seams ──────────────────────────────────────────────────────────────────────────────

		/// <summary>A PastCast load with a site is starting. No-op (stays hidden) when holding is turned off.</summary>
		internal void Begin(string eventLine)
		{
			_armed = false;
			NeverHoldAgain = false;
			SetCounts(0, 0, 0);
			EventLine = eventLine;
			State = HoldEnabled ? LoopGateState.Holding : LoopGateState.Hidden;
		}

		/// <summary>The new loop has begun (its frame arrays are this load's), so counts are now truthful.</summary>
		internal void Arm() => _armed = true;

		/// <summary>The held load's loop has begun (a Cancel has frames to keep).</summary>
		internal bool IsArmed => _armed;

		/// <summary>Whether the engine should report progress (a load is being held and its loop has begun).</summary>
		internal bool IsTracking => _armed && _state is LoopGateState.Holding or LoopGateState.ConfirmingEscape;

		internal void Report(int total, int downloaded, int built)
		{
			if (!IsTracking) return;
			SetCounts(total, downloaded, built);
		}

		/// <summary>Every frame of the loop has settled — release.</summary>
		internal void Complete()
		{
			if (IsTracking) State = LoopGateState.Hidden;
		}

		/// <summary>The load failed or was superseded (not via Cancel). Leaves a cancel in progress alone.</summary>
		internal void Abandon()
		{
			if (_state is LoopGateState.Holding or LoopGateState.ConfirmingEscape) State = LoopGateState.Hidden;
		}

		/// <summary>Site change, Clear, leaving PastCast, a NowCast load — whatever the gate was showing is moot.</summary>
		internal void Dismiss()
		{
			_armed = false;
			State = LoopGateState.Hidden;
		}

		/// <summary>The cancel has been carried out: say what was kept.</summary>
		internal void ShowCancelled(int kept, int total)
		{
			_armed = false;
			KeptText = kept > 0
				? $"{kept} of {total} frames were complete and are kept. The loop plays what's built. Load again to resume."
				: "No frames were complete yet, so nothing was kept. Load again to resume.";
			State = LoopGateState.Cancelled;
		}

		// ── The screen's actions ──────────────────────────────────────────────────────────────────────

		public void RequestEscape()
		{
			if (_state == LoopGateState.Holding) State = LoopGateState.ConfirmingEscape;
		}

		public void KeepWaiting()
		{
			if (_state == LoopGateState.ConfirmingEscape) State = LoopGateState.Holding;
		}

		public void UseMap()
		{
			if (_state != LoopGateState.ConfirmingEscape) return;
			if (_neverHoldAgain) HoldEnabled = false;
			_armed = false;
			State = LoopGateState.Hidden;
		}

		public async Task CancelLoadAsync()
		{
			if (_state != LoopGateState.Holding) return;
			State = LoopGateState.Cancelling;
			await _cancelLoad();
		}

		public void AcknowledgeCancelled()
		{
			if (_state == LoopGateState.Cancelled) State = LoopGateState.Hidden;
		}

		private void SetCounts(int total, int downloaded, int built)
		{
			if (total == _total && downloaded == _downloaded && built == _built) return;
			_total = total; _downloaded = downloaded; _built = built;
			OnPropertyChanged(nameof(Total));
			OnPropertyChanged(nameof(Downloaded));
			OnPropertyChanged(nameof(Built));
			OnPropertyChanged(nameof(DownloadedFraction));
			OnPropertyChanged(nameof(BuiltFraction));
			OnPropertyChanged(nameof(DownloadedText));
			OnPropertyChanged(nameof(BuiltText));
		}
	}
}
