using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The spacing of the DISTANCE rings (Settings → Radar Range Ring), in whatever ground unit
	/// <see cref="DistanceUnits"/> is set to — "50" means 50 km, 50 mi or 50 nm. <see cref="Auto"/> (0) lets the
	/// page pick a step from the reach of the data on screen (radar-scope.js autoStep), so a TDWR's 89 km upper
	/// tilt gets rings every 25 and a NEXRAD's 460 km every 100.
	/// </summary>
	/// <remarks>⚠️ Persisted as the VALUE, never the picker index; an unknown value reads as <see cref="Auto"/>.</remarks>
	public static class RangeRingSpacings
	{
		public const int Auto = 0;

		/// <summary>Every value, in picker order. ⚠️ Parallel to <see cref="Labels"/>.</summary>
		public static IReadOnlyList<int> All { get; } = new[] { Auto, 25, 50, 100 };

		/// <summary>The picker's labels, in <see cref="All"/> order.</summary>
		public static IReadOnlyList<string> Labels { get; } = new[] { "Auto", "25", "50", "100" };

		public static int Normalize(int value) => All.Contains(value) ? value : Auto;

		public static int IndexOf(int value) => Math.Max(0, All.ToList().IndexOf(Normalize(value)));

		public static int FromIndex(int index) => index >= 0 && index < All.Count ? All[index] : Auto;
	}
}
