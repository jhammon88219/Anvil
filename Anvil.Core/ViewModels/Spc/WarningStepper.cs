using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// ONE NowCast warning tile's map controls: the ‹ › arrows that step the map through that type's active
	/// warnings one at a time, and the clickable STATE LINES under its count. Owned by
	/// <see cref="WarningsViewModel"/> (one per tile); the fly itself is the delegate it is given.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE ARROW RULES (the user's, 2026-09-30 mockup):
	/// • ORDER — worst first: tier 2 (Emergency / destructive), then 1 (PDS / considerable), then base; NEWEST
	///   first within a tier. So the first › lands on what matters most.
	/// • › (Forward) — the next in that order, WRAPPING after the last, so it is live whenever the type has any.
	/// • ‹ (Back) — OFF until › has been used, then it retraces what you VISITED (a back stack, not "previous in
	///   the list"). A visited warning that has since expired is skipped.
	/// • A REFRESH re-sorts under you: if the warning you're on expires, › carries on from its SLOT (whatever
	///   moved up into it), and the tile falls back to its threat tag until you step again.
	/// • Each tile keeps its own stack; <see cref="Reset"/> (the tile's count, or NowCast turning off) clears it.
	/// ⚠️ THE STATE LINES (the user's, 2026-09-30 mockup):
	/// • A line per state, most first; a warning counts ONCE, in its first county's state
	///   (<see cref="WarningTarget.StateName"/>), so the lines add up to the count. NO CAP — the tile grows.
	/// • Clicking a line flies to ALL of that state's warnings at once (one frame, all flashing) and lights it.
	///   It does NOT enter the ‹ stack and does NOT narrow the arrows: the next › carries on from where you were.
	/// • The LIT line is the clicked state, else the state of the warning the arrows have you on.
	/// </remarks>
	public sealed class WarningStepper : ObservableObject
	{
		private readonly string _phenom;
		private readonly Func<IReadOnlyList<WarningTarget>, Task> _fly;
		private IReadOnlyList<WarningTarget> _ordered = Array.Empty<WarningTarget>();
		private readonly List<string> _back = new();
		private string? _currentId;
		private int _lastIndex = -1;
		private string? _focusedState;   // a clicked state line, until the arrows / Reset / its expiry clear it
		private string? _shownLit;       // the lit name States was last built with
		private IReadOnlyList<WarningStateLine> _states = Array.Empty<WarningStateLine>();

		public WarningStepper(string phenom, Func<IReadOnlyList<WarningTarget>, Task> fly)
		{
			_phenom = phenom;
			_fly = fly;
		}

		/// <summary>› is live whenever this type has any warning.</summary>
		public bool CanForward => _ordered.Count > 0;

		/// <summary>‹ is live once › has been used AND something visited is still active.</summary>
		public bool CanBack => _back.Any(id => IndexOf(id) >= 0);

		/// <summary>Whether the tile has you somewhere — on one warning, or on a clicked state (the tag line then says where).</summary>
		public bool IsActive => _focusedState is not null || Current is not null;

		/// <summary>"2 of 3 · Cleveland, OK · PDS" on a warning, "All 3 in Oklahoma" on a state; empty otherwise.</summary>
		public string PositionText
		{
			get
			{
				if (_focusedState is { } state)
				{
					var n = _ordered.Count(t => t.StateName == state);
					return n == 1 ? $"1 in {state}" : $"All {n.ToString(CultureInfo.InvariantCulture)} in {state}";
				}
				if (Current is not { } t1) { return string.Empty; }
				var i = IndexOf(t1.Id);
				var parts = new List<string>(3) { $"{i + 1} of {_ordered.Count}" };
				if (t1.Place.Length > 0) { parts.Add(t1.Place); }
				if (TierTag(t1.Phenom, t1.Tier) is { Length: > 0 } tag) { parts.Add(tag); }
				return string.Join(" · ", parts);
			}
		}

		/// <summary>The state lit on the tile: the clicked one, else the state of the warning you're on; null if neither.</summary>
		public string? LitState => _focusedState ?? Current?.StateName;

		/// <summary>The tile's state lines, most warnings first, rebuilt on a refresh and when the lit line moves.</summary>
		public IReadOnlyList<WarningStateLine> States => _states;

		/// <summary>The latest active set (all types — this keeps its own phenom). Called on every refresh.</summary>
		public void Update(IEnumerable<WarningTarget> all)
		{
			_ordered = Order(all.Where(t => t.Phenom == _phenom));
			if (_focusedState is { } s && !_ordered.Any(t => t.StateName == s)) { _focusedState = null; }
			Raise(rebuildStates: true);
		}

		/// <summary>Worst first, newest first within a tier; id breaks ties so the order is stable. Internal for tests.</summary>
		internal static IReadOnlyList<WarningTarget> Order(IEnumerable<WarningTarget> targets) =>
			targets.OrderByDescending(t => t.Tier).ThenByDescending(t => t.Sent).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();

		/// <summary>One phenom's warnings grouped by state: most first, then by name. Internal for tests.</summary>
		internal static IReadOnlyList<(string Name, int Count)> StatesOf(IEnumerable<WarningTarget> targets) =>
			targets.GroupBy(t => t.StateName)
				.Select(g => (g.Key, g.Count()))
				.OrderByDescending(s => s.Item2).ThenBy(s => s.Key, StringComparer.Ordinal)
				.ToList();

		/// <summary>›: the next warning (wrapping), remembering the one you leave for ‹.</summary>
		public void Forward()
		{
			if (_ordered.Count == 0) { return; }
			int next;
			var at = _currentId is null ? -1 : IndexOf(_currentId);
			if (at >= 0)
			{
				_back.Add(_currentId!);
				next = (at + 1) % _ordered.Count;
			}
			else
			{
				// Nothing visited yet → the first. The one you were on EXPIRED → its slot, which now holds the
				// warning that came after it.
				next = _lastIndex < 0 ? 0 : _lastIndex % _ordered.Count;
			}
			Go(next);
		}

		/// <summary>‹: back to the last VISITED warning still active.</summary>
		public void Back()
		{
			while (_back.Count > 0)
			{
				var id = _back[^1];
				_back.RemoveAt(_back.Count - 1);
				var i = IndexOf(id);
				if (i >= 0)
				{
					Go(i);
					return;
				}
			}
			Raise(); // everything left was expired — ‹ goes dead
		}

		/// <summary>A state line clicked: frame and flash every warning of this tile's type in <paramref name="state"/>.</summary>
		public void FocusState(string state)
		{
			var targets = _ordered.Where(t => t.StateName == state).ToList();
			if (targets.Count == 0) { return; }
			_focusedState = state;
			Raise();
			_ = _fly(targets);
		}

		/// <summary>Forget where you are and where you've been (the tile's count; NowCast turning off).</summary>
		public void Reset()
		{
			_currentId = null;
			_lastIndex = -1;
			_focusedState = null;
			_back.Clear();
			Raise();
		}

		private WarningTarget? Current => _currentId is null || IndexOf(_currentId) is var i && i < 0 ? null : _ordered[i];

		private void Go(int index)
		{
			var target = _ordered[index];
			_currentId = target.Id;
			_lastIndex = index;
			_focusedState = null;
			Raise();
			_ = _fly(new[] { target });
		}

		private int IndexOf(string id)
		{
			for (int i = 0; i < _ordered.Count; i++)
			{
				if (_ordered[i].Id == id) { return i; }
			}
			return -1;
		}

		// The elevated tag in the tile's words — the same names WarningThreatCounts uses on the card.
		private static string TierTag(string phenom, int tier) => (phenom, tier) switch
		{
			("TO", 2) => "Emergency",
			("TO", 1) => "PDS",
			("SV", 2) => "Destructive",
			("FF", 2) => "Emergency",
			("FF", 1) => "Considerable",
			_ => string.Empty,
		};

		private void Raise(bool rebuildStates = false)
		{
			OnPropertyChanged(nameof(CanForward));
			OnPropertyChanged(nameof(CanBack));
			OnPropertyChanged(nameof(IsActive));
			OnPropertyChanged(nameof(PositionText));
			var lit = LitState;
			if (rebuildStates || lit != _shownLit)
			{
				_shownLit = lit;
				_states = StatesOf(_ordered)
					.Select(s => new WarningStateLine(s.Name, s.Count, s.Name == lit, this))
					.ToList();
				OnPropertyChanged(nameof(LitState));
				OnPropertyChanged(nameof(States));
			}
		}
	}

	/// <summary>One state line of a NowCast warning tile ("Oklahoma 3"); clicking it flies to those warnings.</summary>
	public sealed class WarningStateLine
	{
		private readonly WarningStepper _owner;

		internal WarningStateLine(string name, int count, bool isLit, WarningStepper owner)
		{
			Name = name;
			Count = count;
			IsLit = isLit;
			_owner = owner;
		}

		public string Name { get; }
		public int Count { get; }
		public string CountText => Count.ToString(CultureInfo.InvariantCulture);

		/// <summary>The clicked state, or the state of the warning the arrows have you on.</summary>
		public bool IsLit { get; }
		public bool IsUnlit => !IsLit;

		/// <summary>The line's click: frame + flash every warning of the tile's type in this state.</summary>
		public void Fly() => _owner.FocusState(Name);
	}
}
