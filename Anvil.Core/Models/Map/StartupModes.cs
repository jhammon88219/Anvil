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
		public const string Fore = "fore"; // ForeCast only (2026-11-16)
		public const string NowFore = "nowfore";
		public const string Past = "past";

		/// <summary>Picker order.</summary>
		public static readonly IReadOnlyList<string> Tokens = new[] { MapOnly, Now, Fore, NowFore, Past };

		/// <summary>The picker's words, in <see cref="Tokens"/> order (short: five share the 394 px picker).</summary>
		public static readonly IReadOnlyList<string> Labels = new[] { "Map only", "NowCast", "ForeCast", "Now + Fore", "PastCast" };

		/// <summary>A known token, else NowCast (the default fixed start).</summary>
		public static string Normalize(string? token) => token is not null && Tokens.Contains(token) ? token : Now;

		public static int IndexOf(string? token) => Tokens.ToList().IndexOf(Normalize(token));

		public static string At(int index) => index >= 0 && index < Tokens.Count ? Tokens[index] : Now;
	}
}
