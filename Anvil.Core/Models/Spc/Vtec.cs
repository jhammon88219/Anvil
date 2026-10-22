using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Anvil.Models
{
	/// <summary>
	/// A P-VTEC string's event identity and end time: "/O.CON.KOUN.TO.W.0012.000000T0000Z-261021T1830Z/" →
	/// Key "OUN.TO.W.12", Ends 2026-10-21 18:30Z. The KEY is how the Warnings day list joins CAP (live) to IEM
	/// (the day so far) — IEM names an event by 3-letter office + phenomena + significance + event id, so this
	/// drops the office's leading K/P/T and the ETN's zero padding. ⚠️ The begin time is NOT read: a follow-up
	/// statement for an event already in effect carries 000000T0000Z there.
	/// </summary>
	public sealed record Vtec(string Key, DateTimeOffset? Ends)
	{
		private static readonly Regex Pattern = new(
			@"/[OTEX]\.[A-Z]{3}\.([A-Z]{4})\.([A-Z]{2})\.([A-Z])\.(\d{4})\.\d{6}T\d{4}Z-(\d{6}T\d{4}Z)/",
			RegexOptions.CultureInvariant);

		/// <summary>The key IEM would give the same event. Pure.</summary>
		public static string KeyOf(string wfo3, string phenom, string sig, int etn) =>
			string.Create(CultureInfo.InvariantCulture, $"{wfo3}.{phenom}.{sig}.{etn}");

		/// <summary>Null when the text holds no P-VTEC string.</summary>
		public static Vtec? Parse(string? text)
		{
			if (string.IsNullOrEmpty(text) || Pattern.Match(text) is not { Success: true } m) { return null; }
			var office = m.Groups[1].Value;
			var key = KeyOf(office[1..], m.Groups[2].Value, m.Groups[3].Value,
				int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
			DateTimeOffset? ends = DateTimeOffset.TryParseExact(m.Groups[5].Value, "yyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var e) ? e : null;
			return new Vtec(key, ends);
		}
	}
}
