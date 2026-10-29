using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// One docked section's measured sizes, for <see cref="DockStackAllocator"/>. Heights in DIPs.
	/// </summary>
	/// <param name="HeaderHeight">The always-shown part: a layer section's collapsed row, or the whole header block.</param>
	/// <param name="IsExpanded">Whether the body shows at all (a collapsed section is its header only).</param>
	/// <param name="BodyMinHeight">The body's controls without its list — they never shrink.</param>
	/// <param name="ListRows">How many rows the body's list holds (0 = no list).</param>
	/// <param name="ListMaxRows">The list's row cap even with room to spare (0 = no cap); rows past it count as hidden.</param>
	/// <param name="MoreLineHeight">The "+N more" line, shown only while some rows are hidden.</param>
	public sealed record DockSectionSize(
		double HeaderHeight,
		bool IsExpanded,
		double BodyMinHeight,
		int ListRows = 0,
		double ListRowHeight = 0,
		int ListMaxRows = 0,
		double MoreLineHeight = 0)
	{
		internal int RowCap => ListMaxRows > 0 ? Math.Min(ListRows, ListMaxRows) : ListRows;

		/// <summary>The body showing <paramref name="rows"/> list rows (+ the "+N more" line while any are hidden).</summary>
		internal double BodyHeightFor(int rows) =>
			BodyMinHeight + rows * ListRowHeight + (rows < ListRows ? MoreLineHeight : 0);

		internal double NaturalBody => IsExpanded ? BodyHeightFor(RowCap) : 0;

		internal double LeastBody => IsExpanded ? BodyHeightFor(0) : 0;

		/// <summary>The most rows whose body fits in <paramref name="height"/> (0 when not even the controls fit).</summary>
		internal int RowsFitting(double height)
		{
			for (int r = RowCap; r > 0; r--)
			{
				if (BodyHeightFor(r) <= height + DockStackAllocator.Epsilon) { return r; }
			}
			return 0;
		}
	}

	/// <summary>What one docked section gets: its body height and how many list rows show / hide behind "+N more".</summary>
	public sealed record DockSectionFit(double BodyHeight, int VisibleRows, int HiddenRows);

	/// <summary>The whole stack's fit. <see cref="Height"/> exceeds the available height only when <see cref="NeedsScroll"/>.</summary>
	public sealed record DockStackFit(IReadOnlyList<DockSectionFit> Sections, double Height, bool NeedsScroll);

	/// <summary>
	/// Shares a temporal pane's height among its DOCKED sections (rail state). The rules, in order:
	/// 1. headers (layer rows and the header block) never shrink;
	/// 2. expanded bodies share what's left — a body shorter than its share keeps its natural height and the rest goes to
	///    the others (water-filling);
	/// 3. only LISTS give way: a squeezed list shows the rows that fit, then a "+N more" line; a body's controls are its
	///    minimum;
	/// 4. if even the minimums don't fit, every body sits at its minimum and the stack scrolls.
	/// </summary>
	/// <remarks>Rows are whole, so a squeezed stack can end a little short of the available height; that leftover is
	/// handed back a row at a time, round-robin in stack order, before the stack settles.</remarks>
	public static class DockStackAllocator
	{
		internal const double Epsilon = 1e-6;

		/// <param name="sections">The docked sections, top first.</param>
		/// <param name="available">The height the stack may use.</param>
		/// <param name="spacing">The gap between neighbouring sections.</param>
		public static DockStackFit Fit(IReadOnlyList<DockSectionSize> sections, double available, double spacing = 0)
		{
			int n = sections.Count;
			if (n == 0) { return new DockStackFit(Array.Empty<DockSectionFit>(), 0, false); }

			double fixedHeight = sections.Sum(s => s.HeaderHeight) + spacing * (n - 1);
			double leftover = available - fixedHeight;
			var rows = new int[n];

			if (leftover + Epsilon < sections.Sum(s => s.LeastBody))
			{
				// Rule 4: minimums only, and the stack scrolls.
				return Result(sections, rows, fixedHeight, needsScroll: true);
			}

			double level = WaterLevel(sections, leftover);
			for (int i = 0; i < n; i++)
			{
				var s = sections[i];
				if (s.IsExpanded) { rows[i] = s.RowsFitting(Math.Clamp(level, s.LeastBody, s.NaturalBody)); }
			}

			// Whole rows leave a little over — hand it back a row at a time, round-robin.
			double used = Bodies(sections, rows);
			bool grew = true;
			while (grew)
			{
				grew = false;
				for (int i = 0; i < n; i++)
				{
					var s = sections[i];
					if (!s.IsExpanded || rows[i] >= s.RowCap) { continue; }
					double more = s.BodyHeightFor(rows[i] + 1) - s.BodyHeightFor(rows[i]);
					if (used + more <= leftover + Epsilon) { rows[i]++; used += more; grew = true; }
				}
			}
			return Result(sections, rows, fixedHeight, needsScroll: false);
		}

		/// <summary>The level t where Σ clamp(t, least, natural) over the expanded bodies equals <paramref name="leftover"/>
		/// (or the tallest natural body, when every body fits whole).</summary>
		private static double WaterLevel(IReadOnlyList<DockSectionSize> sections, double leftover)
		{
			var open = sections.Where(s => s.IsExpanded).ToList();
			if (open.Count == 0) { return 0; }
			double hi = open.Max(s => s.NaturalBody);
			if (open.Sum(s => s.NaturalBody) <= leftover + Epsilon) { return hi; }
			double lo = 0;
			for (int k = 0; k < 100; k++)
			{
				double mid = (lo + hi) / 2;
				double sum = open.Sum(s => Math.Clamp(mid, s.LeastBody, s.NaturalBody));
				if (sum > leftover) { hi = mid; } else { lo = mid; }
			}
			return lo;
		}

		private static double Bodies(IReadOnlyList<DockSectionSize> sections, int[] rows)
		{
			double sum = 0;
			for (int i = 0; i < sections.Count; i++)
			{
				if (sections[i].IsExpanded) { sum += sections[i].BodyHeightFor(rows[i]); }
			}
			return sum;
		}

		private static DockStackFit Result(IReadOnlyList<DockSectionSize> sections, int[] rows, double fixedHeight, bool needsScroll)
		{
			var fits = new DockSectionFit[sections.Count];
			for (int i = 0; i < sections.Count; i++)
			{
				var s = sections[i];
				fits[i] = s.IsExpanded
					? new DockSectionFit(s.BodyHeightFor(rows[i]), rows[i], s.ListRows - rows[i])
					: new DockSectionFit(0, 0, 0);
			}
			return new DockStackFit(fits, fixedHeight + fits.Sum(f => f.BodyHeight), needsScroll);
		}
	}
}
