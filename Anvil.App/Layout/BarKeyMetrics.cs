using System;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Anvil.Layout
{
	/// <summary>
	/// The ONE rule for how big a key in the bottom bar is.
	/// <para>Every interactive key in the OverlayBar — the three temporal keys at the centre and the five
	/// on the right (pane layout + window buttons) — is a SQUARE as tall as the bar's content row, holding
	/// a MARK OVER A NAME. That size is not a constant anywhere: the row stretches to whatever the bar's
	/// tallest content needs (quad panes add a chip row and it grows), each cluster measures the height it
	/// was given, and mirrors it onto the keys' Width.</para>
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

		// ===== Mark size. THREE ratios: a named font glyph, a named drawn icon, and a solo glyph — because
		// a mark that has a name under it only gets part of the square, and one that doesn't gets all of it.
		//
		// ⚠️ SoloGlyphRatio was retired once, when naming the right cluster left nothing in the bar solo, on
		// the standing condition "re-add it only if a genuinely nameless key appears". One has: the Timeframe
		// key beside the temporal trio, which is an ellipsis and nothing else (see TemporalToggles).

		/// <summary>Glyph size for a key carrying a NAME under it — every named glyph key in the bar — as a
		/// fraction of the side. Small because the name takes the rest of the square.</summary>
		public const double LabelledGlyphRatio = 0.30;

		/// <summary>Glyph size for a key with NO name, as a fraction of the side. Half again the labelled
		/// ratio, because the mark has the whole square to itself — matching the labelled size would leave a
		/// nameless key looking like a named one whose word failed to load.</summary>
		public const double SoloGlyphRatio = 0.45;

		/// <summary>Size of DRAWN art — the pane key's rectangles — as a fraction of the side.
		/// <para>⚠️ Deliberately BELOW <see cref="LabelledGlyphRatio"/>, which looks like an inconsistency
		/// and is not: a font glyph carries em-box padding, so a 28px FontSize draws maybe 24px of actual
		/// ink, while a rectangle asked for 28px draws a full 28px. Matching the two numbers would make the
		/// drawn icon visibly the largest mark in the bar.</para></summary>
		public const double DrawnIconRatio = 0.27;

		/// <summary>The square's side for a cluster row of <paramref name="rowHeight"/>.</summary>
		public static double SideFor(double rowHeight) =>
			Math.Max(MinSide, rowHeight - (2 * VerticalInset));

		/// <summary>Glyph size for a glyph-over-name key of <paramref name="side"/>.</summary>
		public static double LabelledGlyphFor(double side) => side * LabelledGlyphRatio;

		/// <summary>Glyph size for a NAMELESS key of <paramref name="side"/>.</summary>
		public static double SoloGlyphFor(double side) => side * SoloGlyphRatio;

		/// <summary>Drawn-art box size for a key of <paramref name="side"/>.</summary>
		public static double DrawnIconFor(double side) => side * DrawnIconRatio;

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
