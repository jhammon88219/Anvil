using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Anvil.Layout
{
	/// <summary>
	/// The ONE rule for how big a key in the bottom bar is.
	/// <para>Every interactive key in the OverlayBar — the temporal keys at the centre and the three on
	/// the right (pane layout + window buttons) — is a SQUARE as tall as the bar's content row, holding
	/// a MARK OVER A NAME. That size is not a constant anywhere: the row stretches to whatever the bar's
	/// tallest content needs (quad panes add a chip row and it grows), each cluster measures the height it
	/// was given, and mirrors it onto the keys' Width.</para>
	/// <para>⚠️ TWO keys break that, both of them nameless, both deliberately:</para>
	/// <para>• the three-dot key at the end of the temporal row is <see cref="NarrowWidthFor"/> wide rather
	/// than square, so it reads as subordinate to the modes beside it (see TemporalToggles);</para>
	/// <para>• the pane-layout key is wider than the square (<see cref="WideKeyWidthFor"/>) and its MARK is
	/// wide too (<see cref="WideIconAspect"/>), because that mark is a picture of the map band.</para>
	/// <para>Both still take their HEIGHT from the same stretch as everything else, so they stay part of
	/// the row — it is only the width and the mark that differ.</para>
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

		// ===== Mark size. TWO ratios: a NAMED font glyph, and a SOLO drawn one — because a mark with a name
		// under it only gets part of the square, and one without gets all of it.
		//
		// ⚠️ The solo ratio was retired once, when naming the right cluster left nothing in the bar solo, on
		// the standing condition "re-add it only if a genuinely nameless key appears". TWO have: the
		// three-dot Timeframe key beside the temporal trio, and the pane-layout key once its "Panes" label
		// came off to make room for the count inside its mark (see the WIDE mark section below).
		//
		// ⚠️ A ratio for NAMED drawn art (0.27) used to sit here. The pane key was its only consumer and its
		// mark is no longer named or square, so it went; the reasoning it carried moved onto the solo one.

		/// <summary>Glyph size for a key carrying a NAME under it — every named glyph key in the bar — as a
		/// fraction of the side. Small because the name takes the rest of the square.</summary>
		public const double LabelledGlyphRatio = 0.30;

		/// <summary>Drawn-art extent for a key with NO name, as a fraction of the side — the mark has the
		/// whole square to itself, so it runs about half again as large as a named one.
		/// <para>⚠️ Deliberately BELOW what a solo FONT glyph would take, which looks like an inconsistency
		/// and is not: a glyph carries em-box padding, so a 28px FontSize draws maybe 24px of actual ink,
		/// while a rectangle asked for 28px draws a full 28px. Matching the numbers would make the drawn
		/// mark visibly the largest in the bar.</para></summary>
		public const double SoloDrawnIconRatio = 0.40;

		/// <summary>How wide a NAMELESS key is relative to the square the named keys occupy. Half: the mark
		/// needs no more, and the narrower key is what keeps it reading as subordinate to the row it sits
		/// beside rather than as another member of it.</summary>
		public const double NarrowWidthRatio = 0.5;

		/// <summary>The square's side for a cluster row of <paramref name="rowHeight"/>.</summary>
		public static double SideFor(double rowHeight) =>
			Math.Max(MinSide, rowHeight - (2 * VerticalInset));

		/// <summary>Glyph size for a glyph-over-name key of <paramref name="side"/>.</summary>
		public static double LabelledGlyphFor(double side) => side * LabelledGlyphRatio;

		/// <summary>Drawn-art extent for a NAMELESS key of <paramref name="side"/>.</summary>
		public static double SoloDrawnIconFor(double side) => side * SoloDrawnIconRatio;

		/// <summary>Width of a NAMELESS key whose named neighbours are <paramref name="side"/> squares.
		/// ⚠️ WIDTH only — its HEIGHT still comes from the same stretch as everything else in the row, so
		/// the key is a narrow upright, not a smaller square floating in the middle of the bar.</summary>
		public static double NarrowWidthFor(double side) => side * NarrowWidthRatio;

		// ===== The WIDE mark. One key breaks the square: the pane-layout key's mark is a literal picture
		// of the map band, which is wide, and the cells inside it are the panes. Everything about it is
		// expressed against ONE reference box (26 x 14) so the proportions here match the drawn markup.

		/// <summary>Aspect (width ÷ height) of the pane key's mark. The map band is WIDE, and the mark is a
		/// picture of it — halving that box gives the two-across and quad cells their shapes for free.</summary>
		public const double WideIconAspect = ReferenceWidth / ReferenceHeight;

		/// <summary>The pane key's mark WIDTH as a fraction of the SQUARE side its neighbours use — applied
		/// to width, not height, because the box is wide.
		/// <para>Large because that key carries NO NAME and no text of any kind: the mark gets the whole
		/// square the label used to share, and it is deliberately larger than that square would comfortably
		/// hold — which is why the KEY widens (see <see cref="WideKeyWidthFor"/>) rather than the mark
		/// shrinking to fit.</para>
		/// <para>⚠️ This number was set to make a pane-COUNT numeral legible inside the quad anchor cell.
		/// That numeral is gone — it never worked in quad (see the history note in MainWindow.xaml) — so the
		/// value is now larger than the shapes alone strictly need. Lower it freely if the mark reads too
		/// big; nothing depends on it any more.</para></summary>
		public const double WideIconRatio = 0.82;

		/// <summary>Breathing room per edge inside the pane key, as a fraction of the side — what keeps the
		/// wide mark off the key's own border. Roughly matches <see cref="NameInset"/>'s share of a square
		/// key, so the mark sits as comfortably in its key as a name does in its.</summary>
		public const double WideKeyInset = 0.13;

		/// <summary>Width of the pane key's mark for a key of <paramref name="side"/>.</summary>
		public static double WideIconWidthFor(double side) => side * WideIconRatio;

		/// <summary>Height of the pane key's mark for a key of <paramref name="side"/>.</summary>
		public static double WideIconHeightFor(double side) => WideIconWidthFor(side) / WideIconAspect;

		/// <summary>
		/// Width of the pane key ITSELF — the one key allowed to be wider than the square, because its mark
		/// is wide and a square would crop it to illegibility.
		/// </summary>
		/// <remarks>
		/// ⚠️ Never NARROWER than the square: the floor keeps it a member of the row on a bar tall enough
		/// that the mark no longer needs the extra width. It is HEIGHT that must still come from the shared
		/// stretch — widening a key is free, and pinning its height would hold the bar open (see the class
		/// note). Widening also costs the bar's centring nothing: the right cluster sits in a star column of
		/// the <c>[*, Auto, *]</c> grid, so the centred temporal keys do not move however wide it gets.
		/// </remarks>
		public static double WideKeyWidthFor(double side) =>
			Math.Max(side, WideIconWidthFor(side) + (2 * WideKeyInset * side));

		/// <summary>Gap between cells inside the pane key's mark, scaled with it so the grooves stay
		/// proportional rather than becoming hairlines on a tall bar.</summary>
		public static double WideIconGapFor(double side) =>
			WideIconHeightFor(side) * (ReferenceGap / ReferenceHeight);

		// The reference box every proportion above is quoted against. These are not pixels — they are the
		// units the drawn markup is authored in, so a shape edited there can be mirrored here by reading
		// the numbers straight off it.
		private const double ReferenceWidth = 26.0;
		private const double ReferenceHeight = 14.0;
		private const double ReferenceGap = 2.0;

		// ===== The NAME under the mark. Shared here for the same reason the square is: both clusters
		// fit names now, they sit side by side, and a name rendered a point larger on one half than the
		// other is exactly the drift this class exists to prevent.

		/// <summary>Horizontal breathing room inside the key, per edge, that the name must not run into.</summary>
		public const double NameInset = 5;

		/// <summary>Ceiling on the name's size. Without it a very tall bar would inflate the words past the
		/// point where they read as labels.</summary>
		public const double MaxNameFont = 15;

		/// <summary>Font size at which the probe measured by <see cref="ProbeWidthOf"/> just fits a key of
		/// <paramref name="side"/>, capped at <see cref="MaxNameFont"/>.</summary>
		public static double NameFontFor(double side, double probeWidth)
		{
			if (probeWidth <= 0) return MaxNameFont;
			var usable = side - (2 * NameInset);
			return Math.Min(MaxNameFont, ProbeFont * usable / probeWidth);
		}

		/// <summary>
		/// Width of the WIDEST of <paramref name="labels"/> rendered at <see cref="ProbeFont"/>. Call once
		/// and cache — the labels are fixed strings, so the answer never changes.
		/// <para>Measured rather than derived from a characters × average-width guess: the guess bakes in a
		/// constant that is wrong for a different typeface and goes stale the moment a name is renamed,
		/// whereas measuring asks the actual font. All keys in a cluster then share the ONE size that fits
		/// the widest of them — sizing each to its own text would render "Map" enormous beside
		/// "Settings".</para>
		/// </summary>
		public static double ProbeWidthOf(params TextBlock[] labels)
		{
			double widest = 0;
			foreach (var label in labels)
			{
				var restore = label.FontSize;
				label.FontSize = ProbeFont;
				label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
				widest = Math.Max(widest, label.DesiredSize.Width);
				label.FontSize = restore;
			}

			return widest;
		}

		/// <summary>Size the probe is measured at. Large, so the scale-down carries no rounding.</summary>
		private const double ProbeFont = 100;
	}
}
