using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// A temporal pane's two states, as persisted tokens (one per mode): Full = the whole pane; Rail = a narrow
	/// full-height rail along its edge, with only the DOCKED sections (<see cref="TemporalPaneDock"/>) beside it.
	/// </summary>
	/// <remarks>⚠️ Persisted as TOKENS, never indices. Unknown or missing = Full (the pane as it always was).</remarks>
	public static class TemporalPaneStates
	{
		public const string Full = "full";
		public const string Rail = "rail";

		public static readonly IReadOnlyList<string> Tokens = new[] { Full, Rail };

		/// <summary>A known token, else Full.</summary>
		public static string Normalize(string? token) =>
			token is not null && Tokens.Contains(token) ? token : Full;

		/// <summary>The collapse / expand button's one click.</summary>
		public static string Next(string? token) => Normalize(token) == Full ? Rail : Full;

		public static bool IsRail(string? token) => Normalize(token) == Rail;
	}
}
