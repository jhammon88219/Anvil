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
	/// prefixes) and in the <c>LayerId</c> of every <c>PanelSection</c> in NowCastTab / PastCastTab.
	/// Change all three.
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

		private static readonly HashSet<string> Known =
			new(StringComparer.Ordinal) { Radar, Outlook, Watches, Warnings, Reports, Damage, Cells };

		/// <summary>Drops unknown and repeated ids, so a hand-edited or older settings file can't send the
		/// page a group it doesn't have.</summary>
		public static List<string> Normalize(IEnumerable<string>? ids) =>
			(ids ?? Enumerable.Empty<string>()).Where(Known.Contains).Distinct(StringComparer.Ordinal).ToList();
	}
}
