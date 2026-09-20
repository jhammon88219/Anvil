using System;
using System.Collections.Generic;
using System.Globalization;

namespace Anvil.Models
{
	/// <summary>
	/// The app's GROUND-DISTANCE unit: the one place metres become something a person reads. Every
	/// distance readout the user sees — the range ruler's chip, the Inspector's range — formats through
	/// here, and the token is pushed to the map page verbatim so the JS side formats identically.
	/// </summary>
	/// <remarks>
	/// ⚠️ GROUND DISTANCE ONLY. Heights, echo tops and VAD layer bounds stay in km whatever this says:
	/// those are meteorological quantities with a conventional unit, not distances the user is measuring.
	/// Don't route them through here "for consistency" — it would make every wind-profile readout disagree
	/// with the literature it is checked against.
	/// ⚠️ TOKENS, not an enum, for the reason <see cref="Services.AppSettings.SettingsTabPlacement"/> is a
	/// string: the value is persisted and also crosses into JS, and an unrecognized one has to fall back to
	/// kilometres rather than fail a settings load or a script call. <see cref="Normalize"/> is that gate —
	/// call it on anything arriving from disk or a picker index.
	/// </remarks>
	public static class DistanceUnits
	{
		/// <summary>Kilometres — the default, and the fallback for any unrecognized token.</summary>
		public const string Kilometers = "km";

		/// <summary>Statute miles.</summary>
		public const string Miles = "mi";

		/// <summary>Nautical miles (aviation + marine convention, and what radar range is often quoted in).</summary>
		public const string NauticalMiles = "nm";

		private const double MetersPerKilometer = 1000.0;
		private const double MetersPerMile = 1609.344;
		private const double MetersPerNauticalMile = 1852.0;

		/// <summary>Every token, in picker order. ⚠️ Parallel to <see cref="Labels"/>.</summary>
		public static IReadOnlyList<string> All { get; } = new[] { Kilometers, Miles, NauticalMiles };

		/// <summary>The picker's labels, in <see cref="All"/> order.</summary>
		public static IReadOnlyList<string> Labels { get; } = new[] { "Kilometers", "Miles", "Nautical miles" };

		/// <summary>Maps anything — a persisted string, a hand-edited file, null — to a token this build knows.</summary>
		public static string Normalize(string? token) => token switch
		{
			Miles => Miles,
			NauticalMiles => NauticalMiles,
			_ => Kilometers,
		};

		/// <summary>The token's position in <see cref="All"/>; 0 (kilometres) for anything unrecognized.</summary>
		public static int IndexOf(string? token) => Normalize(token) switch
		{
			Miles => 1,
			NauticalMiles => 2,
			_ => 0,
		};

		/// <summary>The token at a picker index, clamped — an out-of-range index reads as kilometres.</summary>
		public static string FromIndex(int index) =>
			index >= 0 && index < All.Count ? All[index] : Kilometers;

		/// <summary>Converts metres into the unit's own quantity.</summary>
		public static double FromMeters(double meters, string? unit) => Normalize(unit) switch
		{
			Miles => meters / MetersPerMile,
			NauticalMiles => meters / MetersPerNauticalMile,
			_ => meters / MetersPerKilometer,
		};

		/// <summary>
		/// A distance for display: the converted value and its suffix, e.g. <c>"151 km"</c>. One decimal
		/// below 10 (where a whole number is too coarse to read a gate by), none above it.
		/// </summary>
		public static string Format(double meters, string? unit)
		{
			var token = Normalize(unit);
			var value = FromMeters(meters, token);
			var digits = Math.Abs(value) < 10 ? 1 : 0;
			return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture)
				+ " " + token;
		}
	}
}
