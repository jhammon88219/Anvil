using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Where one PastCast layer's load for the loaded window stands.</summary>
	public enum PastLayerState
	{
		/// <summary>No window loaded yet.</summary>
		Waiting,
		Loading,
		/// <summary>Fetched, with something in it.</summary>
		Loaded,
		/// <summary>Fetched fine, but the window holds none ("none in window").</summary>
		Empty,
		/// <summary>Not fetched on purpose (the past outlook with no product picked, or hidden).</summary>
		Off,
		Failed,
	}

	/// <summary>
	/// ONE PastCast layer's row in the window's activity expander — LAYER · RESULT · SOURCE (mock-up idea A, the user's
	/// pick 2026-10-19): what the loaded event downloaded for it, or why not, and a Retry when it failed. Each PastCast
	/// layer VM owns one (<c>PastStatus</c>; PastAlerts owns two) and reports from its window fetch.
	/// </summary>
	/// <remarks>
	/// ⚠️ UI THREAD: every PastCast fetch's continuation lands back on it (they are awaited from map-ready / property
	/// reactions), so there is no dispatcher here.
	/// ⚠️ TRUTH ONLY, like FeedStatus: a row reports the CURRENT run only (each caller reports after its token check).
	/// ⚠️ CLOCK TIMES ONLY ("failed 3:12 PM") — nothing ticks.
	/// </remarks>
	public sealed class PastLayerStatus : ObservableObject
	{
		private PastLayerState _state = PastLayerState.Waiting;
		private string _resultText = "—", _reason = string.Empty;
		private Action? _retry;

		/// <param name="layerId">The layer's <c>Models.LayerOrder</c> id — its place in the window's layer order.</param>
		/// <param name="source">Where its data comes from, in words ("IEM", "NWS DAT + NCEI").</param>
		public PastLayerStatus(string layerId, string name, string source)
		{
			LayerId = layerId;
			Name = name;
			Source = source;
		}

		public string LayerId { get; }
		public string Name { get; }
		public string Source { get; }

		public PastLayerState State
		{
			get => _state;
			private set
			{
				if (SetProperty(ref _state, value))
				{
					OnPropertyChanged(nameof(CanRetry));
					OnPropertyChanged(nameof(IsIn));
				}
			}
		}

		/// <summary>The RESULT column: "214 reports" · "none in window" · "Loading…" · "failed 3:12 PM" · "not shown" · "—".</summary>
		public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }

		/// <summary>A failure's own words ("NCEI didn't answer") — the RESULT's tooltip.</summary>
		public string Reason { get => _reason; private set => SetProperty(ref _reason, value); }

		/// <summary>The row offers Retry: it failed and its owner can fetch again.</summary>
		public bool CanRetry => _state == PastLayerState.Failed && _retry is not null;

		/// <summary>Done for this window, whatever it found (loaded, none, or not shown on purpose).</summary>
		public bool IsIn => _state is PastLayerState.Loaded or PastLayerState.Empty or PastLayerState.Off;

		/// <summary>The row's Retry button.</summary>
		public void Retry()
		{
			if (CanRetry) { _retry!(); }
		}

		// ── Owner hooks (UI thread) ──

		/// <summary>The owner's way to fetch again — set once, at construction.</summary>
		internal void OnRetry(Action retry)
		{
			_retry = retry;
			OnPropertyChanged(nameof(CanRetry));
		}

		/// <summary>No window to load for.</summary>
		internal void Idle() => Set(PastLayerState.Waiting, "—");

		internal void Loading(string text = "Loading…") => Set(PastLayerState.Loading, text);

		internal void Loaded(string result) => Set(PastLayerState.Loaded, result);

		/// <summary>The fetch worked; the window simply holds none.</summary>
		internal void Empty(string text = "none in window") => Set(PastLayerState.Empty, text);

		internal void Off(string text = "not shown") => Set(PastLayerState.Off, text);

		internal void Failed(string? reason)
		{
			Set(PastLayerState.Failed, $"failed {FeedStatus.ClockMinutes(DateTimeOffset.Now)}");
			Reason = reason ?? string.Empty;
		}

		private void Set(PastLayerState state, string text)
		{
			Reason = string.Empty;
			ResultText = text;
			State = state;
		}

		/// <summary>"1 report" / "214 reports" — the RESULT column's counts. Internal for the owners.</summary>
		internal static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
	}
}
