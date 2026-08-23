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

		/// <summary>Glyph size as a fraction of the square's side.</summary>
		public const double GlyphRatio = 0.30;

		/// <summary>Drawn-icon (the pane-layout Rectangles) size as a fraction of the side. A shade larger
		/// than a glyph: the pane icons are plain rectangles with no strokes or detail to crowd, and at the
		/// glyph ratio they read as too small beside a font glyph of the same nominal size.</summary>
		public const double IconRatio = 0.36;

		/// <summary>The square's side for a cluster row of <paramref name="rowHeight"/>.</summary>
		public static double SideFor(double rowHeight) =>
			Math.Max(MinSide, rowHeight - (2 * VerticalInset));

		/// <summary>The glyph size for a key of <paramref name="side"/>.</summary>
		public static double GlyphFor(double side) => side * GlyphRatio;

		/// <summary>The drawn-icon box size for a key of <paramref name="side"/>.</summary>
		public static double IconFor(double side) => side * IconRatio;
	}
}
