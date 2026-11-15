using System;
using System.Collections.Generic;
using System.Globalization;

namespace Anvil.Models
{
	/// <summary>The five kinds of data Settings → Storage lists, in row order (= the usage bar's order).</summary>
	public enum StorageKind { RadarVolumes, RadarDiagnostics, WeatherData, Logs, UsageHistory }

	/// <summary>How close a kind is to its Max size — the Size column's colour (white / yellow / red).</summary>
	public enum StorageFill { Low, Medium, High }

	/// <summary>One choice in a Max size / Keep for dropdown. <see cref="Value"/> is bytes or days.</summary>
	public sealed record StorageLimitChoice(long Value, string Label)
	{
		public override string ToString() => Label;
	}

	/// <summary>
	/// The rules behind Settings → Storage (<c>StorageManagerSettingsConsole</c>): the fill thresholds, the byte
	/// wording, and every dropdown's choices. One place, so the table and its "?" hints can't disagree.
	/// </summary>
	public static class StorageLimits
	{
		public const long MB = 1024L * 1024;
		public const long GB = 1024L * MB;

		/// <summary>From this share of Max size on, Size turns yellow…</summary>
		public const double MediumAt = 0.60;
		/// <summary>…and from this share on, red. ⚠️ <c>StorageGlossary.Size</c> words these — change both.</summary>
		public const double HighAt = 0.85;

		/// <summary>The colour level of <paramref name="bytes"/> against <paramref name="maxBytes"/>; no cap = Low.</summary>
		public static StorageFill FillOf(long bytes, long? maxBytes)
		{
			if (maxBytes is not { } max || max <= 0) return StorageFill.Low;
			var share = (double)bytes / max;
			return share >= HighAt ? StorageFill.High : share >= MediumAt ? StorageFill.Medium : StorageFill.Low;
		}

		/// <summary>"1.8 GB", "242 MB", "4.7 MB", "0 MB" — one decimal below 10 MB and from 1 GB up.</summary>
		public static string FormatBytes(long bytes)
		{
			if (bytes <= 0) return "0 MB";
			var mb = bytes / (double)MB;
			if (mb >= 1024) return (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
			if (mb >= 10) return Math.Round(mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
			return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
		}

		/// <summary>"85%", "3%", "&lt;1%" — a kind's share of the total.</summary>
		public static string FormatShare(long bytes, long total)
		{
			if (total <= 0 || bytes <= 0) return "0%";
			var pct = bytes * 100.0 / total;
			return pct < 1 ? "<1%" : Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) + "%";
		}

		private static StorageLimitChoice Mb(int mb) =>
			new(mb * MB, mb >= 1024 ? (mb / 1024).ToString(CultureInfo.InvariantCulture) + " GB" : mb.ToString(CultureInfo.InvariantCulture) + " MB");
		private static StorageLimitChoice Gb(int gb) => new(gb * GB, gb.ToString(CultureInfo.InvariantCulture) + " GB");
		private static StorageLimitChoice Days(int d) => new(d, d.ToString(CultureInfo.InvariantCulture) + " days");

		// ── the dropdowns (a persisted value not in its list is added at load, never dropped) ──
		public static readonly IReadOnlyList<StorageLimitChoice> RadarVolumeMax = new[] { Gb(2), Gb(5), Gb(10), Gb(20), Gb(50) };
		public static readonly IReadOnlyList<StorageLimitChoice> WeatherDataMax = new[] { Mb(100), Mb(250), Mb(500), Mb(1024), Mb(2048) };
		public static readonly IReadOnlyList<StorageLimitChoice> WeatherDataKeep = new[] { Days(7), Days(14), Days(30), Days(60), Days(90) };
		public static readonly IReadOnlyList<StorageLimitChoice> LogsMax = new[] { Mb(50), Mb(100), Mb(250), Mb(500) };
		public static readonly IReadOnlyList<StorageLimitChoice> LogsKeep = new[] { Days(14), Days(30), Days(90), Days(180), Days(365) };

		// The radar engine's other three limits (AppSettings.RadarCacheKeepDays / RadarDiagnosticsMaxGb / RadarDiagnosticsKeepDays,
		// read live by Level2RadarService — engine edit OK'd 2026-11-15).
		public static readonly IReadOnlyList<StorageLimitChoice> RadarVolumeKeep = new[] { Days(1), Days(3), Days(5), Days(7), Days(14), Days(30) };
		public static readonly IReadOnlyList<StorageLimitChoice> DiagnosticsMax = new[] { Gb(1), Gb(2), Gb(5), Gb(10) };
		public static readonly IReadOnlyList<StorageLimitChoice> DiagnosticsKeep = new[] { Days(30), Days(90), Days(180), Days(365) };

		/// <summary>The choice in <paramref name="list"/> with <paramref name="value"/>, or a new one worded like
		/// its neighbours (an older build's value, a hand-edited file).</summary>
		public static (IReadOnlyList<StorageLimitChoice> List, StorageLimitChoice Pick) Resolve(
			IReadOnlyList<StorageLimitChoice> list, long value, bool days)
		{
			foreach (var c in list) if (c.Value == value) return (list, c);
			var extra = days ? Days((int)value) : value % GB == 0 ? Gb((int)(value / GB)) : Mb((int)(value / MB));
			var merged = new List<StorageLimitChoice>(list) { extra };
			merged.Sort((a, b) => a.Value.CompareTo(b.Value));
			return (merged, extra);
		}
	}
}
