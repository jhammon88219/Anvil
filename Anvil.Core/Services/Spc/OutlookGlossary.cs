using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The one-sentence hints behind the Outlook Discussion window's "?" marks — one per part
	/// (OutlookDiscussionParser). Same card shape as <see cref="RadarGlossary"/> so Primitives/GlossaryHint shows
	/// them unchanged: Term = the label, Technical = SPC's own marker for that part, Definition = the sentence.
	/// ⚠️ Plain-language wording lives HERE, never in XAML (the RadarGlossary rule).
	/// The sentences are deliberately short for now (the user's call, 2026-10-02).
	/// </summary>
	public static class OutlookGlossary
	{
		private static RadarGlossaryCard Card(string term, string technical, string definition) =>
			new(term, technical, definition, string.Empty, string.Empty);

		public static readonly RadarGlossaryCard Issued = Card("Issued", "e.g. 0258 PM CDT Fri Mar 14 2025",
			"When SPC sent this outlook, in Norman, Oklahoma local time.");

		public static readonly RadarGlossaryCard Valid = Card("Valid", "e.g. Valid 142000Z - 151200Z",
			"The period this outlook covers, in UTC (\"Z\"). Day outlooks always end at 12Z, 7 AM CDT.");

		public static readonly RadarGlossaryCard Forecaster = Card("Forecaster", "the ..Name.. signature",
			"The SPC forecaster who wrote and signed this issuance.");

		public static readonly RadarGlossaryCard NextOutlook = Card("Next outlook", "NOTE: THE NEXT … OUTLOOK IS SCHEDULED BY",
			"When SPC is scheduled to issue the next version of this outlook.");

		public static readonly RadarGlossaryCard Risk = Card("Risk", "...THERE IS A … RISK OF SEVERE THUNDERSTORMS...",
			"The outlook's severe categories and where they are. From least to most: Marginal, Slight, Enhanced, Moderate, High.");

		public static readonly RadarGlossaryCard Alert = Card("Alert", "an extra headline after the risk",
			"A rare extra headline SPC adds when a major outbreak is expected.");

		public static readonly RadarGlossaryCard Summary = Card("Summary", "...SUMMARY...",
			"The short, plain-language version: which hazards, where, and when.");

		public static readonly RadarGlossaryCard Previous = Card("Previous", ".PREV DISCUSSION... /ISSUED …/",
			"The discussion from the earlier issuance, repeated unchanged under this update.");

		private static readonly RadarGlossaryCard Update = Card("Update", "...20z Update... / ...01z Update...",
			"What the forecaster changed at this update, and why.");

		private static readonly RadarGlossaryCard Synopsis = Card("Synopsis", "...Synopsis...",
			"The big picture: the large-scale weather pattern behind the day's storms.");

		private static readonly RadarGlossaryCard Discussion = Card("Discussion", "...Discussion...",
			"The forecaster's reasoning: the pattern, the ingredients, and where storms are most likely.");

		private static readonly RadarGlossaryCard Days = Card("Days", "...Days 4-6/…...",
			"Where and why severe storms are possible on those days, further out and less certain.");

		private static readonly RadarGlossaryCard Region = Card("Region", "a ...Region... header",
			"The forecaster's reasoning for one area: ingredients, timing and expected storm types.");

		/// <summary>The card for a body section, by what it is.</summary>
		public static RadarGlossaryCard For(OutlookSectionKind kind) => kind switch
		{
			OutlookSectionKind.Update => Update,
			OutlookSectionKind.Synopsis => Synopsis,
			OutlookSectionKind.Discussion => Discussion,
			OutlookSectionKind.Days => Days,
			OutlookSectionKind.Alert => Alert,
			_ => Region,
		};

		/// <summary>The dimmed label a body section wears: "20Z UPDATE", "SYNOPSIS", "DAYS 4-6", "REGION"…</summary>
		public static string LabelFor(OutlookSection section) => section.Kind switch
		{
			OutlookSectionKind.Update when section.Title.EndsWith("update", System.StringComparison.OrdinalIgnoreCase)
				=> section.Title.ToUpperInvariant(),
			OutlookSectionKind.Update => "CHANGES",
			OutlookSectionKind.Synopsis => "SYNOPSIS",
			OutlookSectionKind.Discussion => "DISCUSSION",
			OutlookSectionKind.Days => DaysLabel(section.Title),
			OutlookSectionKind.Alert => "ALERT",
			_ => "REGION",
		};

		// "Days 4-6/Fri-Sun - Mid/Lower MS Valley…" → "DAYS 4-6"; "Day 5/Tue - …" → "DAY 5".
		private static string DaysLabel(string title)
		{
			var cut = title.IndexOfAny(new[] { '/', ' ' }, title.IndexOf(' ') + 1);
			return (cut > 0 ? title[..cut] : title).ToUpperInvariant();
		}
	}
}
