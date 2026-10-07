using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The FIXED starts Settings → Startup offers ("Start in…"), as persisted tokens (<c>AppSettings.StartupMode</c>).
	/// The other choice, "Pick up where I left off", is <c>AppSettings.StartupResume</c> — the last session's modes.
	/// </summary>
	/// <remarks>⚠️ Persisted as TOKENS, never indices: the picker's order may change, a saved choice must not.</remarks>
	public static class StartupModes
	{
		public const string MapOnly = "map";
		public const string Now = "now";
		public const string NowFore = "nowfore";
		public const string Past = "past";

		/// <summary>Picker order.</summary>
		public static readonly IReadOnlyList<string> Tokens = new[] { MapOnly, Now, NowFore, Past };

		/// <summary>The picker's words, in <see cref="Tokens"/> order.</summary>
		public static readonly IReadOnlyList<string> Labels = new[] { "Map only", "NowCast", "NowCast + ForeCast", "PastCast" };

		/// <summary>A known token, else NowCast (the default fixed start).</summary>
		public static string Normalize(string? token) => token is not null && Tokens.Contains(token) ? token : Now;

		public static int IndexOf(string? token) => Tokens.ToList().IndexOf(Normalize(token));

		public static string At(int index) => index >= 0 && index < Tokens.Count ? Tokens[index] : Now;
	}
}
