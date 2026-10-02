using System;
using System.Collections.Generic;
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

		/// <summary>Takes the current discussion text. A new text collapses the previous discussion again.</summary>
		public void Load(string? text)
		{
			text ??= string.Empty;
			if (text == _text) return;
			_text = text;
			_parsed = OutlookDiscussionParser.Parse(text);
			Headlines = _parsed.Headlines.Select(h => new OutlookHeadlineRow(h)).ToArray();
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
