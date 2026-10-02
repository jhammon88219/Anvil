using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The Outlook Discussion window's content: SPC's discussion text, parsed (OutlookDiscussionParser) and laid
	/// out as labelled rows, each label with its one-sentence hint (OutlookGlossary). Fed by
	/// <see cref="MapViewModel"/> with whichever discussion the running outlook mode shows (live or PastCast).
	/// </summary>
	/// <remarks>
	/// ⚠️ The text it is handed is sometimes a STATUS line, not a discussion ("Loading forecast discussion…", "pick
	/// Latest to read it"). Anything without an SPC product id is shown as <see cref="Message"/> instead of parts.
	/// </remarks>
	public sealed class OutlookDiscussionViewModel : ObservableObject
	{
		private string _text = string.Empty;
		private OutlookDiscussion _parsed = OutlookDiscussionParser.Parse(string.Empty);
		private bool _isPreviousExpanded;

		private string? _sourceUrl;
		private bool _isArchive;

		/// <summary>Takes the current discussion text and the SPC page it came from (<paramref name="isArchive"/> =
		/// PastCast's per-issuance archive copy). A new text collapses the previous discussion again.</summary>
		public void Load(string? text, string? sourceUrl = null, bool isArchive = false,
			IReadOnlyList<OutlookRiskArea>? riskAreas = null)
		{
			text ??= string.Empty;
			riskAreas ??= Array.Empty<OutlookRiskArea>();
			if (text == _text && sourceUrl == _sourceUrl && isArchive == _isArchive && ReferenceEquals(riskAreas, _riskAreas)) return;
			_sourceUrl = sourceUrl;
			_isArchive = isArchive;
			_riskAreas = riskAreas;
			if (text == _text) { BuildCards(); OnPropertyChanged(string.Empty); return; } // only the source/table moved
			_text = text;
			_parsed = OutlookDiscussionParser.Parse(text);
			Headlines = _parsed.Headlines.Select(h => new OutlookHeadlineRow(h)).ToArray();
			BuildCards();
			Rows = _parsed.Sections.Select(s => new OutlookSectionRow(s)).ToArray();
			PreviousRows = _parsed.PreviousSections.Select(s => new OutlookSectionRow(s)).ToArray();
			_isPreviousExpanded = false;
			OnPropertyChanged(string.Empty); // every property below derives from the one parse
		}

		// ── The parts ──

		public bool IsDiscussion => _parsed.ProductId is not null;
		public bool IsMessage => !IsDiscussion;
		/// <summary>The status line shown in place of the parts (empty text → nothing to say).</summary>
		public string Message => IsDiscussion ? string.Empty : _text.Trim();

		public string Title => IsDiscussion ? _parsed.Title : "Outlook discussion";
		public string Issued => _parsed.Issued ?? string.Empty;
		public string Valid => _parsed.Valid ?? string.Empty;
		public string Forecaster => _parsed.Forecaster ?? string.Empty;
		public string NextOutlook => _parsed.NextOutlook ?? string.Empty;
		public bool HasForecaster => _parsed.Forecaster is not null;
		public bool HasNextOutlook => _parsed.NextOutlook is not null;

		public IReadOnlyList<OutlookHeadlineRow> Headlines { get; private set; } = Array.Empty<OutlookHeadlineRow>();
		public bool HasHeadlines => Headlines.Count > 0;

		// ── RISK CARDS (option B, the user's call 2026-10-02): ALL FIVE severe levels, always, High on the LEFT →
		// Marginal on the right; a level not in this outlook is dimmed. Numbers come from SPC's risk-area table on
		// the same page (SpcRiskTableParser); without a table (older archive pages, Day 4-8) presence comes from the
		// headlines and the cards carry no numbers. General thunder is left out (SPC publishes no figures for it).

		private IReadOnlyList<OutlookRiskArea> _riskAreas = Array.Empty<OutlookRiskArea>();
		private static readonly string[] CardOrder = { "HIGH", "MDT", "ENH", "SLGT", "MRGL" };

		public OutlookRiskCard HighCard { get; private set; } = OutlookRiskCard.Absent("HIGH");
		public OutlookRiskCard ModerateCard { get; private set; } = OutlookRiskCard.Absent("MDT");
		public OutlookRiskCard EnhancedCard { get; private set; } = OutlookRiskCard.Absent("ENH");
		public OutlookRiskCard SlightCard { get; private set; } = OutlookRiskCard.Absent("SLGT");
		public OutlookRiskCard MarginalCard { get; private set; } = OutlookRiskCard.Absent("MRGL");

		/// <summary>The cards row shows on every Day 1-3 discussion (headlines or a table); not on Day 4-8.</summary>
		public bool HasRiskCards => IsDiscussion && (HasHeadlines || _riskAreas.Count > 0);

		private void BuildCards()
		{
			var cards = CardOrder.Select(code =>
			{
				if (_riskAreas.Count > 0)
				{
					var area = _riskAreas.FirstOrDefault(a => a.Code == code);
					return area is null ? OutlookRiskCard.Absent(code) : OutlookRiskCard.WithFigures(area);
				}
				return _parsed.Headlines.Any(h => h.Code == code) ? OutlookRiskCard.NoFigures(code) : OutlookRiskCard.Absent(code);
			}).ToArray();
			(HighCard, ModerateCard, EnhancedCard, SlightCard, MarginalCard) = (cards[0], cards[1], cards[2], cards[3], cards[4]);
		}

		public string Alert => _parsed.Alert ?? string.Empty;
		public bool HasAlert => _parsed.Alert is not null;

		/// <summary>The summary's paragraphs as one text, a blank line between.</summary>
		public string Summary => string.Join("\n\n", _parsed.Summary);
		public bool HasSummary => _parsed.Summary.Count > 0;

		public IReadOnlyList<OutlookSectionRow> Rows { get; private set; } = Array.Empty<OutlookSectionRow>();

		public IReadOnlyList<OutlookSectionRow> PreviousRows { get; private set; } = Array.Empty<OutlookSectionRow>();
		public bool HasPrevious => PreviousRows.Count > 0;

		/// <summary>"issued 1630Z · 11:30 AM CDT · 3 sections" — the box's right-hand caption.
		/// ⚠️ The PREVIOUS row is ALWAYS shown (the user's call, 2026-10-02): SPC repeats the earlier discussion only
		/// under an UPDATE (Day 1's 20Z; some older updates), so a full issuance says so instead of hiding the row.</summary>
		public string PreviousCaption
		{
			get
			{
				if (!HasPrevious) return "None in this issuance";
				var n = PreviousRows.Count;
				var when = _parsed.PreviousIssuedTime is { } p ? $"issued {p} · " : string.Empty;
				return $"{when}{n} section{(n == 1 ? string.Empty : "s")}";
			}
		}

		/// <summary>The previous discussion's box: COLLAPSED by default (the user's call, 2026-10-02).</summary>
		public bool IsPreviousExpanded
		{
			get => _isPreviousExpanded;
			set => SetProperty(ref _isPreviousExpanded, value);
		}

		// ── SOURCE: the SPC page this text came from (the band's last row; option B, the user's call 2026-10-02) ──

		/// <summary>The page's full address — the link target and its tooltip.</summary>
		public string SourceUrl => _sourceUrl ?? string.Empty;
		public bool HasSource => IsDiscussion && !string.IsNullOrEmpty(_sourceUrl);

		/// <summary>"· spc.noaa.gov · archive copy" / "· spc.noaa.gov · latest issuance" — dimmed after the link.</summary>
		public string SourceNote
		{
			get
			{
				if (!HasSource) return string.Empty;
				var host = Uri.TryCreate(_sourceUrl, UriKind.Absolute, out var u) ? u.Host.Replace("www.", string.Empty) : "spc.noaa.gov";
				return $"· {host} · {(_isArchive ? "archive copy" : "latest issuance")}";
			}
		}

		public RadarGlossaryCard SourceHint => OutlookGlossary.Source;

		/// <summary>SPC's original text. ⚠️ Kept but NOT shown (the user's call, 2026-10-02: "keep but hide").</summary>
		public string Raw => _text;

		// ── The hints ──

		public RadarGlossaryCard IssuedHint => OutlookGlossary.Issued;
		public RadarGlossaryCard ValidHint => OutlookGlossary.Valid;
		public RadarGlossaryCard ForecasterHint => OutlookGlossary.Forecaster;
		public RadarGlossaryCard NextOutlookHint => OutlookGlossary.NextOutlook;
		public RadarGlossaryCard RiskHint => OutlookGlossary.Risk;
		public RadarGlossaryCard AlertHint => OutlookGlossary.Alert;
		public RadarGlossaryCard SummaryHint => OutlookGlossary.Summary;
		public RadarGlossaryCard PreviousHint => OutlookGlossary.Previous;
	}

	/// <summary>A risk headline as the window draws it: a bar in the category's SPC colour, the category, the place.</summary>
	public sealed class OutlookHeadlineRow
	{
		// ⚠️ DATA colour (the SPC categorical catalog), never themed. "No severe areas" has no category: a neutral grey.
		private const string NoneFill = "#FF8A8A8A";

		public OutlookHeadlineRow(OutlookHeadline headline)
		{
			Category = headline.Category;
			Text = headline.Text;
			Fill = headline.IsNone
				? NoneFill
				: SpcRiskCatalog.Level(SpcOutlookType.Categorical, headline.Code)?.Fill ?? NoneFill;
		}

		public string Category { get; }
		public string Text { get; }
		public bool HasText => Text.Length > 0;
		public string Fill { get; }

		/// <summary>"HIGH RISK FROM CENTRAL AND NORTHERN OKLAHOMA…" — the one line under the cards.</summary>
		public string Line => HasText ? $"{Category} {Text}" : Category;
	}

	/// <summary>
	/// One risk card: the category's SPC colour strip, its name, and — when SPC's table has the row — the people in
	/// it ("1.9M people"), its area and its largest places, one per line. Absent = dimmed, "Not in this outlook".
	/// </summary>
	public sealed class OutlookRiskCard
	{
		private OutlookRiskCard(string code, bool isPresent, OutlookRiskArea? area)
		{
			var level = SpcRiskCatalog.Level(SpcOutlookType.Categorical, code);
			Name = code switch { "MDT" => "MODERATE", "ENH" => "ENHANCED", "SLGT" => "SLIGHT", "MRGL" => "MARGINAL", _ => code };
			Fill = level?.Fill ?? "#FF8A8A8A"; // ⚠️ DATA colour, never themed
			IsPresent = isPresent;
			HasFigures = area is not null;
			People = area is null ? string.Empty : Compact(area.Population);
			Area = area is null ? string.Empty : $"{area.AreaSqMi.ToString("N0", CultureInfo.InvariantCulture)} sq mi";
			Places = area?.Places ?? Array.Empty<string>();
		}

		public static OutlookRiskCard WithFigures(OutlookRiskArea area) => new(area.Code, true, area);
		public static OutlookRiskCard NoFigures(string code) => new(code, true, null);
		public static OutlookRiskCard Absent(string code) => new(code, false, null);

		public string Name { get; }
		public string Fill { get; }
		public bool IsPresent { get; }
		public bool IsAbsent => !IsPresent;
		public bool HasFigures { get; }
		/// <summary>In the outlook, but this page had no table (older archive pages).</summary>
		public bool IsPresentWithoutFigures => IsPresent && !HasFigures;
		public string People { get; }
		public string Area { get; }
		public IReadOnlyList<string> Places { get; }

		// 1,896,303 → "1.9M"; 31,343,171 → "31.3M"; 280,571 → "281K"; under a thousand as is.
		internal static string Compact(long n) => n switch
		{
			>= 1_000_000 => $"{(n / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)}M",
			>= 1_000 => $"{(n / 1_000.0).ToString("0", CultureInfo.InvariantCulture)}K",
			_ => n.ToString(CultureInfo.InvariantCulture),
		};
	}

	/// <summary>A body section as the window draws it: dimmed label + hint, an optional heading, the paragraphs.</summary>
	public sealed class OutlookSectionRow
	{
		public OutlookSectionRow(OutlookSection section)
		{
			Label = OutlookGlossary.LabelFor(section);
			Hint = OutlookGlossary.For(section.Kind);
			// The heading is the section's own title — shown only when it says more than the label does
			// ("Lower Mississippi Valley" under REGION; not "Synopsis" under SYNOPSIS, not "20Z update").
			Heading = section.Kind == OutlookSectionKind.Update && Label != "CHANGES"
				|| string.Equals(section.Title, Label, StringComparison.OrdinalIgnoreCase)
					? string.Empty
					: section.Title;
			Paragraphs = section.Paragraphs;
		}

		public string Label { get; }
		public RadarGlossaryCard Hint { get; }
		public string Heading { get; }
		public bool HasHeading => Heading.Length > 0;
		public IReadOnlyList<string> Paragraphs { get; }
	}
}
