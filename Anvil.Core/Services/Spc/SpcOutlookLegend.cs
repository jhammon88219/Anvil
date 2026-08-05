using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// The canonical, FULL legend for each SPC outlook product family — every risk / probability level SPC
	/// defines for that product, shown whether or not today's issuance actually contains it. Ordered
	/// least-severe FIRST (top) to most-severe LAST (bottom), matching how a reader scans an escalating
	/// scale (General Thunderstorms light-green → High magenta).
	///
	/// Colors are SPC's own published legend colors:
	///   • Categorical fills are the exact values from SPC's outlook GeoJSON (verified against the live
	///     feed — e.g. TSTM #C1E9C1, MRGL #66A366 — so the swatches match the map fills).
	///   • Probabilistic levels use SPC's published probability palette (the bold swatch colors SPC shows
	///     in its own legend: 2% green → 60% blue). SPC draws a translucent pastel of these as the map
	///     fill; the legend, like SPC's, shows the bold reference color.
	///   • "Significant severe" is a hatch overlay on the map (not a solid), so it's flagged
	///     <see cref="OutlookLegendEntry.IsSignificant"/> and the UI draws it as a hatch pattern.
	///
	/// This is a static reference table (SPC's scale is stable), which is why the full legend can show even
	/// for a product with no areas issued today. Kept alongside <see cref="SpcOutlookColors"/> (the IEM
	/// past-outlook colorizer), the other home of SPC color knowledge.
	/// </summary>
	public static class SpcOutlookLegend
	{
		// ── SPC categorical (least → most severe). Fills verified against SPC's live GeoJSON. ──
		private static readonly OutlookLegendEntry[] Categorical =
		{
			new("General Thunderstorms", "#C1E9C1", "#55A555", false),
			new("Marginal",              "#66A366", "#3C7A3C", false),
			new("Slight",                "#FFE066", "#D6B000", false),
			new("Enhanced",              "#FFA366", "#E07711", false),
			new("Moderate",              "#E6635C", "#C0392B", false),
			new("High",                  "#EE99EE", "#CC33CC", false),
		};

		// ── SPC probability palette (percent → SPC's published legend color). Shared by tornado / wind /
		//    hail / combined; each product exposes only the subset of percents SPC defines for it. ──
		private static readonly Dictionary<double, (string Fill, string Stroke)> Prob = new()
		{
			[0.02] = ("#008B00", "#005200"),
			[0.05] = ("#8B4726", "#5E2F19"),
			[0.10] = ("#FFC800", "#D6A700"),
			[0.15] = ("#FF0000", "#CC0000"),
			[0.30] = ("#FF00FF", "#CC00CC"),
			[0.45] = ("#912CEE", "#6E1FB5"),
			[0.60] = ("#104E8B", "#0B385F"),
		};

		// The "significant severe" hatch row (10%+ significant), appended to a probabilistic product's scale.
		private static readonly OutlookLegendEntry Significant =
			new("Significant (hatched)", "#808080", "#000000", true);

		// ── SPC fire weather (least → most severe, then the two dry-thunderstorm areas). ──
		private static readonly OutlookLegendEntry[] Fire =
		{
			new("Elevated",                   "#FFCC33", "#D6A017", false),
			new("Critical",                   "#FF6600", "#CC4E00", false),
			new("Extremely Critical",         "#FF00FF", "#CC00CC", false),
			new("Isolated Dry Thunderstorm",  "#CC9966", "#A5794A", false),
			new("Scattered Dry Thunderstorm", "#996633", "#73491F", false),
		};

		/// <summary>The full legend for a product type, least-severe first. Empty for a type with no
		/// defined scale (shouldn't happen for the wired products).</summary>
		public static IReadOnlyList<OutlookLegendEntry> For(SpcOutlookType type) => type switch
		{
			SpcOutlookType.Categorical => Categorical,

			// Tornado runs the full percent scale; wind & hail (and the Day 2-3 combined "any severe")
			// start at 5% and omit the 2%/10% tornado-only steps. All end with the significant hatch.
			SpcOutlookType.Tornado => ProbScale(0.02, 0.05, 0.10, 0.15, 0.30, 0.45, 0.60),
			SpcOutlookType.Wind or SpcOutlookType.Hail or SpcOutlookType.ProbabilisticCombined
				=> ProbScale(0.05, 0.15, 0.30, 0.45, 0.60),

			// Days 4-8 probabilistic severe: SPC issues only 15% / 30% (no significant layer).
			SpcOutlookType.ExtendedProbabilistic => ProbScale(withSignificant: false, 0.15, 0.30),

			SpcOutlookType.FireWeather or SpcOutlookType.ExtendedFireWeather => Fire,

			_ => Array.Empty<OutlookLegendEntry>(),
		};

		private static OutlookLegendEntry[] ProbScale(params double[] percents) => ProbScale(true, percents);

		private static OutlookLegendEntry[] ProbScale(bool withSignificant, params double[] percents)
		{
			var rows = percents.Select(p =>
			{
				var (fill, stroke) = Prob[p];
				return new OutlookLegendEntry($"{p * 100:0.#}%", fill, stroke, false);
			});
			return (withSignificant ? rows.Append(Significant) : rows).ToArray();
		}
	}
}
