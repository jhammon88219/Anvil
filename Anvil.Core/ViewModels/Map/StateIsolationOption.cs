namespace Anvil.ViewModels
{
	/// <summary>What one row of the isolation combo does.</summary>
	public enum StateIsolationKind
	{
		/// <summary>Clear everything — no mask at all, the whole map.</summary>
		None,

		/// <summary>Mask everything outside the contiguous 48 (the base extent).</summary>
		Conus,

		/// <summary>Arm hover-to-highlight so the next map click isolates that state.</summary>
		Arm,

		/// <summary>Isolate one named state directly.</summary>
		State
	}

	/// <summary>
	/// One row of the map controls strip's isolation combo — the single control that replaced three
	/// (a CONUS checkbox, an "isolate a state" arm checkbox, and a state-name combo) in the Settings
	/// window's Map tab.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE TOP THREE ROWS ARE ACTIONS, THE REST ARE PLACES, and the two are told apart by
	/// <see cref="Tooltip"/>: only the actions carry hover text, because a state name explains itself and
	/// 52 identical tooltips would be noise. <see cref="StartsStateList"/> marks the first state so the
	/// view can rule a line above it.
	/// ⚠️ Plain and immutable — a row never changes, the SELECTION does. Anything that varies belongs on
	/// <see cref="StateIsolationViewModel"/>.
	/// </remarks>
	public sealed class StateIsolationOption
	{
		public StateIsolationOption(StateIsolationKind kind, string label, string? tooltip = null, bool startsStateList = false)
		{
			Kind = kind;
			Label = label;
			Tooltip = tooltip;
			StartsStateList = startsStateList;
		}

		/// <summary>What picking this row does.</summary>
		public StateIsolationKind Kind { get; }

		/// <summary>The row's text — and, for <see cref="StateIsolationKind.State"/>, the state's name
		/// exactly as <c>state-boundaries.geojson</c> spells it (states.js finds the polygon by name).</summary>
		public string Label { get; }

		/// <summary>Hover text, or null for the state rows (see the remarks).</summary>
		public string? Tooltip { get; }

		/// <summary>True on the FIRST state row only, so the view can draw a rule between the actions above
		/// and the places below.</summary>
		public bool StartsStateList { get; }
	}
}
