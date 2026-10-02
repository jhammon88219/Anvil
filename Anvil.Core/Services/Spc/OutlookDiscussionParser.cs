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
		// ⚠️ The title can carry an AMENDMENT or CORRECTION after it ("DAY 1 CONVECTIVE OUTLOOK AMEND 1", 2011-05-24
		// 2040Z). A bare-title-only rule misfiled the whole heading into the body — so anything after "OUTLOOK" is kept.
		private static readonly Regex TitleRx = new(@"^DAY (\d(?:-\d)?) CONVECTIVE OUTLOOK\b[\s.]*(.*)$", Ci);
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
			string? productId = null, validRaw = null, amendment = null;
			SpcClock? clock = null;
			// ⚠️ The heading is every block up to and INCLUDING the one holding the VALID line (always within the
			// first few) — so a heading line no rule knows can't push the rest of the heading into the body. Only
			// without a VALID line does it fall back to "leading blocks of known heading lines".
			var validBlock = blocks.Take(6).ToList().FindIndex(b => b.Any(l => ValidRx.IsMatch(l)));
			var bi = 0;
			for (; bi < blocks.Count && (bi <= validBlock || (validBlock < 0 && blocks[bi].All(IsHeadingLine))); bi++)
			{
				foreach (var line in blocks[bi])
				{
					Match m;
					if ((m = ProductIdRx.Match(line)).Success) productId = line;
					else if ((m = TitleRx.Match(line)).Success)
					{
						title = $"Day {m.Groups[1].Value} Convective Outlook";
						amendment = Amendment(m.Groups[2].Value);
						if (amendment is not null) title = $"{title} · {amendment}";
					}
					else if (IssuedRx.IsMatch(line)) clock = SpcClock.Read(line);
					else if (ValidRx.IsMatch(line)) validRaw = line;
				}
			}

			// ── The body ──
			var headlines = new List<OutlookHeadline>();
			string? alert = null, forecaster = null, previousIssued = null, previousIssuedTime = null, next = null;
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
				if ((m = NoteRx.Match(first)).Success) { next = $"by {FormatZuluOfDay(m.Groups[1].Value, clock)}"; continue; }
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
					if (SpcClock.Read(raw1) is { } prevClock)
					{
						previousIssued = prevClock.Both;
						previousIssuedTime = prevClock.BothTimeOnly;
					}
					else
					{
						previousIssued = previousIssuedTime = raw1;
					}
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
				// Headerless text with nowhere to go. On an AMENDED issuance, the first such text before any section is
				// the amendment's own note ("AMENDED FOR INCREASED TORNADO PROBS…"), so it is that update's section.
				target ??= amendment is not null && !inPrevious && sections.Count == 0
					? Open(OutlookSectionKind.Update, amendment)
					: Open(OutlookSectionKind.Discussion, string.Empty);
				target.AddRange(Paragraphs(body));
			}

			return new OutlookDiscussion(
				title, productId, clock?.Both, FormatValid(validRaw, clock),
				headlines, alert, summary,
				sections.Select(s => s.Build()).ToArray(),
				forecaster, previousIssued, previousIssuedTime,
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

		// What follows "CONVECTIVE OUTLOOK" on the title line: "AMEND 1" → "Amendment 1", "CORRECTED" → "Correction";
		// anything else is kept as SPC wrote it; nothing → null.
		internal static string? Amendment(string suffix)
		{
			var s = Clean(suffix.Trim('.', ' '));
			if (s.Length == 0) return null;
			var m = Regex.Match(s, @"^(AMEND(?:ED|MENT)?|CORR(?:ECTED|ECTION)?)\s*(\d+)?$", Ci);
			if (!m.Success) return s;
			var word = m.Groups[1].Value.StartsWith("AMEND", StringComparison.OrdinalIgnoreCase) ? "Amendment" : "Correction";
			return m.Groups[2].Success ? $"{word} {m.Groups[2].Value}" : word;
		}

		private static (string Code, string Words) Category(string word) => word.ToUpperInvariant() switch
		{
			"HIGH" => ("HIGH", "HIGH RISK"),
			"MDT" or "MODERATE" => ("MDT", "MODERATE RISK"),
			"ENH" or "ENHANCED" => ("ENH", "ENHANCED RISK"),
			"SLGT" or "SLIGHT" => ("SLGT", "SLIGHT RISK"),
			_ => ("MRGL", "MARGINAL RISK"),
		};

		// ── Times: Zulu first, then AM/PM in SPC's own zone ──

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// The zones an SPC issued line can carry, as hours from UTC. SPC is in Norman (CDT/CST); the rest are
		// here so an odd one still converts rather than silently dropping the AM/PM half.
		private static readonly Dictionary<string, int> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
		{
			["EDT"] = -4, ["EST"] = -5, ["CDT"] = -5, ["CST"] = -6, ["MDT"] = -6, ["MST"] = -7, ["PDT"] = -7, ["PST"] = -8,
		};

		/// <summary>
		/// One issued line ("0258 PM CDT Fri Mar 14 2025") as an instant in SPC's zone. ⚠️ Every converted time in
		/// a discussion uses the ISSUED line's zone, so the window's AM/PM times agree with SPC's own text.
		/// </summary>
		internal sealed record SpcClock(DateTime Local, string Zone, int OffsetHours)
		{
			public DateTime Utc => Local.AddHours(-OffsetHours);

			/// <summary>"1958Z · 2:58 PM CDT Fri Mar 14, 2025".</summary>
			public string Both => $"{Utc.ToString("HHmm", Inv)}Z · {Local.ToString("h:mm tt", Inv)} {Zone} {Local.ToString("ddd MMM d, yyyy", Inv)}";

			/// <summary>"1630Z · 11:30 AM CDT".</summary>
			public string BothTimeOnly => $"{Utc.ToString("HHmm", Inv)}Z · {Local.ToString("h:mm tt", Inv)} {Zone}";

			/// <summary>A UTC instant as "3:00 PM CDT" in this zone.</summary>
			public string LocalTimeOf(DateTime utc) => $"{utc.AddHours(OffsetHours).ToString("h:mm tt", Inv)} {Zone}";

			public static SpcClock? Read(string line)
			{
				var m = IssuedRx.Match(line.Trim());
				if (!m.Success || !ZoneOffsets.TryGetValue(m.Groups[4].Value, out var offset)) return null;
				var hour12 = int.Parse(m.Groups[1].Value, Inv);
				var minute = int.Parse(m.Groups[2].Value, Inv);
				var hour = hour12 % 12 + (m.Groups[3].Value.Equals("PM", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
				if (!DateTime.TryParseExact($"{Title(m.Groups[6].Value)} {m.Groups[7].Value} {m.Groups[8].Value}", "MMM d yyyy",
						Inv, DateTimeStyles.None, out var day) || hour > 23 || minute > 59)
				{
					return null;
				}
				return new SpcClock(day.AddHours(hour).AddMinutes(minute), m.Groups[4].Value.ToUpperInvariant(), offset);
			}
		}

		// SPC's Zulu label: "20Z" on the hour, "1630Z" otherwise.
		private static string Zulu(int hh, int mm) => mm == 0 ? $"{hh:00}Z" : $"{hh:00}{mm:00}Z";

		// The NOTE's "0100Z" → "01Z (8:00 PM CDT)". A time of day only — the note names no date.
		internal static string FormatZuluOfDay(string token, SpcClock? clock)
		{
			var m = Regex.Match(token, @"^(\d{2})(\d{2})Z$", RegexOptions.IgnoreCase);
			if (!m.Success) return token;
			var hh = int.Parse(m.Groups[1].Value, Inv);
			var mm = int.Parse(m.Groups[2].Value, Inv);
			var zulu = Zulu(hh, mm);
			return clock is null ? zulu : $"{zulu} ({clock.LocalTimeOf(new DateTime(2000, 1, 1, hh, mm, 0))})";
		}

		// "Valid 142000Z - 151200Z" → "20Z (3:00 PM CDT) Fri Mar 14 → 12Z (7:00 AM CDT) Sat Mar 15". The date shown is
		// the UTC one; when the AM/PM time falls on the day before, its weekday rides inside the brackets
		// ("01Z (8:00 PM CDT Fri) Sat Mar 15"). The line carries only day-of-month, so each day is the first date from
		// issued−2 to issued+8 (UTC) with that number; without an issued line the days stay bare numbers.
		internal static string? FormatValid(string? line, SpcClock? clock)
		{
			if (line is null) return null;
			var m = ValidRx.Match(line);
			if (!m.Success) return line;
			string Part(int g)
			{
				var dd = int.Parse(m.Groups[g].Value, Inv);
				var hh = int.Parse(m.Groups[g + 1].Value, Inv);
				var mm = int.Parse(m.Groups[g + 2].Value, Inv);
				var zulu = Zulu(hh, mm);
				if (clock is not null)
				{
					var anchor = clock.Utc.Date;
					for (var k = -2; k <= 8; k++)
					{
						var d = anchor.AddDays(k);
						if (d.Day != dd) continue;
						var utc = d.AddHours(hh).AddMinutes(mm);
						var local = utc.AddHours(clock.OffsetHours);
						var otherDay = local.Date != utc.Date ? $" {local.ToString("ddd", Inv)}" : string.Empty;
						return $"{zulu} ({clock.LocalTimeOf(utc)}{otherDay}) {utc.ToString("ddd MMM d", Inv)}";
					}
				}
				return $"{zulu} day {dd}";
			}
			return $"{Part(1)} → {Part(4)}";
		}

		private static string Title(string s) =>
			s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
	}
}
