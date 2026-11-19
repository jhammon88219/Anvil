using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>What a downloaded map file holds, read back from its name.</summary>
	/// <param name="IsFullDetail">Full (true) or Standard.</param>
	/// <param name="Areas">The areas, in <see cref="BasemapAreas.All"/> order.</param>
	/// <param name="Copy">1 for the plain name, 2+ for a kept-beside copy (<c>-2</c>, <c>-3</c>…).</param>
	public sealed record BasemapFileOptions(bool IsFullDetail, IReadOnlyList<BasemapArea> Areas, int Copy)
	{
		/// <summary>The long form, for the map setup screens: "Standard · Lower 48 states, Alaska".</summary>
		public string Description =>
			$"{DetailName} · {AreaList}";

		/// <summary>"Standard" or "Full" — the Which map to use list's DETAIL tag.</summary>
		public string DetailName => IsFullDetail ? "Full" : "Standard";

		/// <summary>Every area by its full name, ", " between — the row's tooltip.</summary>
		public string AreaList => string.Join(", ", Areas.Select(a => a.Name));

		/// <summary>
		/// The SHORT name a list row shows (2026-11-19, canvas "Which map to use" board 4): one or two areas joined by " + "
		/// ("Lower 48 + Alaska"); three or more = the first + "N more" ("Lower 48 + 3 more", the full list in the tooltip);
		/// a kept-beside copy adds " (2)". The detail is its own tag, never part of the name.
		/// </summary>
		public string ShortName
		{
			get
			{
				var names = Areas.Count <= 2
					? string.Join(" + ", Areas.Select(a => a.ShortName))
					: $"{Areas[0].ShortName} + {Areas.Count - 1} more";
				return Copy > 1 ? $"{names} ({Copy.ToString(CultureInfo.InvariantCulture)})" : names;
			}
		}
	}

	/// <summary>
	/// THE naming rule for downloaded maps: the name SAYS the options. Since 2026-11-19 (the user's call, shorter names):
	/// <c>anvil-{area codes in list order}[-full][-N].pmtiles</c> — Standard, the usual case, says nothing, e.g.
	/// <c>anvil-l48-ak.pmtiles</c>, <c>anvil-l48-hi-full.pmtiles</c>, <c>anvil-l48-ak-2.pmtiles</c>. The OLDER rule,
	/// <c>anvil-map-{standard|full}-{codes}[-N].pmtiles</c>, still READS (files on disk are never renamed) but is never written.
	/// The same options again are ALWAYS kept beside the old file, numbered <c>-2</c>, <c>-3</c>… — the next free number,
	/// so deleting every copy resets the next one to the plain name.
	/// </summary>
	public static class BasemapFileNames
	{
		/// <summary>Every map name (new and older rule) starts with this — what the folder listings look for.</summary>
		public const string Prefix = "anvil-";
		/// <summary>The older rule's start (<c>anvil-map-standard-…</c>): read, never written.</summary>
		public const string LegacyPrefix = "anvil-map-";
		public const string Extension = ".pmtiles";
		private const string FullSuffix = "full";

		/// <summary>The name without copy number or extension: <c>anvil-l48-ak</c> / <c>anvil-l48-ak-full</c>. Areas in list order.</summary>
		public static string BaseName(bool isFullDetail, IEnumerable<BasemapArea> areas)
		{
			var ordered = BasemapAreas.All.Where(a => areas.Contains(a)).Select(a => a.Code).ToList();
			if (ordered.Count == 0) throw new ArgumentException("A map needs at least one area.", nameof(areas));
			return Prefix + string.Join("-", ordered) + (isFullDetail ? "-" + FullSuffix : "");
		}

		/// <summary>
		/// The file name a NEW download gets: the plain name if free, else the next free copy number. <paramref name="existing"/>
		/// = the file names already in the folder (any case; other files are ignored).
		/// </summary>
		/// <remarks>⚠️ A copy number taken under EITHER rule counts (an older <c>anvil-map-standard-l48-ak.pmtiles</c> holds copy 1),
		/// so two files of the same options never show the same short name.</remarks>
		public static string NextFileName(bool isFullDetail, IEnumerable<BasemapArea> areas, IEnumerable<string> existing)
		{
			var picked = areas.ToList();
			var baseName = BaseName(isFullDetail, picked);
			var ordered = BasemapAreas.All.Where(picked.Contains).ToList();
			var names = existing.Select(Path.GetFileName).OfType<string>().ToList();
			var taken = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
			var copies = names.Select(TryParse).OfType<BasemapFileOptions>()
				.Where(o => o.IsFullDetail == isFullDetail && o.Areas.SequenceEqual(ordered))
				.Select(o => o.Copy).ToHashSet();
			for (var copy = 1; ; copy++)
			{
				var name = copy == 1 ? baseName + Extension : $"{baseName}-{copy.ToString(CultureInfo.InvariantCulture)}{Extension}";
				if (!copies.Contains(copy) && !taken.Contains(name)) return name;
			}
		}

		/// <summary>Read a downloaded map's options back from its name (either rule); null for anything neither could write.</summary>
		public static BasemapFileOptions? TryParse(string fileName)
		{
			var name = Path.GetFileName(fileName);
			if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
				!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;
			return name.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase)
				? ParseLegacy(name[LegacyPrefix.Length..^Extension.Length].ToLowerInvariant().Split('-'))
				: ParseCurrent(name[Prefix.Length..^Extension.Length].ToLowerInvariant().Split('-'));
		}

		// anvil-{codes}[-full][-N]
		private static BasemapFileOptions? ParseCurrent(string[] parts)
		{
			var rest = parts.ToList();
			if (!TakeCopy(rest, out var copy)) return null;
			var full = rest.Count > 0 && rest[^1] == FullSuffix;
			if (full) rest.RemoveAt(rest.Count - 1);
			return AreasOf(rest) is { } areas ? new BasemapFileOptions(full, areas, copy) : null;
		}

		// anvil-map-{standard|full}-{codes}[-N] (the older rule)
		private static BasemapFileOptions? ParseLegacy(string[] parts)
		{
			if (parts.Length < 2) return null;
			bool full;
			if (parts[0] == FullSuffix) full = true;
			else if (parts[0] == "standard") full = false;
			else return null;
			var rest = parts.Skip(1).ToList();
			if (!TakeCopy(rest, out var copy)) return null;
			return AreasOf(rest) is { } areas ? new BasemapFileOptions(full, areas, copy) : null;
		}

		// A trailing copy number (2+) comes off; a "1" (or less) means the rule never wrote this name.
		private static bool TakeCopy(List<string> parts, out int copy)
		{
			copy = 1;
			if (parts.Count == 0 || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return true;
			if (n < 2) return false;
			copy = n;
			parts.RemoveAt(parts.Count - 1);
			return true;
		}

		// Known codes, each once, in list order — only names the rule could have written.
		private static List<BasemapArea>? AreasOf(List<string> codes)
		{
			var areas = new List<BasemapArea>();
			foreach (var code in codes)
			{
				if (BasemapAreas.ByCode(code) is not { } area || areas.Contains(area)) return null;
				areas.Add(area);
			}
			if (areas.Count == 0) return null;
			return areas.SequenceEqual(BasemapAreas.All.Where(areas.Contains)) ? areas : null;
		}
	}
}
