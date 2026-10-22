using System;
using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// The app-wide UNIT choices of Settings → Units (besides ground distance, which is <see cref="DistanceUnits"/>):
	/// clock style, time zone, speed, height and hail size. Each is a TOKEN family — persisted as a string and pushed
	/// into the map page verbatim — with the same gate as <see cref="DistanceUnits"/>: <c>Normalize</c> maps anything
	/// unknown to the default, so a hand-edited settings file can't fail a load. <see cref="UnitFormat"/> formats with them.
	/// </summary>
	/// <remarks>
	/// ⚠️ Picker order = <c>All</c> = <c>Labels</c>; an index is never persisted, only the token.
	/// ⚠️ The page mirrors these tokens (js/units.js) — add a token here, add it there.
	/// </remarks>
	public static class UnitChoices
	{
		/// <summary>12-hour ("12:04 PM") or 24-hour ("12:04") clock. Ignored while the zone is UTC (always 24-hour Z).</summary>
		public static readonly UnitFamily Clock = new("12h", new[] { "12h", "24h" }, new[] { "12-hour", "24-hour" });

		/// <summary>Your PC's zone, or UTC ("17:04Z" — the SPC / NWS product convention).</summary>
		public static readonly UnitFamily Zone = new("local", new[] { "local", "utc" }, new[] { "Local", "UTC" });

		/// <summary>Wind / motion / velocity speeds.</summary>
		public static readonly UnitFamily Speed = new("mph", new[] { "kt", "mph", "kmh", "ms" }, new[] { "kt", "mph", "km/h", "m/s" });

		/// <summary>Heights above ground (storm-cell tops and core heights).</summary>
		public static readonly UnitFamily Height = new("kft", new[] { "km", "kft", "ft" }, new[] { "km", "kft", "ft" });

		/// <summary>Hail stone size.</summary>
		public static readonly UnitFamily Hail = new("in", new[] { "in", "cm" }, new[] { "in", "cm" });
	}

	/// <summary>One unit choice: its tokens (picker order), their labels, and the default.</summary>
	public sealed class UnitFamily
	{
		public UnitFamily(string fallback, IReadOnlyList<string> all, IReadOnlyList<string> labels)
		{
			Default = fallback;
			All = all;
			Labels = labels;
		}

		/// <summary>The token any unknown value becomes.</summary>
		public string Default { get; }

		/// <summary>Every token, in picker order. ⚠️ Parallel to <see cref="Labels"/>.</summary>
		public IReadOnlyList<string> All { get; }

		/// <summary>The picker's labels.</summary>
		public IReadOnlyList<string> Labels { get; }

		/// <summary>A token this build knows (ordinal match), else <see cref="Default"/>.</summary>
		public string Normalize(string? token)
		{
			foreach (var t in All) { if (string.Equals(t, token, StringComparison.Ordinal)) { return t; } }
			return Default;
		}

		public int IndexOf(string? token)
		{
			var n = Normalize(token);
			for (var i = 0; i < All.Count; i++) { if (All[i] == n) { return i; } }
			return 0;
		}

		/// <summary>The token at a picker index; out of range reads as the default.</summary>
		public string FromIndex(int index) => index >= 0 && index < All.Count ? All[index] : Default;
	}
}
