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
	public sealed record SavedEventLeg(string? SiteId, DateTimeOffset StartUtc, int DurationMinutes)
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
	/// A named past radar event the PastCast window can jump to: one or more <see cref="SavedEventLeg"/>s,
	/// either BUILT IN (curated, bundled, read-only) or the user's own.
	/// </summary>
	/// <remarks>
	/// ⚠️ <b>An event is the Timeframe pickers' three values plus a site, per leg — nothing else.</b> Picking
	/// one fills in the pickers and selects the site; Load does the rest through the unchanged replay path.
	/// There is deliberately no "event mode" in the radar engine.
	/// <para>⚠️ Built-ins can't be renamed or removed. The library refuses, not just the UI.</para>
	/// </remarks>
	public sealed record SavedEvent(
		string Id,
		string Name,
		IReadOnlyList<SavedEventLeg> Legs,
		int DefaultLegIndex,
		string Notes,
		string Source,
		bool IsBuiltIn)
	{
		/// <summary>The leg picking the event lands on.</summary>
		public SavedEventLeg DefaultLeg => Legs[Math.Clamp(DefaultLegIndex, 0, Legs.Count - 1)];

		/// <summary>The event's overall span: earliest leg start. Derived, never stored.</summary>
		public DateTimeOffset StartUtc => Legs.Min(l => l.StartUtc);

		/// <summary>The event's overall span: latest leg end. Derived, never stored.</summary>
		public DateTimeOffset EndUtc => Legs.Max(l => l.EndUtc);
	}
}
