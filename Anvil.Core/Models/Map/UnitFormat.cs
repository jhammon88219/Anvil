using System;
using System.Globalization;

namespace Anvil.Models
{
	/// <summary>The unit choices in force (Settings → Units). Tokens, already normalized.</summary>
	public sealed record UnitSet(string Clock, string Zone, string Speed, string Height, string Hail)
	{
		/// <summary>12-hour local, mph, kft, inches — what every readout showed before Settings → Units.</summary>
		public static UnitSet Default { get; } = new(UnitChoices.Clock.Default, UnitChoices.Zone.Default,
			UnitChoices.Speed.Default, UnitChoices.Height.Default, UnitChoices.Hail.Default);

		/// <summary>Every token through its family's gate.</summary>
		public UnitSet Normalized() => new(UnitChoices.Clock.Normalize(Clock), UnitChoices.Zone.Normalize(Zone),
			UnitChoices.Speed.Normalize(Speed), UnitChoices.Height.Normalize(Height), UnitChoices.Hail.Normalize(Hail));

		public bool IsUtc => Zone == "utc";

		/// <summary>UTC is ALWAYS 24-hour ("17:04Z"), whatever the clock choice says.</summary>
		public bool Is24Hour => IsUtc || Clock == "24h";
	}

	/// <summary>
	/// The ONE place a time, speed, height or hail size becomes text a person reads, in the units chosen in Settings →
	/// Units (ground distance is <see cref="DistanceUnits"/>). <see cref="Current"/> is set by <c>MapViewModel</c> from
	/// the settings and pushed to the page, whose js/units.js MIRRORS these rules — change both.
	/// </summary>
	/// <remarks>
	/// ⚠️ GLOBAL STATE, on purpose: a time is formatted in rows, records and services all over Core, and threading a
	/// unit set through each would touch every constructor for one display preference. Every formatter has a PURE
	/// overload taking the <see cref="UnitSet"/> — tests use those, never <see cref="Apply"/>.
	/// ⚠️ <see cref="Changed"/> fires on the UI thread (the settings setters run there); a view model that shows a
	/// formatted text re-raises it — texts it STORES pick the new units up at their next refresh.
	/// ⚠️ Logs, diagnostics and file names never come through here: they stay UTC / invariant.
	/// </remarks>
	public static class UnitFormat
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		/// <summary>The choices in force.</summary>
		public static UnitSet Current { get; private set; } = UnitSet.Default;

		/// <summary>Raised after <see cref="Current"/> changes.</summary>
		public static event EventHandler? Changed;

		/// <summary>Sets the choices (normalized); raises <see cref="Changed"/> only on a real change.</summary>
		public static void Apply(UnitSet set)
		{
			var n = set.Normalized();
			if (n == Current) { return; }
			Current = n;
			Changed?.Invoke(null, EventArgs.Empty);
		}

		// ── Time ──

		/// <summary>The moment in the display zone (for date parts: with UTC chosen, the DATE is the UTC date too).</summary>
		public static DateTimeOffset Zoned(DateTimeOffset t, UnitSet u) => u.IsUtc ? t.ToUniversalTime() : t.ToLocalTime();
		public static DateTimeOffset Zoned(DateTimeOffset t) => Zoned(t, Current);

		/// <summary>"12:04 PM" · "12:04" · "17:04Z".</summary>
		public static string Clock(DateTimeOffset t, UnitSet u)
		{
			var z = Zoned(t, u);
			return u.IsUtc ? z.ToString("HH:mm", Inv) + "Z"
				: u.Is24Hour ? z.ToString("HH:mm", CultureInfo.CurrentCulture)
				: z.ToString("h:mm tt", CultureInfo.CurrentCulture);
		}
		public static string Clock(DateTimeOffset t) => Clock(t, Current);

		/// <summary>"12:04:05 PM" · "12:04:05" · "17:04:05Z".</summary>
		public static string ClockSeconds(DateTimeOffset t, UnitSet u)
		{
			var z = Zoned(t, u);
			return u.IsUtc ? z.ToString("HH:mm:ss", Inv) + "Z"
				: u.Is24Hour ? z.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
				: z.ToString("h:mm:ss tt", CultureInfo.CurrentCulture);
		}
		public static string ClockSeconds(DateTimeOffset t) => ClockSeconds(t, Current);

