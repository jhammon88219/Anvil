using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// WHERE live updates come from (Settings → Live Data) — persisted as a token in <c>AppSettings.LiveSource</c>.
	/// ONE source at a time: NWWS-OI REPLACES IEM, it never runs beside it (the user's call, 2026-10-19). Polling runs
	/// for every choice; a live source only suspends the pushed feeds' schedules while it is connected.
	/// </summary>
	public static class LiveSources
	{
		/// <summary>IEM's iembot feed (weather.im, room <c>botstalk</c>) — no account. The default.</summary>
		public const string Iem = "iem";

		/// <summary>NOAA Weather Wire Service Open Interface (XMPP) — needs an NWS-issued account.</summary>
		public const string Nwws = "nwws";

		/// <summary>No outside service — every feed polls on its own interval.</summary>
		public const string Poll = "poll";

		/// <summary>The three in Settings order (the picker's index = this list's index).</summary>
		public static IReadOnlyList<string> All { get; } = new[] { Iem, Nwws, Poll };

		/// <summary>An unknown or missing token reads as the default (IEM).</summary>
		public static string Normalize(string? token) => token switch
		{
			Nwws => Nwws,
			Poll => Poll,
			_ => Iem,
		};

		/// <summary>The source's name in the activity expander and Settings.</summary>
		public static string NameOf(string token) => Normalize(token) switch
		{
			Nwws => "NOAA Weather Wire",
			Poll => "Polling only",
			_ => "IEM",
		};
	}
}
