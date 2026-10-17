using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// ONE temporal mode's ACTIVITY (the window's <c>TemporalWindows/TemporalWindowActivityExpander</c>): the radar's live
	/// work (<see cref="Radar"/>, NowCast only), the mode's live feeds (<see cref="Feeds"/>, in the window's layer order),
	/// and the expander's state (Hidden / Minimal / Full, persisted per window).
	/// </summary>
	/// <remarks>
	/// ⚠️ ONLY THAT MODE'S WORK (the user's rule, 2026-10-16): NowCast = the live poll, the site check and its six live
	/// feeds; PastCast and ForeCast have none yet (empty — the window shows a bare band, no tab). Give a mode its own by
	/// building it with a radar activity and/or feeds in <c>MapViewModel</c>.
	/// ⚠️ ONE 1 s TICK for every feed's countdown (<see cref="StartTicking"/>), cancelled in <see cref="Shutdown"/>.
	/// </remarks>
	public sealed class TemporalActivityViewModel : ObservableObject
	{
		private readonly Action<string> _writeState;
		private readonly IDispatcher _dispatcher;
		private readonly CancellationTokenSource _shutdown = new();
		private string _state;

		/// <param name="readState">Reads the persisted state token (an <c>AppSettings</c> property).</param>
		/// <param name="writeState">Writes it back.</param>
		public TemporalActivityViewModel(BarActivityViewModel? radar, IEnumerable<FeedStatus> feeds,
			Func<string> readState, Action<string> writeState, IDispatcher dispatcher)
		{
			Radar = radar;
			Feeds = new ObservableCollection<FeedStatus>(feeds);
			_writeState = writeState;
			_dispatcher = dispatcher;
			_state = ActivityExpanderStates.Normalize(readState());
		}

		/// <summary>The radar's live work (NowCast); null for a mode without radar activity.</summary>
		public BarActivityViewModel? Radar { get; }

		public bool HasRadar => Radar is not null;

		/// <summary>The mode's live feeds, top first in the window's layer order (<see cref="ApplyLayerOrder"/>).</summary>
		public ObservableCollection<FeedStatus> Feeds { get; }

		public bool HasFeeds => Feeds.Count > 0;

		/// <summary>Anything to show at all — false = a bare band with no tab.</summary>
		public bool HasContent => HasRadar || HasFeeds;

		// ── The expander's state (ActivityExpanderStates tokens) ──

		/// <summary>The current token. Setting it persists it.</summary>
		public string State
		{
			get => _state;
			set
			{
				var token = ActivityExpanderStates.Normalize(value);
				if (!SetProperty(ref _state, token)) return;
				_writeState(token);
				OnPropertyChanged(nameof(IsHidden));
				OnPropertyChanged(nameof(IsMinimal));
				OnPropertyChanged(nameof(IsFull));
				OnPropertyChanged(nameof(OpensMore));
				OnPropertyChanged(nameof(TabLabel));
			}
		}

		public bool IsHidden => _state == ActivityExpanderStates.Hidden;
		public bool IsMinimal => ActivityExpanderStates.IsMinimal(_state);
		public bool IsFull => ActivityExpanderStates.IsFull(_state);

		/// <summary>The tab's next click opens more (chevron up) — else it closes.</summary>
		public bool OpensMore => ActivityExpanderStates.OpensMore(_state);

		/// <summary>"More" / "Less" — what the tab's click does.</summary>
		public string TabLabel => OpensMore ? "More" : "Less";

		/// <summary>The tab's click: Hidden → Minimal → Full → Minimal → Hidden.</summary>
		public void Advance() => State = ActivityExpanderStates.Next(_state);

		// ── Feed order + countdowns ──

		/// <summary>Re-sort <see cref="Feeds"/> to the window's layer order (top first); unknown ids keep their place at the end.</summary>
		public void ApplyLayerOrder(IReadOnlyList<string> topFirst)
		{
			var rank = topFirst.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
			var sorted = Feeds.OrderBy(f => rank.TryGetValue(f.LayerId, out var r) ? r : int.MaxValue).ToList();
			for (var i = 0; i < sorted.Count; i++)
			{
				var at = Feeds.IndexOf(sorted[i]);
				if (at != i) Feeds.Move(at, i);
			}
		}

		/// <summary>Starts the 1 s countdown tick (once, at launch). No-op for a mode without feeds.</summary>
		public void StartTicking()
		{
			if (!HasFeeds) return;
			_ = BackgroundRefresh.RunPeriodicAsync(TimeSpan.FromSeconds(1), _ =>
			{
				_dispatcher.Post(() => { foreach (var f in Feeds) f.Tick(); });
				return System.Threading.Tasks.Task.CompletedTask;
			}, _shutdown.Token);
		}

		/// <summary>Stops the tick. Called from <see cref="MapViewModel.Shutdown"/>.</summary>
		public void Shutdown() => _shutdown.Cancel();
	}
}
