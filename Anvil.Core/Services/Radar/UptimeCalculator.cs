using System;
using System.Collections.Generic;
using System.Linq;
using Anvil.Models;

namespace Anvil.Services
{
	/// <summary>
	/// Turns a site's archive volume times into a <see cref="SiteUptimeReport"/> — pure, no I/O, so every rule
	/// below is pinned by <c>UptimeCalculatorTests</c>.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE RULES (the NWS publishes no "N minutes = down" threshold; these are Anvil's, chosen 2026-09-26):
	/// <list type="bullet">
	/// <item>CADENCE is read from the data, not the VCP: the median of the site's nearby volume intervals (the
	/// archive listing has times only, no VCP). So a clear-air site's normal ~10-min spacing never flags.</item>
	/// <item>GAP = an interval longer than max(<see cref="GapFloor"/>, <see cref="GapCadenceMultiple"/> x cadence)
	/// — at least two volumes missing. Listed, NOT counted as downtime.</item>
	/// <item>DOWN = an interval longer than <see cref="RadarSiteStatus.Staleness"/> — the SAME number that turns a
	/// site marker offline, so the strip and the markers can never disagree. Counted from when the next volume
	/// was due (last + cadence) to when data resumed.</item>
	/// <item>The live TAIL (last volume → now) can only be DOWN, never a gap: the archive lags real time by ~10
	/// min, so a short tail is just lag. Same threshold as the offline rule, for the same reason.</item>
	/// <item>A day whose listing failed is UNKNOWN: any interval touching it is skipped (no data because we
	/// couldn't look is not an outage) and it's left out of every percentage.</item>
	/// </list>
	/// </remarks>
	public static class UptimeCalculator
	{
		public static readonly TimeSpan GapFloor = TimeSpan.FromMinutes(15);
		public const double GapCadenceMultiple = 2.5;

		/// <summary>Cadence assumed where the data gives none (no normal intervals nearby) — the slowest real
		/// VCP, so a guess can only make a hole LESS likely to flag.</summary>
		public static readonly TimeSpan DefaultCadence = TimeSpan.FromMinutes(10);

		// How many intervals either side feed the cadence median.
		private const int CadenceNeighbours = 6;

