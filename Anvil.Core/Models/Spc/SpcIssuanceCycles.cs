using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// SPC's convective-outlook ISSUANCE CYCLES as IEM's archive names them (the <c>cycle</c> query value, a UTC
	/// hour), and when each one is issued — the ONE table both outlook sections' Cycle pickers read (PastCast's
	/// historical outlook and ForeCast's live one).
	/// </summary>
	/// <remarks>
	/// ⚠️ MEASURED against IEM's <c>spc_outlook.geojson</c>, not taken from SPC's schedule page (2026-09-30):
	/// Day 1 = 06 · 13 · 16 (the 1630Z) · 20 · 01; Day 2 = 07 · 17; Day 3 = 08 · 20. Days 4-8 have one daily
	/// issuance and no cycle choice. The Day 2 overnight cycle is 07, NOT 06 — the old PastCast list asked for 06,
	/// which IEM never has, so that option could never draw.
	/// ⚠️ <c>valid</c> IS THE DAY THE OUTLOOK IS FOR (its 12Z→12Z convective day), not the day it was issued:
	/// today's Day 2 is <c>valid=tomorrow</c>. <see cref="IssuedAtUtc"/> is relative to that valid day, so a Day 1
	/// 06Z is issued ON the valid day (before its 12Z start) and a Day 2 17Z the day BEFORE it.
	/// </remarks>
	public static class SpcIssuanceCycles
	{
		// Per day, CHRONOLOGICAL: (cycle, hours from 00Z of the VALID day to the nominal issuance).
		private static readonly (int Cycle, double Hours)[] Day1 = { (6, 6), (13, 13), (16, 16.5), (20, 20), (1, 25) };
		private static readonly (int Cycle, double Hours)[] Day2 = { (7, -17), (17, -7) };
		private static readonly (int Cycle, double Hours)[] Day3 = { (8, -40), (20, -28) };

		private static (int Cycle, double Hours)[] Table(int day) => day switch
		{
			1 => Day1,
			2 => Day2,
			3 => Day3,
			_ => Array.Empty<(int, double)>(),
		};

		/// <summary>The day's cycles, earliest issuance first. Empty for days 4-8.</summary>
		public static IReadOnlyList<int> For(int day) => Table(day).Select(t => t.Cycle).ToArray();

		/// <summary>When <paramref name="cycle"/> of <paramref name="day"/> is nominally issued, for the outlook
		/// valid on <paramref name="validDay"/>. SPC's real stamps run a few minutes either side.</summary>
		public static DateTimeOffset IssuedAtUtc(int day, int cycle, DateOnly validDay)
		{
			var hours = Table(day).FirstOrDefault(t => t.Cycle == cycle).Hours;
			return new DateTimeOffset(validDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(hours);
		}

		/// <summary>The picker's name for a cycle: "06Z", "1630Z" (IEM's 16 is SPC's 1630Z update), "20Z".</summary>
		public static string Label(int cycle) => cycle == 16 ? "1630Z" : $"{cycle:D2}Z";

		/// <summary>The SPC convective day (12Z→12Z) containing <paramref name="utc"/> — IEM's <c>valid</c> for its Day 1.</summary>
		public static DateOnly ConvectiveDay(DateTimeOffset utc)
		{
			var d = utc.UtcDateTime;
			return DateOnly.FromDateTime(d.Hour >= 12 ? d : d.AddDays(-1));
		}
	}
}
