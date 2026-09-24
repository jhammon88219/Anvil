using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// How ONE range ring is stroked (Settings → Radar Range Ring): opacity, thickness and line pattern. The
	/// colour is NOT here — each ring's colour is its own <see cref="ScopeColors"/> token on AppSettings, because
	/// the reflectivity outline's colour is <c>ScopeColor</c>, which the range ruler shares.
	/// </summary>
	/// <remarks>
	/// ⚠️ An immutable record persisted WHOLE: AppSettings auto-saves on its own PropertyChanged, so a style is
	/// changed by replacing the record (<c>with</c>), never by mutating it. Every value is clamped by
	/// <see cref="Normalized"/> on the way in, because a hand-edited file lands straight in a MapLibre paint
	/// property (a negative width or an unknown pattern throws inside render and blanks the layers above).
	/// </remarks>
	public sealed record RingStyle(int OpacityPct, double Width, string Line)
	{
		public const int MinOpacityPct = 5, MaxOpacityPct = 100;
		public const double MinWidth = 0.5, MaxWidth = 6;

		/// <summary>The reflectivity outline as it always looked: solid, fairly strong.</summary>
		public static RingStyle OutlineDefault { get; } = new(55, 1.3, RingLines.Solid);

		/// <summary>The velocity reach: dashed, a touch stronger than the outline so the dash still reads.</summary>
		public static RingStyle VelocityDefault { get; } = new(60, 1.2, RingLines.Dashed);

		/// <summary>The distance rings: faint and dotted — a ruler, not a boundary.</summary>
		public static RingStyle DistanceDefault { get; } = new(45, 0.8, RingLines.Dotted);

		/// <summary>This style with every value inside its range (unknown pattern → solid; NaN width → 1).</summary>
		public RingStyle Normalized() => new(
			Math.Clamp(OpacityPct, MinOpacityPct, MaxOpacityPct),
			double.IsFinite(Width) ? Math.Round(Math.Clamp(Width, MinWidth, MaxWidth), 1) : 1,
			RingLines.Normalize(Line));
	}

	/// <summary>How the distance rings' labels ("100 mi", "200 mi"…) are drawn: opacity, text size, halo width,
	/// font, letter spacing, where they sit against their ring, whether the unit is written, and how many label
	/// lines run out from the site. The colours are AppSettings.DistanceLabelColor / DistanceLabelHaloColor.</summary>
	/// <remarks>⚠️ Fields added after the first release carry DEFAULTS in the constructor: System.Text.Json fills
	/// a parameter missing from an older settings file with its default value, so an old file still loads.</remarks>
	public sealed record RingLabelStyle(int OpacityPct, double Size, double Halo,
		string Font = RingLabelFonts.Medium, double Spacing = 0, string Placement = RingLabelPlacements.Above,
		bool ShowUnits = true, int Axes = 1)
	{
		public const int MinOpacityPct = 5, MaxOpacityPct = 100;
		public const double MinSize = 8, MaxSize = 24;
		public const double MinHalo = 0, MaxHalo = 4;
		public const double MinSpacing = 0, MaxSpacing = 0.5;

		/// <summary>How many label lines may run out from the site, evenly spaced from the handle's bearing.</summary>
		public static IReadOnlyList<int> AxesChoices { get; } = new[] { 1, 2, 4 };

		public static RingLabelStyle Default { get; } = new(100, 10, 1.2);

		public RingLabelStyle Normalized() => new(
			Math.Clamp(OpacityPct, MinOpacityPct, MaxOpacityPct),
			double.IsFinite(Size) ? Math.Round(Math.Clamp(Size, MinSize, MaxSize)) : Default.Size,
			double.IsFinite(Halo) ? Math.Round(Math.Clamp(Halo, MinHalo, MaxHalo), 1) : Default.Halo,
			RingLabelFonts.Normalize(Font),
			double.IsFinite(Spacing) ? Math.Round(Math.Clamp(Spacing, MinSpacing, MaxSpacing), 2) : 0,
			RingLabelPlacements.Normalize(Placement),
			ShowUnits,
			AxesChoices.Contains(Axes) ? Axes : 1);
	}

	/// <summary>The label font — only stacks the bundled glyph host actually serves (Assets/Map/fonts); a stack it
	/// lacks renders NOTHING. radar-scope.js maps the token to the stack name (FONTS there — change both).</summary>
	public static class RingLabelFonts
	{
		public const string Regular = "regular", Medium = "medium", Italic = "italic";
		public static IReadOnlyList<string> All { get; } = new[] { Regular, Medium, Italic };
		public static IReadOnlyList<string> Labels { get; } = new[] { "Regular", "Medium", "Italic" };
		public static string Normalize(string? token) => All.Contains(token) ? token! : Medium;
		public static int IndexOf(string? token) => All.ToList().IndexOf(Normalize(token));
		public static string FromIndex(int index) => index >= 0 && index < All.Count ? All[index] : Medium;
	}

	/// <summary>Where a label sits against its ring. radar-scope.js maps it to a text-offset (change both).</summary>
	public static class RingLabelPlacements
	{
		public const string Above = "above", On = "on", Below = "below";
		public static IReadOnlyList<string> All { get; } = new[] { Above, On, Below };
		public static IReadOnlyList<string> Labels { get; } = new[] { "Above", "On the ring", "Below" };
		public static string Normalize(string? token) => All.Contains(token) ? token! : Above;
		public static int IndexOf(string? token) => All.ToList().IndexOf(Normalize(token));
		public static string FromIndex(int index) => index >= 0 && index < All.Count ? All[index] : Above;
	}

	/// <summary>The size of the round KNOBS that ride the outer ring — the distance-label handle and the range
	/// ruler's knob share it, so the two stay drawn identically.</summary>
	public static class RingKnobSize
	{
		public const double Min = 20, Max = 48, Default = 32;
		public static double Normalize(double px) => double.IsFinite(px) ? Math.Round(Math.Clamp(px, Min, Max)) : Default;
	}

	/// <summary>
	/// A ring's line pattern. The TOKEN is what is persisted and sent to the page; radar-scope.js maps it to a
	/// MapLibre <c>line-dasharray</c> (DASHES there — change both).
	/// </summary>
	public static class RingLines
	{
		public const string Solid = "solid", Dashed = "dashed", Dotted = "dotted", DashDot = "dashdot";

		/// <summary>Every token, in picker order. ⚠️ Parallel to <see cref="Labels"/>.</summary>
		public static IReadOnlyList<string> All { get; } = new[] { Solid, Dashed, Dotted, DashDot };

		public static IReadOnlyList<string> Labels { get; } = new[] { "Solid", "Dashed", "Dotted", "Dash-dot" };

		public static string Normalize(string? token) => All.Contains(token) ? token! : Solid;

		public static int IndexOf(string? token) => All.ToList().IndexOf(Normalize(token));

		public static string FromIndex(int index) => index >= 0 && index < All.Count ? All[index] : Solid;
	}

	/// <summary>Where the distance rings' labels sit: a bearing in whole degrees clockwise from north, 0–359.</summary>
	public static class RingLabelBearing
	{
		public static double Normalize(double degrees)
		{
			if (!double.IsFinite(degrees))
			{
				return 0;
			}
			var d = Math.Round(degrees) % 360;
			return d < 0 ? d + 360 : d;
		}
	}
}
