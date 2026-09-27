using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Splits an MD / MPD product (as IEM's <c>api/1/nwstext</c> returns it) into its labelled sections. Pure.
	/// </summary>
	/// <remarks>
	/// The products are teletype text: a label at the start of a line ends in "..." and its content runs on
	/// until the next label ("AREAS AFFECTED...LOWER MI", "SUMMARY...", "DISCUSSION..."). SPC wrote them in
	/// capitals until ~2016 and WPC writes mixed case ("Areas affected..."), so labels match case-blind.
	/// ⚠️ THE DISCUSSION ENDS AT THE FORECASTER SIGNATURE (a line opening "..", "..ROGERS/KERR.. 05/20/2013"),
	/// not at a label — after it come the "PLEASE SEE" boilerplate, the ATTN WFO list and the LAT...LON
	/// coordinate block, none of which is prose.
	/// </remarks>
	public static class MesoDiscussionTextParser
	{
		// Label at line start (case-blind), then "...", then the first line of content.
		private static readonly Regex Label = new(
			@"^\s*(AREAS AFFECTED|CONCERNING|VALID|PROBABILITY OF WATCH ISSUANCE|SUMMARY|DISCUSSION|ATTN)\s*\.\.\.(.*)$",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// ⚠️ VALID carries NO "..." ("VALID 201634Z - 201730Z"), so it has a pattern of its own.
		private static readonly Regex ValidLine = new(@"^\s*(VALID)\s+(\d{6}Z.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// The header fields are one paragraph each and end at the first blank line; only SUMMARY and
		// DISCUSSION (and the repeated ATTN lines) span paragraphs.
		private static readonly string[] SingleParagraph = { "AREAS AFFECTED", "CONCERNING", "VALID", "PROBABILITY OF WATCH ISSUANCE" };

		private static readonly Regex Signature = new(@"^\s*\.\.(?<names>[^.].*?)\.\.\s*(?<date>\S*)\s*$", RegexOptions.Compiled);

		public static MesoDiscussionText Parse(string text)
		{
			var sections = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
			string? current = null;
			var forecasters = string.Empty;

			foreach (var rawLine in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
			{
				var line = rawLine.TrimEnd();
				if (line.TrimStart().StartsWith("LAT...LON", StringComparison.OrdinalIgnoreCase)) { break; }

				var sig = Signature.Match(line);
				if (sig.Success && current is not null && !Label.IsMatch(line))
				{
					forecasters = sig.Groups["names"].Value.Trim();
					current = null; // what follows the signature is boilerplate until ATTN
					continue;
				}

				// "...PLEASE SEE WWW.SPC.NOAA.GOV FOR GRAPHIC PRODUCT..." — boilerplate that ends the prose.
				// ⚠️ WPC signs with a bare name on its own line JUST BEFORE it ("Wegman"), not SPC's "..NAME..",
				// so that last short paragraph is lifted out of the discussion into the signature.
				if (line.TrimStart().StartsWith("...", StringComparison.Ordinal) &&
					line.Contains("please see", StringComparison.OrdinalIgnoreCase))
				{
					if (current is not null && forecasters.Length == 0 && TakeTrailingName(sections[current]) is { } name)
					{
						forecasters = name;
					}
					current = null;
					continue;
				}

				var m = Label.Match(line);
				if (!m.Success) { m = ValidLine.Match(line); }
				if (m.Success)
				{
					current = m.Groups[1].Value.ToUpperInvariant();
					if (!sections.TryGetValue(current, out var sb)) { sections[current] = sb = new StringBuilder(); }
					sb.Append(m.Groups[2].Value.Trim()).Append('\n');
					continue;
				}

				if (current is null) { continue; }
				if (line.Trim().Length == 0 && Array.IndexOf(SingleParagraph, current) >= 0)
				{
					current = null; // a header field is over at its blank line
					continue;
				}
				sections[current].Append(line.Trim()).Append('\n');
			}

			string Get(string label) => sections.TryGetValue(label, out var sb) ? Unwrap(sb.ToString()) : string.Empty;

			return new MesoDiscussionText(
				AreasAffected: Get("AREAS AFFECTED"),
				Concerning: Get("CONCERNING"),
				Valid: Get("VALID"),
				WatchProbability: Get("PROBABILITY OF WATCH ISSUANCE"),
				Summary: Get("SUMMARY"),
				Discussion: Get("DISCUSSION"),
				Forecasters: forecasters,
				Attn: CleanAttn(Get("ATTN")),
				Raw: text ?? string.Empty);
		}

		// If the section's last paragraph is a lone short line with no sentence punctuation (a forecaster's
		// name), remove it from the section and return it.
		private static string? TakeTrailingName(StringBuilder sb)
		{
			var body = sb.ToString().TrimEnd();
			var cut = body.LastIndexOf("\n\n", StringComparison.Ordinal);
			if (cut < 0) { return null; }
			var last = body[(cut + 2)..].Trim();
			if (last.Length == 0 || last.Contains('\n') || last.Split(' ').Length > 3 || ".!?".Contains(last[^1])) { return null; }
			sb.Clear().Append(body[..cut]).Append('\n');
			return last;
		}

		/// <summary>Undoes the teletype line wrap: lines join with a space, blank lines stay paragraph breaks.</summary>
		internal static string Unwrap(string block)
		{
			var paragraphs = Regex.Split(block.Trim(), @"\n\s*\n")
				.Select(p => Regex.Replace(p.Trim(), @"\s*\n\s*", " "))
				.Where(p => p.Length > 0);
			return string.Join("\n\n", paragraphs);
		}

		// "WFO...DTX...APX...IWX...GRR..." → "DTX · APX · IWX · GRR"
		private static string CleanAttn(string attn)
		{
			var parts = attn.Split(new[] { "..." }, StringSplitOptions.RemoveEmptyEntries)
				.Select(p => p.Trim().TrimEnd('.'))
				.Where(p => p.Length > 0 && !p.Equals("WFO", StringComparison.OrdinalIgnoreCase)
					&& !p.Equals("RFC", StringComparison.OrdinalIgnoreCase));
			return string.Join(" · ", parts);
		}
	}
}