		/// <param name="volumeTimes">Volume start times, any order. Include the day BEFORE the window, so the
		/// first interval is a real one rather than "since the window opened".</param>
		/// <param name="knownUtcDays">UTC days whose listing succeeded (an empty day that was listed is known).</param>
		/// <param name="windowStartUtc">Start of the reported window.</param>
		/// <param name="nowUtc">Now — the window's end, and where an ongoing outage runs to.</param>
		/// <param name="dayZone">Zone the per-day buckets are cut in (the user's local days; tests use UTC).</param>
		public static SiteUptimeReport Compute(
			string siteId,
			IEnumerable<DateTimeOffset> volumeTimes,
			IReadOnlySet<DateOnly> knownUtcDays,
			DateTimeOffset windowStartUtc,
			DateTimeOffset nowUtc,
			TimeZoneInfo dayZone)
		{
			var times = volumeTimes.Select(t => t.ToUniversalTime()).Where(t => t <= nowUtc).Distinct().OrderBy(t => t).ToList();
			var holes = new List<UptimeHole>();

			bool Known(DateTimeOffset from, DateTimeOffset to)
			{
				for (var d = DateOnly.FromDateTime(from.UtcDateTime); d <= DateOnly.FromDateTime(to.UtcDateTime); d = d.AddDays(1))
				{
					if (!knownUtcDays.Contains(d)) return false;
				}
				return true;
			}

			var intervals = new List<TimeSpan>(Math.Max(0, times.Count - 1));
			for (var i = 1; i < times.Count; i++) intervals.Add(times[i] - times[i - 1]);

			// Between volumes.
			for (var i = 0; i < intervals.Count; i++)
			{
				var a = times[i];
				var b = times[i + 1];
				if (b <= windowStartUtc || !Known(a, b)) continue;
				var cadence = CadenceAround(intervals, i);
				var kind = Classify(intervals[i], cadence);
				if (kind is { } k) holes.Add(Clip(new UptimeHole(k, a + cadence, b, Ongoing: false), windowStartUtc));
			}

			// Before the first volume: the window opened (or the lead day began) with nothing — only when the
			// lead-in is known, else we simply didn't look.
			var leadStart = windowStartUtc.AddDays(-1);
			var first = times.Count > 0 ? times[0] : (DateTimeOffset?)null;
			var leadEnd = first ?? nowUtc;
			if (leadEnd > windowStartUtc && Known(leadStart, leadEnd) && leadEnd - leadStart > RadarSiteStatus.Staleness)
			{
				holes.Add(Clip(new UptimeHole(UptimeHoleKind.Down, leadStart, leadEnd, Ongoing: first is null), windowStartUtc));
			}

			// The live tail: last volume → now. Down only (a short tail is archive lag).
			if (times.Count > 0)
			{
				var last = times[^1];
				var cadence = CadenceAround(intervals, intervals.Count - 1);
				if (nowUtc - last > RadarSiteStatus.Staleness && Known(last, nowUtc))
				{
					holes.Add(Clip(new UptimeHole(UptimeHoleKind.Down, last + cadence, nowUtc, Ongoing: true), windowStartUtc));
				}
			}

			holes.RemoveAll(h => h.EndUtc <= h.StartUtc);
			holes.Sort((x, y) => y.StartUtc.CompareTo(x.StartUtc));

			// Per-day buckets in the user's zone, and the overall share.
			var days = new List<UptimeDay>();
			var knownSpan = TimeSpan.Zero;
			var downSpan = TimeSpan.Zero;
			var localStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(windowStartUtc, dayZone).DateTime);
			var localEnd = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, dayZone).DateTime);
			for (var d = localStart; d <= localEnd; d = d.AddDays(1))
			{
				var from = Max(StartOfLocalDay(d, dayZone), windowStartUtc);
				var to = Min(StartOfLocalDay(d.AddDays(1), dayZone), nowUtc);
				var volumes = times.Count(t => t >= from && t < to);
				if (to <= from || !Known(from, to))
				{
					days.Add(new UptimeDay(d, null, volumes));
					continue;
				}
				var span = to - from;
				var down = DownWithin(holes, from, to);
				knownSpan += span;
				downSpan += down;
				days.Add(new UptimeDay(d, 1 - down / span, volumes));
			}

			double? up = knownSpan > TimeSpan.Zero ? 1 - downSpan / knownSpan : null;
			return new SiteUptimeReport(siteId, windowStartUtc, nowUtc, up, days, holes);
		}

		internal static UptimeHoleKind? Classify(TimeSpan interval, TimeSpan cadence)
		{
			if (interval > RadarSiteStatus.Staleness) return UptimeHoleKind.Down;
			var gapAt = TimeSpan.FromTicks(Math.Max(GapFloor.Ticks, (long)(cadence.Ticks * GapCadenceMultiple)));
			return interval > gapAt ? UptimeHoleKind.Gap : null;
		}

		// Median of the NORMAL intervals (≤ GapFloor) near index i — a hole must not inflate its own yardstick.
		internal static TimeSpan CadenceAround(IReadOnlyList<TimeSpan> intervals, int i)
		{
			var near = new List<TimeSpan>();
			for (var j = Math.Max(0, i - CadenceNeighbours); j <= Math.Min(intervals.Count - 1, i + CadenceNeighbours); j++)
			{
				if (j != i && intervals[j] <= GapFloor && intervals[j] > TimeSpan.Zero) near.Add(intervals[j]);
			}
			if (near.Count == 0) return DefaultCadence;
			near.Sort();
			return near[near.Count / 2];
		}

		private static TimeSpan DownWithin(IEnumerable<UptimeHole> holes, DateTimeOffset from, DateTimeOffset to)
		{
			var total = TimeSpan.Zero;
			foreach (var h in holes)
			{
				if (h.Kind != UptimeHoleKind.Down) continue;
				var s = Max(h.StartUtc, from);
				var e = Min(h.EndUtc, to);
				if (e > s) total += e - s;
			}
			return total;
		}

		private static UptimeHole Clip(UptimeHole h, DateTimeOffset windowStart) =>
			h.StartUtc < windowStart ? h with { StartUtc = windowStart } : h;

		private static DateTimeOffset StartOfLocalDay(DateOnly day, TimeZoneInfo zone)
		{
			var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
			return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
		}

		private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
		private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
	}
}
