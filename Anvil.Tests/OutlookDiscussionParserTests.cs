using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Anvil.Models;
using Anvil.Services;
using Xunit;

namespace Anvil.Tests
{
	/// <summary>
	/// <see cref="OutlookDiscussionParser"/> against REAL SPC discussions (Fixtures/spc-disc-*.txt, fetched
	/// 2026-10-02 — every Day 1 cycle, Days 2/3/4-8, quiet days to High Risks, 2004-2026). The corpus test is the
	/// one that matters: no line of SPC's text may vanish unless it is one of the parts the window drops on purpose.
	/// </summary>
	public class OutlookDiscussionParserTests
	{
		private static string Fixture(string name) =>
			File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"spc-disc-{name}.txt"));

		public static IEnumerable<object[]> Corpus() =>
			Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "spc-disc-*.txt")
				.Select(p => new object[] { Path.GetFileNameWithoutExtension(p)["spc-disc-".Length..] });

		[Theory]
		[MemberData(nameof(Corpus))]
		public void Corpus_LosesNoText(string name)
		{
			var raw = Fixture(name);
			var d = OutlookDiscussionParser.Parse(raw);
			var output = Letters(string.Join(" ", Everything(d)));

			foreach (var block in Regex.Split(raw.Replace("\r", ""), @"\n\s*\n"))
			{
				var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
				if (lines.Count == 0 || DroppedBlock(lines)) continue;
				foreach (var line in lines.Where(l => !DroppedLine(l)))
				{
					Assert.True(output.Contains(Letters(line), StringComparison.Ordinal), $"{name}: lost \"{line}\"");
				}
			}
		}

		[Theory]
		[MemberData(nameof(Corpus))]
		public void Corpus_HasHeadingParts(string name)
		{
			var d = OutlookDiscussionParser.Parse(Fixture(name));
			Assert.Matches(@"^Day \d(-\d)? Convective Outlook( · .+)?$", d.Title);
			Assert.NotNull(d.ProductId);
			Assert.NotNull(d.Issued);
			Assert.Contains(" → ", d.Valid);
			Assert.NotNull(d.Forecaster);
			Assert.NotEmpty(d.Sections);
			if (name.StartsWith("d48", StringComparison.Ordinal))
			{
				Assert.Empty(d.Headlines);   // Day 4-8 has no categorical headline…
				Assert.Null(d.NextOutlook);  // …and no NOTE line
			}
			else
			{
				Assert.NotEmpty(d.Headlines);
				Assert.StartsWith("by ", d.NextOutlook);
			}
		}

		[Fact]
		public void Update_SplitsTheIssuanceFromThePreviousDiscussion()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20250314_2000"));
			Assert.Equal("Day 1 Convective Outlook", d.Title);
			Assert.Equal("1958Z · 2:58 PM CDT Fri Mar 14, 2025", d.Issued);
			Assert.Equal("20Z (3:00 PM CDT) Fri Mar 14 → 12Z (7:00 AM CDT) Sat Mar 15", d.Valid);
			var h = Assert.Single(d.Headlines);
			Assert.Equal("MDT", h.Code);
			Assert.Equal("MODERATE RISK", h.Category);
			Assert.Equal("THE LOWER/MID MISSISSIPPI VALLEY INTO THE LOWER OHIO VALLEY", h.Text);
			Assert.Single(d.Summary);
			var update = Assert.Single(d.Sections);
			Assert.Equal(OutlookSectionKind.Update, update.Kind);
			Assert.Equal("20Z update", update.Title);
			Assert.Equal(4, update.Paragraphs.Count);
			Assert.Equal("Lyons", d.Forecaster);
			Assert.Equal("1630Z · 11:30 AM CDT Fri Mar 14, 2025", d.PreviousIssued);
			Assert.Equal("1630Z · 11:30 AM CDT", d.PreviousIssuedTime);
			Assert.Equal(new[] { OutlookSectionKind.Synopsis, OutlookSectionKind.Region, OutlookSectionKind.Region },
				d.PreviousSections.Select(s => s.Kind));
			Assert.Equal("Lower Mississippi Valley", d.PreviousSections[2].Title);
			Assert.Equal("by 01Z (8:00 PM CDT)", d.NextOutlook);
		}

		// An AMENDED issuance ("DAY 1 CONVECTIVE OUTLOOK AMEND 1"): the heading must still be the heading — this
		// once dumped title/issued/valid into the body — and the amendment's own note is its section.
		[Fact]
		public void Amended_KeepsItsHeading_AndTheNoteIsTheAmendmentSection()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20110524_2000_amend1"));
			Assert.Equal("Day 1 Convective Outlook · Amendment 1", d.Title);
			Assert.Equal("2043Z · 3:43 PM CDT Tue May 24, 2011", d.Issued);
			Assert.Equal("2040Z (3:40 PM CDT) Tue May 24 → 12Z (7:00 AM CDT) Wed May 25", d.Valid);
			Assert.Equal(new[] { "HIGH", "MDT", "SLGT" }, d.Headlines.Select(h => h.Code));
			var amendment = d.Sections[0];
			Assert.Equal(OutlookSectionKind.Update, amendment.Kind);
			Assert.Equal("Amendment 1", amendment.Title);
			Assert.Equal("AMENDED FOR INCREASED TORNADO PROBS OVER NC AND SRN VA", amendment.Paragraphs[0]);
			Assert.Equal("AMENDMENT", OutlookGlossary.LabelFor(amendment));
			Assert.DoesNotContain(Everything(d), t => t.Contains("NWS STORM PREDICTION CENTER", StringComparison.OrdinalIgnoreCase));
		}

		[Theory]
		[InlineData("AMEND 1", "Amendment 1")]
		[InlineData("...AMENDED", "Amendment")]
		[InlineData("CORRECTED", "Correction")]
		[InlineData("", null)]
		[InlineData("RESENT", "RESENT")]
		public void Amendment_Words(string suffix, string? expected) =>
			Assert.Equal(expected, OutlookDiscussionParser.Amendment(suffix));

		// 01Z on Mar 15 UTC is 8 PM Mar 14 in Norman: the AM/PM half carries its own weekday.
		[Fact]
		public void Valid_LocalTimeOnTheDayBefore_SaysWhichDay()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20250315_0100"));
			Assert.Equal("0101Z · 8:01 PM CDT Fri Mar 14, 2025", d.Issued);
			Assert.Equal("01Z (8:00 PM CDT Fri) Sat Mar 15 → 12Z (7:00 AM CDT) Sat Mar 15", d.Valid);
			Assert.Equal("by 06Z (1:00 AM CDT)", d.NextOutlook);
		}

		// Winter: SPC's issued line says CST (UTC−6), and every conversion follows it.
		[Fact]
		public void Winter_ConvertsInCst()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20240115_1300"));
			Assert.Equal("1228Z · 6:28 AM CST Mon Jan 15, 2024", d.Issued);
			Assert.Equal("13Z (7:00 AM CST) Mon Jan 15 → 12Z (6:00 AM CST) Tue Jan 16", d.Valid);
			Assert.Equal("by 1630Z (10:30 AM CST)", d.NextOutlook);
		}

		// The window's PREVIOUS row: three rows + a caption on a 20Z update; "None in this issuance" otherwise.
		[Fact]
		public void ViewModel_Previous_IsThereOnAnUpdate_AndSaysNoneOtherwise()
		{
			var vm = new Anvil.ViewModels.OutlookDiscussionViewModel();
			vm.Load(Fixture("d1_20250314_2000"));
			Assert.True(vm.HasPrevious);
			Assert.Equal(3, vm.PreviousRows.Count);
			Assert.Equal("issued 1630Z · 11:30 AM CDT · 3 sections", vm.PreviousCaption);
			Assert.False(vm.IsPreviousExpanded);

			vm.IsPreviousExpanded = true;
			vm.Load(Fixture("d1_20240506_1630")); // a full issuance: no previous, and the box collapses again
			Assert.False(vm.HasPrevious);
			Assert.Equal("None in this issuance", vm.PreviousCaption);
			Assert.False(vm.IsPreviousExpanded);
		}

		// SOURCE: the page the text came from, and whether it is PastCast's archive copy or the live page.
		[Fact]
		public void ViewModel_Source_SaysWhereTheTextCameFrom()
		{
			const string archive = "https://www.spc.noaa.gov/products/outlook/archive/2011/day1otlk_20110524_2000.html";
			var vm = new Anvil.ViewModels.OutlookDiscussionViewModel();
			vm.Load(Fixture("d1_20110524_2000_amend1"), archive, isArchive: true);
			Assert.True(vm.HasSource);
			Assert.Equal(archive, vm.SourceUrl);
			Assert.Equal("· spc.noaa.gov · archive copy", vm.SourceNote);

			vm.Load(Fixture("d1_20110524_2000_amend1"), "https://www.spc.noaa.gov/products/outlook/day1otlk.html", isArchive: false);
			Assert.Equal("· spc.noaa.gov · latest issuance", vm.SourceNote); // same text, new source: still updates

			vm.Load("Loading forecast discussion…", archive, isArchive: true);
			Assert.False(vm.HasSource); // a status line never shows a link
		}

		[Fact]
		public void ViewModel_StatusText_IsAMessage_NotParts()
		{
			var vm = new Anvil.ViewModels.OutlookDiscussionViewModel();
			vm.Load("Loading forecast discussion…");
			Assert.True(vm.IsMessage);
			Assert.Equal("Loading forecast discussion…", vm.Message);
		}

		[Fact]
		public void Legacy2004_DropsThePointsAndNotice_KeepsFourHeadlinesAndTheAlert()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20040524_1300"));
			Assert.Equal(new[] { "HIGH", "MDT", "SLGT", "SLGT" }, d.Headlines.Select(x => x.Code));
			Assert.StartsWith("OUTBREAK OF SEVERE THUNDERSTORMS INCLUDING STRONG TORNADOES...VERY LARGE HAIL", d.Alert);
			Assert.Equal(4, d.Sections.Count);
			Assert.All(d.Sections, s => Assert.Equal(OutlookSectionKind.Region, s.Kind));
			// Station ids from the points paragraph ("25 NNW OMA 20 WNW MLI …") must not leak in anywhere.
			Assert.DoesNotContain(Everything(d), t => t.Contains("NNW OMA", StringComparison.Ordinal));
			Assert.DoesNotContain(Everything(d), t => t.Contains("DECODING THE POINTS", StringComparison.Ordinal));
			Assert.Equal("PETERS / GUYER", d.Forecaster);
		}

		[Fact]
		public void Dashed_EmphasisLine_IsTheAlert()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20110427_1300"));
			Assert.Equal("DANGEROUS TORNADO OUTBREAK IS EXPECTED FROM LATE MORNING INTO LATE EVENING ACROSS THE TN VALLEY REGION", d.Alert);
			Assert.Equal(OutlookSectionKind.Synopsis, d.Sections[0].Kind);
		}

		// 2011's 20Z: the emphasis line sits AFTER the update header, and the update's text follows it headerless.
		[Fact]
		public void Update2011_AlertAfterTheUpdateHeader_TextStaysInTheUpdate()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20110427_2000"));
			Assert.StartsWith("A DANGEROUS OUTBREAK OF SEVERE THUNDERSTORM AND TORNADOES APPEARS UNDERWAY", d.Alert);
			var update = Assert.Single(d.Sections);
			Assert.Equal("20Z update", update.Title);
			Assert.StartsWith("REMNANTS OF THE MORNING", update.Paragraphs[0]);
			Assert.Equal("KERR", d.Forecaster);
			Assert.Equal(OutlookSectionKind.Alert, d.PreviousSections[0].Kind);
		}

		[Fact]
		public void Changes2011_IsAnUpdateWithOneParagraphPerItem()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20110428_0100"));
			var changes = Assert.Single(d.Sections);
			Assert.Equal(OutlookSectionKind.Update, changes.Kind);
			Assert.Equal("CHANGES TO PREVIOUS OUTLOOK INCLUDE", changes.Title);
			Assert.Equal("1/ EXTEND HIGH RISK TO INCLUDE MORE OF NWRN GA", changes.Paragraphs[0]);
			Assert.Equal(7, changes.Paragraphs.Count);
		}

		// A wrapped line that happens to start "2312. " is NOT a list item.
		[Fact]
		public void NumberAtALineStart_DoesNotSplitAParagraph()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20260920_0100"));
			Assert.DoesNotContain(d.Sections.SelectMany(s => s.Paragraphs), p => p.StartsWith("2312", StringComparison.Ordinal));
		}

		[Fact]
		public void Quiet_Day_IsTheNoSevereHeadline()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20261002_1300"));
			var h = Assert.Single(d.Headlines);
			Assert.True(h.IsNone);
			Assert.Null(d.Alert);
			Assert.False(d.HasPrevious);
		}

		[Fact]
		public void Day48_DaysSection_UnderTheDiscussionWrapper()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d48_20250311"));
			var days = Assert.Single(d.Sections);
			Assert.Equal(OutlookSectionKind.Days, days.Kind);
			Assert.Equal("Days 4-6/Fri-Sun - Mid/Lower MS Valley to the Southeast and Mid-Atlantic", days.Title);
			Assert.Equal("12Z (7:00 AM CDT) Fri Mar 14 → 12Z (7:00 AM CDT) Wed Mar 19", d.Valid);
		}

		[Fact]
		public void Wrap_AfterAnEllipsis_JoinsWithoutASpace()
		{
			var d = OutlookDiscussionParser.Parse(Fixture("d1_20110427_2000"));
			Assert.Contains("THE WESTERN CAROLINAS...TENNESSEE...KENTUCKY...SOUTHEASTERN INDIANA", d.Headlines[1].Text);
		}

		[Fact]
		public void Empty_ParsesToNothing()
		{
			var d = OutlookDiscussionParser.Parse(null);
			Assert.Empty(d.Headlines);
			Assert.Empty(d.Sections);
			Assert.Null(d.Issued);
		}

		// ── Helpers ──

		private static IEnumerable<string> Everything(OutlookDiscussion d)
		{
			foreach (var h in d.Headlines) { yield return h.Category; yield return h.Text; }
			if (d.Alert is not null) yield return d.Alert;
			foreach (var p in d.Summary) yield return p;
			foreach (var s in d.Sections.Concat(d.PreviousSections))
			{
				yield return s.Title;
				foreach (var p in s.Paragraphs) yield return p;
			}
		}

		private static string Letters(string s)
		{
			var sb = new StringBuilder(s.Length);
			foreach (var c in s) { if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c)); }
			return sb.ToString();
		}

		// The parts the window drops on purpose, recognised independently of the parser's own rules.
		private static bool DroppedBlock(List<string> lines)
		{
			var joined = string.Join(" ", lines);
			return joined.Contains("TO THE RIGHT OF A LINE FROM", StringComparison.OrdinalIgnoreCase) // legacy points
				|| lines[0].Equals("...NOTICE...", StringComparison.OrdinalIgnoreCase)
				|| Regex.IsMatch(lines[0], @"^\.\.\.(THERE IS A .* RISK OF|NO SEVERE THUNDERSTORM)", RegexOptions.IgnoreCase); // headline: kept, but reworded
		}

		private static bool DroppedLine(string line) =>
			line.StartsWith("...", StringComparison.Ordinal) && line.EndsWith("...", StringComparison.Ordinal) // a one-line header (its title is checked by the structure tests)
			|| line.StartsWith("---", StringComparison.Ordinal)
			|| Regex.IsMatch(line, @"^(ZCZC|ACUS48|SPC AC |CLICK TO GET|NOTE: |NWS STORM PREDICTION CENTER|VALID |\.PREV)", RegexOptions.IgnoreCase)
			|| Regex.IsMatch(line, @"^DAY \S+ CONVECTIVE OUTLOOK\b", RegexOptions.IgnoreCase)
			|| Regex.IsMatch(line, @"^\d{3,4} (AM|PM) ", RegexOptions.IgnoreCase)
			|| Regex.IsMatch(line, @"^\.\.[^.].*\.\. \d{2}/\d{2}/\d{4}$");
	}
}
