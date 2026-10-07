using System;
using System.Collections.Generic;
using System.Linq;

namespace Anvil.Models
{
	/// <summary>
	/// The ids of the map overlays a user can re-stack by dragging the layer sections of the NowCast and
	/// PastCast windows. A list of them is TOP-FIRST: the first id draws on top.
	/// </summary>
	/// <remarks>
	/// ⚠️ MIRRORED in <c>Assets/Map/js/layers.js</c> GROUPS (which also maps each id to its layer-id
	/// prefixes) and in the <c>LayerId</c> of every <c>PanelSection</c> in NowCastTab / PastCastTab / ForeCastTab
	/// (incl. the GHOST rows). Change all three.
	/// </remarks>
	public static class LayerOrder
	{
		public const string Radar = "radar";
		public const string Outlook = "outlook";
		public const string Watches = "watches";
		public const string Warnings = "warnings";
		public const string Reports = "reports";
		public const string Damage = "damage";
		public const string Cells = "cells";
		public const string Discussions = "mds";
		public const string Tropical = "tropical";

		private static readonly HashSet<string> Known =
			new(StringComparer.Ordinal) { Radar, Outlook, Watches, Warnings, Reports, Damage, Cells, Discussions, Tropical };

		/// <summary>The map's DEFAULT stack, top first. ⚠️ MIRRORS <c>layers.js</c> GROUPS order (DEFAULT_ORDER).</summary>
		public static IReadOnlyList<string> Default { get; } =
			new[] { Cells, Reports, Damage, Warnings, Watches, Tropical, Discussions, Outlook, Radar };

		/// <summary>Drops unknown and repeated ids, so a hand-edited or older settings file can't send the
		/// page a group it doesn't have.</summary>
		public static List<string> Normalize(IEnumerable<string>? ids) =>
			(ids ?? Enumerable.Empty<string>()).Where(Known.Contains).Distinct(StringComparer.Ordinal).ToList();

		/// <summary>
		/// A saved order with every id it doesn't name filled in at its DEFAULT neighbour: directly beneath the
		/// nearest group above it in <see cref="Default"/>, or on top if none is. ⚠️ The SAME rule as
		/// <c>layers.js effectiveOrder()</c> — so a window showing a layer the save never named (a ghost row)
		/// shows it exactly where the map already draws it. Empty in = <see cref="Default"/> out.
		/// </summary>
		public static List<string> Complete(IEnumerable<string>? ids)
		{
			var outList = Normalize(ids);
			for (int d = 0; d < Default.Count; d++)
			{
				var id = Default[d];
				if (outList.Contains(id)) { continue; }
				int at = 0;
				for (int k = d - 1; k >= 0; k--)
				{
					int above = outList.IndexOf(Default[k]);
					if (above >= 0) { at = above + 1; break; }
				}
				outList.Insert(at, id);
			}
			return outList;
		}
	}
}
