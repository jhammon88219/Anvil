using System.Collections.Generic;

namespace Anvil.Models
{
	/// <summary>
	/// One SPC convective outlook discussion, split into its parts (Services/Spc/OutlookDiscussionParser). Every
	/// field is optional except the lists: SPC's layout has drifted since 2004, and a part a given issuance
	/// doesn't carry is simply absent, never invented. <see cref="Raw"/> keeps the original text.
	/// </summary>
	public sealed record OutlookDiscussion(
		string Title,                                     // "Day 1 Convective Outlook"
		string? ProductId,                                // "SPC AC 141958" (the UTC day+time it was SENT)
		string? Issued,                                   // "2:58 PM CDT Fri Mar 14, 2025"
		string? Valid,                                    // "20Z Fri Mar 14 → 12Z Sat Mar 15"
		IReadOnlyList<OutlookHeadline> Headlines,         // risk headlines, as SPC ordered them (highest first)
		string? Alert,                                    // the rare emphasis headline ("--DANGEROUS TORNADO OUTBREAK…--")
		IReadOnlyList<string> Summary,                    // ...SUMMARY... paragraphs (2016 on)
		IReadOnlyList<OutlookSection> Sections,           // this issuance: the update (if any) first, then the rest
		string? Forecaster,                               // "Smith / Moore" — the issuance's own signature
		string? PreviousIssued,                           // "11:30 AM CDT Fri Mar 14, 2025" — set only on an update
		IReadOnlyList<OutlookSection> PreviousSections,   // the earlier discussion an update repeats below itself
		string? NextOutlook,                              // "by 01Z"
		string Raw)
	{
		public bool HasPrevious => PreviousSections.Count > 0;
	}

	/// <summary>A risk headline: the category (catalog code + words) and the rest of the line, verbatim.</summary>
	/// <param name="Code">Categorical catalog code (MRGL/SLGT/ENH/MDT/HIGH); empty for "no severe areas".</param>
	/// <param name="Category">"MODERATE RISK", or "NO SEVERE THUNDERSTORM AREAS FORECAST".</param>
	/// <param name="Text">Where and when, as SPC wrote it (all caps by SPC's own convention); empty for none.</param>
	public sealed record OutlookHeadline(string Code, string Category, string Text)
	{
		public bool IsNone => Code.Length == 0;
	}

	/// <summary>One titled part of the discussion body.</summary>
	/// <param name="Title">SPC's header text without the dots ("Lower Mississippi Valley"); "20Z update" for an update.</param>
	public sealed record OutlookSection(OutlookSectionKind Kind, string Title, IReadOnlyList<string> Paragraphs);

	/// <summary>What a section IS — drives its label and hint (Services/Spc/OutlookGlossary).</summary>
	public enum OutlookSectionKind
	{
		/// <summary>"...20z Update..." / "...20Z OUTLOOK UPDATE..." / a 2011-era "CHANGES TO PREVIOUS OUTLOOK".</summary>
		Update,
		/// <summary>"Synopsis", "Synopsis/Discussion", "Synoptic Setup" — the large-scale pattern.</summary>
		Synopsis,
		/// <summary>A bare "Discussion" (Day 4-8's group, or a modern Day 3 with one section), or untitled text.</summary>
		Discussion,
		/// <summary>Day 4-8's per-day block: "Days 4-6/Fri-Sun - Mid/Lower MS Valley…".</summary>
		Days,
		/// <summary>An emphasis headline met INSIDE the previous discussion (it has no headline zone of its own).</summary>
		Alert,
		/// <summary>Any other titled section — in practice a region.</summary>
		Region,
	}
}
