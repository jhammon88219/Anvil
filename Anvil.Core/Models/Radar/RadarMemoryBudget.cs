using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The "Radar memory" budget (Settings → Radar → Radar memory): how much RAM a PastCast loop's decoded geometry may
	/// hold, turned into the loop's frame cap. Pure math — the setting lives in <c>AppSettings.RadarMemoryGb</c> and the
	/// cap is applied by <c>RadarLoopEngine</c> through <c>ReplayFramePlan.Choose</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ WHY A BUDGET AND NOT THE OLD FIXED 40: the 40 was sized against the renderer's "~4,192 MB JS heap"
	/// (performance.memory.jsHeapSizeLimit). That limit does NOT hold typed-array geometry — a 60-frame 4-pane Enid loop
	/// (2026-10-08) held 4,235 MB with heapMb above the limit and nothing failed. The real ceiling is the machine's RAM,
	/// which only the user can share out, so they pick it; WebView2 enforces nothing,
	/// Anvil enforces it by SIZING the loop.
	/// <para>COST = products per frame × <see cref="PerProductFrameMb"/>. Reflectivity, velocity and SRV (the trio) are
	/// built on EVERY frame whatever the panes show (radar.js velPrefetch, Rule 3); each visible dual-pol product (CC, KDP,
	/// ZDR, SW) adds one. So one pane costs 3 products at least, not 1. Loops of ≤ 12 frames also prefetch the four dual-pol
	/// products (FULL_PREFETCH_MAX_FRAMES) — always under any budget's floor, so ignored here.</para>
	/// </remarks>
	public static class RadarMemoryBudget
	{
		/// <summary>Measured MB of decoded geometry per product per frame (Enid 2026, KVNX, 4,235 MB ÷ 60 frames ÷ 4
		/// products, Inspect off). Re-measure with the Dev frame-cap override, never by taste.</summary>
		public const double PerProductFrameMb = 17.6;

		/// <summary>Products every frame builds whatever is shown: reflectivity + velocity + SRV.</summary>
		public const int TrioProducts = 3;

		/// <summary>The fewest frames any budget allows, so a tiny budget still loads a usable loop.</summary>
		public const int MinFrames = 20;

		public const double MinGb = 1.0;
		public const double StepGb = 0.5;

		private static readonly HashSet<string> Trio = new(StringComparer.Ordinal) { "reflectivity", "velocity", "srv" };

		/// <summary>This PC's physical RAM in GB (.NET's view of available physical memory; Core stays Windows-free).</summary>
		public static double MachineRamGb()
		{
			var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
			return bytes > 0 ? bytes / (1024.0 * 1024 * 1024) : 8.0;
		}

		/// <summary>The default: a quarter of RAM, on the 0.5 GB step, never below <see cref="MinGb"/>.</summary>
		public static double RecommendedGb(double ramGb) => Clamp(Snap(ramGb / 4), ramGb);

		/// <summary>The most the slider allows: three quarters of RAM.</summary>
		public static double MaxGb(double ramGb) => Math.Max(MinGb, Math.Floor(ramGb * 0.75 / StepGb) * StepGb);

		/// <summary>The effective budget: the saved value clamped to this PC, or the recommended default when unset
		/// (null — so a RAM upgrade moves the default).</summary>
		public static double Effective(double? savedGb, double ramGb) =>
			savedGb is { } gb && !double.IsNaN(gb) ? Clamp(Snap(gb), ramGb) : RecommendedGb(ramGb);

		public static double Clamp(double gb, double ramGb) => Math.Clamp(gb, MinGb, MaxGb(ramGb));

		private static double Snap(double gb) => Math.Round(gb / StepGb) * StepGb;

		/// <summary>Products a frame costs for these visible pane products: the trio + each distinct other product.</summary>
		public static int ProductsFor(IEnumerable<string> visibleProducts) =>
			TrioProducts + visibleProducts.Where(p => !Trio.Contains(p)).Distinct(StringComparer.Ordinal).Count();

		/// <summary>The WORST case for a layout of <paramref name="panes"/> panes — every pane a different dual-pol
		/// product (the Settings readout's "at least" figure).</summary>
		public static int WorstProductsFor(int panes) => TrioProducts + Math.Clamp(panes, 0, 4);

		/// <summary>The PastCast frame cap: as many frames as the budget holds at this cost, floored at
		/// <see cref="MinFrames"/>.</summary>
		public static int FrameCap(double budgetGb, int products) =>
			Math.Max(MinFrames, (int)Math.Floor(budgetGb * 1024 / (PerProductFrameMb * Math.Max(1, products))));

		/// <summary>Estimated MB a loop of <paramref name="frames"/> frames holds at this cost.</summary>
		public static double EstimatedMb(int frames, int products) => frames * products * PerProductFrameMb;

		/// <summary>The Settings warning tier for a budget.</summary>
		public static RadarMemoryLevel LevelOf(double budgetGb, double ramGb) =>
			budgetGb > ramGb / 2 ? RadarMemoryLevel.High
			: FrameCap(budgetGb, WorstProductsFor(4)) <= MinFrames ? RadarMemoryLevel.Low
			: RadarMemoryLevel.Fine;
	}

	/// <summary>The Settings warning tier for a radar memory budget.</summary>
	public enum RadarMemoryLevel
	{
		/// <summary>Within the sensible range.</summary>
		Fine,
		/// <summary>Over half the PC's RAM — the PC may slow down or the map may go black.</summary>
		High,
		/// <summary>So small that 4-pane loops sit on the frame floor.</summary>
		Low,
	}
}
