using System;

namespace Anvil.Layout
{
	/// <summary>
	/// The ONE rule for how big a key in the bottom bar is.
	/// <para>Every interactive key in the OverlayBar — the three temporal keys at the centre and the seven
	/// 32px-era keys on the right (pane layout + window buttons) — is a SQUARE as tall as the bar's content
	/// row. That size is not a constant anywhere: the row stretches to whatever the bar's tallest content
	/// needs (quad panes add a chip row and it grows), each cluster measures the height it was given, and
	/// mirrors it onto the keys' Width.</para>
	/// <para>This class exists because TWO controls do that measuring — <c>TemporalToggles</c> for the
	/// centre and <c>MainWindow</c> for the right cluster — and if each kept its own copy of the arithmetic
	/// the two halves of one bar would drift apart the first time either was tuned. The keys sit side by
	/// side; they have to agree.</para>
	/// <para>⚠️ Callers must set only WIDTH from <see cref="SideFor"/>, never Height, and let the key's
	/// height come from a vertical STRETCH. A key that demanded the height it was last given would hold the
	/// bar open at that height forever — the row feeds the key, and the key would feed the row right back.
	/// See the note in TemporalToggles.</para>
	/// </summary>
	internal static class BarKeyMetrics
	{
		/// <summary>Vertical inset of a key inside the row, per edge. 0 = the key fills the row, which is
		/// the tallest it can be without growing the bar; the bar's own 10px padding already supplies the
		/// breathing room that makes this read as "almost the height of the bar".</summary>
		public const double VerticalInset = 0;

		/// <summary>Smallest square we will draw, in case a row measures degenerate (0 during the first
		/// layout pass, or a host that forgets to stretch). Keeps keys clickable rather than vanishing.</summary>
		public const double MinSide = 28;

		// ===== Icon size. THREE ratios, because a key's mark has to share the square with whatever else
		// the key holds. One constant for all of them was wrong: it left the glyph-only keys drawing an
		// ~18px mark in a 62px square while the labelled keys were already full.

		/// <summary>Glyph size for a key that also carries a NAME under it (the temporal keys), as a
		/// fraction of the side. Small because the name takes the rest of the square.</summary>
		public const double LabelledGlyphRatio = 0.30;

		/// <summary>Glyph size for a key whose ONLY content is the glyph (the right cluster), as a fraction
		/// of the side. It gets the whole square, so it can be half again the labelled size.</summary>
		public const double SoloGlyphRatio = 0.45;

		/// <summary>Size of DRAWN art — the pane key's rectangles — as a fraction of the side.
		/// <para>⚠️ Deliberately BELOW <see cref="SoloGlyphRatio"/>, which looks like an inconsistency and
		/// is not: a font glyph carries em-box padding, so a 28px FontSize draws maybe 24px of actual ink,
		/// while a rectangle asked for 28px draws a full 28px. Matching the two numbers would make the
		/// drawn icon visibly the largest mark in the bar.</para></summary>
		public const double DrawnIconRatio = 0.40;

		/// <summary>The square's side for a cluster row of <paramref name="rowHeight"/>.</summary>
		public static double SideFor(double rowHeight) =>
			Math.Max(MinSide, rowHeight - (2 * VerticalInset));

		/// <summary>Glyph size for a glyph-over-name key of <paramref name="side"/>.</summary>
		public static double LabelledGlyphFor(double side) => side * LabelledGlyphRatio;

		/// <summary>Glyph size for a glyph-only key of <paramref name="side"/>.</summary>
		public static double SoloGlyphFor(double side) => side * SoloGlyphRatio;

		/// <summary>Drawn-art box size for a key of <paramref name="side"/>.</summary>
		public static double DrawnIconFor(double side) => side * DrawnIconRatio;
	}
}
