using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// One radar's stretch of a saved event: which site had the storm, from when, for how long.
	/// </summary>
	/// <remarks>
	/// ⚠️ <b>A leg is NOT "when the radar was on".</b> Every WSR-88D records around the clock; a leg marks the
	/// stretch where that radar has the useful view (the storm is in range, or better placed than the last
	/// one). Legs may OVERLAP — that is the storm being in range of both.
	/// <para>⚠️ <b>The start is UTC, never local.</b> The Timeframe pickers work in local time, which is fine
	/// for one person on one machine; a built-in list is shared, and El Reno started at 23:03Z everywhere.
	/// The conversion to the pickers' local frame happens once, at apply time
	/// (<c>RadarViewModel.ApplyReplayWindow</c>).</para>
	/// <para>⚠️ <see cref="DurationMinutes"/> must be one of <see cref="AllowedDurationMinutes"/> — the
	/// Timeframe window is a six-segment picker and a 90-minute leg would have no segment to light.</para>
	/// </remarks>
	/// <param name="SiteId">The radar's ICAO ("KTLX"), or null for a window-only event (Load then waits for
	/// a site click, exactly as it does with no site selected today).</param>
	/// <param name="Key">The leg's KEY TIME — the moment this radar's view is FOR (the tornado on the ground,
	/// the landfall, the derecho crossing a city). Optional; null = not known. See <see cref="SavedEventKey"/>.</param>
	public sealed record SavedEventLeg(string? SiteId, DateTimeOffset StartUtc, int DurationMinutes, SavedEventKey? Key = null)
	{
		/// <summary>
		/// The window lengths the Timeframe picker offers, in its order.
		/// ⚠️ A COPY of <c>RadarViewModel.PastEventMinutesByIndex</c>, kept here so the library can validate
		/// without reaching into a view model; <c>SavedEventLibraryTests</c> fails if the two drift.
		/// </summary>
		public static IReadOnlyList<int> AllowedDurationMinutes { get; } = new[] { 30, 60, 120, 180, 360, 720 };

		/// <summary>When this leg's window ends.</summary>
		public DateTimeOffset EndUtc => StartUtc.AddMinutes(DurationMinutes);
	}

	/// <summary>
	/// A leg's key time: when (and optionally where) the thing the leg exists to show happened — JSON
	/// <c>"key": { "startUtc", "endUtc"?, "place"? }</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ <b>It belongs to the LEG, not the event.</b> Katrina has two landfalls, one per radar; a derecho crosses
	/// a city per leg. What the time is CALLED comes from the event's kind (<see cref="SavedEventKinds.KeyLabel"/>),
	/// so it is never stored.
	/// <para>⚠️ It must OVERLAP its leg's window, not sit inside it: a derecho can enter an office's area before
	/// that radar's leg starts (KILN, 2012) or a DC crossing outlast the leg by minutes. A key entirely outside
	/// the window would describe a moment the leg can't show.</para>
	/// </remarks>
	/// <param name="EndUtc">Null for an instant (a landfall); set for a span (a tornado's track).</param>
	/// <param name="Place">"Buras", "Cedar Rapids" — empty when the event's name already says where.</param>
	public sealed record SavedEventKey(DateTimeOffset StartUtc, DateTimeOffset? EndUtc = null, string Place = "");

	/// <summary>
	/// A named past radar event the PastCast window can jump to: one or more <see cref="SavedEventLeg"/>s,
	/// either BUILT IN (curated, bundled, read-only) or the user's own.
	/// </summary>
	/// <remarks>
	/// ⚠️ <b>An event is the Timeframe pickers' three values plus a site, per leg — nothing else.</b> Picking
	/// one fills in the pickers and selects the site; Load does the rest through the unchanged replay path.
	/// There is deliberately no "event mode" in the radar engine.
	/// <para>⚠️ Built-ins can't be renamed or removed. The library refuses, not just the UI.</para>
	/// <para><see cref="Kind"/> only sorts and filters the list; it never changes how the event loads.</para>
	/// </remarks>
	public sealed record SavedEvent(
		string Id,
		string Name,
		IReadOnlyList<SavedEventLeg> Legs,
		int DefaultLegIndex,
		string Notes,
		string Source,
		bool IsBuiltIn,
		SavedEventKind Kind = SavedEventKind.Other)
	{
		/// <summary>The leg picking the event lands on.</summary>
		public SavedEventLeg DefaultLeg => Legs[Math.Clamp(DefaultLegIndex, 0, Legs.Count - 1)];

		/// <summary>The event's overall span: earliest leg start. Derived, never stored.</summary>
		public DateTimeOffset StartUtc => Legs.Min(l => l.StartUtc);

		/// <summary>The event's overall span: latest leg end. Derived, never stored.</summary>
		public DateTimeOffset EndUtc => Legs.Max(l => l.EndUtc);
	}

	/// <summary>
	/// What kind of storm a saved event is — the JSON <c>"type"</c> ("tornado" / "hurricane" / "derecho").
	/// </summary>
	/// <remarks>
	/// ⚠️ <see cref="Other"/> is the default so a missing type is harmless (user events saved before types
	/// existed carry none; new ones pick one); an UNKNOWN type string is a parse error, so a typo in a built-in
	/// is caught rather than shown as Other. The list groups in this declaration order with Other last
	/// (<see cref="Anvil.Services.SavedEventLibrary.GetEvents"/>), built-ins and the user's own together.
	/// </remarks>
	public enum SavedEventKind
	{
		Other = 0,
		Tornado,
		Hurricane,
		Derecho,
	}

	/// <summary>The words each <see cref="SavedEventKind"/> is shown with.</summary>
	public static class SavedEventKinds
	{
		/// <summary>The kinds a user may give their own event, in the picker's order (Other is legacy only).</summary>
		public static IReadOnlyList<SavedEventKind> Pickable { get; } =
			new[] { SavedEventKind.Tornado, SavedEventKind.Hurricane, SavedEventKind.Derecho };

		/// <summary>"Tornado" — the badge and the filter pill.</summary>
		public static string Label(SavedEventKind kind) => kind switch
		{
			SavedEventKind.Tornado => "Tornado",
			SavedEventKind.Hurricane => "Hurricane",
			SavedEventKind.Derecho => "Derecho",
			_ => "Other",
		};

		/// <summary>What a leg's <see cref="SavedEventKey"/> is called for this kind of storm.</summary>
		public static string KeyLabel(SavedEventKind kind) => kind switch
		{
			SavedEventKind.Tornado => "On the ground",
			SavedEventKind.Hurricane => "Landfall",
			SavedEventKind.Derecho => "Across",
			_ => "Key time",
		};
	}
}
