using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The temporal window's ACTIVITY EXPANDER states (<c>TemporalWindows/TemporalWindowActivityExpander</c>), as persisted
	/// tokens (<c>AppSettings.NowCastActivity</c> / <c>PastCastActivity</c> / <c>ForeCastActivity</c>).
	/// </summary>
	/// <remarks>
	/// ONE TAB, BACK AND FORTH (the user's call, 2026-10-17): Hidden → Minimal → Full → Minimal → Hidden. Minimal is TWO
	/// tokens because the tab must remember which way it was heading — "˄ More" on the way up, "˅ Less" on the way down.
	/// ⚠️ Persisted as TOKENS, never indices.
	/// </remarks>
	public static class ActivityExpanderStates
	{
		public const string Hidden = "hidden";
		public const string MinimalRising = "min-up";
		public const string Full = "full";
		public const string MinimalFalling = "min-down";

		public static readonly IReadOnlyList<string> Tokens = new[] { Hidden, MinimalRising, Full, MinimalFalling };

		/// <summary>A known token, else Hidden (the thin strip).</summary>
		public static string Normalize(string? token) => token is not null && Tokens.Contains(token) ? token : Hidden;

		/// <summary>The tab's one click: the next token round the back-and-forth.</summary>
		public static string Next(string? token) => Normalize(token) switch
		{
			Hidden => MinimalRising,
			MinimalRising => Full,
			Full => MinimalFalling,
			_ => Hidden,
		};

		public static bool IsMinimal(string? token) => Normalize(token) is MinimalRising or MinimalFalling;

		public static bool IsFull(string? token) => Normalize(token) == Full;

		/// <summary>The next click OPENS more (the chevron points up, "More") — else it closes ("Less").</summary>
		public static bool OpensMore(string? token) => Normalize(token) is Hidden or MinimalRising;
	}
}
