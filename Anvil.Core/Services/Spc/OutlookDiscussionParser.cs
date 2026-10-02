using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Splits SPC's convective outlook discussion (the &lt;pre&gt; text, Day 1-3 and 4-8, 2004 to now) into an
	/// <see cref="OutlookDiscussion"/>. Pure: text in, parts out — no I/O.
	/// </summary>
	/// <remarks>
	/// The rules come from a line-by-line study of 26 real issuances (2026-10-02; every cycle, quiet days to High
	/// Risks, 2004-2026). The ones that bite:
	/// <list type="bullet">
	/// <item>A HEADER is a "..."-wrapped run of lines that STARTS a blank-separated block. Body lines can also begin
	/// with "..." or "--" mid-paragraph, so only a block's first line can open one.</item>
	/// <item>A header may wrap. If the block's LAST line ends in "..." the whole block is header (a headline, an
	/// emphasis line), split only where a line ending "..." is followed by one starting "..." (Day 4-8's
	/// "...DISCUSSION..." directly over "...Days 4-6/..."). Otherwise the header ends at the first line ending
	/// "..." and the rest of the block is body.</item>
	/// <item>An update's SIGNATURE sits mid-text: what follows ".PREV DISCUSSION... /ISSUED …/" is the earlier
	/// issuance, repeated, and it has NO signature of its own.</item>
	/// <item>The EMPHASIS headline has no fixed wording ("...MAJOR TORNADO OUTBREAK…", "--DANGEROUS…--"). It is a
	/// header with no body that is either followed straight by another header, or met before any summary or
	/// non-update section.</item>
	/// <item>Legacy (2004-era) points paragraphs ("… TO THE RIGHT OF A LINE FROM 25 NNW OMA …") and the NOTICE
	/// about them are dropped; so are the link line and the wire-routing header.</item>
	/// </list>
	/// </remarks>
	public static class OutlookDiscussionParser
	{
		private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

		private static readonly Regex ProductIdRx = new(@"^SPC AC (\d{6})$", Ci);
		private static readonly Regex WireRx = new(@"^(ZCZC\b.*|[A-Z]{4}\d{2} [A-Z]{4} \d{6})$", Ci);
		private static readonly Regex TitleRx = new(@"^DAY (\d(?:-\d)?) CONVECTIVE OUTLOOK$", Ci);
		private static readonly Regex IssuerRx = new(@"^NWS STORM PREDICTION CENTER\b", Ci);
		private static readonly Regex IssuedRx = new(@"^(\d{1,2}?)(\d{2}) (AM|PM) ([A-Z]{3}) ([A-Z]{3}) ([A-Z]{3}) (\d{1,2}) (\d{4})$", Ci);
		private static readonly Regex ValidRx = new(@"^VALID (\d{2})(\d{2})(\d{2})Z - (\d{2})(\d{2})(\d{2})Z$", Ci);

		private static readonly Regex HeadlineRx = new(
			@"^THERE IS A (HIGH|MDT|MODERATE|ENH|ENHANCED|SLGT|SLIGHT|MRGL|MARGINAL) RISK OF (?:SEVERE THUNDERSTORMS|SVR TSTMS)\s*(.*)$", Ci);
		private static readonly Regex NoSevereRx = new(@"^NO SEVERE THUNDERSTORM AREAS FORECAST$", Ci);
		private static readonly Regex UpdateRx = new(@"^(\d{2})Z (?:OUTLOOK )?UPDATE$", Ci);
		private static readonly Regex SynopsisRx = new(@"^SYNOP", Ci);
		private static readonly Regex DaysRx = new(@"^DAYS? \d", Ci);
		private static readonly Regex SignatureRx = new(@"^\.\.(?!\.)(.+?)\.\.\s+\d{1,2}/\d{1,2}/\d{4}$", Ci);
		private static readonly Regex PrevRx = new(@"^\.PREV(?:IOUS)? DISCUSSION\.{2,3}\s*/ISSUED\s+(.+?)\s*/?$", Ci);
		private static readonly Regex NoteRx = new(@"^NOTE: THE NEXT .*OUTLOOK IS SCHEDULED (?:BY|FOR) (\S+?)\.?$", Ci);
		private static readonly Regex ChangesRx = new(@"^CHANGES TO PREVIOUS OUTLOOK", Ci);
		private static readonly Regex ListItemRx = new(@"^(\d+[/.)]|[-*•])\s");
		private static readonly Regex Spaces = new(@"\s{2,}");

		private sealed class SectionBuilder
		{
			public OutlookSectionKind Kind;
			public string Title = string.Empty;
			public readonly List<string> Paragraphs = new();
			public OutlookSection Build() => new(Kind, Title, Paragraphs.ToArray());
		}

		public static OutlookDiscussion Parse(string? raw)
		{
			raw ??= string.Empty;
			var blocks = Blocks(raw);

			// ── The heading blocks: ID, title, issued, valid (+ Day 4-8's wire routing) ──
			string title = "Convective Outlook";
			string? productId = null, issued = null, validRaw = null;
			DateOnly? issuedDate = null;
			var bi = 0;
			for (; bi < blocks.Count && blocks[bi].All(IsHeadingLine); bi++)
			{
				foreach (var line in blocks[bi])
				{
					Match m;
					if ((m = ProductIdRx.Match(line)).Success) productId = line;
					else if ((m = TitleRx.Match(line)).Success) title = $"Day {m.Groups[1].Value} Convective Outlook";
					else if (IssuedRx.IsMatch(line)) (issued, issuedDate) = FormatIssued(line);
					else if (ValidRx.IsMatch(line)) validRaw = line;
				}
			}

			// ── The body ──
			var headlines = new List<OutlookHeadline>();
			string? alert = null, forecaster = null, previousIssued = null, next = null;
			var summary = new List<string>();
			var sections = new List<SectionBuilder>();
			var previous = new List<SectionBuilder>();
			var inPrevious = false;
			var summarySeen = false;
			List<string>? target = null;   // where body paragraphs go; null = open an untitled section
			var discard = new List<string>(); // the NOTICE's paragraphs land here and go nowhere

			List<SectionBuilder> Current() => inPrevious ? previous : sections;

			List<string> Open(OutlookSectionKind kind, string sectionTitle)
			{
				var s = new SectionBuilder { Kind = kind, Title = sectionTitle };
				Current().Add(s);
				return s.Paragraphs;
			}

			void Header(string text, bool hasBody, bool nextIsHeader)
			{
				Match m;
				if ((m = HeadlineRx.Match(text)).Success)
				{
					if (!inPrevious)
					{
						var (code, words) = Category(m.Groups[1].Value);
						headlines.Add(new OutlookHeadline(code, words, Clean(m.Groups[2].Value)));
					}
					target = null;
					return;
				}
				if (NoSevereRx.IsMatch(text))
				{
					if (!inPrevious) headlines.Add(new OutlookHeadline(string.Empty, "NO SEVERE THUNDERSTORM AREAS FORECAST", string.Empty));
					target = null;
					return;
				}
				var upper = text.ToUpperInvariant();
				if (upper == "SUMMARY")
				{
					if (inPrevious) { target = Open(OutlookSectionKind.Discussion, "Summary"); }
					else { target = summary; summarySeen = true; }
					return;
				}
				if (upper == "NOTICE") { target = discard; return; }
				if ((m = UpdateRx.Match(text)).Success) { target = Open(OutlookSectionKind.Update, $"{m.Groups[1].Value}Z update"); return; }
				if (upper == "DISCUSSION")
				{
					// Day 4-8's group header directly over a "...Days…" header is a wrapper, not a section.
					target = !hasBody && nextIsHeader ? null : Open(OutlookSectionKind.Discussion, "Discussion");
					return;
				}
				if (SynopsisRx.IsMatch(text)) { target = Open(OutlookSectionKind.Synopsis, text); return; }
				if (DaysRx.IsMatch(text)) { target = Open(OutlookSectionKind.Days, text); return; }

				var beforeContent = !summarySeen && Current().All(s => s.Kind == OutlookSectionKind.Update);
				if (!hasBody && (nextIsHeader || beforeContent))
				{
					// The emphasis headline. ⚠️ `target` is left alone: in 2011's 20Z the update's text
					// continues straight after it, with no header of its own.
					if (inPrevious) Open(OutlookSectionKind.Alert, text);
					else alert = alert is null ? text : $"{alert} {text}";
					return;
				}
				target = Open(OutlookSectionKind.Region, text);
			}

			for (var i = bi; i < blocks.Count; i++)
			{
				var b = blocks[i];
				var first = b[0];
				Match m;

				if (first.StartsWith("CLICK TO GET", StringComparison.OrdinalIgnoreCase)) continue;
				if ((m = NoteRx.Match(first)).Success) { next = $"by {m.Groups[1].Value}"; continue; }
				if (b.Count == 1 && (m = SignatureRx.Match(first)).Success)
				{
					if (!inPrevious && forecaster is null) forecaster = m.Groups[1].Value.Replace("/", " / ");
					target = null;
					continue;
				}
				if (b.Count == 1 && (m = PrevRx.Match(first)).Success)
				{
					inPrevious = true;
					var raw1 = m.Groups[1].Value.Trim();
					previousIssued = IssuedRx.IsMatch(raw1) ? FormatIssued(raw1).Text : raw1;
					target = null;
					continue;
				}
				if (first.StartsWith("---", StringComparison.Ordinal) && first.EndsWith("---", StringComparison.Ordinal)) continue;
				if (first.StartsWith("--", StringComparison.Ordinal) && b[^1].EndsWith("--", StringComparison.Ordinal))
				{
					var text = Clean(Join(b).Trim('-', ' '));
					if (inPrevious) Open(OutlookSectionKind.Alert, text);
					else alert = alert is null ? text : $"{alert} {text}";
					continue;
				}
				if (!first.StartsWith("...", StringComparison.Ordinal)
					&& string.Join(" ", b).Contains("TO THE RIGHT OF A LINE FROM", StringComparison.OrdinalIgnoreCase))
				{
					continue; // legacy points paragraph
				}

				// Headers at the top of the block, then its body.
				var headers = new List<string>();
				var pos = 0;
				var allHeader = b[^1].EndsWith("...", StringComparison.Ordinal);
				while (pos < b.Count && IsHeaderStart(b[pos]))
				{
					int end;
					if (allHeader)
					{
						end = pos;
						while (end < b.Count - 1 && !(b[end].EndsWith("...", StringComparison.Ordinal) && IsHeaderStart(b[end + 1]))) end++;
					}
					else
					{
						end = -1;
						for (var k = pos; k < b.Count; k++)
						{
							if (b[k].EndsWith("...", StringComparison.Ordinal) && (k > pos || b[k].Length > 3)) { end = k; break; }
						}
						if (end < 0) break; // a body paragraph that merely starts with "..."
					}
					headers.Add(Clean(Join(b.Skip(pos).Take(end - pos + 1)).Trim('.', ' ')));
					pos = end + 1;
				}
				var body = b.Skip(pos).ToList();

				for (var h = 0; h < headers.Count; h++)
				{
					var last = h == headers.Count - 1;
					var hasBody = last && body.Count > 0;
					var nextIsHeader = !last || (!hasBody && (i + 1 >= blocks.Count || EndsContent(blocks[i + 1][0])));
					Header(headers[h], hasBody, nextIsHeader);
				}

				if (body.Count == 0) continue;
				if (headers.Count == 0 && ChangesRx.IsMatch(body[0]))
				{
					target = Open(OutlookSectionKind.Update, Clean(body[0]).TrimEnd(':'));
					body.RemoveAt(0);
					if (body.Count == 0) continue;
				}
				target ??= Open(OutlookSectionKind.Discussion, string.Empty);
				target.AddRange(Paragraphs(body));
			}

			return new OutlookDiscussion(
				title, productId, issued, FormatValid(validRaw, issuedDate),
				headlines, alert, summary,
				sections.Select(s => s.Build()).ToArray(),
				forecaster, previousIssued,
				previous.Select(s => s.Build()).ToArray(),
				next, raw);
		}

		// ── Pieces ──

		private static List<List<string>> Blocks(string raw)
		{
			var blocks = new List<List<string>>();
			var current = new List<string>();
			foreach (var rawLine in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
			{
				var line = rawLine.Trim();
				if (line.Length == 0)
				{
					if (current.Count > 0) { blocks.Add(current); current = new List<string>(); }
				}
				else
				{
					current.Add(line);
				}
			}
			if (current.Count > 0) blocks.Add(current);
			return blocks;
		}

		private static bool IsHeadingLine(string line) =>
			ProductIdRx.IsMatch(line) || WireRx.IsMatch(line) || TitleRx.IsMatch(line)
			|| IssuerRx.IsMatch(line) || IssuedRx.IsMatch(line) || ValidRx.IsMatch(line);

		private static bool IsHeaderStart(string line) =>
			line.StartsWith("...", StringComparison.Ordinal)
			&& !line.StartsWith("...CONT...", StringComparison.OrdinalIgnoreCase);

		// What can follow a header that has no body of its own and still leave it body-less: another header,
		// an emphasis line, or the end of the content (signature, PREV marker, link, note).
		private static bool EndsContent(string line) =>
			IsHeaderStart(line) || line.StartsWith("--", StringComparison.Ordinal)
			|| SignatureRx.IsMatch(line) || PrevRx.IsMatch(line)
			|| line.StartsWith("CLICK TO GET", StringComparison.OrdinalIgnoreCase) || NoteRx.IsMatch(line);

		// A block's lines → paragraphs. In a block that OPENS as a list ("1/ …", "- …"), every item starts a
		// paragraph of its own. ⚠️ Only then: a wrapped line can begin "2312. " (a time) mid-paragraph.
		private static IEnumerable<string> Paragraphs(List<string> lines)
		{
			var isList = ListItemRx.IsMatch(lines[0]);
			var current = new List<string>();
			foreach (var line in lines)
			{
				if (isList && current.Count > 0 && ListItemRx.IsMatch(line))
				{
					yield return Join(current);
					current.Clear();
				}
				current.Add(line);
			}
			if (current.Count > 0) yield return Join(current);
		}

		// Wrapped lines → one line. A line ending in "..." joins with NO space: SPC uses "..." as a separator
		// ("NEB...NERN KS"), and a wrap right after one would otherwise read "NEB... NERN".
		private static string Join(IEnumerable<string> lines)
		{
			var sb = new System.Text.StringBuilder();
			foreach (var line in lines)
			{
				if (sb.Length > 0 && !sb.ToString().EndsWith("...", StringComparison.Ordinal)) sb.Append(' ');
				sb.Append(line);
			}
			return Clean(sb.ToString());
		}

		private static string Clean(string s) => Spaces.Replace(s, " ").Trim();

		private static (string Code, string Words) Category(string word) => word.ToUpperInvariant() switch
		{
			"HIGH" => ("HIGH", "HIGH RISK"),
			"MDT" or "MODERATE" => ("MDT", "MODERATE RISK"),
			"ENH" or "ENHANCED" => ("ENH", "ENHANCED RISK"),
			"SLGT" or "SLIGHT" => ("SLGT", "SLIGHT RISK"),
			_ => ("MRGL", "MARGINAL RISK"),
		};

		// "0258 PM CDT Fri Mar 14 2025" → "2:58 PM CDT Fri Mar 14, 2025" (+ the date, to anchor the valid days).
		internal static (string Text, DateOnly? Date) FormatIssued(string line)
		{
			var m = IssuedRx.Match(line);
			if (!m.Success) return (line, null);
			var hour = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
			var mon = Title(m.Groups[6].Value);
			var day = int.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture);
			var year = int.Parse(m.Groups[8].Value, CultureInfo.InvariantCulture);
			var text = $"{hour}:{m.Groups[2].Value} {m.Groups[3].Value.ToUpperInvariant()} {m.Groups[4].Value.ToUpperInvariant()} "
				+ $"{Title(m.Groups[5].Value)} {mon} {day}, {year}";
			DateOnly? date = DateTime.TryParseExact($"{mon} {day} {year}", "MMM d yyyy", CultureInfo.InvariantCulture,
				DateTimeStyles.None, out var d) ? DateOnly.FromDateTime(d) : null;
			return (text, date);
		}

		// "Valid 142000Z - 151200Z" → "20Z Fri Mar 14 → 12Z Sat Mar 15". The line carries only day-of-month, so
		// each day is the first date from issued−2 to issued+8 with that day number (UTC vs local, Day 4-8's
		// reach); without an issued date the days stay bare numbers.
		internal static string? FormatValid(string? line, DateOnly? issued)
		{
			if (line is null) return null;
			var m = ValidRx.Match(line);
			if (!m.Success) return line;
			string Part(int g)
			{
				var dd = int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
				var hh = m.Groups[g + 1].Value;
				var mm = m.Groups[g + 2].Value;
				var time = mm == "00" ? $"{hh}Z" : $"{hh}{mm}Z";
				if (issued is { } i)
				{
					for (var k = -2; k <= 8; k++)
					{
						var d = i.AddDays(k);
						if (d.Day == dd) return $"{time} {d.ToString("ddd MMM d", CultureInfo.InvariantCulture)}";
					}
				}
				return $"{time} day {dd}";
			}
			return $"{Part(1)} → {Part(4)}";
		}

		private static string Title(string s) =>
			s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
	}
}
