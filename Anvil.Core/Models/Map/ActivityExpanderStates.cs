using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The temporal window's ACTIVITY EXPANDER states (<c>TemporalPanes/TemporalPaneActivityExpander</c>), as persisted
	/// tokens (<c>AppSettings.NowCastActivity</c> / <c>PastCastActivity</c> / <c>ForeCastActivity</c>).
	/// </summary>
	/// <remarks>
	/// TWO STATES (the user's call, 2026-10-19 — three were too alike): Hidden (two lines) ⇄ Full (the FEED table), one tab.
	/// The retired Minimal tokens ("min-up" / "min-down", 2026-10-17 → 19) read as Full, the nearer of the two.
	/// ⚠️ Persisted as TOKENS, never indices.
	/// </remarks>
	public static class ActivityExpanderStates
	{
		public const string Hidden = "hidden";
		public const string Full = "full";

		public static readonly IReadOnlyList<string> Tokens = new[] { Hidden, Full };

		/// <summary>A known token, else Hidden (the thin strip). A retired Minimal token = Full.</summary>
		public static string Normalize(string? token) =>
			token is "min-up" or "min-down" ? Full
			: token is not null && Tokens.Contains(token) ? token : Hidden;

		/// <summary>The tab's one click.</summary>
		public static string Next(string? token) => Normalize(token) == Hidden ? Full : Hidden;

		public static bool IsFull(string? token) => Normalize(token) == Full;

		/// <summary>The next click OPENS (the chevron points up, "More") — else it closes ("Less").</summary>
		public static bool OpensMore(string? token) => Normalize(token) == Hidden;
	}
}
