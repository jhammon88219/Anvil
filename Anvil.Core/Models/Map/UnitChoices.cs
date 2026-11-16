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

		/// <summary>How a calendar date reads: "Nov 16, 2026" · "11/16/2026" · "16/11/2026" · "2026-11-16". The buttons carry
		/// the NAMES; the Settings sample shows the result.</summary>
		public static readonly UnitFamily Date = new("written", new[] { "written", "monthfirst", "dayfirst", "iso" },
			new[] { "Written", "Month first", "Day first", "ISO" });

		/// <summary>How a latitude / longitude reads: "35.333° N, 97.278° W" or "35°19′59″ N, 97°16′41″ W".</summary>
		public static readonly UnitFamily Coords = new("decimal", new[] { "decimal", "dms" }, new[] { "Decimal", "Deg · min · sec" });

		/// <summary>
		/// Settings → General → Units' "Unit set": a PRESET (every unit fixed, the pickers locked) or Custom (the pickers
		/// free; the last custom picks are kept apart, so a preset never overwrites them). Default Custom — the shipped
		/// defaults (km + mph) match no preset.
		/// </summary>
		public static readonly UnitFamily Set = new("custom", new[] { "us", "metric", "forecaster", "custom" },
			new[] { "US", "Metric", "Forecaster", "Custom" });

		/// <summary>The whole set a PRESET fixes (null for "custom"): ground distance + the <see cref="UnitSet"/>.</summary>
		public static (string Distance, UnitSet Units)? Preset(string token) => token switch
		{
			"us" => (DistanceUnits.Miles, new UnitSet("12h", "local", "mph", "kft", "in", "written", "decimal")),
			"metric" => (DistanceUnits.Kilometers, new UnitSet("24h", "local", "kmh", "km", "cm", "dayfirst", "decimal")),
			"forecaster" => (DistanceUnits.NauticalMiles, new UnitSet("24h", "utc", "kt", "kft", "in", "iso", "decimal")),
			_ => null,
		};
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
