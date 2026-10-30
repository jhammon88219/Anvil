using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	// ===== EDGE-HATCH (experimental bolt-on, the user's call 2026-10-30 — grep EDGE-HATCH to find every piece) =====
	// A cross-hatch band where the chrome meets the map — above the bottom tiers and along an open temporal pane's map
	// side — whose lines thin out and/or fade toward the map, in place of the black cast shadow. Settings → Edges.

	/// <summary>The hatch's look, as persisted tokens (<c>AppSettings.EdgeHatchStyle</c>). Shadow = the old black cast shadow.</summary>
	public static class EdgeHatchStyles
	{
		public const string Thin = "thin";
		public const string Fade = "fade";
		public const string Both = "both";
		public const string Shadow = "shadow";

		public static readonly IReadOnlyList<string> Tokens = new[] { Thin, Fade, Both, Shadow };
		public static readonly IReadOnlyList<string> Labels = new[] { "Thinning", "Fading", "Both", "Black shadow" };

		/// <summary>A known token, else <see cref="Both"/> (the default).</summary>
		public static string Normalize(string? token) => token is not null && Tokens.Contains(token) ? token : Both;

		public static int IndexOf(string? token) => Tokens.ToList().IndexOf(Normalize(token));

		public static string At(int index) => Tokens[Math.Clamp(index, 0, Tokens.Count - 1)];

		/// <summary>A hatch draws (anything but the black shadow).</summary>
		public static bool IsHatch(string? token) => Normalize(token) != Shadow;

		/// <summary>Lines drop out with distance (Thinning or Both).</summary>
		public static bool Thins(string? token) => Normalize(token) is Thin or Both;

		/// <summary>Lines fade with distance (Fading or Both).</summary>
		public static bool Fades(string? token) => Normalize(token) is Fade or Both;
	}

	/// <summary>
	/// The hatch's line colour (<c>AppSettings.EdgeHatchColor</c>): a <c>#RRGGBB</c>, or empty = the SURFACE the band hangs
	/// on (the tier, or the pane — the chrome dissolving into the map, themed). Presets + any custom colour (the swatch
	/// picker's "+" chip), the scope-colour pattern (<see cref="ScopeColors"/>).
	/// </summary>
	/// <remarks>
	/// The surface colour is nearly black in the dark theme, so over a dark basemap it only shows where something bright
	/// is under it (the user's note, 2026-10-30) — hence the presets lean light / vivid. The old tokens ("surface",
	/// "black", "white" = 35% white) migrate on read; the 35% now lives in the line-opacity slider.
	/// </remarks>
	public static class EdgeHatchColors
	{
		/// <summary>The surface the band hangs on — the default, and what anything unrecognized falls back to.</summary>
		public const string Surface = "";

		/// <summary>The presets in picker order: hex (or empty) and a name for the tooltip.</summary>
		public static IReadOnlyList<(string Hex, string Name)> Presets { get; } = new[]
		{
			(Surface, "Bar surface (follows the theme)"),
			("#9CA3AF", "Grey"),
			("#FFFFFF", "White"),
			("#22D3EE", "Cyan"),
			("#FFD60A", "Yellow"),
			("#FF9F1A", "Orange"),
			("#FF4FD8", "Magenta"),
			("#000000", "Black"),
		};

		/// <summary>An upper-case <c>#RRGGBB</c>, or <see cref="Surface"/> for empty / unrecognized. Migrates the retired tokens.</summary>
		public static string Normalize(string? token) => token switch
		{
			"surface" => Surface,
			"black" => "#000000",
			"white" => "#FFFFFF",
			_ => ScopeColors.Normalize(token),
		};

		/// <summary>The preset index of a colour; -1 for a valid hex that isn't a preset (a custom colour).</summary>
		public static int IndexOf(string? token)
		{
			var hex = Normalize(token);
			for (int i = 0; i < Presets.Count; i++)
			{
				if (Presets[i].Hex == hex) { return i; }
			}
			return -1;
		}

		public static string FromIndex(int index) => Presets[Math.Clamp(index, 0, Presets.Count - 1)].Hex;

		/// <summary>The retired "white" token meant white at this line opacity, percent.</summary>
		internal const double RetiredWhiteOpacity = 35;
	}

	/// <summary>The hatch's numbers: defaults (the user's picks from the mock-up, 2026-10-30) and ranges.</summary>
	public static class EdgeHatchSizes
	{
		public const double DefaultWidth = 35, MinWidth = 16, MaxWidth = 72;
		public const double DefaultSpacing = 5, MinSpacing = 3, MaxSpacing = 12;
		public const double DefaultWeight = 2, MinWeight = 0.5, MaxWeight = 2;
		public const double DefaultOpacity = 100, MinOpacity = 10, MaxOpacity = 100;

		/// <summary>The lines' opacity, percent (whole steps).</summary>
		public static double Opacity(double v) => double.IsFinite(v) ? Math.Clamp(Math.Round(v), MinOpacity, MaxOpacity) : DefaultOpacity;

		public static double Width(double v) => double.IsFinite(v) ? Math.Clamp(Math.Round(v), MinWidth, MaxWidth) : DefaultWidth;

		public static double Spacing(double v) => double.IsFinite(v) ? Math.Clamp(Math.Round(v), MinSpacing, MaxSpacing) : DefaultSpacing;

		/// <summary>Half-pixel steps.</summary>
		public static double Weight(double v) => double.IsFinite(v) ? Math.Clamp(Math.Round(v * 2) / 2, MinWeight, MaxWeight) : DefaultWeight;
	}

	/// <summary>Which way a hatch band runs from the edge it hangs on.</summary>
	public enum EdgeHatchDirection
	{
		/// <summary>Up from its bottom edge (above the bottom tiers).</summary>
		Up,
		/// <summary>Right from its left edge (a left-edge pane's map side).</summary>
		Right,
		/// <summary>Left from its right edge (ForeCast's map side).</summary>
		Left,
	}

	/// <summary>One hatch line, in BAND coordinates: the edge runs along y = 0, x ∈ [0, length]; y grows into the map.</summary>
	public readonly record struct HatchSegment(double X1, double Y1, double X2, double Y2);

	/// <summary>
	/// The hatch's lines (pure, so it's tested). Two families of 45° lines start every <c>spacing</c> along the edge and
	/// run into the band. THINNING = each line reaches a share of the depth by its index — 1 in 2 lines stops at ¼, 1 in
	/// 4 at ½, 1 in 8 at ¾, the rest run the full depth — so the crossing grid halves its density at each step and
	/// dissolves. Without thinning every line runs the full depth (the fade, if any, is the brush's). Everything is
	/// clipped to the band.
	/// </summary>
	public static class EdgeHatchGeometry
	{
		public static IReadOnlyList<HatchSegment> Build(double length, double depth, double spacing, bool thin)
		{
			var lines = new List<HatchSegment>();
			if (!(length > 0) || !(depth > 0) || !double.IsFinite(length) || !double.IsFinite(depth)) { return lines; }
			spacing = Math.Max(1, double.IsFinite(spacing) ? spacing : EdgeHatchSizes.DefaultSpacing);

			int first = -(int)Math.Ceiling(depth / spacing) - 1;
			int last = (int)Math.Ceiling(length / spacing) + 1;
			for (int k = first; k <= last; k++)
			{
				double x0 = k * spacing;
				double reach = (thin ? Reach(Math.Abs(k) + 1) : 1) * depth;
				foreach (var dir in new[] { 1, -1 })
				{
					// x(t) = x0 + dir·t, y(t) = t, t ∈ [0, reach]; keep the part with x ∈ [0, length].
					double lo = dir > 0 ? -x0 : x0 - length;
					double hi = dir > 0 ? length - x0 : x0;
					double t0 = Math.Max(0, lo), t1 = Math.Min(reach, hi);
					if (t1 <= t0) { continue; }
					lines.Add(new HatchSegment(x0 + dir * t0, t0, x0 + dir * t1, t1));
				}
			}
			return lines;
		}

		/// <summary>How far line <paramref name="i"/> (1-based) reaches, as a share of the depth.</summary>
		internal static double Reach(int i)
		{
			int zeros = 0;
			while (i > 0 && i % 2 == 0 && zeros < 3) { i /= 2; zeros++; }
			return (zeros + 1) / 4.0;
		}
	}
}
