using System;
using System.Collections.Generic;

namespace Anvil.ViewModels
{
	/// <summary>
	/// A single-selection "which one is open" group backed by an enum with a <c>None</c> member: at most one
	/// value is open at a time, so opening one closes whichever was open. Wraps the enum current-value plus
	/// the open/close projection logic that <see cref="MapViewModel"/>'s temporal cards and right-side panels
	/// each spelled out by hand.
	///
	/// The owning VM supplies a <paramref name="raiseChanged"/> callback that fires
	/// <c>OnPropertyChanged</c> for the enum property AND every bool projection, so the existing x:Bind
	/// bindings keep working unchanged; this type only owns the current value + the set/clear rules.
	/// </summary>
	public sealed class ExclusiveOpen<TEnum> where TEnum : struct, Enum
	{
		private readonly TEnum _none;
		private readonly Action _raiseChanged;
		private TEnum _current;

		/// <param name="none">The enum member meaning "nothing open" (the default state).</param>
		/// <param name="raiseChanged">Fires the owner's change notifications (enum property + bool projections).</param>
		public ExclusiveOpen(TEnum none, Action raiseChanged)
		{
			_none = none;
			_current = none;
			_raiseChanged = raiseChanged;
		}

		/// <summary>The value currently open (<c>None</c> = nothing). Setting a different value fires the
		/// change callback once.</summary>
		public TEnum Current
		{
			get => _current;
			set
			{
				if (EqualityComparer<TEnum>.Default.Equals(_current, value)) { return; }
				_current = value;
				_raiseChanged();
			}
		}

		/// <summary>Whether <paramref name="which"/> is the one currently open.</summary>
		public bool IsOpen(TEnum which) => EqualityComparer<TEnum>.Default.Equals(_current, which);

		/// <summary>Two-way projection setter for one member: <c>true</c> opens it (closing any other);
		/// <c>false</c> closes it only if it was the one open (so a stale <c>false</c> from a sibling
		/// projection can't clobber a different open card).</summary>
		public void SetOpen(TEnum which, bool value)
		{
			if (value) { Current = which; }
			else if (IsOpen(which)) { Current = _none; }
		}
	}
}
