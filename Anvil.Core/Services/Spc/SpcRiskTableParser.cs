using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Reads SPC's CATEGORICAL risk-area table ("Day 1 Risk | Area (sq. mi.) | Area Pop. | Some Larger Population
	/// Centers in Risk Area") out of an outlook page's HTML — the same page the discussion text is scraped from, live
	/// or archive. Pure: HTML in, rows out; empty when the page has no such table (quiet days, older archive pages,
	/// Day 4-8).
	/// </summary>
	/// <remarks>
	/// ⚠️ SPC writes the headers with &amp;nbsp; ("Area&amp;nbsp;Pop.") — decode before matching, or nothing matches.
	/// The decoded U+00A0 is then folded to a plain space by the <c>\s+</c> collapse in <see cref="Cells"/>.
	/// ⚠️ A page carries FOUR such tables (categorical, then tornado / wind / hail probabilities). Only the one whose
	/// first header is "Day N Risk" is categorical; the others start "Day N Tornado Risk" etc.
	/// </remarks>
	public static class SpcRiskTableParser
	{
		private static readonly Regex TableRx = new(@"<table.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
		private static readonly Regex RowRx = new(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
		private static readonly Regex CellRx = new(@"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
		private static readonly Regex TagRx = new(@"<[^>]+>");
		private static readonly Regex CategoricalHeaderRx = new(@"^Day \d(?:-\d)? Risk$", RegexOptions.IgnoreCase);

		public static IReadOnlyList<OutlookRiskArea> Parse(string? html)
		{
			if (string.IsNullOrEmpty(html)) return Array.Empty<OutlookRiskArea>();
			foreach (Match table in TableRx.Matches(html))
			{
				var rows = RowRx.Matches(table.Value).Select(r => Cells(r.Groups[1].Value)).Where(c => c.Count > 0).ToList();
				var header = rows.FindIndex(c => c.Count >= 4 && CategoricalHeaderRx.IsMatch(c[0])
					&& c.Any(x => x.Contains("Pop.", StringComparison.OrdinalIgnoreCase)));
				if (header < 0) continue;

				var areas = new List<OutlookRiskArea>();
				foreach (var c in rows.Skip(header + 1))
				{
					if (c.Count < 4 || Code(c[0]) is not { } code) continue;
					if (!TryNumber(c[1], out var area) || !TryNumber(c[2], out var people)) continue;
					var places = c[3].Split("...", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						.Where(p => p.Length > 0).ToArray();
					areas.Add(new OutlookRiskArea(code, area, people, places));
				}
				return areas;
			}
			return Array.Empty<OutlookRiskArea>();
		}

		private static List<string> Cells(string row) =>
			CellRx.Matches(row)
				.Select(m => Regex.Replace(WebUtility.HtmlDecode(TagRx.Replace(m.Groups[1].Value, string.Empty)), @"\s+", " ").Trim())
				.ToList();

		private static string? Code(string name) => name.ToUpperInvariant() switch
		{
			"HIGH" => "HIGH",
			"MODERATE" => "MDT",
			"ENHANCED" => "ENH",
			"SLIGHT" => "SLGT",
			"MARGINAL" => "MRGL",
			_ => null,
		};

		private static bool TryNumber(string s, out long n) =>
			long.TryParse(s.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
	}
}