		/// <summary>A span: "5:00–7:00 PM" (12-hour, one shared AM/PM), "5:00 AM–1:00 PM", "17:00–19:00", "22:00–00:00Z".</summary>
		public static string ClockSpan(DateTimeOffset a, DateTimeOffset b, UnitSet u)
		{
			if (u.Is24Hour)
			{
				var za = Zoned(a, u);
				var head = za.ToString("HH:mm", Inv);
				return head + "–" + Clock(b, u);
			}
			var la = Zoned(a, u);
			var lb = Zoned(b, u);
			var cur = CultureInfo.CurrentCulture;
			return la.ToString("tt", cur) == lb.ToString("tt", cur)
				? la.ToString("h:mm", cur) + "–" + lb.ToString("h:mm tt", cur)
				: la.ToString("h:mm tt", cur) + "–" + lb.ToString("h:mm tt", cur);
		}
		public static string ClockSpan(DateTimeOffset a, DateTimeOffset b) => ClockSpan(a, b, Current);

		/// <summary>A date part in the display zone, e.g. <c>Date(t, "ddd MMM d")</c> → "Tue May 20".</summary>
		public static string Date(DateTimeOffset t, string format, UnitSet u) => Zoned(t, u).ToString(format, CultureInfo.CurrentCulture);
		public static string Date(DateTimeOffset t, string format) => Date(t, format, Current);

		// ── Speed ──

		private const double MsPerKt = 0.514444;
		private const double MsPerMph = 0.44704;

		/// <summary>"28 mph" · "24 kt" · "45 km/h" · "12.3 m/s" (m/s keeps one decimal — its numbers are small).</summary>
		public static string SpeedFromMs(double ms, UnitSet u)
		{
			var (v, digits) = u.Speed switch
			{
				"kt" => (ms / MsPerKt, 0),
				"kmh" => (ms * 3.6, 0),
				"ms" => (ms, 1),
				_ => (ms / MsPerMph, 0),
			};
			return v.ToString("F" + digits.ToString(Inv), CultureInfo.CurrentCulture) + " " + SpeedLabel(u);
		}
		public static string SpeedFromMs(double ms) => SpeedFromMs(ms, Current);
		public static string SpeedFromKt(double kt, UnitSet u) => SpeedFromMs(kt * MsPerKt, u);
		public static string SpeedFromKt(double kt) => SpeedFromKt(kt, Current);
		public static string SpeedFromMph(double mph, UnitSet u) => SpeedFromMs(mph * MsPerMph, u);
		public static string SpeedFromMph(double mph) => SpeedFromMph(mph, Current);

		/// <summary>The speed unit's suffix ("mph", "km/h"…).</summary>
		public static string SpeedLabel(UnitSet u) => UnitChoices.Speed.Labels[UnitChoices.Speed.IndexOf(u.Speed)];
		public static string SpeedLabel() => SpeedLabel(Current);

		/// <summary>Converts m/s into the speed unit's own number (legends that print bare numbers).</summary>
		public static double SpeedValueFromMs(double ms, UnitSet u) => u.Speed switch
		{
			"kt" => ms / MsPerKt,
			"kmh" => ms * 3.6,
			"ms" => ms,
			_ => ms / MsPerMph,
		};
		public static double SpeedValueFromMs(double ms) => SpeedValueFromMs(ms, Current);

		// ── Height ──

		/// <summary>"22.4 kft" · "6.8 km" · "22,400 ft" (feet rounded to the hundred — the data isn't finer).</summary>
		public static string HeightFromKft(double kft, UnitSet u) => u.Height switch
		{
			"km" => (kft * 0.3048).ToString("F1", CultureInfo.CurrentCulture) + " km",
			"ft" => (Math.Round(kft * 10) * 100).ToString("N0", CultureInfo.CurrentCulture) + " ft",
			_ => kft.ToString("F1", CultureInfo.CurrentCulture) + " kft",
		};
		public static string HeightFromKft(double kft) => HeightFromKft(kft, Current);

		// ── Hail ──

		/// <summary>"1.75 in" · "4.4 cm".</summary>
		public static string HailFromInches(double inches, UnitSet u) => u.Hail == "cm"
			? (inches * 2.54).ToString("F1", CultureInfo.CurrentCulture) + " cm"
			: inches.ToString("0.00", CultureInfo.CurrentCulture) + " in";
		public static string HailFromInches(double inches) => HailFromInches(inches, Current);

		// ── The page ──

		/// <summary>The set as the page's js/units.js reads it (tokens only — never free text into the page).</summary>
		public static string PageJson(UnitSet u) =>
			$"{{\"clock\":\"{u.Clock}\",\"zone\":\"{u.Zone}\",\"speed\":\"{u.Speed}\",\"height\":\"{u.Height}\",\"hail\":\"{u.Hail}\"}}";
	}
}
