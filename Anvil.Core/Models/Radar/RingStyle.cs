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

	/// <summary>How the distance rings' labels are drawn: opacity, text size and the dark halo behind them.
	/// The colour is AppSettings.DistanceLabelColor (empty = the distance rings' own colour).</summary>
	public sealed record RingLabelStyle(int OpacityPct, double Size, double Halo)
	{
		public const int MinOpacityPct = 5, MaxOpacityPct = 100;
		public const double MinSize = 8, MaxSize = 20;
		public const double MinHalo = 0, MaxHalo = 4;

		public static RingLabelStyle Default { get; } = new(100, 10, 1.2);

		public RingLabelStyle Normalized() => new(
			Math.Clamp(OpacityPct, MinOpacityPct, MaxOpacityPct),
			double.IsFinite(Size) ? Math.Round(Math.Clamp(Size, MinSize, MaxSize)) : Default.Size,
			double.IsFinite(Halo) ? Math.Round(Math.Clamp(Halo, MinHalo, MaxHalo), 1) : Default.Halo);
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
