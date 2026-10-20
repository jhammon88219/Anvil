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
		/// <summary>The row title: "Standard · Lower 48 states, Alaska".</summary>
		public string Description =>
			$"{(IsFullDetail ? "Full" : "Standard")} · {string.Join(", ", Areas.Select(a => a.Name))}";
	}

	/// <summary>
	/// THE naming rule for downloaded maps (the user's calls, 2026-10-20): the name SAYS the options —
	/// <c>anvil-map-{standard|full}-{area codes in list order}.pmtiles</c>, e.g. <c>anvil-map-standard-l48-ak.pmtiles</c>.
	/// The same options again are ALWAYS kept beside the old file, numbered <c>-2</c>, <c>-3</c>… — the next free number,
	/// so deleting every copy resets the next one to the plain name.
	/// </summary>
	public static class BasemapFileNames
	{
		public const string Prefix = "anvil-map-";
		public const string Extension = ".pmtiles";

		/// <summary>The name without copy number or extension: <c>anvil-map-standard-l48-ak</c>. Areas are put in list order.</summary>
		public static string BaseName(bool isFullDetail, IEnumerable<BasemapArea> areas)
		{
			var ordered = BasemapAreas.All.Where(a => areas.Contains(a)).Select(a => a.Code).ToList();
			if (ordered.Count == 0) throw new ArgumentException("A map needs at least one area.", nameof(areas));
			return Prefix + (isFullDetail ? "full" : "standard") + "-" + string.Join("-", ordered);
		}

		/// <summary>
		/// The file name a NEW download gets: the plain name if free, else the next free copy number. <paramref name="existing"/>
		/// = the file names already in the folder (any case; other files are ignored).
		/// </summary>
		public static string NextFileName(bool isFullDetail, IEnumerable<BasemapArea> areas, IEnumerable<string> existing)
		{
			var baseName = BaseName(isFullDetail, areas);
			var taken = new HashSet<string>(existing.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
			if (!taken.Contains(baseName + Extension)) return baseName + Extension;
			for (var copy = 2; ; copy++)
			{
				var name = $"{baseName}-{copy.ToString(CultureInfo.InvariantCulture)}{Extension}";
				if (!taken.Contains(name)) return name;
			}
		}

		/// <summary>Read a downloaded map's options back from its name; null for anything not named by this rule.</summary>
		public static BasemapFileOptions? TryParse(string fileName)
		{
			var name = Path.GetFileName(fileName);
			if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
				!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;

			var parts = name[Prefix.Length..^Extension.Length].ToLowerInvariant().Split('-');
			if (parts.Length < 2) return null;
			bool full;
			if (parts[0] == "full") full = true;
			else if (parts[0] == "standard") full = false;
			else return null;

			var copy = 1;
			var codes = parts.Skip(1).ToList();
			if (int.TryParse(codes[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
			{
				if (n < 2) return null;
				copy = n;
				codes.RemoveAt(codes.Count - 1);
			}

			var areas = new List<BasemapArea>();
			foreach (var code in codes)
			{
				if (BasemapAreas.ByCode(code) is not { } area || areas.Contains(area)) return null;
				areas.Add(area);
			}
			if (areas.Count == 0) return null;
			// Only names this rule could have written: areas in list order.
			if (!areas.SequenceEqual(BasemapAreas.All.Where(areas.Contains))) return null;
			return new BasemapFileOptions(full, areas, copy);
		}
	}
}
